using System.Runtime.InteropServices;

namespace MobMate.Core.Native;

public static partial class NativeLibraryBridge
{
    // v1, sequential/default 8-byte packing on both Windows and Linux x64.
    [StructLayout(LayoutKind.Sequential)]
    internal struct VcFillerClip
    {
        internal ulong Id;
        internal IntPtr Samples;
        internal uint NumSamples, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VcFillerUpdate
    {
        internal uint Size, Version;
        internal ulong Generation;
        internal uint Enabled, SampleRate, ClipCount, Reserved;
        internal IntPtr Clips;
        internal ulong PreviousClipId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VcFillerSnapshot
    {
        internal uint Size, Version;
        internal ulong Generation, Utterance, StartedSerial, StartedGeneration, StartedUtterance, StartedClipId;
        internal uint PreparedCount, Active;
    }

    internal sealed record VcFillerPcm(ulong Id, short[] Samples);
    internal sealed record VcFillerBank(ulong Generation, bool Enabled, VcFillerPcm[] Clips, ulong PreviousClipId = 0);
    internal delegate int VcFillerExchangeCall(IntPtr handle, IntPtr update, ref VcFillerSnapshot snapshot);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_filler_exchange(IntPtr handle, IntPtr update, ref VcFillerSnapshot snapshot);

    internal static bool VcFillerAbiSupported => IntPtr.Size == 8 &&
        Marshal.SizeOf<VcFillerClip>() == 24 && Marshal.SizeOf<VcFillerUpdate>() == 48 &&
        Marshal.SizeOf<VcFillerSnapshot>() == 64;

    // The general process-buffer wrapper accepts any positive native count.
    // Filler requires the entire bounded PCM to have been converted, not a
    // partially written output array with an otherwise successful return.
    internal static NativeLibraryResult ProcessVoiceConversionFillerBuffer(IntPtr handle, short[] input, short[] output)
    {
        if (handle == IntPtr.Zero || input.Length is < 1 or > 144000 || output.Length != input.Length)
            return new() { Success = false };
        var pinIn = GCHandle.Alloc(input, GCHandleType.Pinned);
        var pinOut = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            var result = mmv2_vc_process_buffer(handle, pinIn.AddrOfPinnedObject(), input.Length, pinOut.AddrOfPinnedObject(), out var json);
            try
            {
                return new() { Success = result == input.Length,
                    RawJson = json == IntPtr.Zero ? "{}" : Marshal.PtrToStringUTF8(json) ?? "{}" };
            }
            finally { if (json != IntPtr.Zero) mmv2_free_string(json); }
        }
        finally { pinOut.Free(); pinIn.Free(); }
    }

    // Caller owns the shared session lock, including native handle destruction.
    // Native owns a synchronous copy; none of these pins survives the call.
    // A null bank is a status-only exchange (update == NULL).
    internal static bool TryVoiceConversionFillerExchange(IntPtr handle, VcFillerBank? bank,
        out VcFillerSnapshot snapshot, VcFillerExchangeCall? callForTest = null)
    {
        snapshot = new() { Size = 64, Version = 1 };
        if (handle == IntPtr.Zero || !VcFillerAbiSupported) return false;
        if (bank != null && (bank.Generation == 0 || bank.Clips.Length > 5 ||
            bank.Clips.Any(c => c.Id == 0 || c.Samples.Length is < 1 or > 144000) ||
            bank.Clips.Select(c => c.Id).Distinct().Count() != bank.Clips.Length)) return false;
        var pins = new List<GCHandle>(7);
        try
        {
            IntPtr Pin(object value)
            {
                var pin = GCHandle.Alloc(value, GCHandleType.Pinned);
                pins.Add(pin);
                return pin.AddrOfPinnedObject();
            }
            var updatePtr = IntPtr.Zero;
            if (bank != null)
            {
                var clips = bank.Clips.Select(c => new VcFillerClip
                {
                    Id = c.Id, Samples = Pin(c.Samples), NumSamples = (uint)c.Samples.Length
                }).ToArray();
                var update = new VcFillerUpdate
                {
                    Size = 48, Version = 1, Generation = bank.Generation, Enabled = bank.Enabled ? 1u : 0u,
                    SampleRate = 48000, ClipCount = (uint)clips.Length,
                    Clips = clips.Length == 0 ? IntPtr.Zero : Pin(clips), PreviousClipId = bank.PreviousClipId
                };
                updatePtr = Pin(new[] { update });
            }
            var result = callForTest != null
                ? callForTest(handle, updatePtr, ref snapshot)
                : mmv2_vc_filler_exchange(handle, updatePtr, ref snapshot);
            return result == 0 && snapshot.Size == 64 && snapshot.Version == 1 &&
                snapshot.PreparedCount <= 5 && snapshot.Active <= 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return false; } // Optional ABI must never prevent VC Start.
        finally
        {
            for (var i = pins.Count - 1; i >= 0; --i) pins[i].Free();
        }
    }
}
