using System.Runtime.InteropServices;

namespace NetManager.Windows;

/// <summary>
/// IP Helper API (iphlpapi.dll). MIB_IPFORWARD_ROW2 is accessed through raw offsets so the layout is explicit:
/// <code>
///   0 InterfaceLuid (8)   8 InterfaceIndex (4)
///  12 DestinationPrefix.Prefix SOCKADDR_INET (28)   40 PrefixLength (1)
///  44 NextHop SOCKADDR_INET (28)   72 SitePrefixLength (1)
///  76 ValidLifetime   80 PreferredLifetime   84 Metric   88 Protocol (NL_ROUTE_PROTOCOL)
///  92 Loopback  93 AutoconfigureAddress  94 Publish  95 Immortal   96 Age   100 Origin (NL_ROUTE_ORIGIN)
/// </code>
/// Total size 104 bytes. In a SOCKADDR_INET holding an IPv4 address the family is at +0 and the 4 address bytes at +4.
/// </summary>
internal static class NativeMethods
{
    internal const ushort AF_INET = 2;
    internal const int RowSize = 104;
    internal const int TableHeaderSize = 8; // ULONG NumEntries + padding to 8-byte alignment

    internal const int OffIfIndex = 8, OffDest = 12, OffPrefixLen = 40, OffNextHop = 44,
        OffMetric = 84, OffProtocol = 88, OffLoopback = 92, OffOrigin = 100;

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern int GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern void InitializeIpForwardEntry(IntPtr row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern int CreateIpForwardEntry2(IntPtr row);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    internal static extern int DeleteIpForwardEntry2(IntPtr row);
}
