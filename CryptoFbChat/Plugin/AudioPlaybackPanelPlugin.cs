using System.ComponentModel.Composition;
using System.Windows.Forms;

namespace NAudioDemo.AudioPlaybackDemo
{
    [Export(typeof(INAudioDemoPlugin))]
    public class AudioPlaybackPanelPlugin : INAudioDemoPlugin
    {
        public string Name
        {
            get { return "Audio File Playback"; }
        }
        
        [Import]
        public ExportFactory<AudioPlaybackPanel> PanelFactory { get; set; }

        public Control CreatePanel()
        {
            return PanelFactory.CreateExport().Value; //new AudioPlaybackPanel();
        }
    }
}
