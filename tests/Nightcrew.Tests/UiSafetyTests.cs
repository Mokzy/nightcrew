using System.Windows.Threading;
using Nightcrew.App.Ui;
using Nightcrew.Core;
using Xunit;

namespace Nightcrew.Tests;

public sealed class UiSafetyTests
{
    [Fact]
    public void A_panel_error_is_logged_instead_of_ending_the_app()
    {
        var log = new Log();
        Sta.Run(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            UiSafety.Install(dispatcher, log);
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("boom")));
            dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame); // without the handler, "boom" escapes from here
        });

        Assert.Equal(["Panel error: boom"], log.Warnings);
    }

    private sealed class Log : ILog
    {
        public List<string> Warnings { get; } = [];

        public void Info(string message) { }

        public void Warn(string message) => Warnings.Add(message);
    }
}
