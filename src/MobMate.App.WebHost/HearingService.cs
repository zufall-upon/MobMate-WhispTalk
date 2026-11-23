using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using MobMate.Core.Asr;
using MobMate.Core.Audio;
using MobMate.Core.Native;
using Microsoft.Extensions.Logging;

// ============================================================
//  ASR バックエンド抽象
// ============================================================

/// <summary>
/// Hearing 用 ASR バックエンド抽象。
/// 16 kHz mono Int16 LE PCM バイト列を受け取りテキストを返す。
/// null = 認識失敗または無音スキップ。
/// </summary>
internal interface IHearingAsrBackend
{
    string EngineName { get; }
    string? Transcribe(byte[] pcm16le, int sampleRate, string lang);
}

// ============================================================
//  WhisperHearingBackend
// ============================================================

/// <summary>
/// mmv2_transcribe_pcm16 を介して Whisper モデルで認識する。
/// WAV ファイルの書き出しは一切行わない。GCHandle.Pinned でサンプルを直接渡す。
/// </summary>
internal sealed class WhisperHearingBackend : IHearingAsrBackend
{
    public string EngineName => "whisper";

    private readonly string _nativeExePath;
    private readonly string _modelPath;
    private readonly string _whisperDllDir;
    private readonly int    _threads;
    private readonly bool   _useGpu;

    public WhisperHearingBackend(
        string nativeExePath, string modelPath, string whisperDllDir,
        int threads = 2, bool useGpu = false)
    {
        _nativeExePath = nativeExePath;
        _modelPath     = modelPath;
        _whisperDllDir = whisperDllDir;
        _threads       = threads;
        _useGpu        = useGpu;
    }

    public string? Transcribe(byte[] pcm16le, int sampleRate, string lang)
    {
        var samples = new short[pcm16le.Length / 2];
        Buffer.BlockCopy(pcm16le, 0, samples, 0, pcm16le.Length);

        var optionsJson = JsonSerializer.Serialize(new
        {
            engine        = "whisper",
            modelPath     = _modelPath,
            whisperDllDir = _whisperDllDir,
            language      = lang == "auto" ? "" : lang,
            threads       = _threads,
            useGpu        = _useGpu,
            usePrefilter  = true,
            scene         = "desktop_hearing",
        });

        var result = NativeLibraryBridge.TranscribePcm16(
            _nativeExePath, samples, sampleRate, optionsJson);

        return result.Success ? result.SelectedText?.Trim() : null;
    }
}

// ============================================================
//  SenseVoiceHearingBackend
// ============================================================

/// <summary>
/// SenseVoiceAsrService で認識する (JA/ZH/KO/EN/YUE のみ)。
/// auto 指定時は "ja" にフォールバック。
/// </summary>
internal sealed class SenseVoiceHearingBackend : IHearingAsrBackend
{
    public string EngineName => "sense_voice";

    private readonly SenseVoiceAsrService _svc;

    public SenseVoiceHearingBackend(SenseVoiceAsrService svc) { _svc = svc; }

    public string? Transcribe(byte[] pcm16le, int sampleRate, string lang)
    {
        var floats = ConvertInt16ToFloat(pcm16le);
        var svLang = lang == "auto" ? "ja" : lang;
        return _svc.Transcribe(floats, sampleRate, svLang);
    }

    private static float[] ConvertInt16ToFloat(byte[] pcm16le)
    {
        var samples = new float[pcm16le.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm16le, i * 2) / 32768.0f;
        return samples;
    }
}

// ============================================================
//  JointAutoHearingBackend
// ============================================================

/// <summary>
/// Whisper と SenseVoice を並列実行し、品質が高い方（長い方）を選択する。
/// 両方失敗なら null。将来のエンジン追加は _candidates リストに追加するだけ。
/// </summary>
internal sealed class JointAutoHearingBackend : IHearingAsrBackend
{
    public string EngineName => "joint_auto";

    private readonly IReadOnlyList<IHearingAsrBackend> _candidates;

    public JointAutoHearingBackend(IReadOnlyList<IHearingAsrBackend> candidates)
    {
        _candidates = candidates;
    }

    public string? Transcribe(byte[] pcm16le, int sampleRate, string lang)
    {
        return _candidates
            .AsParallel()
            .Select(b =>
            {
                try { return b.Transcribe(pcm16le, sampleRate, lang); }
                catch { return null; }
            })
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .OrderByDescending(r => r!.EnumerateRunes().Count())
            .FirstOrDefault();
    }
}

// ============================================================
//  設定・状態型
// ============================================================

public sealed class HearingConfig
{
    public string Engine        { get; set; } = "joint_auto";
    public string Lang          { get; set; } = "auto";
    public string TranslateTo   { get; set; } = "OFF";
    public int    ChunkMergeMs  { get; set; } = 2000;
    public string AudioMode     { get; set; } = "loopback";
    public string MicDeviceHint { get; set; } = "";
    public int    OverlayFontSize        { get; set; } = 36;
    public string OverlayBackground      { get; set; } = "blue";
    public int    OverlayOpacity         { get; set; } = 72;
    public int    OverlayHistory         { get; set; } = 6;
    public int    OverlayDoneKeepSeconds { get; set; } = 12;
    public string OverlayPosition        { get; set; } = "custom";
    public string OverlayFlow            { get; set; } = "horizontalRight";
}

public sealed class HearingStatus
{
    public bool    Running        { get; set; }
    public string  SourceText     { get; set; } = "";
    public string  TranslatedText { get; set; } = "";
    public string  SelectedEngine { get; set; } = "";
    public long    UpdatedAtMs    { get; set; }
    public int     HistoryCount   { get; set; }
    public string? Error          { get; set; }
}

public sealed class HearingHistoryItem
{
    public long   AtMs           { get; set; }
    public string SourceText     { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    public string SelectedEngine { get; set; } = "";
}

// ============================================================
//  HearingService
// ============================================================

public sealed class HearingService : IDisposable
{
    // --- 依存 ---
    private readonly string               _nativeExePath;
    private readonly string               _whisperModelPath;
    private readonly string               _whisperDllDir;
    private readonly int                  _whisperThreads;
    private readonly bool                 _whisperUseGpu;
    private readonly SenseVoiceAsrService? _senseVoice;
    private readonly TranslationService?  _translation;
    private readonly ILogger              _logger;
    private readonly AddonOperationGate? _addonOperationGate;

    // --- 状態 ---
    private HearingConfig _config = new();
    private volatile bool _running;
    private volatile bool _intentionalStop;
    private volatile string _lastSource = "";
    private volatile string _lastTranslated = "";
    private volatile string _lastEngine = "";
    private long   _lastUpdateMs;  // _lock 保護
    private readonly Queue<HearingHistoryItem> _history = new(20);
    private readonly object _lock = new();
    private readonly object _lifecycleLock = new();
    private string? _lastError;

    // --- プロセス管理 ---
    private Process? _loopbackProc;
    private Thread?  _loopbackThread;
    private IntPtr   _loopbackJob = IntPtr.Zero;
    private int      _crashCount;
    private long     _firstCrashMs;

    // --- PCM 蓄積 ---
    private readonly MemoryStream _pcmAcc = new();
    private int _lastPeak;
    private int _gateRejectStreak;

    // --- VAD 自動チャンク ---
    private bool _vadActive        = false;
    private int  _vadSilenceFrames = 0;

    // --- ASR 並列化防止 (native DLL はスレッドセーフでないため1並列に制限) ---
    private readonly SemaphoreSlim _asrSemaphore = new(1, 1);

    // --- SileroVAD ハンドル (IntPtr.Zero = 未初期化/失敗) ---
    private IntPtr _sileroVad = IntPtr.Zero;

    // --- 短文重複抑制 ---
    private readonly Dictionary<string, long> _recentShortCaptions = new();

    // --- バックエンドインスタンス (Start時に確定) ---
    private IHearingAsrBackend? _currentBackend;
    private int _disposed;

    // --- 定数 ---
    private const double VOICE_GATE_MIN_RMS  = 200.0;
    private const int    VOICE_GATE_MIN_PEAK = 600;
    private const int    GATE_REJECT_MAX     = 3;
    private const int    MAX_HISTORY         = 20;
    private const int    WASAPI_MAX_RETRIES  = 5;
    private const long   WASAPI_CRASH_WINDOW = 15_000;
    private const long   SHORT_REPEAT_TTL    = 2_500;
    private const int    SHORT_REPEAT_MAX_CP = 8;
    private const int    VAD_SILENCE_FRAMES   = 15;         // 15 × 200ms = 3000ms 無音でフラッシュ
    private const int    VAD_MAX_WINDOW_BYTES = 8000 * 16000 * 2 / 1000; // 最大 8秒バッファ

    public HearingService(
        string nativeExePath,
        string whisperModelPath,
        string whisperDllDir,
        int whisperThreads,
        SenseVoiceAsrService? senseVoice,
        TranslationService? translation,
        ILogger logger,
        bool whisperUseGpu = false,
        string? sileroVadModelPath = null,
        string? onnxRuntimeDllDir  = null,
        AddonOperationGate? addonOperationGate = null)
    {
        _nativeExePath    = nativeExePath;
        _whisperModelPath = whisperModelPath;
        _whisperDllDir    = whisperDllDir;
        _whisperThreads   = whisperThreads;
        _whisperUseGpu    = whisperUseGpu;
        _senseVoice       = senseVoice;
        _translation      = translation;
        _logger           = logger;
        _addonOperationGate = addonOperationGate;

        // SileroVAD 初期化 (失敗してもRMSフォールバックで動作)
        if (!string.IsNullOrEmpty(sileroVadModelPath) && !string.IsNullOrEmpty(onnxRuntimeDllDir))
        {
            _sileroVad = NativeLibraryBridge.VadCreate(nativeExePath, sileroVadModelPath, onnxRuntimeDllDir);
            if (_sileroVad == IntPtr.Zero)
                _logger.LogWarning("[Hearing] SileroVAD 初期化失敗 → RMS フォールバック");
            else
                _logger.LogInformation("[Hearing] SileroVAD 初期化成功");
        }
    }

    // ---- 公開 API ----

    public HearingConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    public void SetConfig(HearingConfig config)
    {
        lock (_lock) _config = config;
    }

    public void Start(IDisposable? workLease = null)
    {
        lock (_lifecycleLock) StartLocked(workLease);
    }

    private void StartLocked(IDisposable? workLease)
    {
        var started = false;
        var markedRunning = false;
        try
        {
            lock (_lock)
            {
                if (_running) return;
                _lastError = null;
            }
            if (_loopbackThread?.IsAlive == true) throw new InvalidOperationException("Previous hearing session is still stopping");

            var cfg = GetConfig();

            // MobMateLoopbackPcm.exe の存在確認
            var loopbackExe = Path.Combine(
                Path.GetDirectoryName(_nativeExePath) ?? ".",
                "..",
                "MobMateLoopbackPcm.exe");
            loopbackExe = Path.GetFullPath(loopbackExe);

            if (!File.Exists(loopbackExe))
            {
                var err = $"MobMateLoopbackPcm.exe が見つかりません: {loopbackExe}";
                lock (_lock) { _lastError = err; }
                throw new InvalidOperationException(err);
            }

            _currentBackend = BuildBackend(cfg);
            _intentionalStop = false;
            _running = true;
            markedRunning = true;
            _crashCount = 0;
            _firstCrashMs = 0;

            _loopbackThread = new Thread(() =>
            {
                try { LoopbackLoop(loopbackExe, cfg); }
                catch (Exception ex)
                {
                    _running = false;
                    if (!_intentionalStop)
                    {
                        lock (_lock) { _lastError = ex.Message; }
                        _logger.LogWarning(ex, "[Hearing] loopback worker stopped unexpectedly");
                    }
                }
                finally { workLease?.Dispose(); }
            })
            {
                IsBackground = true,
                Name = "HearingLoopback",
            };
            _loopbackThread.Start();
            started = true;
            _logger.LogInformation("[Hearing] 開始 engine={Engine} lang={Lang} audioMode={Mode}",
                cfg.Engine, cfg.Lang, cfg.AudioMode);
        }
        finally
        {
            if (!started)
            {
                if (markedRunning) _running = false;
                workLease?.Dispose();
            }
        }
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            StopInternal();
        }
    }

    private void StopInternal()
    {
        _intentionalStop = true;
        _running = false;
        KillLoopbackProcess();
        try { _loopbackThread?.Join(3000); } catch { }
        if (_loopbackThread?.IsAlive != true) _loopbackThread = null;
        KillOrphanLoopbackProcesses();
        _logger.LogInformation("[Hearing] 停止");
    }

    public HearingStatus GetStatus()
    {
        lock (_lock)
        {
            return new HearingStatus
            {
                Running        = _running,
                SourceText     = _lastSource,
                TranslatedText = _lastTranslated,
                SelectedEngine = _lastEngine,
                UpdatedAtMs    = _lastUpdateMs,
                HistoryCount   = _history.Count,
                Error          = _lastError,
            };
        }
    }

    public IReadOnlyList<HearingHistoryItem> GetHistory(int max = 20)
    {
        lock (_lock)
        {
            return _history.TakeLast(Math.Min(max, MAX_HISTORY)).ToList();
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopInternal();
        _pcmAcc.Dispose();
        _asrSemaphore.Dispose();
        NativeLibraryBridge.VadFree(_sileroVad);
        _sileroVad = IntPtr.Zero;
        }
    }

    // ---- バックエンド選択 ----

    private IHearingAsrBackend BuildBackend(HearingConfig cfg)
    {
        var whisper = new WhisperHearingBackend(
            _nativeExePath, _whisperModelPath, _whisperDllDir,
            _whisperThreads, _whisperUseGpu);

        return cfg.Engine switch
        {
            "whisper"     => whisper,
            "sense_voice" => _senseVoice != null
                             ? new SenseVoiceHearingBackend(_senseVoice)
                             : whisper,
            _             => // "joint_auto" および未知のエンジン名
                             _senseVoice != null
                             ? new JointAutoHearingBackend([whisper, new SenseVoiceHearingBackend(_senseVoice)])
                             : whisper,
        };
    }

    // ---- ループバックプロセス管理 ----

    private void LoopbackLoop(string loopbackExe, HearingConfig cfg)
    {
        while (_running)
        {
            try
            {
                StartLoopbackProcess(loopbackExe, cfg);
                ReadLoopbackOutput();
            }
            catch (Exception ex) when (!_intentionalStop)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (now - _firstCrashMs > WASAPI_CRASH_WINDOW)
                {
                    _crashCount = 0;
                    _firstCrashMs = now;
                }
                if (++_crashCount > WASAPI_MAX_RETRIES)
                {
                    _logger.LogError(ex, "[Hearing] WASAPI helper クラッシュ上限超過、停止");
                    _running = false;
                    lock (_lock) { _lastError = "WASAPI helper クラッシュ上限超過"; }
                    return;
                }
                _logger.LogWarning("[Hearing] WASAPI helper クラッシュ #{Count}、500ms 後再起動", _crashCount);
                Thread.Sleep(500);
            }
        }
    }

    private void StartLoopbackProcess(string loopbackExe, HearingConfig cfg)
    {
        KillLoopbackProcess();

        var args = cfg.AudioMode == "mic"
            ? $"--flow capture --device \"{cfg.MicDeviceHint}\" --rate 16000 --chunkms 200"
            : "--flow render --rate 16000 --chunkms 200";

        _loopbackProc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = loopbackExe,
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = false,
                CreateNoWindow         = true,
            },
        };
        _loopbackProc.Start();
        AttachLoopbackToKillOnCloseJob();
        _logger.LogInformation("[Hearing] MobMateLoopbackPcm.exe 起動 pid={Pid} args={Args}",
            _loopbackProc.Id, args);
    }

    private void ReadLoopbackOutput()
    {
        if (_loopbackProc == null) return;
        var stdout = _loopbackProc.StandardOutput;
        var cfg    = GetConfig();

        while (_running && !stdout.EndOfStream)
        {
            var line = stdout.ReadLine();
            if (line == null) break;

            if (line.StartsWith("PEAK ", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(5), out var peak))
            {
                _lastPeak = peak;
            }
            else if (line.StartsWith("PCM ", StringComparison.Ordinal))
            {
                try
                {
                    var b64 = line.AsSpan(4);
                    var chunk = Convert.FromBase64String(b64.ToString());
                    _pcmAcc.Write(chunk);

                    // VAD: 200ms チャンク単位で音声/無音を判定し、無音終端でフラッシュ
                    // SileroVAD (初期化済みの場合) または RMS+Peak フォールバック
                    bool isVoice = IsVoiceChunk(chunk);
                    if (isVoice)
                    {
                        _vadActive = true;
                        _vadSilenceFrames = 0;
                    }
                    else if (_vadActive)
                    {
                        _vadSilenceFrames++;
                    }

                    bool silenceFlush = _vadActive && _vadSilenceFrames >= VAD_SILENCE_FRAMES;
                    bool maxFlush     = _pcmAcc.Length >= VAD_MAX_WINDOW_BYTES;

                    if (silenceFlush)
                    {
                        var pcmBytes = _pcmAcc.ToArray();
                        _pcmAcc.SetLength(0);
                        _vadActive = false;
                        _vadSilenceFrames = 0;
                        NativeLibraryBridge.VadReset(_sileroVad); // 発話終端: LSTM リセット
                        DispatchAsr(pcmBytes, cfg);
                    }
                    else if (maxFlush)
                    {
                        var pcmBytes = _vadActive ? _pcmAcc.ToArray() : null;
                        _pcmAcc.SetLength(0);
                        _vadActive = false;
                        _vadSilenceFrames = 0;
                        NativeLibraryBridge.VadReset(_sileroVad);
                        if (pcmBytes != null)
                            DispatchAsr(pcmBytes, cfg);
                        else
                            _logger.LogDebug("[Hearing] VAD 最大バッファ到達 (無音) → 破棄");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Hearing] PCM decode error");
                }
            }
        }
    }

    private void AttachLoopbackToKillOnCloseJob()
    {
        if (_loopbackProc == null || !OperatingSystem.IsWindows()) return;

        CloseLoopbackJob();
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            _logger.LogWarning("[Hearing] JobObject 作成失敗 error={Error}", Marshal.GetLastWin32Error());
            return;
        }

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };
        var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, ptr, (uint)size))
            {
                _logger.LogWarning("[Hearing] JobObject 設定失敗 error={Error}", Marshal.GetLastWin32Error());
                NativeMethods.CloseHandle(job);
                return;
            }
            if (!NativeMethods.AssignProcessToJobObject(job, _loopbackProc.Handle))
            {
                _logger.LogWarning("[Hearing] MobMateLoopbackPcm.exe JobObject 登録失敗 pid={Pid} error={Error}", _loopbackProc.Id, Marshal.GetLastWin32Error());
                NativeMethods.CloseHandle(job);
                return;
            }
            _loopbackJob = job;
            _logger.LogInformation("[Hearing] MobMateLoopbackPcm.exe JobObject 登録 pid={Pid}", _loopbackProc.Id);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private void CloseLoopbackJob()
    {
        if (_loopbackJob == IntPtr.Zero) return;
        try { NativeMethods.CloseHandle(_loopbackJob); } catch { }
        _loopbackJob = IntPtr.Zero;
    }

    private void KillLoopbackProcess()
    {
        try
        {
            if (_loopbackProc is { HasExited: false })
                _loopbackProc.Kill(entireProcessTree: true);
        }
        catch { }
        try { _loopbackProc?.WaitForExit(2000); } catch { }
        CloseLoopbackJob();
        _loopbackProc?.Dispose();
        _loopbackProc = null;
        _pcmAcc.SetLength(0);
        _lastPeak = 0;
        _gateRejectStreak = 0;
        _vadActive = false;
        _vadSilenceFrames = 0;
        NativeLibraryBridge.VadReset(_sileroVad); // LSTM状態リセット
    }

    private void KillOrphanLoopbackProcesses()
    {
        foreach (var proc in Process.GetProcessesByName("MobMateLoopbackPcm"))
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
            try { proc.Dispose(); } catch { }
        }
    }

    // ---- ASR + 翻訳 ----

    private void DispatchAsr(byte[] pcmBytes, HearingConfig cfg)
    {
        var backend = _currentBackend!;
        var workLease = _addonOperationGate?.TryEnterWork();
        if (_addonOperationGate is not null && workLease is null) return;
        try
        {
        _ = Task.Run(() =>
        {
            try
            {
            // native DLL はスレッドセーフでないため、同時実行を 1 に制限
            if (!_asrSemaphore.Wait(0))
            {
                _logger.LogDebug("[Hearing] ASR busy → chunk skipped");
                return;
            }
            try
            {
                var gatedPcm = HearingNoiseGate.ApplyPoc17(pcmBytes, 16000);
                var text = backend.Transcribe(gatedPcm, 16000, cfg.Lang)?.Trim();
                if (string.IsNullOrEmpty(text)) return;
                if (ShouldSuppressShortRepeat(text)) return;

                string translated = "";
                if (cfg.TranslateTo != "OFF" && _translation != null && _translation.IsLoaded)
                {
                    var srcLang = cfg.Lang == "auto" ? "ja" : cfg.Lang;
                    translated = _translation.Translate(text, srcLang, cfg.TranslateTo) ?? "";
                }

                PushHistory(text, translated, backend.EngineName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Hearing] ASR dispatch error");
            }
            finally
            {
                _asrSemaphore.Release();
            }
            }
            finally { workLease?.Dispose(); }
        });
        }
        catch { workLease?.Dispose(); throw; }
    }

    private void PushHistory(string source, string translated, string engine)
    {
        lock (_lock)
        {
            _lastSource     = source;
            _lastTranslated = translated;
            _lastEngine     = engine;
            _lastUpdateMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _history.Enqueue(new HearingHistoryItem
            {
                AtMs           = _lastUpdateMs,
                SourceText     = source,
                TranslatedText = translated,
                SelectedEngine = engine,
            });
            while (_history.Count > MAX_HISTORY)
                _history.Dequeue();
        }
        _logger.LogInformation("[Hearing] [{Engine}] {Source}", engine, source);
    }

    // ---- VoiceGate ----

    /// <summary>
    /// VAD 判定。SileroVAD が初期化済みならそちらを優先、
    /// 初期化失敗時は RMS+Peak フォールバック。
    /// </summary>
    private bool IsVoiceChunk(byte[] chunk)
    {
        var rmsGate = VoiceGate(chunk, _lastPeak, VOICE_GATE_MIN_RMS, VOICE_GATE_MIN_PEAK);
        if (_sileroVad != IntPtr.Zero)
        {
            double score = NativeLibraryBridge.VadScore(_sileroVad, chunk);
            if (score >= 0) return score >= 0.42 || rmsGate; // Silero start threshold + RMS rescue
            // score < 0 → エラー: RMSフォールバック
        }
        return rmsGate;
    }

    private static bool VoiceGate(byte[] pcm16le, int peak, double minRms, int minPeak)
    {
        return CalcRms16Le(pcm16le) >= minRms || peak >= minPeak;
    }

    private static double CalcRms16Le(byte[] pcm16le)
    {
        if (pcm16le.Length < 2) return 0;
        long sum = 0;
        int count = pcm16le.Length / 2;
        for (int i = 0; i < count; i++)
        {
            var s = BitConverter.ToInt16(pcm16le, i * 2);
            sum += (long)s * s;
        }
        return Math.Sqrt((double)sum / count);
    }

    // ---- 短文重複抑制 ----

    private bool ShouldSuppressShortRepeat(string text)
    {
        var key = NormalizeKey(text);
        int cp = key.EnumerateRunes().Count();
        if (cp == 0 || cp > SHORT_REPEAT_MAX_CP) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_recentShortCaptions)
        {
            var staleKeys = _recentShortCaptions
                .Where(kv => now - kv.Value > SHORT_REPEAT_TTL)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in staleKeys) _recentShortCaptions.Remove(k);
            bool dup = _recentShortCaptions.ContainsKey(key);
            _recentShortCaptions[key] = now;
            return dup;
        }
    }

    private static string NormalizeKey(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[\s\p{P}、。！？「」（）\[\]…・〜ー]+", "");

    private static class NativeMethods
    {
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        internal const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
