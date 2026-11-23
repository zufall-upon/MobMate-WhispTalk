using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MobMate.Core.Asr;
using MobMate.Core.Audio;
using MobMate.Core.Native;
using Microsoft.Extensions.Logging;

public sealed class LinuxHearingService : IDisposable
{
    private readonly string _nativeExePath;
    private readonly string _whisperModelPath;
    private readonly string _whisperDllDir;
    private readonly bool _whisperUseGpu;
    private readonly SenseVoiceAsrService? _senseVoice;
    private readonly TranslationService? _translation;
    private readonly ILogger _logger;
    private readonly Action<LinuxHearingConfig> _saveConfig;
    private readonly object _lock = new();
    private readonly Queue<LinuxHearingHistoryItem> _history = new();
    private readonly SemaphoreSlim _asrGate = new(1, 1);
    private readonly Dictionary<string, long> _recentShortCaptions = new();

    private LinuxHearingConfig _config;
    private LinuxHearingStatus _status = new();
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Process? _captureProc;
    private int _disposed;

    // SileroVAD
    private readonly IntPtr _sileroVad;

    // PCM accumulation + VAD state
    private readonly MemoryStream _pcmAcc = new();
    private bool _vadActive;
    private int _vadSilenceFrames;
    private int _lastPeak;

    private const int SampleRate       = 16000;
    private const int BytesPerSample   = 2;
    private const int FrameMs          = 200;
    private const int FrameBytes       = SampleRate * BytesPerSample * FrameMs / 1000; // 6400
    private const int VadSilenceFrames = 15;         // 15 × 200ms = 3000ms → flush
    private const int VadMaxWindowBytes = 8000 * SampleRate * BytesPerSample / 1000;   // ~8秒
    private const double VoiceGateMinRms  = 200.0;
    private const int    VoiceGateMinPeak = 600;
    private const int    MaxHistory        = 20;
    private const int    ShortRepeatMaxCp  = 8;
    private const long   ShortRepeatTtl   = 2_500;

    public LinuxHearingService(
        LinuxHearingConfig initialConfig,
        Action<LinuxHearingConfig> saveConfig,
        string nativeExePath,
        string whisperModelPath,
        string whisperDllDir,
        bool whisperUseGpu,
        SenseVoiceAsrService? senseVoice,
        TranslationService? translation,
        ILogger logger,
        string? sileroVadModelPath = null,
        string? onnxRuntimeDllDir  = null)
    {
        _config          = initialConfig;
        _saveConfig      = saveConfig;
        _nativeExePath   = nativeExePath;
        _whisperModelPath = whisperModelPath;
        _whisperDllDir   = whisperDllDir;
        _whisperUseGpu   = whisperUseGpu;
        _senseVoice      = senseVoice;
        _translation     = translation;
        _logger          = logger;
        _status          = new LinuxHearingStatus { SelectedEngine = _config.Engine };

        // SileroVAD 初期化 (失敗してもRMSフォールバックで動作)
        if (!string.IsNullOrEmpty(sileroVadModelPath) && !string.IsNullOrEmpty(onnxRuntimeDllDir))
        {
            _sileroVad = NativeLibraryBridge.VadCreate(nativeExePath, sileroVadModelPath, onnxRuntimeDllDir);
            if (_sileroVad == IntPtr.Zero)
                _logger.LogWarning("[Hearing] SileroVAD 初期化失敗 → RMS フォールバック");
            else
                _logger.LogInformation("[Hearing] SileroVAD 初期化成功");
        }
        else
        {
            _logger.LogInformation("[Hearing] SileroVAD path not configured → RMS フォールバック");
        }
    }

    public LinuxHearingConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    public void SetConfig(LinuxHearingConfig config)
    {
        lock (_lock)
        {
            _config = config;
            _status.SelectedEngine = config.Engine;
        }
        _saveConfig(config);
    }

    public LinuxHearingStatus GetStatus()
    {
        lock (_lock)
        {
            _status.HistoryCount = _history.Count;
            return new LinuxHearingStatus
            {
                Running        = _status.Running,
                SourceText     = _status.SourceText,
                TranslatedText = _status.TranslatedText,
                SelectedEngine = _status.SelectedEngine,
                UpdatedAtMs    = _status.UpdatedAtMs,
                HistoryCount   = _status.HistoryCount,
                Error          = _status.Error,
            };
        }
    }

    public IReadOnlyList<LinuxHearingHistoryItem> GetHistory(int max = 20)
    {
        lock (_lock) return _history.TakeLast(Math.Clamp(max, 1, MaxHistory)).ToArray();
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_lock)
        {
            if (_status.Running) return;
            _status.Running = true;
            _status.Error   = null;
            _status.SelectedEngine = _config.Engine;
        }
        _cts         = new CancellationTokenSource();
        _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        _logger.LogInformation("[Hearing] Linux capture started engine={Engine} lang={Lang} mode={Mode} source={Source}",
            _config.Engine, _config.Lang, _config.AudioMode,
            string.IsNullOrWhiteSpace(_config.MicDeviceHint) ? "(default)" : _config.MicDeviceHint);
    }

    public void Stop()
    {
        StopInternal();
    }

    private void StopInternal()
    {
        lock (_lock) _status.Running = false;
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_captureProc is { HasExited: false }) _captureProc.Kill(entireProcessTree: true);
        }
        catch { }
        try { _captureTask?.Wait(2000); } catch { }
        _captureTask = null;
        _cts?.Dispose();
        _cts = null;
        _captureProc?.Dispose();
        _captureProc = null;
        ResetVadState();
        _logger.LogInformation("[Hearing] Linux capture stopped");
    }

    private void ResetVadState()
    {
        _pcmAcc.SetLength(0);
        _vadActive        = false;
        _vadSilenceFrames = 0;
        _lastPeak         = 0;
        NativeLibraryBridge.VadReset(_sileroVad);
    }

    // 死リンク(dead monitor link)対策の自己修復パラメータ。
    // PipeWire は sink の format 再ネゴ等で monitor node を再生成することがあり、
    // hearing-start 時に起動した pw-record が古い node に張り付いたまま
    // 純無音(全ゼロ PCM)を出し続けるケースがある(fresh プロセスは正常)。
    // 直近 DeadLinkSilenceFrames フレームが連続して「全ゼロ同然」なら recorder を再起動する。
    // 実音声中(非ゼロが届く)は発火せず、本物の無音中は無害な定期再接続になる。
    private const int DeadLinkPeakThreshold = 24;   // これ未満のピークは実質デジタル無音
    private const int DeadLinkSilenceFrames = 40;   // 40 × 200ms = 8秒 連続無音で再接続

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        try
        {
            var recorder = ResolveRecorder();
            var frameBuffer = new byte[FrameBytes];
            var restarts = 0;

            // recorder を起動し直せるよう外側ループで包む。
            while (!ct.IsCancellationRequested)
            {
                var cfg = GetConfig();
                var psi = BuildRecorderPsi(recorder, cfg);

                _captureProc = Process.Start(psi);
                if (_captureProc == null) throw new InvalidOperationException($"{recorder} failed to start");

                var stream = _captureProc.StandardOutput.BaseStream;
                ResetVadState();
                var framesSinceNonZero = 0;
                var deadLink = false;

                while (!ct.IsCancellationRequested)
                {
                    var read = await ReadFullChunkAsync(stream, frameBuffer, ct);
                    if (read <= 0) break;                 // EOF → recorder 終了
                    if (read < FrameBytes) continue;

                    // 死リンク検知: 連続無音フレーム数を数える。
                    if (PeakAbs16Le(frameBuffer) >= DeadLinkPeakThreshold)
                        framesSinceNonZero = 0;
                    else if (++framesSinceNonZero >= DeadLinkSilenceFrames)
                    {
                        deadLink = true;
                        break;                            // recorder を再起動する
                    }

                    _pcmAcc.Write(frameBuffer, 0, FrameBytes);

                    bool isVoice = IsVoiceChunk(frameBuffer);
                    if (isVoice)
                    {
                        _vadActive        = true;
                        _vadSilenceFrames = 0;
                    }
                    else if (_vadActive)
                    {
                        _vadSilenceFrames++;
                    }

                    bool silenceFlush = _vadActive && _vadSilenceFrames >= VadSilenceFrames;
                    bool maxFlush     = _pcmAcc.Length >= VadMaxWindowBytes;

                    if (silenceFlush)
                    {
                        var pcmBytes = _pcmAcc.ToArray();
                        ResetVadState();
                        DispatchAsr(pcmBytes, GetConfig());
                    }
                    else if (maxFlush)
                    {
                        var pcmBytes = _vadActive ? _pcmAcc.ToArray() : null;
                        ResetVadState();
                        if (pcmBytes != null)
                            DispatchAsr(pcmBytes, GetConfig());
                        else
                            _logger.LogDebug("[Hearing] VAD 最大バッファ到達 (無音) → 破棄");
                    }
                }

                // recorder 後始末
                try { if (_captureProc is { HasExited: false }) _captureProc.Kill(true); } catch { }
                try { _captureProc?.Dispose(); } catch { }
                _captureProc = null;

                if (ct.IsCancellationRequested) break;

                if (deadLink)
                {
                    restarts++;
                    _logger.LogWarning(
                        "[Hearing] capture link silent for {Sec}s → restarting {Recorder} (attempt {N})",
                        DeadLinkSilenceFrames * FrameMs / 1000, Path.GetFileName(recorder), restarts);
                    try { await Task.Delay(800, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    continue;                              // recorder 再起動
                }

                // EOF: recorder が予期せず終了 → 一旦再起動を試みる(数回まで)
                if (restarts < 5)
                {
                    restarts++;
                    _logger.LogWarning("[Hearing] {Recorder} exited → restarting (attempt {N})", Path.GetFileName(recorder), restarts);
                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    continue;
                }
                SetError($"{Path.GetFileName(recorder)} exited unexpectedly");
                break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Hearing] Linux capture failed");
            SetError(ex.Message);
        }
        finally
        {
            lock (_lock) _status.Running = false;
        }
    }

    private ProcessStartInfo BuildRecorderPsi(string recorder, LinuxHearingConfig cfg)
    {
        var psi = new ProcessStartInfo(recorder)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,
            CreateNoWindow         = true,
        };
        // When using the bundled parec (absolute path inside libs/pactl/linux-x64/), prepend
        // its directory to LD_LIBRARY_PATH so it finds the co-located libpulse.so.0.
        if (Path.IsPathRooted(recorder) && recorder.Contains(Path.Combine("pactl", "linux-x64")))
        {
            var libDir = Path.GetDirectoryName(recorder) ?? string.Empty;
            if (!string.IsNullOrEmpty(libDir))
            {
                var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing)
                    ? libDir : $"{libDir}:{existing}";
            }
        }
        if (Path.GetFileName(recorder).Equals("pw-record", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--raw");
            psi.ArgumentList.Add("--format=s16");
            psi.ArgumentList.Add("--rate=16000");
            psi.ArgumentList.Add("--channels=1");
            psi.ArgumentList.Add("--latency=200ms");
            var hint = cfg.MicDeviceHint;
            if (string.IsNullOrWhiteSpace(hint) && cfg.AudioMode == "loopback")
            {
                // pw-record は sink の node.name や "<name>.monitor" を --target に渡すと
                // 環境によって default source(マイク)へ fallback する。Ubuntu/PipeWire では
                // @DEFAULT_SINK@ の node id を渡すと playback monitor に正しく接続される。
                var sinkId = TryDetectDefaultSinkId();
                if (!string.IsNullOrWhiteSpace(sinkId))
                {
                    _logger.LogInformation("[Hearing] Auto-detected sink monitor target id: {Target}", sinkId);
                    hint = sinkId;
                }
            }
            if (!string.IsNullOrWhiteSpace(hint))
            {
                psi.ArgumentList.Add("--target");
                psi.ArgumentList.Add(hint);
            }
            psi.ArgumentList.Add("-");
        }
        else
        {
            psi.ArgumentList.Add("--raw");
            psi.ArgumentList.Add("--format=s16le");
            psi.ArgumentList.Add("--rate=16000");
            psi.ArgumentList.Add("--channels=1");
            psi.ArgumentList.Add("--latency-msec=200");
            if (!string.IsNullOrWhiteSpace(cfg.MicDeviceHint))
            {
                psi.ArgumentList.Add("--device=" + cfg.MicDeviceHint);
            }
            else if (cfg.AudioMode == "loopback")
            {
                // loopback mode: parec without --device defaults to the default SOURCE (microphone).
                // Use --device=<sink_name>.monitor to capture system playback instead.
                // This works on both Bazzite (PipeWire+PulseAudio compat) and Ubuntu.
                var sinkName = TryDetectDefaultSinkName();
                if (!string.IsNullOrWhiteSpace(sinkName))
                {
                    var monitorDevice = sinkName + ".monitor";
                    _logger.LogInformation("[Hearing] parec loopback: using monitor device {Device}", monitorDevice);
                    psi.ArgumentList.Add("--device=" + monitorDevice);
                }
            }
        }
        return psi;
    }

    private static int PeakAbs16Le(byte[] pcm16le)
    {
        int peak = 0;
        var count = pcm16le.Length / 2;
        for (var i = 0; i < count; i++)
        {
            int s = BitConverter.ToInt16(pcm16le, i * 2);
            if (s < 0) s = -s;
            if (s > peak) peak = s;
        }
        return peak;
    }

    private static string ResolveRecorder()
    {
        if (CommandExists("parec")) return "parec";
        // Check bundled parec in libs/pactl/linux-x64/ (same dir as bundled pactl + libpulse).
        var bundled = ResolveBundledParecPath();
        if (!string.IsNullOrEmpty(bundled)) return bundled;
        if (CommandExists("pw-record")) return "pw-record";
        return "parec";
    }

    private static string ResolveBundledParecPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "libs", "pactl", "linux-x64", "parec");
        return File.Exists(candidate) ? candidate : string.Empty;
    }

    /// <summary>
    /// wpctl inspect @DEFAULT_SINK@ を実行し既定 sink の node id を返す。
    /// 失敗時は null。
    /// </summary>
    private static string? TryDetectDefaultSinkId()
    {
        if (!CommandExists("wpctl")) return null;
        try
        {
            var psi = new ProcessStartInfo("wpctl", "inspect @DEFAULT_SINK@")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            var m = Regex.Match(output, @"^id\s+(\d+)\b", RegexOptions.Multiline);
            if (m.Success) return m.Groups[1].Value;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 既定 sink の node.name (例: alsa_output.pci-0000_c8_00.6.analog-stereo) を返す。
    /// parec --device=&lt;name&gt;.monitor のデバイス名として使う。失敗時は null。
    /// pactl があれば "pactl info" を優先。なければ "wpctl inspect @DEFAULT_SINK@" にフォールバック。
    /// </summary>
    private static string ResolveBundledPactlPath()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "libs", "pactl", "linux-x64", "pactl");
        return File.Exists(candidate) ? candidate : string.Empty;
    }

    private static string? TryDetectDefaultSinkName()
    {
        // 1. pactl info → "Default Sink: <name>" (Bazzite/Fedora, and Steam pressure-vessel).
        // Prefer system pactl; fall back to the bundled pactl (co-located libpulse) because the
        // Steam runtime container does not expose system pactl/wpctl on PATH.
        var pactl = CommandExists("pactl") ? "pactl" : ResolveBundledPactlPath();
        if (!string.IsNullOrEmpty(pactl))
        {
            try
            {
                var psi = new ProcessStartInfo(pactl, "info")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                if (Path.IsPathRooted(pactl) && pactl.Contains(Path.Combine("pactl", "linux-x64")))
                {
                    var libDir = Path.GetDirectoryName(pactl) ?? string.Empty;
                    if (!string.IsNullOrEmpty(libDir))
                    {
                        var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
                        psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? libDir : $"{libDir}:{existing}";
                    }
                }
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    var m = Regex.Match(output, @"^Default Sink:\s*(\S+)", RegexOptions.Multiline);
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
        }

        // 2. wpctl inspect @DEFAULT_SINK@ → node.name = "..." (Ubuntu/no-pactl)
        if (CommandExists("wpctl"))
        {
            try
            {
                var psi = new ProcessStartInfo("wpctl", "inspect @DEFAULT_SINK@")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    var m = Regex.Match(output, @"node\.name\s*=\s*""([^""]+)""");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
        }

        return null;
    }

    private static bool CommandExists(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, name))) return true;
            }
            catch { }
        }
        return false;
    }

    private static async Task<int> ReadFullChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length && !ct.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// VAD判定。Linux loopback では PipeWire monitor 音が強くても Silero が no-voice
    /// を返すことがあるため、Silero true または RMS/Peak gate true で voice 扱いにする。
    /// </summary>
    private bool IsVoiceChunk(byte[] chunk)
    {
        var rmsGate = CalcRms16Le(chunk) >= VoiceGateMinRms || _lastPeak >= VoiceGateMinPeak;
        if (_sileroVad != IntPtr.Zero)
        {
            double score = NativeLibraryBridge.VadScore(_sileroVad, chunk);
            if (score >= 0) return score >= 0.42 || rmsGate;
            // score < 0 → エラー: RMSフォールバック
        }
        return rmsGate;
    }

    private static int _dumpSeq;
    private void DispatchAsr(byte[] pcm16le, LinuxHearingConfig cfg)
    {
        if (!_asrGate.Wait(0)) return;
        _ = Task.Run(() =>
        {
            try
            {
                // Optional field diagnostic for Linux audio routing investigations.
                if (Environment.GetEnvironmentVariable("MOBMATE_HEARING_DUMP") == "1")
                {
                    var rms = CalcRms16Le(pcm16le);
                    var seq = Interlocked.Increment(ref _dumpSeq);
                    _logger.LogInformation("[Hearing][DIAG] chunk#{Seq} bytes={Bytes} ms={Ms} rms={Rms:F1}",
                        seq, pcm16le.Length, pcm16le.Length / 2 / (SampleRate / 1000), rms);
                    try { WriteWavDump($"/tmp/mobmate-chunk-{seq}.wav", pcm16le); } catch { }
                }
                var gatedPcm = HearingNoiseGate.ApplyPoc17(pcm16le, SampleRate);
                var (text, engine) = Transcribe(gatedPcm, cfg);
                if (string.IsNullOrWhiteSpace(text)) return;
                var trimmed = text.Trim();
                if (!HasMeaningfulCaptionText(trimmed)) return;
                if (ShouldSuppressShortRepeat(trimmed)) return;
                var translated = TranslateIfNeeded(trimmed, cfg);
                PushHistory(trimmed, translated, engine);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Hearing] ASR failed");
                SetError(ex.Message);
            }
            finally
            {
                _asrGate.Release();
            }
        });
    }

    private (string? Text, string Engine) Transcribe(byte[] pcm16le, LinuxHearingConfig cfg)
    {
        // sense_voice直指定: SenseVoice優先 (変更なし)
        if (cfg.Engine == "sense_voice" && _senseVoice?.IsAvailable == true)
        {
            var text = _senseVoice.Transcribe(ConvertInt16ToFloat(pcm16le), SampleRate, cfg.Lang == "auto" ? "ja" : cfg.Lang);
            if (!string.IsNullOrWhiteSpace(text))
                return (text.Trim(), "sense_voice");
        }

        // joint_auto / whisper: native(Whisper)優先
        if (!string.IsNullOrWhiteSpace(_whisperModelPath) && !string.IsNullOrWhiteSpace(_whisperDllDir))
        {
            var samples = new short[pcm16le.Length / 2];
            Buffer.BlockCopy(pcm16le, 0, samples, 0, pcm16le.Length);
            var optionsJson = JsonSerializer.Serialize(new
            {
                engine        = "whisper",
                modelPath     = _whisperModelPath,
                whisperDllDir = _whisperDllDir,
                language      = cfg.Lang == "auto" ? "" : cfg.Lang,
                threads       = 2,
                useGpu        = _whisperUseGpu,
                usePrefilter  = true,
                scene         = "desktop_hearing",
            });
            var result = NativeLibraryBridge.TranscribePcm16(_nativeExePath, samples, SampleRate, optionsJson);
            if (result.Success && !string.IsNullOrWhiteSpace(result.SelectedText)) return (result.SelectedText, "whisper");
            if (!result.Success) _logger.LogDebug("[Hearing] whisper failed: {Message}", result.Message);
        }

        // joint_auto: Whisper失敗時のみSenseVoiceをfallback
        if (cfg.Engine == "joint_auto" && _senseVoice?.IsAvailable == true)
        {
            var text = _senseVoice.Transcribe(ConvertInt16ToFloat(pcm16le), SampleRate, cfg.Lang == "auto" ? "ja" : cfg.Lang);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var trimmed = text.Trim();
                if (HasMeaningfulCaptionText(trimmed))
                {
                    _logger.LogDebug("[Hearing] joint_auto: whisper empty, sense_voice fallback: {Text}", trimmed);
                    return (trimmed, "sense_voice");
                }
                _logger.LogDebug("[Hearing] sense_voice low-content result suppressed: {Text}", trimmed);
            }
        }

        return (null, cfg.Engine);
    }

    private string TranslateIfNeeded(string text, LinuxHearingConfig cfg)
    {
        if (cfg.TranslateTo == "OFF" || _translation?.IsLoaded != true) return string.Empty;
        var srcLang = cfg.Lang == "auto" ? "ja" : cfg.Lang;
        return _translation.Translate(text, srcLang, cfg.TranslateTo) ?? string.Empty;
    }

    private void PushHistory(string source, string translated, string engine)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_lock)
        {
            _status.SourceText     = source;
            _status.TranslatedText = translated;
            _status.SelectedEngine = engine;
            _status.UpdatedAtMs    = now;
            _status.Error          = null;
            _history.Enqueue(new LinuxHearingHistoryItem { AtMs = now, SourceText = source, TranslatedText = translated, SelectedEngine = engine });
            while (_history.Count > MaxHistory) _history.Dequeue();
            _status.HistoryCount = _history.Count;
        }
        _logger.LogInformation("[Hearing] [{Engine}] {Source}", engine, source);
    }

    private void SetError(string? error)
    {
        lock (_lock)
        {
            _status.Error       = error;
            _status.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    private bool ShouldSuppressShortRepeat(string text)
    {
        var key = NormalizeKey(text);
        int cp  = key.EnumerateRunes().Count();
        if (cp == 0 || cp > ShortRepeatMaxCp) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_recentShortCaptions)
        {
            var stale = _recentShortCaptions
                .Where(kv => now - kv.Value > ShortRepeatTtl)
                .Select(kv => kv.Key).ToList();
            foreach (var k in stale) _recentShortCaptions.Remove(k);
            bool dup = _recentShortCaptions.ContainsKey(key);
            _recentShortCaptions[key] = now;
            return dup;
        }
    }

    private static string NormalizeKey(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[\s\p{P}、。！？「」（）\[\]…・〜ー]+", "");

    private static bool HasMeaningfulCaptionText(string text) =>
        NormalizeKey(text).EnumerateRunes().Count() >= 2;

    private static float[] ConvertInt16ToFloat(byte[] pcm16le)
    {
        var samples = new float[pcm16le.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm16le, i * 2) / 32768.0f;
        return samples;
    }

    private static void WriteWavDump(string path, byte[] pcm16le)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        int dataLen = pcm16le.Length, sr = SampleRate;
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(sr); bw.Write(sr * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
        bw.Write(pcm16le);
    }

    private static double CalcRms16Le(byte[] pcm16le)
    {
        if (pcm16le.Length < 2) return 0;
        long sum   = 0;
        var  count = pcm16le.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var s = BitConverter.ToInt16(pcm16le, i * 2);
            sum += (long)s * s;
        }
        return Math.Sqrt((double)sum / count);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopInternal();
        _asrGate.Dispose();
        _pcmAcc.Dispose();
        NativeLibraryBridge.VadFree(_sileroVad);
    }
}
