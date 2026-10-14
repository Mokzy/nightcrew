namespace Nightcrew.App.Ui;

public enum PanelClick { Open, Close, SwitchPage, Ignore }

/// <summary>What a click on the tray icon does to the panel. Left click asks for the main page, right click for settings.</summary>
public static class PanelToggle
{
    /// <summary>
    /// Clicking the tray icon takes focus from the open panel first, which hides it, so the click usually arrives with
    /// the panel just hidden. The same button then leaves it closed; the other button reopens it on the other page.
    /// <paramref name="onSettings"/> is the page the panel shows, or showed when it hid.
    /// </summary>
    public static PanelClick Decide(bool visible, bool onSettings, bool justHid, bool wantSettings) =>
        visible ? (onSettings == wantSettings ? PanelClick.Close : PanelClick.SwitchPage)
        : justHid && onSettings == wantSettings ? PanelClick.Ignore
        : PanelClick.Open;
}
