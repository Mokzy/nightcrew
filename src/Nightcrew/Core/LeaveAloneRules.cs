namespace Nightcrew.Core;

/// <summary>
/// The built-in lists of apps Efficiency mode never touches. Entries are lower-case process names without ".exe", as
/// <see cref="ProcessEntry"/> has them; an entry ending in * matches any name that starts with the rest.
/// </summary>
public static class LeaveAloneRules
{
    /// <summary>Terminals and editors, Docker/WSL/VMs, VPNs, password managers and SSH agents, local models, remote desktop.</summary>
    public static readonly IReadOnlyList<string> Categories =
    [
        "windowsterminal", "openconsole", "conhost", "code", "cursor", "devenv", "rider64", "idea64", "pycharm64",
        "webstorm64", "clion64", "goland64", "datagrip64", "rubymine64", "phpstorm64", "studio64", "wezterm-gui",
        "alacritty",
        "docker desktop", "com.docker.*", "wsl", "wslhost", "wslservice", "vmmem*", "virtualbox", "virtualboxvm",
        "vmware", "vmware-vmx",
        "nordvpn", "openvpn*", "wireguard", "tailscale*", "protonvpn*", "mullvad*",
        "1password", "bitwarden", "nordpass", "keepassxc", "dashlane*", "proton pass", "ssh-agent", "pageant",
        "ollama*", "lm studio",
        "mstsc", "anydesk", "teamviewer*", "parsec*", "rustdesk", "remoting_host",
    ];

    /// <summary>Game launchers and anti-cheat, and so everything they start.</summary>
    public static readonly IReadOnlyList<string> Games =
    [
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "riotclientservices", "riot client",
        "riotclientux*", "leagueclient*", "vgtray", "eadesktop", "ealauncher", "battle.net", "galaxyclient*", "upc",
        "ubisoftconnect", "easyanticheat*", "beservice", "faceit*",
    ];

    /// <summary>Windows itself, the shell, accessibility and input tools, system utilities, and Nightcrew.</summary>
    public static readonly IReadOnlyList<string> System =
    [
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "fontdrvhost", "dwm",
        "userinit", "explorer", "sihost", "shellexperiencehost", "shellhost", "startmenuexperiencehost", "searchhost",
        "searchapp", "textinputhost", "ctfmon", "chsime", "lockapp", "applicationframehost", "runtimebroker",
        "svchost", "taskhostw", "dllhost", "systemsettings", "taskmgr", "procexp*", "systeminformer", "powertoys*",
        "widgets", "widgetservice", "phoneexperiencehost", "crossdevice*", "securityhealthsystray", "narrator",
        "magnify", "nvda", "nightcrew",
    ];

    /// <summary>Where a family's root stops climbing: an app started by one of these is its own root.</summary>
    public static readonly IReadOnlyList<string> StopAbove =
    [
        "system", "smss", "csrss", "wininit", "winlogon", "services", "userinit", "explorer", "sihost", "svchost",
        "dllhost", "runtimebroker", "taskhostw",
    ];

    /// <summary>Runtimes many unrelated apps share, so an orphan is never grouped with a family by this name.</summary>
    public static readonly IReadOnlyList<string> SharedRuntimes =
    [
        "msedgewebview2", "node", "bun", "deno", "python", "pythonw", "java", "javaw", "cmd", "powershell", "pwsh",
    ];

    public static bool Matches(IReadOnlyList<string> list, string name)
    {
        foreach (var entry in list)
        {
            if (entry.EndsWith("*", StringComparison.Ordinal))
            {
                if (name.StartsWith(entry.Substring(0, entry.Length - 1), StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(entry, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
