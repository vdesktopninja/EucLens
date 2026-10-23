using System.Security.Principal;

namespace EucLens.Core;

/// <summary>
/// One EUC Lens per Windows user and session. The names live in the session-local kernel namespace (<c>Local\</c>), so another user, or
/// the same user in another Remote Desktop / VDI session on the same machine, is never blocked. A second start signals the first
/// instance to show its window and ends; the first instance keeps the only sampler. No elevation is needed.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex lockHandle;
    private readonly EventWaitHandle activation;
    private readonly Thread listener;
    private volatile bool disposed;

    private SingleInstance(Mutex lockHandle, EventWaitHandle activation)
    {
        this.lockHandle = lockHandle; this.activation = activation;
        listener = new Thread(Listen) { IsBackground = true, Name = "EucLens single-instance listener" };
        listener.Start();
    }

    /// <summary>Raised on a background thread when another start asked this instance to show itself.</summary>
    public event Action? ActivationRequested;

    public static string DefaultName()
    {
        var id = WindowsIdentity.GetCurrent().User?.Value;
        return $@"Local\EucLens-{id ?? Environment.UserName}";
    }

    /// <summary>
    /// Returns the guard when this is the first instance. Returns null when another instance owns the name; that instance has then been
    /// asked to show its window (<paramref name="signalled"/> says whether the request reached it).
    /// </summary>
    public static SingleInstance? TryAcquire(string name, out bool signalled)
    {
        signalled = false;
        var mutex = new Mutex(true, name + ".lock", out var created);
        if (created)
        {
            try { return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, name + ".activate")); }
            catch { mutex.Dispose(); throw; }
        }
        mutex.Dispose();
        // The first instance creates its event right after the lock; give it a moment when both start together.
        for (var attempt = 0; attempt < 20 && !signalled; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(name + ".activate", out var existing)) { using (existing) signalled = existing.Set(); }
            else Thread.Sleep(50);
        }
        return null;
    }

    private void Listen()
    {
        while (!disposed)
        {
            try { if (activation.WaitOne(250) && !disposed) ActivationRequested?.Invoke(); }
            catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { lockHandle.ReleaseMutex(); } catch (ApplicationException) { /* released by another thread: the OS frees it at process exit */ }
        lockHandle.Dispose();
        activation.Dispose();
    }
}
