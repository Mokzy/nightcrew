using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;
using Nightcrew.Core;
using Nightcrew.Platform;
using Xunit;

namespace Nightcrew.Tests;

/// <summary>The Windows readers, against windows and sockets these tests open themselves.</summary>
public sealed class WindowReaderTests
{
    private static readonly int Self = Process.GetCurrentProcess().Id;

    /// <summary>Shows a form far off screen on an STA thread, reads the owners, and closes it.</summary>
    private static bool ListedWith(Action<Form> setUp) => Sta.Run(() =>
    {
        using var form = new Form
        {
            Text = "Nightcrew test window",
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-32000, -32000),
        };
        setUp(form);
        form.Show();
        Application.DoEvents();
        try
        {
            return WindowOwners.List().Contains(Self);
        }
        finally
        {
            form.Close();
        }
    });

    [Fact]
    public void A_normal_window_makes_its_process_an_owner() =>
        Assert.True(ListedWith(_ => { }));

    [Fact]
    public void A_window_owned_by_another_window_does_not() =>
        Assert.False(ListedWith(form => form.ShowInTaskbar = false)); // WinForms gives it a hidden owner

    /// <summary>A tool window with no owner and no WS_EX_APPWINDOW, which WinForms adds to anything in the taskbar.</summary>
    private sealed class ToolForm : Form
    {
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle = (parameters.ExStyle & ~0x40000) | 0x80; // drop WS_EX_APPWINDOW, add WS_EX_TOOLWINDOW
                return parameters;
            }
        }
    }

    [Fact]
    public void A_tool_window_does_not() => Assert.False(Sta.Run(() =>
    {
        using var form = new ToolForm { Text = "Nightcrew tool window", StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-32000, -32000) };
        form.Show();
        Application.DoEvents();
        try
        {
            return WindowOwners.List().Contains(Self);
        }
        finally
        {
            form.Close();
        }
    }));

    [Fact]
    public void A_window_without_a_title_does_not() =>
        Assert.False(ListedWith(form => form.Text = ""));

    [Fact]
    public void A_listener_and_a_loopback_connection_to_it_are_both_read()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(AddressFamily.InterNetwork);
            client.Connect(IPAddress.Loopback, port);

            var rows = LoopbackConnections.List().Where(row => row.OwnerPid == Self).ToList();

            Assert.Contains(rows, row => row.State == TcpState.Listen && row.LocalPort == port);
            Assert.Contains(rows, row => row.State == TcpState.Established && row.RemotePort == port && row.RemoteIsLoopback);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void An_ipv6_loopback_connection_is_read_as_loopback()
    {
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new TcpClient(AddressFamily.InterNetworkV6);
            client.Connect(IPAddress.IPv6Loopback, port);

            var rows = LoopbackConnections.List().Where(row => row.OwnerPid == Self).ToList();

            Assert.Contains(rows, row => row.State == TcpState.Established && row.RemotePort == port && row.RemoteIsLoopback);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class CountingTable : IProcessTable
    {
        public int Lists { get; private set; }

        public IReadOnlyList<ProcessEntry> List()
        {
            Lists++;
            return [new ProcessEntry(1, 0, "x")];
        }

        public bool TryGetTimes(int pid, out DateTime startUtc, out TimeSpan cpu)
        {
            startUtc = default;
            cpu = default;
            return false;
        }
    }

    [Fact]
    public void The_process_list_is_read_once_per_tick()
    {
        var inner = new CountingTable();
        var clock = new FakeClock();
        var table = new TickProcessTable(inner, clock);

        table.List();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        table.List();
        Assert.Equal(1, inner.Lists);

        clock.Advance(TimeSpan.FromSeconds(10));
        table.List();
        Assert.Equal(2, inner.Lists);
    }
}
