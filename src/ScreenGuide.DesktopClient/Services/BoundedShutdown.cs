namespace ScreenGuide.DesktopClient.Services;

/// <summary>Exit must finish even if native model loading or an IPC peer does not acknowledge cleanup.</summary>
public static class BoundedShutdown
{
    public static async Task<bool> TryCompleteAsync(
        Func<CancellationToken, Task> cleanup, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await cleanup(deadline.Token).WaitAsync(deadline.Token);
            return true;
        }
        catch
        {
            // This is only used when the owning process is already committed to exiting.
            return false;
        }
    }
}
