using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Windows' TCP tables, IPv4 and IPv6, with the owning process of each row.</summary>
public static class LoopbackConnections
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint MIB_TCP_STATE_LISTEN = 2;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const int Row4Size = 24;  // state, local address, local port, remote address, remote port, pid
    private const int Row6Size = 56;  // local address[16], scope, port, remote address[16], scope, port, state, pid

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);

    /// <summary>Every row of both tables; empty for a table Windows won't give.</summary>
    public static IReadOnlyList<TcpRow> List()
    {
        var rows = new List<TcpRow>();
        Read(AF_INET, Row4Size, (buffer, at) => rows.Add(new TcpRow(
            OwnerPid: Marshal.ReadInt32(buffer, at + 20),
            LocalPort: Port(Marshal.ReadInt32(buffer, at + 8)),
            RemotePort: Port(Marshal.ReadInt32(buffer, at + 16)),
            RemoteIsLoopback: (Marshal.ReadInt32(buffer, at + 12) & 0xFF) == 127,
            State: StateOf((uint)Marshal.ReadInt32(buffer, at)))));
        Read(AF_INET6, Row6Size, (buffer, at) => rows.Add(new TcpRow(
            OwnerPid: Marshal.ReadInt32(buffer, at + 52),
            LocalPort: Port(Marshal.ReadInt32(buffer, at + 20)),
            RemotePort: Port(Marshal.ReadInt32(buffer, at + 44)),
            RemoteIsLoopback: IsLoopback6(buffer, at + 24),
            State: StateOf((uint)Marshal.ReadInt32(buffer, at + 48)))));
        return rows;
    }

    private static void Read(int family, int rowSize, Action<IntPtr, int> row)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_ALL, 0);
                if (status == ERROR_INSUFFICIENT_BUFFER) continue; // the table grew; size now holds the new length
                if (status != 0) return;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++) row(buffer, 4 + i * rowSize);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>The port is in network byte order in the low 16 bits.</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static TcpState StateOf(uint state) => state switch
    {
        MIB_TCP_STATE_LISTEN => TcpState.Listen,
        MIB_TCP_STATE_ESTAB => TcpState.Established,
        _ => TcpState.Other,
    };

    /// <summary>::1, or an IPv4-mapped 127.x.x.x.</summary>
    private static bool IsLoopback6(IntPtr buffer, int at)
    {
        var bytes = new byte[16];
        Marshal.Copy(buffer + at, bytes, 0, 16);
        var zeroTo9 = bytes.Take(10).All(b => b == 0);
        if (zeroTo9 && bytes[10] == 0 && bytes[11] == 0 && bytes.Skip(12).Take(3).All(b => b == 0) && bytes[15] == 1) return true;
        return zeroTo9 && bytes[10] == 0xFF && bytes[11] == 0xFF && bytes[12] == 127;
    }
}
