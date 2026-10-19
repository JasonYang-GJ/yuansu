using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using ScreenGuide.DesktopProtocol;

namespace ScreenGuide.DesktopClient.Services;

public sealed class DesktopClientSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _activationPipe;
    private Task? _listener;
    private Action<bool>? _activationHandler;

    private DesktopClientSingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _activationPipe = ResolveActivationPipeName();
        _listener = ListenAsync(_stopping.Token);
    }

    public static DesktopClientSingleInstance? TryAcquire()
    {
        var mutex = new Mutex(
            initiallyOwned: false,
            ResolveMutexName());
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
        }

        return new DesktopClientSingleInstance(mutex);
    }

    public void SetActivationHandler(Action<bool> handler) => _activationHandler = handler;
    public void SetActivationHandler(Action handler) => _activationHandler = _ => handler();

    public static async Task<bool> TrySignalExistingAsync(
        CancellationToken cancellationToken = default, bool showManagement = false)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                ResolveActivationPipeName(),
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1000, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(new byte[] { showManagement ? (byte)2 : (byte)1 }, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _mutex.Dispose();
        _stopping.Dispose();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    _activationPipe,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var signal = new byte[1];
                if (await pipe.ReadAsync(signal, cancellationToken).ConfigureAwait(false) > 0)
                {
                    if (signal[0] is 1 or 2) _activationHandler?.Invoke(signal[0] == 2);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }

    private static string ResolveActivationPipeName() =>
        ResolveHostPipeName() + ".Activate";

    private static string ResolveMutexName()
    {
        var customPipeName = Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME");
        if (string.IsNullOrWhiteSpace(customPipeName))
        {
            return DesktopIpcEndpoint.CurrentUserMutexName("DesktopClient");
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(customPipeName.Trim())))[..16];
        return $"{DesktopIpcEndpoint.CurrentUserMutexName("DesktopClient")}.{hash}";
    }

    private static string ResolveHostPipeName()
    {
        var customPipeName = Environment.GetEnvironmentVariable("SCREEN_GUIDE_PIPE_NAME");
        return string.IsNullOrWhiteSpace(customPipeName)
            ? DesktopIpcEndpoint.CurrentUserPipeName()
            : customPipeName.Trim();
    }
}
