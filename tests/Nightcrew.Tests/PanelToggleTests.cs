using Nightcrew.App.Ui;
using Xunit;

namespace Nightcrew.Tests;

public sealed class PanelToggleTests
{
    [Theory]
    // Closed, nothing just happened: open on the page that button asks for.
    [InlineData(false, false, false, false, PanelClick.Open)]
    [InlineData(false, false, false, true, PanelClick.Open)]
    // The click took focus from the open panel, which hid it: the same button leaves it closed...
    [InlineData(false, false, true, false, PanelClick.Ignore)]
    [InlineData(false, true, true, true, PanelClick.Ignore)]
    // ...and the other button reopens it on the other page.
    [InlineData(false, false, true, true, PanelClick.Open)]
    [InlineData(false, true, true, false, PanelClick.Open)]
    // Still open (it never had focus): same button closes, other button switches.
    [InlineData(true, false, false, false, PanelClick.Close)]
    [InlineData(true, true, false, false, PanelClick.SwitchPage)]
    [InlineData(true, false, false, true, PanelClick.SwitchPage)]
    public void A_tray_click_closes_switches_or_opens(bool visible, bool onSettings, bool justHid, bool wantSettings, PanelClick expected)
    {
        Assert.Equal(expected, PanelToggle.Decide(visible, onSettings, justHid, wantSettings));
    }
}
