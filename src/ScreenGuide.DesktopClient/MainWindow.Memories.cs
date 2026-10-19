using System.Windows.Threading;

namespace ScreenGuide.DesktopClient;

public partial class MainWindow
{
    private void OpenAssistantMemories()
    {
        if (_forceClose || _lifetime.IsCancellationRequested) return;
        ShowWindowAndPage(AppPage.Settings);
        // This is navigation only. Existing explicit save/selection/outbound consent
        // remain the only ways to store a memory or send it with a chat Turn.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_forceClose && SettingsPage.IsVisible) LocalMemorySection.BringIntoView();
        }));
    }
}
