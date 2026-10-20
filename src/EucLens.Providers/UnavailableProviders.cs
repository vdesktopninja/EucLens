using EucLens.Core;

namespace EucLens.Providers;

public abstract class UnavailableProvider(string name) : IMetricsProvider
{
    private bool disposed;
    public string Name => name;
    public Task<ProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.FromResult(new ProviderAvailability(false, "Unavailable: integration requires research and is not implemented in this milestone."));
    }
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.FromException(new NotSupportedException($"{Name}: integration unavailable."));
    }
    public Task<SessionSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        return Task.FromException<SessionSnapshot>(new NotSupportedException($"{Name}: no metrics are available."));
    }
    public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
}
public sealed class CitrixHdxProvider() : UnavailableProvider("Citrix HDX");
public sealed class MicrosoftRdpProvider() : UnavailableProvider("Microsoft RDP");
public sealed class NvidiaGpuProvider() : UnavailableProvider("NVIDIA NVML");
