using System.Runtime.InteropServices;
using System.Text;

namespace SmartPrint.Agent;

/// <summary>
/// Reads the currently connected Wi-Fi SSID using the Windows Native Wi-Fi API.
/// Returns null when the WLAN service is unavailable or the adapter is not associated.
/// </summary>
internal static class WindowsWifiNetworkName
{
    private const uint ClientVersion = 2;
    private const int CurrentConnectionOpcode = 7;
    private const int ConnectionAttributesSsidOffset = 520;
    private const int Dot11SsidBytesOffset = 524;
    private const int MaximumSsidBytes = 32;

    [DllImport("wlanapi.dll", EntryPoint = "WlanOpenHandle")]
    private static extern uint WlanOpenHandle(
        uint clientVersion,
        IntPtr reserved,
        out uint negotiatedVersion,
        out IntPtr clientHandle);

    [DllImport("wlanapi.dll", EntryPoint = "WlanQueryInterface")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opcode,
        IntPtr reserved,
        out uint dataSize,
        out IntPtr data,
        out int valueType);

    [DllImport("wlanapi.dll", EntryPoint = "WlanFreeMemory")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll", EntryPoint = "WlanCloseHandle")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    public static string? TryGetCurrentSsid(string networkInterfaceId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(networkInterfaceId, out var interfaceGuid))
            return null;

        IntPtr clientHandle = IntPtr.Zero;
        IntPtr data = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out clientHandle) != 0 ||
                clientHandle == IntPtr.Zero)
                return null;

            if (WlanQueryInterface(
                    clientHandle,
                    ref interfaceGuid,
                    CurrentConnectionOpcode,
                    IntPtr.Zero,
                    out var dataSize,
                    out data,
                    out _) != 0 ||
                data == IntPtr.Zero ||
                dataSize < Dot11SsidBytesOffset)
                return null;

            var ssidLength = Marshal.ReadInt32(data, ConnectionAttributesSsidOffset);
            if (ssidLength is < 1 or > MaximumSsidBytes ||
                dataSize < (uint)(Dot11SsidBytesOffset + ssidLength))
                return null;

            var bytes = new byte[ssidLength];
            Marshal.Copy(IntPtr.Add(data, Dot11SsidBytesOffset), bytes, 0, ssidLength);
            var ssid = Encoding.UTF8.GetString(bytes).Trim();
            return string.IsNullOrWhiteSpace(ssid) || ssid.Any(char.IsControl) ? null : ssid;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
            if (clientHandle != IntPtr.Zero) _ = WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }
}
