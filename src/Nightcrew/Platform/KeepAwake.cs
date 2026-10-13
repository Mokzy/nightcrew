using System.ComponentModel;
using System.Runtime.InteropServices;
using Nightcrew.Core;

namespace Nightcrew.Platform;

public sealed class KeepAwake : IKeepAwake, IDisposable
{
    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const int PowerRequestSystemRequired = 1;
    private const int PowerRequestExecutionRequired = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private IntPtr _request = IntPtr.Zero;

    public bool IsHeld { get; private set; }

    public void Hold()
    {
        if (IsHeld) return;
        if (_request == IntPtr.Zero)
        {
            var context = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = "Nightcrew: agents working",
            };
            var request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == InvalidHandle) throw new Win32Exception();
            _request = request;
        }
        if (!PowerSetRequest(_request, PowerRequestSystemRequired)) throw new Win32Exception();
        if (!PowerSetRequest(_request, PowerRequestExecutionRequired))
        {
            var error = new Win32Exception();
            PowerClearRequest(_request, PowerRequestSystemRequired);
            throw error;
        }
        IsHeld = true;
    }

    public void Release()
    {
        if (!IsHeld) return;
        PowerClearRequest(_request, PowerRequestExecutionRequired);
        PowerClearRequest(_request, PowerRequestSystemRequired);
        IsHeld = false;
    }

    public void Dispose()
    {
        Release();
        if (_request == IntPtr.Zero) return;
        CloseHandle(_request);
        _request = IntPtr.Zero;
    }
}
