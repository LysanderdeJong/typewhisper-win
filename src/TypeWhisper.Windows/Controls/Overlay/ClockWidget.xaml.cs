using System.Windows.Controls;
using System.Windows.Threading;

namespace TypeWhisper.Windows.Controls.Overlay;

public partial class ClockWidget : UserControl, IDisposable
{
    private readonly DispatcherTimer _timer;

    public ClockWidget()
    {
        InitializeComponent();
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm");
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();
}
