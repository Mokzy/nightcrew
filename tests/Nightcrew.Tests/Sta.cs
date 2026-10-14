using System.Runtime.ExceptionServices;

namespace Nightcrew.Tests;

/// <summary>Runs WPF code on its own STA thread, the way the tray app's UI thread is set up.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    public static void Run(Action work) => Run(() =>
    {
        work();
        return 0;
    });
}
