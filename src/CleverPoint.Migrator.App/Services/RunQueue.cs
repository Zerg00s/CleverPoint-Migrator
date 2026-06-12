namespace CleverPoint.Migrator.App.Services;

/// <summary>
/// Caps concurrent migrations at the configured maximum; anything beyond
/// queues and waits its turn (the UI shows "Queued..." meanwhile). Parallel
/// runs already share one request budget per tenant via RequestThrottle, so
/// concurrency never multiplies SharePoint load.
/// </summary>
public static class RunQueue
{
    private static readonly object Gate = new();
    private static readonly Queue<TaskCompletionSource> Waiters = new();
    private static int _running;
    private static int _limit = 2;

    public static void Configure(int maxParallel) => _limit = Math.Clamp(maxParallel, 1, 3);

    public static int RunningCount { get { lock (Gate) return _running; } }
    public static int QueuedCount { get { lock (Gate) return Waiters.Count; } }

    public static async Task<IDisposable> EnterAsync()
    {
        TaskCompletionSource? waiter = null;
        lock (Gate)
        {
            if (_running < _limit) _running++;
            else
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Waiters.Enqueue(waiter);
            }
        }
        if (waiter != null) await waiter.Task;
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                if (Waiters.Count > 0) Waiters.Dequeue().SetResult();
                else _running--;
            }
        }
    }
}
