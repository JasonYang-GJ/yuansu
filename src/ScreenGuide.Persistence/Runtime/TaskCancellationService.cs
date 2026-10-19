using ScreenGuide.Core.Tasking;

namespace ScreenGuide.Persistence.Runtime;

public sealed class TaskCancellationService(
    ILocalTaskStore store,
    TaskCancellationRegistry registry)
{
    public async Task<bool> RequestAsync(
        Guid taskId,
        Guid sourceDeviceId,
        Guid? commandId = null,
        CancellationToken cancellationToken = default,
        long? expectedVersion = null)
    {
        var accepted = await store.RequestCancellationAsync(
            taskId,
            sourceDeviceId,
            commandId,
            cancellationToken,
            expectedVersion).ConfigureAwait(false);
        if (accepted)
        {
            registry.RequestCancellation(taskId);
        }

        return accepted;
    }
}
