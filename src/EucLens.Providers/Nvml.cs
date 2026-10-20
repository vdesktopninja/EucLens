using System.Runtime.InteropServices;
using System.Text;

namespace EucLens.Providers;

/// <summary>Result of one NVML query: a value, or the NVML return code that explains its absence.</summary>
public readonly record struct NvmlValue(double? Value, int Code = 0, string? Error = null)
{
    public static NvmlValue Ok(double value) => new(value);
}

public sealed record NvmlDeviceSample(int Index, string Name, uint PciVendorId, uint PciDeviceId, uint PciSubSystemId,
    string BusId, string VirtualizationMode, NvmlValue GpuUtilization, NvmlValue MemoryControllerUtilization,
    NvmlValue MemoryUsedBytes, NvmlValue MemoryTotalBytes, NvmlValue EncoderUtilization, NvmlValue DecoderUtilization);

public interface INvmlClient : IDisposable
{
    /// <summary>True when nvml.dll was found and nvmlInit_v2 succeeded.</summary>
    bool IsAvailable { get; }
    string Status { get; }
    string? DriverVersion { get; }
    IReadOnlyList<NvmlDeviceSample> Sample();
}

/// <summary>
/// Optional NVIDIA Management Library backend. nvml.dll ships with the NVIDIA driver and is never bundled.
/// It is only loaded from System32 or the NVSMI directory (absolute paths), never from the search path.
/// </summary>
public sealed unsafe class NvmlClient : INvmlClient
{
    private const int Success = 0;
    private nint library;
    private nint[] devices = [];
    private bool initialized;
    private delegate* unmanaged<int> shutdown;
    private delegate* unmanaged<int, nint> errorString;
    private delegate* unmanaged<nint, byte*, uint, int> getName;
    private delegate* unmanaged<nint, PciInfo*, int> getPci;
    private delegate* unmanaged<nint, uint*, int> getUtilization;
    private delegate* unmanaged<nint, ulong*, int> getMemory;
    private delegate* unmanaged<nint, uint*, uint*, int> getEncoder, getDecoder;
    private delegate* unmanaged<nint, int*, int> getVirtualization;

    public bool IsAvailable => initialized;
    public string Status { get; private set; } = "Not initialized.";
    public string? DriverVersion { get; private set; }

    public NvmlClient() : this(DefaultPaths()) { }
    public NvmlClient(IEnumerable<string> candidatePaths)
    {
        if (!OperatingSystem.IsWindows()) { Status = "NVML requires Windows."; return; }
        var path = candidatePaths.FirstOrDefault(File.Exists);
        if (path is null) { Status = "nvml.dll not found (no NVIDIA driver with NVML in System32 or NVSMI)."; return; }
        if (!NativeLibrary.TryLoad(path, out library)) { Status = $"nvml.dll could not be loaded from {path}."; return; }
        try { Initialize(path); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException)
        { Status = $"NVML is incompatible: {ex.Message}"; Unload(); }
    }

    private static IEnumerable<string> DefaultPaths() =>
    [
        Path.Combine(Environment.SystemDirectory, "nvml.dll"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
    ];

    private nint Export(string name) => NativeLibrary.GetExport(library, name);
    private nint Optional(string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;

    private void Initialize(string path)
    {
        var init = Export("nvmlInit_v2");
        shutdown = (delegate* unmanaged<int>)Export("nvmlShutdown");
        errorString = (delegate* unmanaged<int, nint>)Optional("nvmlErrorString");
        var code = ((delegate* unmanaged<int>)init)();
        if (code != Success) { Status = $"nvmlInit_v2 failed: {Error(code)}"; Unload(); return; }
        initialized = true;
        getName = (delegate* unmanaged<nint, byte*, uint, int>)Export("nvmlDeviceGetName");
        getPci = (delegate* unmanaged<nint, PciInfo*, int>)Optional("nvmlDeviceGetPciInfo_v3");
        getUtilization = (delegate* unmanaged<nint, uint*, int>)Optional("nvmlDeviceGetUtilizationRates");
        getMemory = (delegate* unmanaged<nint, ulong*, int>)Optional("nvmlDeviceGetMemoryInfo");
        getEncoder = (delegate* unmanaged<nint, uint*, uint*, int>)Optional("nvmlDeviceGetEncoderUtilization");
        getDecoder = (delegate* unmanaged<nint, uint*, uint*, int>)Optional("nvmlDeviceGetDecoderUtilization");
        getVirtualization = (delegate* unmanaged<nint, int*, int>)Optional("nvmlDeviceGetVirtualizationMode");
        var driver = stackalloc byte[96];
        var getDriver = (delegate* unmanaged<byte*, uint, int>)Optional("nvmlSystemGetDriverVersion");
        if (getDriver != null && getDriver(driver, 96) == Success) DriverVersion = Utf8(driver, 96);
        uint count = 0;
        var getCount = (delegate* unmanaged<uint*, int>)Export("nvmlDeviceGetCount_v2");
        var getHandle = (delegate* unmanaged<uint, nint*, int>)Export("nvmlDeviceGetHandleByIndex_v2");
        code = getCount(&count);
        if (code != Success) { Status = $"nvmlDeviceGetCount_v2 failed: {Error(code)}"; return; }
        var handles = new List<nint>();
        for (uint i = 0; i < count; i++)
        {
            nint handle = 0;
            if (getHandle(i, &handle) == Success) handles.Add(handle);
        }
        devices = handles.ToArray();
        Status = $"NVML loaded from {path}; {devices.Length} device(s); driver {DriverVersion ?? "unknown"}.";
    }

    public IReadOnlyList<NvmlDeviceSample> Sample()
    {
        if (!initialized) return [];
        var result = new NvmlDeviceSample[devices.Length];
        var name = stackalloc byte[96];
        var rates = stackalloc uint[2];
        var memory = stackalloc ulong[3]; // total, free, used
        for (var i = 0; i < devices.Length; i++)
        {
            var device = devices[i];
            var deviceName = getName(device, name, 96) == Success ? Utf8(name, 96) : "NVIDIA device";
            PciInfo pci = default;
            var hasPci = getPci != null && getPci(device, &pci) == Success;
            var mode = "unknown";
            if (getVirtualization != null)
            {
                int value;
                var code = getVirtualization(device, &value);
                mode = code != Success ? $"unknown ({Error(code)})" : value switch
                {
                    0 => "none (bare metal)", 1 => "passthrough", 2 => "vGPU guest", 3 => "vGPU host", 4 => "vSGA host",
                    _ => $"code {value}"
                };
            }
            NvmlValue utilization = Missing(getUtilization), memoryController = Missing(getUtilization);
            if (getUtilization != null)
            {
                var code = getUtilization(device, rates);
                utilization = code == Success ? NvmlValue.Ok(rates[0]) : Fail(code);
                memoryController = code == Success ? NvmlValue.Ok(rates[1]) : Fail(code);
            }
            NvmlValue used = Missing(getMemory), total = Missing(getMemory);
            if (getMemory != null)
            {
                var code = getMemory(device, memory);
                total = code == Success ? NvmlValue.Ok(memory[0]) : Fail(code);
                used = code == Success ? NvmlValue.Ok(memory[2]) : Fail(code);
            }
            result[i] = new(i, deviceName, hasPci ? pci.PciDeviceId & 0xFFFF : 0, hasPci ? pci.PciDeviceId >> 16 : 0,
                hasPci ? pci.PciSubSystemId : 0, hasPci ? Utf8(pci.BusId, 32) : "", mode,
                utilization, memoryController, used, total, Codec(getEncoder, device), Codec(getDecoder, device));
        }
        return result;
    }

    private NvmlValue Codec(delegate* unmanaged<nint, uint*, uint*, int> query, nint device)
    {
        if (query == null) return Missing(query);
        uint value, period;
        var code = query(device, &value, &period);
        return code == Success ? NvmlValue.Ok(value) : Fail(code);
    }

    private static NvmlValue Missing(void* function) => function == null ? new(null, 13, "Function not exported by this NVML version.") : default;
    private NvmlValue Fail(int code) => new(null, code, Error(code));
    private string Error(int code)
    {
        if (errorString != null && errorString(code) is var text && text != 0) return $"{Marshal.PtrToStringUTF8(text)} (NVML {code})";
        return code switch { 3 => "Not supported (NVML 3)", 4 => "No permission (NVML 4)", _ => $"NVML error {code}" };
    }
    private static string Utf8(byte* buffer, int length)
    {
        var span = new ReadOnlySpan<byte>(buffer, length);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    private void Unload()
    {
        if (library != 0) { NativeLibrary.Free(library); library = 0; }
        initialized = false;
        devices = [];
    }

    public void Dispose()
    {
        if (initialized && shutdown != null) shutdown();
        Unload();
    }

    /// <summary>nvmlPciInfo_t as used by nvmlDeviceGetPciInfo_v3.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        public fixed byte BusId[32];
    }
}
