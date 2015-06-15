using Facebook;
using NAudio.Wave;
using NAudioDemo.NetworkChatDemo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CryptoFbChat
{
    public partial class MainForm : Form
    {
        private string myLocalIp = "192.168.1.2";
        private WaveInEvent waveIn;
        private IWavePlayer waveOut;
        private BufferedWaveProvider waveProvider;
        private UdpClient udpSender;
        private UdpClient udpListener;
        private INetworkChatCodec selectedCodec;
        private volatile bool connected = false;
        private RijndaelManaged myRijndael = new RijndaelManaged();
        private Int64 fbAppId = 1404447426539494;
        string redirectFbPath = "https://apps.facebook.com/nurecryptochat";
        Dictionary<int, IPEndPoint> threadMappings = new Dictionary<int, IPEndPoint>();
        RSACryptoServiceProvider myouRSA = new RSACryptoServiceProvider();
        int inputDeviceNumber;

        public MainForm()
        {
            InitializeComponent();
            PopulateInputDevicesCombo();

            List<INetworkChatCodec> codecs = new List<INetworkChatCodec>();
            codecs.Add(new ALawChatCodec());
            codecs.Add(new MuLawChatCodec());
            PopulateCodecsCombo(codecs);
            Disposed += OnFormDisposed;
            startFbLogin();
            listBoxMembers.Items.Clear();
        }

        private void startFbLogin()
        {
            var fb = new FacebookClient();
            var query = new Dictionary<string, object>();
            query["client_id"] = fbAppId;
            query["redirect_uri"] = redirectFbPath;
            query["response_type"] = "token";
            query["display"] = "popup";

            Uri loginUri = fb.GetLoginUrl(query);
            webBrowser1.Navigate(loginUri);
        }

        void OnFormDisposed(object sender, EventArgs e)
        {
            Disconnect();
        }

        private void PopulateCodecsCombo(IEnumerable<INetworkChatCodec> codecs)
        {
            comboBoxCodecs.Items.Add(new CodecComboItem { Text = "No codec compression", Codec = null });

            var sorted = from codec in codecs
                         where codec.IsAvailable
                         orderby codec.BitsPerSecond ascending
                         select codec;

            foreach (var codec in sorted)
            {
                string bitRate = codec.BitsPerSecond == -1 ? "VBR" : String.Format("{0:0.#}kbps", codec.BitsPerSecond / 1000.0);
                string text = String.Format("{0} ({1})", codec.Name, bitRate);
                comboBoxCodecs.Items.Add(new CodecComboItem { Text = text, Codec = codec });
            }

            comboBoxCodecs.SelectedIndex = 0;
        }

        class CodecComboItem
        {
            public string Text { get; set; }
            public INetworkChatCodec Codec { get; set; }
            public override string ToString()
            {
                return Text;
            }
        }

        private void PopulateInputDevicesCombo()
        {
            for (int n = 0; n < WaveIn.DeviceCount; n++)
            {
                var capabilities = WaveIn.GetCapabilities(n);
                comboBoxInputDevices.Items.Add(capabilities.ProductName);
            }
            if (comboBoxInputDevices.Items.Count > 0)
            {
                comboBoxInputDevices.SelectedIndex = 0;
            }
        }
        
        private void GiveAESKeyAsync()
        {
            int port = 7081;
            if (threadMappings[Thread.CurrentThread.ManagedThreadId].ToString().StartsWith("109"))
                port = 7082;
            TcpClient tcpConnection = new TcpClient(new IPEndPoint(IPAddress.Parse(myLocalIp), port));
            tcpConnection.Connect(threadMappings[Thread.CurrentThread.ManagedThreadId]);
            var stream = tcpConnection.GetStream();

            BinaryFormatter serializer = new BinaryFormatter();
            RSAParameters myouParams = (RSAParameters)serializer.Deserialize(stream);
            myouRSA.ImportParameters(myouParams);
            byte[] encryptedByRsaAesKey = myouRSA.Encrypt(myRijndael.Key, true);
            tcpConnection.Client.Send(encryptedByRsaAesKey);
            tcpConnection.Close();
            threadMappings.Remove(Thread.CurrentThread.ManagedThreadId);
        }

        private void ConnectOneMemberAsync()
        {
            IPEndPoint currentMemberIp = threadMappings[Thread.CurrentThread.ManagedThreadId];
            Connect(currentMemberIp, inputDeviceNumber, selectedCodec);
        }

        private void Connect(IPEndPoint endPoint, int inputDeviceNumber, INetworkChatCodec codec)
        {
            waveIn = new WaveInEvent();
            waveIn.BufferMilliseconds = 50;
            waveIn.DeviceNumber = inputDeviceNumber;
            if (codec != null)
                waveIn.WaveFormat = codec.RecordFormat;
            else
                waveIn.WaveFormat = new WaveFormat(8000, 16, 1);
            waveIn.DataAvailable += waveIn_DataAvailable;
            waveIn.StartRecording();

            udpSender = new UdpClient();
            udpListener = new UdpClient();

            udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpListener.Client.Bind(new IPEndPoint(IPAddress.Parse(myLocalIp), 7080));

            udpSender.Connect(endPoint);

            waveOut = new WaveOut(WaveCallbackInfo.FunctionCallback());
            waveProvider = new BufferedWaveProvider(waveIn.WaveFormat);
            waveOut.Init(waveProvider);
            waveOut.Play();

            connected = true;
            var state = new ListenerThreadState { Codec = codec, EndPoint = endPoint };
            ThreadPool.QueueUserWorkItem(ListenerThread, state);
        }

        private void Disconnect()
        {
            if (connected)
            {
                connected = false;
                waveIn.DataAvailable -= waveIn_DataAvailable;
                waveIn.StopRecording();
                waveOut.Stop();

                udpSender.Close();
                udpListener.Close();
                waveIn.Dispose();
                waveOut.Dispose();

                if (selectedCodec != null)
                    selectedCodec.Dispose();
            }
        }

        class ListenerThreadState
        {
            public IPEndPoint EndPoint { get; set; }
            public INetworkChatCodec Codec { get; set; }
        }

        private void ListenerThread(object state)
        {
            var listenerThreadState = (ListenerThreadState)state;
            var endPoint = listenerThreadState.EndPoint;
            try
            {
                while (connected)
                {
                    byte[] b = udpListener.Receive(ref endPoint);

                    string roundtrip = DecryptStringFromBytes(b, myRijndael.Key, myRijndael.IV);
                    byte[] decrypted = Encoding.Unicode.GetBytes(roundtrip);

                    if (listenerThreadState.Codec != null)
                    {
                        byte[] decoded = listenerThreadState.Codec.Decode(decrypted, 0, decrypted.Length);
                        waveProvider.AddSamples(decoded, 0, decoded.Length);
                    }
                    else
                        waveProvider.AddSamples(decrypted, 0, decrypted.Length);
                }
            }
            catch (SocketException)
            {
            }
        }

        private byte[] EncryptStringToBytes(string plainText, byte[] Key, byte[] IV)
        {
            if (plainText == null || plainText.Length <= 0)
                throw new ArgumentNullException("plainText");
            if (Key == null || Key.Length <= 0)
                throw new ArgumentNullException("Key");
            if (IV == null || IV.Length <= 0)
                throw new ArgumentNullException("IV");
            byte[] encrypted;


            ICryptoTransform encryptor = myRijndael.CreateEncryptor(myRijndael.Key, myRijndael.IV);

            using (MemoryStream msEncrypt = new MemoryStream())
            {
                using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                {
                    using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                    {
                        swEncrypt.Write(plainText);
                    }
                    encrypted = msEncrypt.ToArray();
                }
            }

            return encrypted;
        }

        private string DecryptStringFromBytes(byte[] cipherText, byte[] Key, byte[] IV)
        {
            if (cipherText == null || cipherText.Length <= 0)
                throw new ArgumentNullException("cipherText");
            if (Key == null || Key.Length <= 0)
                throw new ArgumentNullException("Key");
            if (IV == null || IV.Length <= 0)
                throw new ArgumentNullException("IV");

            string plaintext = null;

            ICryptoTransform decryptor = myRijndael.CreateDecryptor(myRijndael.Key, myRijndael.IV);

            using (MemoryStream msDecrypt = new MemoryStream(cipherText))
            {
                using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                {
                    using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                    {
                        plaintext = srDecrypt.ReadToEnd();
                    }
                }
            }

            return plaintext;
        }

        void waveIn_DataAvailable(object sender, WaveInEventArgs e)
        {
            byte[] encoded;
            if (selectedCodec == null)
            {
                encoded = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, encoded, e.BytesRecorded);
            }
            else
                encoded = selectedCodec.Encode(e.Buffer, 0, e.BytesRecorded);

            string s = Encoding.Unicode.GetString(encoded);
            byte[] encrypted = EncryptStringToBytes(s, myRijndael.Key, myRijndael.IV);

            udpSender.Send(encrypted, encrypted.Count());
        }
    }
}