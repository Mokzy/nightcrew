using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class EfficiencyPlannerTests
{
    private const int Session = 1;
    private static readonly DateTime T0 = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private readonly List<ProcessInfo> _processes = [];
    private readonly HashSet<int> _windows = [];
    private readonly HashSet<int> _holders = [];
    private readonly List<TcpRow> _tcp = [];
    private readonly Settings _settings = new();

    public EfficiencyPlannerTests()
    {
        Add(4, 0, "system", 0, session: 0);
        Add(2, 1, "explorer", 1);
    }

    /// <summary>A process that started <paramref name="minute"/> minutes after T0.</summary>
    private ProcessInfo Add(int pid, int parent, string name, int minute, int session = Session)
    {
        var process = new ProcessInfo(pid, parent, name, T0.AddMinutes(minute), session);
        _processes.Add(process);
        return process;
    }

    private void Window(int pid) => _windows.Add(pid);

    private EfficiencyPlan Plan(Func<ProcessInfo, string>? displayName = null) =>
        new EfficiencyPlanner(_settings, displayName).Plan(new EfficiencySnapshot(Session, _processes, _windows, _holders, _tcp));

    private static int[] Pids(AppFamily family) => family.Processes.Select(p => p.Pid).ToArray();

    private static string RuleFor(EfficiencyPlan plan, string rootName) => Assert.Single(plan.Kept, k => k.RootName == rootName).Rule;

    [Fact]
    public void A_window_owner_and_its_helpers_are_one_app()
    {
        Add(10, 2, "chrome", 5);
        Add(11, 10, "chrome", 6);
        Add(12, 10, "chrome", 6);
        Window(10);

        var family = Assert.Single(Plan().Apply);

        Assert.Equal([10, 11, 12], Pids(family));
        Assert.Equal("chrome", family.RootName);
        Assert.Equal(new ProcessRef(10, T0.AddMinutes(5)), family.Root);
    }

    [Fact]
    public void The_root_is_the_app_that_started_the_window_owner_and_comes_first()
    {
        Add(20, 2, "outlookhost", 5);
        Add(21, 20, "olk", 6);
        Add(22, 21, "msedgewebview2", 7);
        Window(21);

        Assert.Equal([20, 21, 22], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void The_root_stops_below_system_processes()
    {
        Add(5, 4, "svchost", 0);
        Add(30, 5, "whatsapp.root", 5, session: Session);
        Window(30);

        var family = Assert.Single(Plan().Apply);

        Assert.Equal("whatsapp.root", family.RootName);
        Assert.Equal([30], Pids(family));
    }

    [Fact]
    public void A_parent_that_started_after_its_child_is_a_reused_pid_and_is_not_followed()
    {
        Add(41, 2, "spotify", 9); // took over the pid of the real parent, which has exited
        Add(40, 41, "notepad", 5);
        Window(40);

        var family = Assert.Single(Plan().Apply);

        Assert.Equal("notepad", family.RootName);
        Assert.Equal([40], Pids(family));
    }

    [Fact]
    public void Orphans_with_the_roots_name_join_its_app()
    {
        Add(10, 2, "chrome", 5);
        Add(13, 999, "chrome", 6); // its parent has exited
        Add(14, 13, "chrome", 7);
        Window(10);

        Assert.Equal([10, 13, 14], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void Orphans_of_shared_runtimes_never_join_an_app()
    {
        Add(50, 2, "node", 5);
        Add(51, 999, "node", 6);
        Window(50);

        Assert.Equal([50], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void A_window_in_another_session_is_ignored()
    {
        Add(60, 2, "notepad", 5, session: 2);
        Window(60);

        var plan = Plan();

        Assert.Empty(plan.Apply);
        Assert.Empty(plan.Kept);
    }

    [Fact]
    public void An_app_running_an_agent_is_left_alone()
    {
        Add(70, 2, "chatgpt", 5);
        Add(71, 70, "codex", 6);
        Window(70);

        var plan = Plan();

        Assert.Empty(plan.Apply);
        Assert.Equal("agent", RuleFor(plan, "chatgpt"));
    }

    [Fact]
    public void An_app_with_a_process_keeping_the_laptop_awake_is_left_alone()
    {
        Add(80, 2, "blender", 5);
        Add(81, 80, "blender", 6);
        Window(80);
        _holders.Add(81);

        Assert.Equal("holder", RuleFor(Plan(), "blender"));
    }

    [Fact]
    public void A_browser_with_a_connector_running_is_left_alone()
    {
        Add(90, 2, "brave", 5);
        Add(91, 90, "cmd", 6);
        Add(92, 91, "chrome-native-host", 6);
        Window(90);

        Assert.Equal("connector", RuleFor(Plan(), "brave"));
    }

    [Fact]
    public void An_app_an_agent_is_connected_to_over_loopback_is_left_alone()
    {
        Add(100, 2, "claude", 5);
        Add(101, 100, "node", 6);
        Add(110, 2, "postman", 5);
        Window(110);
        _tcp.Add(new TcpRow(110, 5173, 0, false, TcpState.Listen));
        _tcp.Add(new TcpRow(101, 50000, 5173, true, TcpState.Established));

        Assert.Equal("connection", RuleFor(Plan(), "postman"));
    }

    [Fact]
    public void A_connection_that_is_not_loopback_or_not_from_an_agent_keeps_nothing()
    {
        Add(100, 2, "claude", 5);
        Add(120, 2, "spotify", 5);
        Add(121, 2, "discord", 5);
        Add(130, 2, "postman", 5);
        Window(130);
        _tcp.Add(new TcpRow(130, 5173, 0, false, TcpState.Listen));
        _tcp.Add(new TcpRow(100, 50000, 5173, false, TcpState.Established)); // not loopback
        _tcp.Add(new TcpRow(121, 50001, 5173, true, TcpState.Established)); // not an agent

        Assert.Equal("postman", Assert.Single(Plan().Apply).RootName);
    }

    [Theory]
    [InlineData("code", "category")]
    [InlineData("windowsterminal", "category")]
    [InlineData("ollama app", "category")]
    [InlineData("com.docker.backend", "category")]
    [InlineData("steam", "game")]
    [InlineData("explorer", "system")]
    [InlineData("nightcrew", "system")]
    public void Built_in_lists_leave_the_app_alone(string name, string rule)
    {
        Add(140, 2, name, 5);
        Window(140);

        Assert.Equal(rule, RuleFor(Plan(), name));
    }

    [Fact]
    public void A_game_started_by_a_launcher_is_left_alone_with_it()
    {
        Add(150, 2, "steam", 5);
        Add(151, 150, "eldenring", 6);
        Window(151);

        Assert.Equal("game", RuleFor(Plan(), "steam"));
    }

    [Fact]
    public void A_console_window_counts_as_a_terminal_by_its_host()
    {
        Add(160, 2, "cmd", 5);
        Add(161, 160, "conhost", 5);
        Window(161);

        Assert.Equal("category", RuleFor(Plan(), "cmd"));
    }

    [Fact]
    public void A_terminal_helper_inside_an_app_without_its_own_window_does_not_keep_the_app()
    {
        Add(170, 2, "slack", 5);
        Add(171, 170, "cmd", 6);
        Add(172, 171, "conhost", 6);
        Window(170);

        Assert.Equal([170, 171, 172], Pids(Assert.Single(Plan().Apply)));
    }

    [Fact]
    public void Your_list_leaves_the_app_alone_whatever_the_case()
    {
        _settings.LeaveAlone = ["OLK"];
        Add(180, 2, "olk", 5);
        Window(180);

        Assert.Equal("yours", RuleFor(Plan(), "olk"));
    }

    [Fact]
    public void One_kept_member_keeps_the_whole_app()
    {
        Add(10, 2, "chrome", 5);
        Add(11, 10, "chrome", 6);
        Window(10);
        _holders.Add(11);

        var plan = Plan();

        Assert.Empty(plan.Apply);
        Assert.Equal("holder", RuleFor(plan, "chrome"));
    }

    [Fact]
    public void Two_windows_of_one_app_make_one_family()
    {
        Add(10, 2, "chrome", 5);
        Add(11, 10, "chrome", 6);
        Window(10);
        Window(11);

        Assert.Single(Plan().Apply);
    }

    [Fact]
    public void The_display_name_comes_from_the_root()
    {
        Add(20, 2, "olk", 5);
        Window(20);

        var family = Assert.Single(Plan(root => root.Name == "olk" ? "Microsoft Outlook" : "?").Apply);

        Assert.Equal("Microsoft Outlook", family.DisplayName);
        Assert.Equal("olk", family.RootName);
    }

    [Fact]
    public void Apps_started_by_explorer_stay_their_own_apps_while_explorer_owns_a_window()
    {
        // File Explorer windows belong to explorer, which also started most apps.
        Window(2);
        Add(10, 2, "chrome", 5);
        Add(11, 10, "chrome", 6);
        Window(10);
        Add(20, 2, "olk", 5);
        Window(20);

        var plan = Plan();

        Assert.Equal([[10, 11], [20]], plan.Apply.Select(Pids));
        Assert.Equal("system", RuleFor(plan, "explorer"));
    }

    [Theory]
    [InlineData(30, 40)]
    [InlineData(40, 30)]
    public void Two_windows_of_one_app_where_one_process_lost_its_parent_make_one_family(int withParent, int orphan)
    {
        Add(withParent, 2, "notepad", 5);
        Add(orphan, 999, "notepad", 6); // its parent has exited
        Window(withParent);
        Window(orphan);

        var family = Assert.Single(Plan().Apply);

        Assert.Equal(withParent, family.Root.Pid);
        Assert.Equal(new[] { withParent, orphan }.OrderBy(pid => pid == withParent ? 0 : 1), Pids(family));
    }

    [Fact]
    public void No_app_is_ever_planned_without_processes()
    {
        Window(2);
        Add(10, 2, "notepad", 5);
        Add(13, 999, "notepad", 6);
        Add(14, 999, "notepad", 7);
        Window(10);
        Window(13);
        Window(14);

        Assert.All(Plan().Apply, family => Assert.NotEmpty(family.Processes));
    }
}
