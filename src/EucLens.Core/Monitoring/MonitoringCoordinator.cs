namespace EucLens.Core;

public sealed record MonitoringTransition(MonitoringState From, MonitoringState To, DateTimeOffset At);

/// <summary>
/// The only owner of the sampling schedule and of the monitoring state machine. One instance exists per process; its single
/// loop never looks at a window, so hiding, minimizing or restoring the window cannot start, stop or duplicate sampling.
/// <para>
/// Stopped → Running (Start); Running → Paused (Pause); Paused → Running (Resume); Running or Paused → Stopped (Stop).
/// Any other request is refused and returns false. While not Running the loop is suspended without timer wake-ups.
/// </para>
/// </summary>
public sealed class MonitoringCoordinator
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan interval;
    private MonitoringState state;
    private long generation;
    private TaskCompletionSource running = NewSignal();
    private int loops;

    /// <param name="startRunning">The existing behaviour: sampling begins as soon as the loop is started.</param>
    public MonitoringCoordinator(TimeProvider? clock = null, TimeSpan? interval = null, bool startRunning = true)
    {
        this.clock = clock ?? TimeProvider.System;
        this.interval = interval ?? DefaultInterval;
        if (startRunning) { state = MonitoringState.Running; running.TrySetResult(); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MonitoringState State { get { lock (gate) return state; } }
    /// <summary>Raised synchronously on the thread that changed the state, after the change is visible.</summary>
    public event Action<MonitoringTransition>? Transitioned;
    /// <summary>Number of completed collection ticks since the loop started.</summary>
    public long Ticks => Interlocked.Read(ref ticks);
    private long ticks;

    public bool Start() => Move(MonitoringState.Running, MonitoringState.Stopped);
    public bool Pause() => Move(MonitoringState.Paused, MonitoringState.Running);
    public bool Resume() => Move(MonitoringState.Running, MonitoringState.Paused);
    public bool Stop() => Move(MonitoringState.Stopped, MonitoringState.Running, MonitoringState.Paused);

    private bool Move(MonitoringState to, params MonitoringState[] allowedFrom)
    {
        MonitoringTransition transition;
        lock (gate)
        {
            if (!allowedFrom.Contains(state)) return false;
            transition = new(state, to, clock.GetUtcNow());
            state = to;
            generation++;
            if (to == MonitoringState.Running) running.TrySetResult();
            else if (running.Task.IsCompleted) running = NewSignal();
        }
        Transitioned?.Invoke(transition);
        return true;
    }

    /// <summary>
    /// Runs the loop until cancelled. <paramref name="tick"/> performs one collection; it must handle its own failures.
    /// Calling this twice concurrently is a programming error: there is exactly one loop.
    /// </summary>
    public async Task RunAsync(Func<CancellationToken, Task> tick, CancellationToken token)
    {
        if (Interlocked.Exchange(ref loops, 1) == 1) throw new InvalidOperationException("The monitoring loop is already running.");
        PeriodicTimer? timer = null;
        try
        {
            long timerGeneration = -1;
            while (!token.IsCancellationRequested)
            {
                Task signal;
                lock (gate) signal = running.Task;
                await signal.WaitAsync(token).ConfigureAwait(true);
                long current;
                lock (gate) current = generation;
                // A resume restarts the cadence so the first sample after a pause is not followed by a second one at once.
                if (timer is null || timerGeneration != current) { timer?.Dispose(); timer = new PeriodicTimer(interval, clock); timerGeneration = current; }
                if (State != MonitoringState.Running) continue;
                await tick(token).ConfigureAwait(true);
                Interlocked.Increment(ref ticks);
                if (State != MonitoringState.Running) continue;
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(true)) break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { timer?.Dispose(); Interlocked.Exchange(ref loops, 0); }
    }
}
