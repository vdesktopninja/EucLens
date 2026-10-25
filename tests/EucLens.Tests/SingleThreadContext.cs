using System.Collections.Concurrent;

namespace EucLens.Tests;

/// <summary>
/// A dedicated single-threaded synchronization context standing in for the WPF dispatcher: everything a test body does, and every
/// continuation it awaits, runs on one thread, one callback at a time. Background work (Task.Run) still runs on the thread pool, exactly
/// as in the application, and posts its results back here.
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
    private readonly Thread thread;
    private volatile bool closed;

    private SingleThreadContext()
    {
        thread = new Thread(Pump) { IsBackground = true, Name = "test-ui" };
    }

    /// <summary>Runs the body on a fresh UI-like thread. Returns when the body has finished, its queue is drained and the thread has exited.</summary>
    public static async Task RunAsync(Func<Task> body)
    {
        var context = new SingleThreadContext();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.thread.Start();
        context.Post(_ =>
        {
            Task task;
            try { task = body(); }
            catch (Exception ex) { task = Task.FromException(ex); }
            // The completion check is itself posted, so the body's own continuations have all run before the context closes.
            task.ContinueWith(t => context.Post(_ => context.Close(t, finished), null), TaskScheduler.Default);
        }, null);
        await finished.Task;
        if (!context.thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test UI thread did not exit.");
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (closed) return;
        try { queue.Add((d, state)); } catch (InvalidOperationException) { /* completed while posting */ }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == thread.ManagedThreadId) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ => { try { d(state); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, null);
        done.Wait();
        if (error is not null) throw error;
    }

    private void Close(Task body, TaskCompletionSource finished)
    {
        // Callbacks that were already queued still run before the thread leaves; nothing is posted after this point.
        closed = true;
        while (queue.TryTake(out var item)) item.Callback(item.State);
        queue.CompleteAdding();
        if (body.IsFaulted) finished.TrySetException(body.Exception!.InnerExceptions);
        else if (body.IsCanceled) finished.TrySetCanceled();
        else finished.TrySetResult();
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
    }
}
