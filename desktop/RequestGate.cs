namespace BoothShelf;

internal sealed class RequestGate(int concurrency, Func<int, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null)
{
    private readonly object gate = new();
    private readonly Func<int, CancellationToken, Task> wait = delay ?? ((milliseconds, token) => Task.Delay(milliseconds, token));
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private int limit = concurrency > 0 ? concurrency : throw new ArgumentOutOfRangeException(nameof(concurrency));
    private int active;
    private DateTimeOffset resumeAt;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        while (true)
        {
            Task pending;
            int milliseconds;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                milliseconds = (int)Math.Clamp(Math.Ceiling((resumeAt - now()).TotalMilliseconds), 0, int.MaxValue);
                if (milliseconds == 0 && active < limit) { active++; break; }
                pending = changed.Task;
            }
            if (milliseconds > 0) await wait(milliseconds, token);
            else await pending.WaitAsync(token);
        }
        try { return await action(); }
        finally
        {
            lock (gate) { active--; Pulse(); }
        }
    }

    internal void Backoff(int milliseconds)
    {
        lock (gate)
        {
            limit = Math.Max(1, limit / 2);
            var next = now().AddMilliseconds(Math.Max(0, milliseconds));
            if (next > resumeAt) resumeAt = next;
            Pulse();
        }
    }

    private void Pulse()
    {
        var previous = changed;
        changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}
