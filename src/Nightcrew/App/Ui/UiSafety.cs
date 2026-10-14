using System.Windows.Threading;
using Nightcrew.Core;

namespace Nightcrew.App.Ui;

public static class UiSafety
{
    /// <summary>
    /// Logs and swallows errors raised on the WPF dispatcher: the panel's clicks, layout and rendering. WinForms runs
    /// the message loop and doesn't catch these, so one would end the app with the power settings still changed
    /// until the watchdog puts them back.
    /// </summary>
    public static void Install(Dispatcher dispatcher, ILog log) => dispatcher.UnhandledException += (_, e) =>
    {
        log.Warn($"Panel error: {e.Exception.Message}");
        e.Handled = true;
    };
}
