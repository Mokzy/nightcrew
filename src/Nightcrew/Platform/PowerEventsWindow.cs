using System.Runtime.InteropServices;
using System.Windows.Forms;
using Nightcrew.Core;

namespace Nightcrew.Platform;

/// <summary>Hidden top-level window (not message-only, so it gets broadcasts) for lid, suspend/resume and session-end messages.</summary>
public sealed class PowerEventsWindow : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WM_QUERYENDSESSION = 0x11;
    private const int WM_ENDSESSION = 0x16;
    private const int WM_POWERBROADCAST = 0x218;
    private const long PBT_APMSUSPEND = 0x4;
    private const long PBT_APMRESUMEAUTOMATIC = 0x12;
    private const long PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);

    private IntPtr _lidNotification;
    private IntPtr _suspendNotification;

    public PowerEventsWindow()
    {
        CreateHandle(new CreateParams { Caption = "Nightcrew power events", Style = WS_POPUP, Parent = IntPtr.Zero });
        var lidSwitch = PowerBroadcast.LidSwitchStateChange;
        _lidNotification = RegisterPowerSettingNotification(Handle, ref lidSwitch, DEVICE_NOTIFY_WINDOW_HANDLE);
        _suspendNotification = RegisterSuspendResumeNotification(Handle, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    public LidState Lid { get; private set; }

    public event Action<LidState>? LidChanged;
    public event Action? Suspending;
    public event Action? Resumed;
    public event Action? SessionEnding;

    /// <summary>The session end announced by SessionEnding was cancelled (WM_ENDSESSION with wParam FALSE).</summary>
    public event Action? SessionEndCancelled;

    /// <summary>The session is ending for real (WM_ENDSESSION with wParam TRUE); the process may be ended any time after this returns.</summary>
    public event Action? SessionEnded;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_QUERYENDSESSION)
        {
            SessionEnding?.Invoke();
            m.Result = new IntPtr(1);
            return;
        }
        if (m.Msg == WM_ENDSESSION)
        {
            if (m.WParam == IntPtr.Zero) SessionEndCancelled?.Invoke();
            else SessionEnded?.Invoke();
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_POWERBROADCAST)
        {
            switch ((long)m.WParam)
            {
                case PBT_POWERSETTINGCHANGE:
                    var lid = PowerBroadcast.ParseLid(m.LParam);
                    if (lid != LidState.Unknown)
                    {
                        Lid = lid;
                        LidChanged?.Invoke(lid);
                    }
                    break;
                case PBT_APMSUSPEND:
                    Suspending?.Invoke();
                    break;
                case PBT_APMRESUMEAUTOMATIC:
                    Resumed?.Invoke();
                    break;
            }
            m.Result = new IntPtr(1);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_lidNotification != IntPtr.Zero)
        {
            UnregisterPowerSettingNotification(_lidNotification);
            _lidNotification = IntPtr.Zero;
        }
        if (_suspendNotification != IntPtr.Zero)
        {
            UnregisterSuspendResumeNotification(_suspendNotification);
            _suspendNotification = IntPtr.Zero;
        }
        DestroyHandle();
    }
}
