using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace EucLens.Providers;

public sealed class WindowsBlastSessionContext : IBlastSessionContext
{
    public BlastSessionContext GetCurrent()
    {
        if (!OperatingSystem.IsWindows()) return new(-1, false, false, "Real monitoring requires Windows.");
        return GetWindowsContext();
    }

    [SupportedOSPlatform("windows")]
    private static BlastSessionContext GetWindowsContext()
    {
        using var process = Process.GetCurrentProcess();
        var session = process.SessionId;
        // Check both vendor generations, both registry views, and the Agent service.
        var installed = false;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var path in new[] { @"SOFTWARE\Omnissa\Horizon\Agent", @"SOFTWARE\Omnissa\Horizon Agent",
                         @"SOFTWARE\VMware, Inc.\VMware VDM\Agent", @"SYSTEM\CurrentControlSet\Services\wsnm" })
            {
                using var key = root.OpenSubKey(path);
                installed |= key is not null;
            }
        }
        if (!installed) return new(session, false, false, "Horizon Agent installation was not detected (registry/service registration).");
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8 /* WTSConnectState */, out var buffer, out var bytes))
            return new(session, true, false, $"Unable to determine current session state (Windows error {Marshal.GetLastWin32Error()}).");
        try
        {
            var active = bytes >= sizeof(int) && Marshal.ReadInt32(buffer) == 0 /* WTSActive */;
            return new(session, true, active, active ? $"Windows session {session} is active." : $"Windows session {session} is disconnected or inactive.");
        }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);
    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
