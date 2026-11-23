using System.Runtime.InteropServices;
using System.Text;
using MobMate.Core.Native;

namespace MobMate.Core.Realtime;

/// <summary>Per-call, bounded managed output callback; exceptions never cross the native boundary.</summary>
public static class RealtimeReplayFinalizer
{
    public static NativeLibraryBridge.ReplayFinalizer Create(
        Func<RealtimePollResult, string, string, RealtimeFinalOutput> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return (pollJson, language, scene, text, textCapacity, engine, engineCapacity, reason, reasonCapacity) =>
        {
            try
            {
                if (pollJson == IntPtr.Zero) return -1;
                var poll = RealtimeSessionController.ParseNativePoll(Marshal.PtrToStringUTF8(pollJson) ?? "{}");
                if (poll.Error is not null || poll.State == "failed") return -1;
                var result = process(poll, Marshal.PtrToStringUTF8(language) ?? "", Marshal.PtrToStringUTF8(scene) ?? "");
                return Write(text, textCapacity, result.Text) && Write(engine, engineCapacity, result.Engine) &&
                    Write(reason, reasonCapacity, result.DecisionReason) ? 0 : -1;
            }
            catch (Exception) { return -1; }
        };
    }

    private static bool Write(IntPtr destination, int capacity, string value)
    {
        if (destination == IntPtr.Zero || capacity <= 0 || value.Contains('\0')) return false;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length >= capacity) return false;
        Marshal.Copy(bytes, 0, destination, bytes.Length);
        Marshal.WriteByte(destination, bytes.Length, 0);
        return true;
    }
}
