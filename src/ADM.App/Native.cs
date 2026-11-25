using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ADM.App;

static class Native
{
    const uint EsContinuous = 0x80000000;
    const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeWaitHandle CreateWaitableTimer(IntPtr attributes, bool manualReset, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelWaitableTimer(SafeWaitHandle timer);

    // Its three arguments and its answer are BOOLEANs, one byte each.
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool wakeEventsDisabled);

    static SafeWaitHandle? _wakeTimer;

    /// <summary>Puts the PC to sleep, or into hibernation, letting programs and wake timers have their say as usual.</summary>
    public static void Suspend(bool hibernate) => SetSuspendState(hibernate, force: false, wakeEventsDisabled: false);

    /// <summary>Keeps the machine from sleeping while transfers run.</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous);

    /// <summary>
    /// Arms a waitable timer that can wake the PC from sleep for the next scheduled queue start
    /// (honoured only when "Allow wake timers" is enabled in Windows power options).
    /// </summary>
    public static void SetWakeTime(DateTime? at)
    {
        _wakeTimer ??= CreateWaitableTimer(IntPtr.Zero, true, null);
        if (_wakeTimer.IsInvalid) return;
        if (at is not DateTime when)
        {
            CancelWaitableTimer(_wakeTimer);
            return;
        }
        long due = when.ToUniversalTime().ToFileTimeUtc();
        SetWaitableTimer(_wakeTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, true);
    }
}
