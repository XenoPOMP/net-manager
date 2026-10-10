using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using NetManager.Core.Localization;
using NetManager.Core.Models;
using NetManager.Core.Services;
using static NetManager.Windows.NativeMethods;

namespace NetManager.Windows;

/// <summary>Read / create / delete IPv4 routes through the IP Helper API.</summary>
internal static class IpForwardTable
{
    internal readonly record struct Row(
        int InterfaceIndex, string Destination, int PrefixLength, string? NextHop,
        int Metric, int Protocol, int Origin, bool Loopback);

    public static List<Row> Read()
    {
        int rc = GetIpForwardTable2(AF_INET, out var table);
        if (rc != 0) throw new NetworkOperationException(Loc.T("err.routeTable", new Win32Exception(rc).Message));
        try
        {
            int count = Marshal.ReadInt32(table);
            var rows = new List<Row>(count);
            for (int i = 0; i < count; i++)
            {
                var r = table + TableHeaderSize + i * RowSize;
                string? nextHop = ReadAddress(r + OffNextHop);
                if (nextHop == "0.0.0.0") nextHop = null;
                rows.Add(new Row(
                    Marshal.ReadInt32(r + OffIfIndex),
                    ReadAddress(r + OffDest) ?? "0.0.0.0",
                    Marshal.ReadByte(r + OffPrefixLen),
                    nextHop,
                    Marshal.ReadInt32(r + OffMetric),
                    Marshal.ReadInt32(r + OffProtocol),
                    Marshal.ReadInt32(r + OffOrigin),
                    Marshal.ReadByte(r + OffLoopback) != 0));
            }
            return rows;
        }
        finally { FreeMibTable(table); }
    }

    public static void Create(int ifIndex, string destination, int prefix, string? nextHop, int metric)
    {
        var row = Marshal.AllocHGlobal(RowSize);
        try
        {
            InitializeIpForwardEntry(row);
            Marshal.WriteInt32(row + OffIfIndex, ifIndex);
            WriteAddress(row + OffDest, destination);
            Marshal.WriteByte(row + OffPrefixLen, (byte)prefix);
            WriteAddress(row + OffNextHop, nextHop ?? "0.0.0.0");
            Marshal.WriteInt32(row + OffMetric, metric);
            Marshal.WriteInt32(row + OffProtocol, 3); // MIB_IPPROTO_NETMGMT, same as `route add`
            Check(CreateIpForwardEntry2(row), "err.createRoute");
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    public static void Delete(int ifIndex, string destination, int prefix, string? nextHop)
    {
        var row = Marshal.AllocHGlobal(RowSize);
        try
        {
            InitializeIpForwardEntry(row);
            Marshal.WriteInt32(row + OffIfIndex, ifIndex);
            WriteAddress(row + OffDest, destination);
            Marshal.WriteByte(row + OffPrefixLen, (byte)prefix);
            WriteAddress(row + OffNextHop, nextHop ?? "0.0.0.0");
            Check(DeleteIpForwardEntry2(row), "err.deleteRoute");
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    private static void Check(int rc, string messageKey)
    {
        if (rc == 0) return;
        // 5010 = ERROR_OBJECT_ALREADY_EXISTS: callers (profile activation) treat "the route is already there" as success.
        throw new NetworkOperationException(Loc.T(messageKey, new Win32Exception(rc).Message, rc),
            kind: rc == 5010 ? NetworkErrorKind.AlreadyExists : NetworkErrorKind.General);
    }

    private static string? ReadAddress(IntPtr sockaddrInet)
    {
        if (Marshal.ReadInt16(sockaddrInet) != AF_INET) return null;
        var b = new byte[4];
        Marshal.Copy(sockaddrInet + 4, b, 0, 4);
        return new IPAddress(b).ToString();
    }

    private static void WriteAddress(IntPtr sockaddrInet, string ip)
    {
        // Clear the whole 28-byte SOCKADDR_INET, then set family + address.
        for (int i = 0; i < 28; i += 4) Marshal.WriteInt32(sockaddrInet + i, 0);
        Marshal.WriteInt16(sockaddrInet, (short)AF_INET);
        Marshal.Copy(IPAddress.Parse(ip).GetAddressBytes(), 0, sockaddrInet + 4, 4);
    }
}
