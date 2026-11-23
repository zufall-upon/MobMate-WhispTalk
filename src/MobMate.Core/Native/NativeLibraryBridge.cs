using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MobMate.Core.Native;

/// <summary>
/// P/Invoke bridge to mobmate_native_bridge.dll / libmobmate_native_bridge.so.
/// Loads the native library, calls version functions, and manages native string lifetime.
/// </summary>
public static partial class NativeLibraryBridge
{
    private const string DllName = "mobmate_native_bridge.dll";

    // Resolved at runtime by SetNativePath(); used by DllImportResolver on Linux.
    private static string? _resolvedDllPath;
    private static readonly object NativeLoadLock = new();
    private static readonly HashSet<string> PreparedNativeRoots = new(StringComparer.OrdinalIgnoreCase);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_api_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr mmv2_native_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern void mmv2_free_string(IntPtr p);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_audio_input_devices(out IntPtr outJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_transcribe_wav(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string wavPathUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string optionsJsonUtf8,
        out IntPtr outJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_transcribe_pcm16(
        IntPtr pcm16Ptr,
        int count,
        int sampleRateHz,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string optionsJsonUtf8,
        out IntPtr outJson);

    // Realtime session ABI (internal for RealtimeSessionController)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_realtime_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string? optionsJsonUtf8,
        out IntPtr outHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_realtime_start(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_realtime_stop(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_realtime_poll(
        IntPtr handle,
        out IntPtr outJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mmv2_realtime_destroy(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void VadStartCallback(ulong sequence, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_realtime_set_vad_start_callback(
        IntPtr handle, VadStartCallback? callback, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_realtime_bench_manifest(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string manifestPathUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string outJsonlPathUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string optionsJsonUtf8,
        out IntPtr outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int ReplayFinalizer(IntPtr pollJson, IntPtr language, IntPtr scene,
        IntPtr textUtf8, int textCapacity, IntPtr engineUtf8, int engineCapacity,
        IntPtr reasonUtf8, int reasonCapacity);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_realtime_bench_manifest_processed(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string manifestPathUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string outJsonlPathUtf8,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string optionsJsonUtf8,
        ReplayFinalizer finalizer, out IntPtr outJson);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AddonTranscriber(IntPtr pcm16, int count, int sampleRate, IntPtr textUtf8, int textCapacity, out long inferenceMs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int AddonTranscriberV2(IntPtr engineId, IntPtr language, IntPtr hintMode,
        IntPtr pcm16, int count, int sampleRate, IntPtr textUtf8, int textCapacity, out long inferenceMs);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_set_addon_transcriber_v2(AddonTranscriberV2 callback);
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_set_addon_joint_transcriber_v2(AddonTranscriberV2 callback);
    private static AddonTranscriberV2? _addonTranscriberV2;
    private static AddonTranscriberV2? _addonJointTranscriberV2;

    public static void RegisterAddonTranscriberV2(string nativeExePath, AddonTranscriberV2 callback, bool joint = false)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (NativeLoadLock)
        {
            if (!File.Exists(ResolveDllPath(nativeExePath))) throw new FileNotFoundException("Native bridge is unavailable");
            LoadNativeBridge(nativeExePath);
            var result = joint ? mmv2_set_addon_joint_transcriber_v2(callback) : mmv2_set_addon_transcriber_v2(callback);
            if (result != 0) throw new InvalidOperationException("Native v2 addon callback registration failed");
            if (joint) _addonJointTranscriberV2 = callback; else _addonTranscriberV2 = callback;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_set_addon_transcriber(AddonTranscriber callback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_set_addon_joint_transcriber(AddonTranscriber callback);

    // The native callback can outlive an individual poll, stop, or benchmark call.
    private static AddonTranscriber? _addonTranscriber;
    private static AddonTranscriber? _addonJointTranscriber;

    public static void RegisterAddonTranscriber(string nativeExePath, AddonTranscriber callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (NativeLoadLock)
        {
            if (!File.Exists(ResolveDllPath(nativeExePath))) throw new FileNotFoundException("Native bridge is unavailable");
            LoadNativeBridge(nativeExePath);
            if (mmv2_set_addon_transcriber(callback) != 0) throw new InvalidOperationException("Native add-on callback registration failed");
            _addonTranscriber = callback;
        }
    }

    public static void RegisterAddonJointTranscriber(string nativeExePath, AddonTranscriber callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (NativeLoadLock)
        {
            if (!File.Exists(ResolveDllPath(nativeExePath))) throw new FileNotFoundException("Native bridge is unavailable");
            LoadNativeBridge(nativeExePath);
            if (mmv2_set_addon_joint_transcriber(callback) != 0) throw new InvalidOperationException("Native Joint add-on callback registration failed");
            _addonJointTranscriber = callback;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string? optionsJsonUtf8,
        out IntPtr outHandle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_start(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_stop(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_poll_status(IntPtr handle, out IntPtr outJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_process_buffer(IntPtr handle,
        IntPtr input, int numSamples, IntPtr output, out IntPtr outPressureJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_inject_and_capture(IntPtr handle,
        IntPtr input, int numSamples, IntPtr output, out IntPtr outJson);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_vc_load_models(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mmv2_vc_unload_models(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mmv2_vc_destroy(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int mmv2_clipboard_set_text(
        [MarshalAs(UnmanagedType.LPUTF8Str)]
        string utf8Text);

    // Phase 7.4-2: TTS PCM16 playback (Windows: WASAPI, Linux: miniaudio)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_tts_play_pcm16(
        IntPtr samples,
        int sampleCount,
        int sampleRate,
        int channels,
        int monitorVolumePercent);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_tts_stop();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void TtsStartedCallback(ulong requestId, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_tts_play_filler_pcm16(ulong requestId, IntPtr samples,
        int sampleCount, int sampleRate, int channels, int monitorVolumePercent,
        TtsStartedCallback started, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mmv2_tts_cancel_filler_and_wait(ulong requestId);

    // All routers/sessions in this host share this allocator, just as they share
    // the single native PCM output. Session/config/utterance resets never reset it.
    private static long _nextFillerRequestId;
    internal static ulong NextFillerRequestId()
    {
        var previous = Volatile.Read(ref _nextFillerRequestId);
        while (true)
        {
            if (previous < 0 || previous == long.MaxValue) throw new InvalidOperationException("Filler playback identity exhausted");
            var observed = Interlocked.CompareExchange(ref _nextFillerRequestId, previous + 1, previous);
            if (observed == previous) return (ulong)(previous + 1);
            previous = observed;
        }
    }

    internal static bool SupportsIdentifiedFiller(string nativeExePath)
    {
        try
        {
            var library = LoadNativeBridge(nativeExePath);
            try
            {
                return NativeLibrary.TryGetExport(library, "mmv2_tts_play_filler_pcm16", out _) &&
                    NativeLibrary.TryGetExport(library, "mmv2_tts_cancel_filler_and_wait", out _);
            }
            finally { NativeLibrary.Free(library); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or BadImageFormatException) { return false; }
    }

    internal static NativeLibraryResult PlayFillerPcm16(string nativeExePath, ulong requestId,
        short[] samples, int sampleRate, int channels, int monitorVolumePercent, Action started)
    {
        if (requestId == 0 || samples.Length == 0 || sampleRate <= 0 || channels is < 1 or > 2)
            return new() { Success = false, Message = "Invalid filler PCM arguments" };
        TtsStartedCallback callback = (_, _) => { try { started(); } catch { /* Never cross the ABI. */ } };
        var pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            var library = LoadNativeBridge(nativeExePath);
            try
            {
                var result = mmv2_tts_play_filler_pcm16(requestId, pinned.AddrOfPinnedObject(), samples.Length,
                    sampleRate, channels, monitorVolumePercent, callback, IntPtr.Zero);
                return new() { Success = result == 0, Message = result == 0 ? "Playback completed" : $"Filler playback: {result}" };
            }
            finally { NativeLibrary.Free(library); }
        }
        catch (Exception ex) { return new() { Success = false, Message = $"Filler playback unavailable: {ex.Message}" }; }
        finally { GC.KeepAlive(callback); pinned.Free(); }
    }

    internal static NativeLibraryResult CancelFillerAndWait(string nativeExePath, ulong requestId)
    {
        try
        {
            var library = LoadNativeBridge(nativeExePath);
            try
            {
                var result = mmv2_tts_cancel_filler_and_wait(requestId);
                return new() { Success = result == 0, Message = result == 0 ? "Filler drained" : $"Filler cancel: {result}" };
            }
            finally { NativeLibrary.Free(library); }
        }
        catch (Exception ex) { return new() { Success = false, Message = $"Filler cancel unavailable: {ex.Message}" }; }
    }

    // Set TTS output target (Windows: MMDevice GUID, Linux: PipeWire/PulseAudio sink name)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mmv2_tts_set_output_device(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? deviceName);

    // Phase 7A: Speaker identification
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mmv2_speaker_reset();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mmv2_speaker_set_enabled(int enabled);

    /// <summary>
    /// Resolves the native library path relative to the native executable directory.
    /// On Linux returns libmobmate_native_bridge.so; on Windows returns .dll.
    /// </summary>
    public static string ResolveDllPath(string nativeExePath)
    {
        var dir = Path.GetDirectoryName(nativeExePath) ?? ".";
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "mobmate_native_bridge.dll"
            : "libmobmate_native_bridge.so";
        return Path.Combine(dir, fileName);
    }

    private static IntPtr LoadNativeBridge(string nativeExePath)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        PrepareNativeDependencySearchPath(nativeExePath);
        return NativeLibrary.Load(dllPath);
    }

    private static void PrepareNativeDependencySearchPath(string nativeExePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var nativeDir = Path.GetDirectoryName(Path.GetFullPath(nativeExePath));
        if (string.IsNullOrWhiteSpace(nativeDir)) return;
        var appRoot = Directory.GetParent(nativeDir)?.FullName ?? nativeDir;
        lock (NativeLoadLock)
        {
            if (!PreparedNativeRoots.Add(appRoot)) return;
            var candidates = new[]
            {
                nativeDir,
                appRoot,
                Path.Combine(appRoot, "libs", "onnxruntime"),
                Path.Combine(appRoot, "libs", "vulkan"),
                Path.Combine(appRoot, "libs", "sherpa-onnx"),
            }
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var existing = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prefix = candidates.Where(p => !existing.Contains(p));
            var updatedPath = string.Join(Path.PathSeparator, prefix.Concat(new[] { currentPath }).Where(s => !string.IsNullOrWhiteSpace(s)));
            Environment.SetEnvironmentVariable("PATH", updatedPath);
        }
    }

    /// <summary>
    /// Call once at startup (before any P/Invoke) to register the DLL import resolver.
    /// Required on Linux where DllImport("mobmate_native_bridge.dll") does not match the .so name.
    /// Safe to call on Windows too (resolver falls through to default behaviour).
    /// </summary>
    public static void SetNativePath(string nativeExePath)
    {
        _resolvedDllPath = ResolveDllPath(nativeExePath);
        PrepareNativeDependencySearchPath(nativeExePath);
        NativeLibrary.SetDllImportResolver(
            typeof(NativeLibraryBridge).Assembly,
            static (name, _, _) =>
            {
                if (name == DllName && _resolvedDllPath != null && File.Exists(_resolvedDllPath))
                    return NativeLibrary.Load(_resolvedDllPath);
                return IntPtr.Zero;
            });
    }

    /// <summary>
    /// Set the TTS output device target.
    /// Windows uses the MMDevice GUID; Linux uses the PipeWire/PulseAudio sink name.
    /// </summary>
    public static void SetTtsOutputDevice(string nativeExePath, string? deviceName, string? deviceId = null)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath)) return;
        try
        {
            LoadNativeBridge(nativeExePath);
            var target = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !string.IsNullOrWhiteSpace(deviceId)
                ? deviceId
                : deviceName;
            mmv2_tts_set_output_device(target);
        }
        catch { /* ignore native output target failures */ }
    }

    /// <summary>
    /// Loads the native DLL and queries version information.
    /// </summary>
    public static NativeLibraryResult GetVersion(string nativeExePath)
    {
        var dllPath = ResolveDllPath(nativeExePath);

        if (!File.Exists(dllPath))
        {
            return new NativeLibraryResult
            {
                Success = false,
                Message = $"DLL not found: {dllPath}",
            };
        }

        try
        {
            // Load the DLL explicitly so DllImport can find it
            var handle = LoadNativeBridge(nativeExePath);
            if (handle == IntPtr.Zero)
            {
                return new NativeLibraryResult
                {
                    Success = false,
                    Message = $"Failed to load DLL: {dllPath}",
                };
            }

            var apiVer = mmv2_api_version();
            var nativeVerPtr = mmv2_native_version();
            var nativeVer = nativeVerPtr != IntPtr.Zero
                ? Marshal.PtrToStringUTF8(nativeVerPtr) ?? "(null)"
                : "(null)";

            return new NativeLibraryResult
            {
                Success = true,
                ApiVersion = apiVer,
                NativeVersion = nativeVer,
                Message = $"API v{apiVer}, native {nativeVer}",
            };
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult
            {
                Success = false,
                Message = $"DLL load error: {ex.Message}",
            };
        }
    }

    public static string GetAudioInputDevicesJson(string nativeExePath)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath))
        {
            return $"{{\"ok\":false,\"devices\":[],\"error\":\"DLL not found: {EscapeJson(dllPath)}\"}}";
        }

        try
        {
            LoadNativeBridge(nativeExePath);
            var ret = mmv2_audio_input_devices(out IntPtr outJson);
            if (outJson == IntPtr.Zero)
            {
                return $"{{\"ok\":false,\"devices\":[],\"error\":\"audio input devices returned null (ret={ret})\"}}";
            }

            var rawJson = Marshal.PtrToStringUTF8(outJson) ?? "{\"ok\":false,\"devices\":[]}";
            mmv2_free_string(outJson);
            return rawJson;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return $"{{\"ok\":false,\"devices\":[],\"error\":\"audio input devices load failed: {EscapeJson(ex.Message)}\"}}";
        }
    }

    public static NativeLibraryResult RealtimeBenchManifest(
        string nativeExePath,
        string manifestPath,
        string outJsonlPath,
        string optionsJson)
        => RealtimeBenchManifest(nativeExePath, manifestPath, outJsonlPath, optionsJson, null);

    public static NativeLibraryResult RealtimeBenchManifest(
        string nativeExePath, string manifestPath, string outJsonlPath, string optionsJson,
        ReplayFinalizer? finalizer)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath))
        {
            return new NativeLibraryResult { Success = false, Message = $"DLL not found: {dllPath}" };
        }

        try
        {
            LoadNativeBridge(nativeExePath);
            IntPtr outJson;
            var ret = finalizer is null
                ? mmv2_realtime_bench_manifest(manifestPath, outJsonlPath, optionsJson, out outJson)
                : mmv2_realtime_bench_manifest_processed(manifestPath, outJsonlPath, optionsJson, finalizer, out outJson);
            GC.KeepAlive(finalizer);
            if (outJson == IntPtr.Zero)
            {
                return new NativeLibraryResult { Success = false, Message = $"Native realtime bench returned null output (ret={ret})" };
            }
            var rawJson = Marshal.PtrToStringUTF8(outJson) ?? "{}";
            mmv2_free_string(outJson);
            return new NativeLibraryResult { Success = ret == 0, RawJson = rawJson, Message = rawJson };
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = ex.Message };
        }
    }

    public static NativeLibraryResult VoiceConversionCreate(string nativeExePath, string optionsJson, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath))
            return new NativeLibraryResult { Success = false, Message = $"DLL not found: {dllPath}" };

        try
        {
            LoadNativeBridge(nativeExePath);
            var ret = mmv2_vc_create(optionsJson, out handle);
            return new NativeLibraryResult
            {
                Success = ret == 0 && handle != IntPtr.Zero,
                Message = ret == 0 ? "created" : $"native vc create failed: {ret}"
            };
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = ex.Message };
        }
    }

    public static NativeLibraryResult VoiceConversionStart(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return new NativeLibraryResult { Success = false, Message = "invalid handle" };
        var ret = mmv2_vc_start(handle);
        return new NativeLibraryResult { Success = ret == 0, Message = ret == 0 ? "started" : $"native vc start failed: {ret}" };
    }

    public static NativeLibraryResult VoiceConversionStop(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return new NativeLibraryResult { Success = false, Message = "invalid handle" };
        var ret = mmv2_vc_stop(handle);
        return new NativeLibraryResult { Success = ret == 0, Message = ret == 0 ? "stopped" : $"native vc stop failed: {ret}" };
    }

    public static NativeLibraryResult VoiceConversionPollStatus(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return new NativeLibraryResult { Success = false, Message = "not created" };
        try
        {
            var ret = mmv2_vc_poll_status(handle, out IntPtr outJson);
            if (outJson == IntPtr.Zero)
                return new NativeLibraryResult { Success = false, Message = $"native vc status returned null (ret={ret})" };
            var rawJson = Marshal.PtrToStringUTF8(outJson) ?? "{}";
            mmv2_free_string(outJson);
            return new NativeLibraryResult { Success = ret == 0, RawJson = rawJson, Message = rawJson };
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Offline DSP processing: runs the Prosody Neutralizer chain on a PCM16 buffer
    /// without audio devices. Returns processed samples and pressure stats JSON.
    /// </summary>
    public static NativeLibraryResult VoiceConversionProcessBuffer(IntPtr handle, short[] input, short[] output)
    {
        if (handle == IntPtr.Zero)
            return new NativeLibraryResult { Success = false, Message = "invalid handle" };
        if (input == null || output == null || input.Length == 0 || output.Length < input.Length)
            return new NativeLibraryResult { Success = false, Message = "invalid buffer" };

        var pinIn = System.Runtime.InteropServices.GCHandle.Alloc(input, System.Runtime.InteropServices.GCHandleType.Pinned);
        var pinOut = System.Runtime.InteropServices.GCHandle.Alloc(output, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var ret = mmv2_vc_process_buffer(handle,
                pinIn.AddrOfPinnedObject(), input.Length,
                pinOut.AddrOfPinnedObject(), out IntPtr jsonPtr);
            string? rawJson = null;
            if (jsonPtr != IntPtr.Zero)
            {
                rawJson = Marshal.PtrToStringUTF8(jsonPtr);
                mmv2_free_string(jsonPtr);
            }
            return new NativeLibraryResult
            {
                Success = ret > 0,
                RawJson = rawJson ?? "{}",
                Message = ret > 0 ? $"processed {ret} samples" : $"process_buffer failed: {ret}"
            };
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = ex.Message };
        }
        finally
        {
            pinIn.Free();
            pinOut.Free();
        }
    }

    /// <summary>
    /// Live-path inject: feed PCM through the running session's VAD+DSP pipeline
    /// (same path as on_capture). Requires VoiceConversionStart first.
    /// </summary>
    public static NativeLibraryResult VoiceConversionInjectAndCapture(IntPtr handle, short[] input, short[] output)
    {
        if (handle == IntPtr.Zero)
            return new NativeLibraryResult { Success = false, Message = "invalid handle" };
        if (input == null || output == null || input.Length == 0 || output.Length < input.Length)
            return new NativeLibraryResult { Success = false, Message = "invalid buffer" };

        var pinIn = System.Runtime.InteropServices.GCHandle.Alloc(input, System.Runtime.InteropServices.GCHandleType.Pinned);
        var pinOut = System.Runtime.InteropServices.GCHandle.Alloc(output, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var ret = mmv2_vc_inject_and_capture(handle,
                pinIn.AddrOfPinnedObject(), input.Length,
                pinOut.AddrOfPinnedObject(), out IntPtr jsonPtr);
            string? rawJson = null;
            if (jsonPtr != IntPtr.Zero)
            {
                rawJson = Marshal.PtrToStringUTF8(jsonPtr);
                mmv2_free_string(jsonPtr);
            }
            return new NativeLibraryResult
            {
                Success = ret > 0,
                RawJson = rawJson ?? "{}",
                Message = ret > 0 ? $"injected {ret} samples (live route)" : $"inject_and_capture failed: {ret}"
            };
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = ex.Message };
        }
        finally
        {
            pinIn.Free();
            pinOut.Free();
        }
    }

    /// <summary>
    /// Load RVC ONNX models for voice conversion. Requires modelPath in options.
    /// </summary>
    public static NativeLibraryResult VoiceConversionLoadModels(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return new NativeLibraryResult { Success = false, Message = "invalid handle" };
        var ret = mmv2_vc_load_models(handle);
        return new NativeLibraryResult
        {
            Success = ret == 0,
            Message = ret == 0 ? "models loaded" : $"model load failed: {ret}"
        };
    }

    /// <summary>
    /// Unload VC ONNX models and free GPU/CPU memory.
    /// </summary>
    public static void VoiceConversionUnloadModels(IntPtr handle)
    {
        if (handle != IntPtr.Zero) mmv2_vc_unload_models(handle);
    }

    public static void VoiceConversionDestroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero) mmv2_vc_destroy(handle);
    }

    /// <summary>
    /// Transcribes a WAV file via the native DLL.
    /// </summary>
    public static NativeLibraryResult TranscribeWav(
        string nativeExePath,
        string wavPath,
        string modelPath,
        string whisperDllDir,
        string language,
        int threads)
    {
        var dllPath = ResolveDllPath(nativeExePath);

        if (!File.Exists(dllPath))
        {
            return new NativeLibraryResult
            {
                Success = false,
                Message = $"DLL not found: {dllPath}",
            };
        }

        try
        {
            var handle = LoadNativeBridge(nativeExePath);
            if (handle == IntPtr.Zero)
            {
                return new NativeLibraryResult
                {
                    Success = false,
                    Message = $"Failed to load DLL: {dllPath}",
                };
            }

            // Build JSON options
            var optionsJson = $"{{\"modelPath\":\"{EscapeJson(modelPath)}\"," +
                              $"\"whisperDllDir\":\"{EscapeJson(whisperDllDir)}\"," +
                              $"\"language\":\"{EscapeJson(language)}\"," +
                              $"\"threads\":{threads}," +
                              $"\"useGpu\":false}}";

            var ret = mmv2_transcribe_wav(wavPath, optionsJson, out IntPtr outJson);
            if (outJson == IntPtr.Zero)
            {
                return new NativeLibraryResult
                {
                    Success = false,
                    Message = "Native returned null output",
                };
            }

            // Read and free native string (native returns UTF-8)
            var rawJson = Marshal.PtrToStringUTF8(outJson) ?? "{}";
            mmv2_free_string(outJson);

            return ParseTranscribeResultJson(rawJson);
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult
            {
                Success = false,
                Message = $"Transcribe error: {ex.Message}",
            };
        }
    }

    /// <summary>
    /// Int16 LE mono PCM を直接 Whisper/Moonshine で推論する。WAV ファイル書き出しは行わない。
    /// GCHandle.Pinned でサンプルをネイティブに直接渡す。
    /// </summary>
    public static NativeLibraryResult TranscribePcm16(
        string nativeExePath,
        short[] samples,
        int sampleRateHz,
        string optionsJson)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath))
            return new NativeLibraryResult { Success = false, Message = $"DLL not found: {dllPath}" };

        try
        {
            LoadNativeBridge(nativeExePath);
            var handle = GCHandle.Alloc(samples, GCHandleType.Pinned);
            try
            {
                var ret = mmv2_transcribe_pcm16(
                    handle.AddrOfPinnedObject(), samples.Length, sampleRateHz,
                    optionsJson, out IntPtr outJson);

                if (outJson == IntPtr.Zero)
                    return new NativeLibraryResult { Success = false, Message = "Native returned null" };

                var rawJson = Marshal.PtrToStringUTF8(outJson) ?? "{}";
                mmv2_free_string(outJson);
                return ParseTranscribeResultJson(rawJson);
            }
            finally { handle.Free(); }
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = $"TranscribePcm16 error: {ex.Message}" };
        }
    }

    /// <summary>
    /// CJK文字（日本語・中国語・韓国語）を含む認識テキストの文中スペースを除去する。
    /// CJK文字が含まれない場合（英語など）はそのまま返す。
    /// </summary>
    private static string RemoveCjkIntraSpaces(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // CJK統合漢字・ひらがな・カタカナ・ハングル等を含む場合はスペースを除去
        foreach (char c in text)
        {
            if ((c >= 0x3000 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7FF))
                return text.Replace(" ", "");
        }
        return text;
    }

    private static NativeLibraryResult ParseTranscribeResultJson(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();

            if (ok)
            {
                var transcript = root.TryGetProperty("transcript", out var tp)
                    ? tp.GetString() ?? "" : "";
                var latency = root.TryGetProperty("latencyMs", out var lp)
                    ? lp.GetInt32() : 0;
                var selectedText = root.TryGetProperty("selected_text", out var stp)
                    ? stp.GetString() ?? "" : transcript;
                var selectedEngine = root.TryGetProperty("selected_engine", out var sep)
                    ? sep.GetString() ?? "" : "";
                var decisionReason = root.TryGetProperty("decision_reason", out var drp)
                    ? drp.GetString() ?? "" : "";
                var resultLanguage = root.TryGetProperty("language", out var langp)
                    ? langp.GetString() ?? "" : "";
                var confidenceAvailable = root.TryGetProperty("confidence_available", out var cap)
                    && cap.ValueKind == JsonValueKind.True;
                double? confidenceScore = root.TryGetProperty("confidence_score", out var csp)
                    && csp.ValueKind == JsonValueKind.Number ? csp.GetDouble() : null;
                var confidenceUnavailableReason = root.TryGetProperty("confidence_unavailable_reason", out var curp)
                    ? curp.GetString() ?? "" : "";
                transcript = RemoveCjkIntraSpaces(transcript);
                selectedText = RemoveCjkIntraSpaces(selectedText);
                return new NativeLibraryResult
                {
                    Success = true,
                    Transcript = transcript,
                    SelectedText = selectedText,
                    SelectedEngine = selectedEngine,
                    DecisionReason = decisionReason,
                    Language = resultLanguage,
                    ConfidenceAvailable = confidenceAvailable,
                    ConfidenceScore = confidenceScore,
                    ConfidenceUnavailableReason = confidenceUnavailableReason,
                    LatencyMs = latency,
                    RawJson = rawJson,
                    Message = $"Transcribed in {latency}ms",
                };
            }
            else
            {
                var error = root.TryGetProperty("error", out var ep)
                    ? ep.GetString() ?? "unknown" : "unknown";
                var stage = root.TryGetProperty("stage", out var sp)
                    ? sp.GetString() ?? "unknown" : "unknown";
                return new NativeLibraryResult
                {
                    Success = false,
                    Message = error,
                    ErrorStage = stage,
                    RawJson = rawJson,
                };
            }
        }
        catch (JsonException jex)
        {
            return new NativeLibraryResult
            {
                Success = false,
                Message = $"Failed to parse native JSON: {jex.Message}",
                RawJson = rawJson,
            };
        }
    }

    /// <summary>
    /// Plays PCM16 audio data via native waveOut API.
    /// Returns 0 on success, negative on error.
    /// </summary>
    public static NativeLibraryResult PlayPcm16(
        string nativeExePath,
        short[] samples,
        int sampleRate,
        int channels,
        int monitorVolumePercent = 0)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath))
        {
            return new NativeLibraryResult { Success = false, Message = $"DLL not found: {dllPath}" };
        }

        if (samples == null || samples.Length == 0 || sampleRate <= 0 || channels <= 0)
        {
            return new NativeLibraryResult { Success = false, Message = "Invalid arguments" };
        }

        try
        {
            LoadNativeBridge(nativeExePath);
            var handle = GCHandle.Alloc(samples, GCHandleType.Pinned);
            try
            {
                var ret = mmv2_tts_play_pcm16(handle.AddrOfPinnedObject(), samples.Length, sampleRate, channels, monitorVolumePercent);
                return ret switch
                {
                    0 => new NativeLibraryResult { Success = true, Message = "Playback completed" },
                    -1 => new NativeLibraryResult { Success = false, Message = "Already playing (call StopPcm16 first)" },
                    -2 => new NativeLibraryResult { Success = false, Message = "Invalid arguments" },
                    -3 => new NativeLibraryResult { Success = false, Message = "waveOutOpen failed" },
                    -4 => new NativeLibraryResult { Success = false, Message = "waveOutWrite failed" },
                    _ => new NativeLibraryResult { Success = false, Message = $"Native error: {ret}" }
                };
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = $"Playback error: {ex.Message}" };
        }
    }

    /// <summary>
    /// Stops ongoing TTS PCM16 playback.
    /// Returns 0 on success, -1 if not playing.
    /// </summary>
    public static NativeLibraryResult StopPcm16(string nativeExePath)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath))
        {
            return new NativeLibraryResult { Success = false, Message = $"DLL not found: {dllPath}" };
        }

        try
        {
            LoadNativeBridge(nativeExePath);
            var ret = mmv2_tts_stop();
            return ret == 0
                ? new NativeLibraryResult { Success = true, Message = "Playback stopped" }
                : new NativeLibraryResult { Success = false, Message = "Not playing" };
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = $"Stop error: {ex.Message}" };
        }
    }

    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:   sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ---- SileroVAD P/Invoke ----

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mmv2_vad_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ortDllDir);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern double mmv2_vad_score(IntPtr handle, IntPtr pcm16, int sampleCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mmv2_vad_reset(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mmv2_vad_free(IntPtr handle);

    /// <summary>SileroVAD ハンドルを初期化する。失敗時は IntPtr.Zero。</summary>
    public static IntPtr VadCreate(string nativeExePath, string modelPath, string ortDllDir)
    {
        var dllPath = ResolveDllPath(nativeExePath);
        if (!File.Exists(dllPath)) return IntPtr.Zero;
        try
        {
            LoadNativeBridge(nativeExePath);
            return mmv2_vad_create(modelPath, ortDllDir);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>PCM16LE チャンクを評価し音声確率 [0,1] を返す。エラー時 -1。</summary>
    public static double VadScore(IntPtr handle, byte[] pcm16le)
    {
        if (handle == IntPtr.Zero || pcm16le.Length < 2) return -1.0;
        var samples = new short[pcm16le.Length / 2];
        Buffer.BlockCopy(pcm16le, 0, samples, 0, pcm16le.Length);
        var gh = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try { return mmv2_vad_score(handle, gh.AddrOfPinnedObject(), samples.Length); }
        finally { gh.Free(); }
    }

    /// <summary>LSTM 状態をリセットする（発話境界で呼ぶ）。</summary>
    public static void VadReset(IntPtr handle)
    {
        if (handle != IntPtr.Zero) mmv2_vad_reset(handle);
    }

    /// <summary>ハンドルを解放する。</summary>
    public static void VadFree(IntPtr handle)
    {
        if (handle != IntPtr.Zero) mmv2_vad_free(handle);
    }

    /// <summary>
    /// WAVファイルをフルJSON optionsで推論する。live ASRと同じ transcribe_pcm16_core 経路を使う。
    /// </summary>
    public static NativeLibraryResult TranscribeWavWithOptionsJson(string wavPath, string optionsJson)
    {
        try
        {
            var ret = mmv2_transcribe_wav(wavPath, optionsJson, out IntPtr outJson);
            if (outJson == IntPtr.Zero)
                return new NativeLibraryResult { Success = false, Message = "native returned null" };
            var rawJson = Marshal.PtrToStringUTF8(outJson) ?? "{}";
            mmv2_free_string(outJson);
            return ParseTranscribeResultJson(rawJson);
        }
        catch (Exception ex)
        {
            return new NativeLibraryResult { Success = false, Message = ex.Message };
        }
    }
}
