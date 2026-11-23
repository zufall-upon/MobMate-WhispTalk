using Microsoft.Win32;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MobMate.Core.Tts;

/// <summary>
/// Windows SAPI COM wrapper for text-to-speech. No NuGet dependencies.
/// </summary>
public sealed class WindowsSapiTtsService : IPrimaryTtsService
{
    private readonly object _lock = new();
    private object? _voice;
    private object? _monitorVoice;
    private bool _isSpeaking;
    private bool _disposed;
    // Phase 7.2: runtime config
    private TtsConfig _config = new TtsConfig();
    // Phase 7.2-5: Queue metrics
    private int _droppedCount;
    // Phase 7.3-1: Loopback detection tracking
    private string? _lastTtsText;
    private DateTime _lastTtsStartedAt;
    private readonly TtsWavOutputStore? _wavOutputStore;
    private readonly ILogger? _logger;

    public WindowsSapiTtsService(TtsWavOutputStore? wavOutputStore = null, ILogger? logger = null)
    {
        _wavOutputStore = wavOutputStore;
        _logger = logger;
    }

    // IPrimaryTtsService
    public bool IsAvailable => !_disposed && EnsureVoice(out _);
    public string EngineName => "windows_sapi";

    public TtsStatus GetStatus()
    {
        lock (_lock)
        {
            if (_disposed)
                return new TtsStatus { Available = false, IsSpeaking = false, Error = "Disposed" };

            bool available = EnsureVoice(out string? error);
            return new TtsStatus
            {
                Available = available,
                IsSpeaking = _isSpeaking,
                Error = error,
                OutputDeviceId = _config.OutputDeviceId,
                OutputDeviceName = _config.OutputDeviceName,
                OutputDeviceMode = string.IsNullOrEmpty(_config.OutputDeviceId) ? "system_default" : "named",
                WirelessLikely = IsWirelessDeviceName(_config.OutputDeviceName),
                PreSilenceMs = _config.PreSilenceMs,
                FallbackReason = null,
                PendingCount = 0,
                DroppedCount = _droppedCount,
                LastTtsText = _lastTtsText,
                LastTtsStartedAt = _lastTtsStartedAt != default ? _lastTtsStartedAt.ToString("o") : null,
                // Phase 7.2-MON: Monitor status
                MonitorEnabled = _config.MonitorVolumePercent > 0,
                     MonitorVolumePercent = _config.MonitorVolumePercent,
                    WavOutputEnabled = _config.WavOutputEnabled,
                MonitorFallbackReason = null,
                // Phase 7.4: Multi-engine status
                Engine = _config.Engine,
                EffectiveEngine = _config.Engine, // For now, effective = configured (no fallback logic yet)
                EngineAvailable = available,
                EngineError = error,
                VoicevoxApi = _config.VoicevoxApi,
                VoicevoxSpeakerId = _config.VoicevoxSpeakerId,
                VoicevoxAutoEmotion = _config.VoicevoxAutoEmotion,
                TtsReflectEmotion = _config.TtsReflectEmotion,
                TtsReflectContourStrength = _config.TtsReflectContourStrength,
                TtsReflectToneEmphasis = _config.TtsReflectToneEmphasis,
                PiperPlusModelId = _config.PiperPlusModelId,
                PiperPlusModelInstalled = false, // Will be populated when Piper catalog is implemented
                PiperPlusRuntimeAvailable = false, // Will be populated when Piper runtime check is implemented
                MossModelId = _config.MossModelId,
                MossVoiceId = _config.MossVoiceId
            };
        }
    }

    /// <summary>
    /// Apply runtime TTS configuration (Phase 7.2).
    /// </summary>
    public void SetConfig(TtsConfig config)
    {
        lock (_lock)
        {
            _config = config;
        }
    }

    public TtsConfig GetConfig()
    {
       lock (_lock)
            {
                return new TtsConfig
                {
                    OutputDeviceId = _config.OutputDeviceId,
                    OutputDeviceName = _config.OutputDeviceName,
                    PreSilenceMs = _config.PreSilenceMs,
                    MinIntervalMs = _config.MinIntervalMs,
                    MaxTextLength = _config.MaxTextLength,
                    MonitorVolumePercent = _config.MonitorVolumePercent,
                    WavOutputEnabled = _config.WavOutputEnabled,
                    // Phase 7.4: Multi-engine config
                    Engine = _config.Engine,
                    VoicevoxApi = _config.VoicevoxApi,
                    VoicevoxSpeakerId = _config.VoicevoxSpeakerId,
                    VoicevoxAutoEmotion = _config.VoicevoxAutoEmotion,
                    TtsReflectEmotion = _config.TtsReflectEmotion,
                    TtsReflectContourStrength = _config.TtsReflectContourStrength,
                    TtsReflectToneEmphasis = _config.TtsReflectToneEmphasis,
                    PiperPlusExePath = _config.PiperPlusExePath,
                    PiperPlusModelId = _config.PiperPlusModelId,
                    PiperPlusPersistent = _config.PiperPlusPersistent,
                    KokoroModelId = _config.KokoroModelId,
                    KokoroModelDir = _config.KokoroModelDir,
                    KokoroSpeakerId = _config.KokoroSpeakerId,
                    KokoroSpeed = _config.KokoroSpeed,
                    MossModelId = _config.MossModelId,
                    MossModelDir = _config.MossModelDir,
                    MossVoiceId = _config.MossVoiceId,
                    MossMaxNewFrames = _config.MossMaxNewFrames
                };
            }
    }

    /// <summary>
    /// Enumerate TTS output devices via SAPI (Phase 7.2).
    /// Uses GetEnumerator to iterate audio output tokens.
    /// Device names are derived from SAPI token GUIDs.
    /// </summary>
    public TtsDeviceList EnumerateDevices()
    {
        lock (_lock)
        {
            if (_disposed)
                return new TtsDeviceList { Ok = false, Error = "Disposed" };

            try
            {
                if (!EnsureVoice(out string? error))
                    return new TtsDeviceList { Ok = false, Error = error ?? "SAPI not available" };

                if (_voice == null)
                    return new TtsDeviceList { Ok = false, Error = "Voice object null" };

                var voiceType = _voice.GetType();

                // Get current audio output token ID for IsDefault comparison
                string? currentTokenId = null;
                try
                {
                    var audioOutput = voiceType.InvokeMember("AudioOutput",
                        System.Reflection.BindingFlags.GetProperty, null, _voice, null);
                    if (audioOutput != null)
                    {
                        var tokenId = audioOutput.GetType().InvokeMember("ID",
                            System.Reflection.BindingFlags.GetProperty, null, audioOutput, null);
                        currentTokenId = tokenId?.ToString();
                    }
                }
                catch { /* ignore */ }

                var devices = new List<TtsDeviceInfo>();

                // Enumerate via GetAudioOutputs + GetEnumerator
                try
                {
                    var audioOutputs = voiceType.InvokeMember("GetAudioOutputs",
                        System.Reflection.BindingFlags.InvokeMethod, null, _voice, null);
                    if (audioOutputs != null)
                    {
                        var aosType = audioOutputs.GetType();
                        var enumerator = aosType.InvokeMember("GetEnumerator",
                            System.Reflection.BindingFlags.InvokeMethod, null, audioOutputs, null);

                        if (enumerator != null)
                        {
                            var enumType = enumerator.GetType();
                            int index = 0;
                            while (true)
                            {
                                var moveNext = enumType.InvokeMember("MoveNext",
                                    System.Reflection.BindingFlags.InvokeMethod, null, enumerator, null);
                                if (!(moveNext is bool bn) || !bn)
                                    break;

                                var current = enumType.InvokeMember("Current",
                                    System.Reflection.BindingFlags.GetProperty, null, enumerator, null);
                                if (current != null)
                                {
                                    var tokenId = current.GetType().InvokeMember("ID",
                                        System.Reflection.BindingFlags.GetProperty, null, current, null)?.ToString();

                                    if (!string.IsNullOrEmpty(tokenId))
                                    {
                                        var guid = ExtractGuidFromTokenId(tokenId);
                                        var deviceNames = GetDeviceNames(guid);
                                        var endpointName = deviceNames.EndpointName ?? guid;
                                        var friendlyName = deviceNames.FriendlyName ?? endpointName;
                                        var detailedName = BuildDetailedDeviceName(endpointName, guid, tokenId);
                                        devices.Add(new TtsDeviceInfo
                                        {
                                            Id = guid,
                                            Name = detailedName,
                                            FriendlyName = friendlyName,
                                            TokenId = tokenId,
                                            IsDefault = tokenId == currentTokenId,
                                            WirelessLikely = IsWirelessDeviceName(detailedName)
                                        });
                                    }
                                }
                                index++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    return new TtsDeviceList { Ok = false, Error = $"Enumeration failed: {ex.Message}" };
                }

                return new TtsDeviceList { Ok = true, Devices = devices.ToArray() };
            }
            catch (Exception ex)
            {
                return new TtsDeviceList { Ok = false, Error = $"Device enumeration failed: {ex.Message}" };
            }
        }
    }

    private static string ExtractGuidFromTokenId(string tokenId)
    {
        // Token ID format: ...MMAudioOut\{0.0.0.00000000}.{GUID}
        // The GUID includes braces: {xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}
        var lastDot = tokenId.LastIndexOf('.');
        if (lastDot > 0 && lastDot < tokenId.Length - 1)
        {
            var afterDot = tokenId.Substring(lastDot + 1);
            if (!string.IsNullOrEmpty(afterDot))
                return afterDot;
        }
        return tokenId;
    }

    private static string BuildDetailedDeviceName(string friendlyName, string deviceGuid, string tokenId)
    {
        var shortId = ShortDeviceId(deviceGuid);
        var tokenTail = ShortTokenTail(tokenId);
        if (!string.IsNullOrEmpty(tokenTail) && tokenTail != shortId)
        {
            return $"{friendlyName} [{shortId} / {tokenTail}]";
        }
        return $"{friendlyName} [{shortId}]";
    }

    private static string ShortDeviceId(string deviceGuid)
    {
        var cleaned = deviceGuid.Trim().Trim('{', '}');
        return cleaned.Length <= 8 ? cleaned : cleaned.Substring(cleaned.Length - 8);
    }

    private static string ShortTokenTail(string tokenId)
    {
        var cleaned = tokenId.Trim().TrimEnd('\\');
        var slash = cleaned.LastIndexOf('\\');
        if (slash >= 0 && slash < cleaned.Length - 1)
        {
            cleaned = cleaned.Substring(slash + 1);
        }
        cleaned = cleaned.Trim().Trim('{', '}');
        return cleaned.Length <= 8 ? cleaned : cleaned.Substring(cleaned.Length - 8);
    }

    private static (string? EndpointName, string? FriendlyName) GetDeviceNames(string deviceGuid)
    {
        try
        {
            var regPath = $"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MMDevices\\Audio\\Render\\{deviceGuid}\\Properties";
            using var key = Registry.LocalMachine.OpenSubKey(regPath);
            if (key != null)
            {
                var endpointName = key.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                var friendlyName = key.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                if (!string.IsNullOrEmpty(endpointName) && endpointName.Contains('\uFFFD'))
                    endpointName = null;
                return (string.IsNullOrWhiteSpace(endpointName) ? null : endpointName,
                    string.IsNullOrWhiteSpace(friendlyName) ? null : friendlyName);
            }
        }
        catch { /* ignore */ }
        return (null, null);
    }

    /// <summary>
    /// Conservative wireless/Bluetooth heuristic based on device display name (Phase 7.2-4).
    /// Returns true if the name contains common wireless keywords.
    /// </summary>
    private static bool IsWirelessDeviceName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName))
            return false;

        var lower = deviceName.ToLowerInvariant();
        // Conservative keyword list to avoid false positives
        var wirelessKeywords = new[]
        {
            "bluetooth", "bt ", "bt-", "wireless", "airpod", "airdots",
            "真無線", "ワイヤレス", "ブルートゥース"
        };
        return wirelessKeywords.Any(k => lower.Contains(k));
    }

    /// <summary>
    /// Try to set SAPI AudioOutput to the device with the given GUID (Phase 7.2-3).
    /// Returns null on success, or a fallback reason string on failure.
    /// On failure, resets to system default by setting AudioOutput to null.
    /// </summary>
    private string? TrySetAudioOutput(string deviceGuid)
    {
        try
        {
            if (_voice == null)
                return "Voice object null";

            var voiceType = _voice.GetType();

            // Get all audio outputs
            var audioOutputs = voiceType.InvokeMember("GetAudioOutputs",
                System.Reflection.BindingFlags.InvokeMethod, null, _voice, null);
            if (audioOutputs == null)
                return "GetAudioOutputs returned null";

            // Find the token matching the device GUID
            var aosType = audioOutputs.GetType();
            var enumerator = aosType.InvokeMember("GetEnumerator",
                System.Reflection.BindingFlags.InvokeMethod, null, audioOutputs, null);

            object? targetToken = null;
            if (enumerator != null)
            {
                var enumType = enumerator.GetType();
                while (true)
                {
                    var moveNext = enumType.InvokeMember("MoveNext",
                        System.Reflection.BindingFlags.InvokeMethod, null, enumerator, null);
                    if (!(moveNext is bool bn) || !bn)
                        break;

                    var current = enumType.InvokeMember("Current",
                        System.Reflection.BindingFlags.GetProperty, null, enumerator, null);
                    if (current != null)
                    {
                        var tokenId = current.GetType().InvokeMember("ID",
                            System.Reflection.BindingFlags.GetProperty, null, current, null)?.ToString();
                        if (!string.IsNullOrEmpty(tokenId))
                        {
                            var guid = ExtractGuidFromTokenId(tokenId);
                            if (guid == deviceGuid)
                            {
                                targetToken = current;
                                break;
                            }
                        }
                    }
                }
            }

            if (targetToken == null)
            {
                // Device not found, fall back to default
                try { voiceType.InvokeMember("AudioOutput", System.Reflection.BindingFlags.SetProperty, null, _voice, new object[] { null }); } catch { }
                return $"Device '{deviceGuid}' not found in audio outputs";
            }

            // Set the audio output
            voiceType.InvokeMember("AudioOutput", System.Reflection.BindingFlags.SetProperty, null, _voice, new object[] { targetToken });
            return null; // Success
        }
        catch (Exception ex)
        {
            // Reset to default on any error
            try
            {
                if (_voice != null)
                {
                    _voice.GetType().InvokeMember("AudioOutput", System.Reflection.BindingFlags.SetProperty, null, _voice, new object[] { null });
                }
            }
            catch { /* ignore reset failure */ }
            return $"Failed to set audio output: {ex.Message}";
        }
    }

    public TtsResult SpeakAsync(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TtsResult { Success = false, Error = "Empty text" };

        lock (_lock)
        {
            if (_disposed)
                return new TtsResult { Success = false, Error = "Disposed" };

            if (!EnsureVoice(out string? error))
                return new TtsResult { Success = false, Error = error ?? "SAPI not available" };

            if (_voice == null)
                return new TtsResult { Success = false, Error = "Voice object null" };

            // Phase 7.3-1: Min interval guard
            var minIntervalMs = _config.MinIntervalMs;
            if (minIntervalMs > 0 && _lastTtsStartedAt != default)
            {
                var elapsed = (DateTime.UtcNow - _lastTtsStartedAt).TotalMilliseconds;
                if (elapsed < minIntervalMs)
                {
                    _droppedCount++;
                    return new TtsResult { Success = false, Error = $"min_interval ({minIntervalMs}ms not elapsed)" };
                }
            }

            // Phase 7.3-1: Max text length guard
            var maxLength = _config.MaxTextLength;
            if (maxLength > 0 && text.Length > maxLength)
            {
                _droppedCount++;
                return new TtsResult { Success = false, Error = $"text too long ({text.Length} > {maxLength})" };
            }

            // Phase 7.2-5: Queue policy
            if (_isSpeaking && !interrupt)
            {
                _droppedCount++;
                return new TtsResult { Success = false, Error = "busy" };
            }

            try
                {
                // Phase 7.2-3: Route to configured output device
                string? fallbackReason = null;
                var targetDeviceId = _config.OutputDeviceId;
                if (!string.IsNullOrEmpty(targetDeviceId))
                {
                    fallbackReason = TrySetAudioOutput(targetDeviceId);
                }

                // Phase 7.2-4: Pre-silence for wireless devices
                var preSilenceMs = _config.PreSilenceMs;
                if (preSilenceMs <= 0 && IsWirelessDeviceName(_config.OutputDeviceName))
                {
                    preSilenceMs = 600; // Default 600ms for wireless
                }

                // Purge any pending speech first (or interrupt if speaking)
                try { _voice.GetType().InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, _voice, new object[] { "", 2 }); } catch { }
                _isSpeaking = true;
                // Phase 7.3-1: Track last TTS for loopback detection
                _lastTtsText = text;
                _lastTtsStartedAt = DateTime.UtcNow;

                // Fire-and-forget: speak synchronously on a background thread
                var voiceRef = _voice;
                var lockRef = _lock;
                var silenceMs = preSilenceMs;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Phase 7.2-4: Pre-silence delay for wireless device wake-up
                        if (silenceMs > 0)
                        {
                            await Task.Delay(silenceMs);
                        }
                        // SPF_DEFAULT = 0 (synchronous, blocks until speech is done)
                        voiceRef.GetType().InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, voiceRef, new object[] { text, 0 });
                    }
                    catch { /* ignore */ }
                    finally
                    {
                        lock (lockRef)
                        {
                            _isSpeaking = false;
                        }
                    }
                });

                if (_config.WavOutputEnabled && _wavOutputStore != null)
                    _ = RenderSapiWavAsync(text);

                // Phase 7.2-MON-2: Monitor playback on system default output.
                // If main TTS already targets system default, monitor would duplicate speech on the same device.
                var monitorVolume = _config.MonitorVolumePercent;
                if (monitorVolume > 0 && !IsMainOutputSystemDefault(targetDeviceId))
                {
                    if (EnsureMonitorVoice(out string? monError))
                    {
                        var monitorVoiceRef = _monitorVoice;
                        // Update volume in case config changed
                        try
                        {
                            monitorVoiceRef.GetType().InvokeMember("Volume",
                                System.Reflection.BindingFlags.SetProperty, null, monitorVoiceRef,
                                new object[] { monitorVolume });
                        }
                        catch { /* ignore volume set failure */ }
                        // Purge any pending monitor speech if interrupt
                        if (interrupt)
                        {
                            try { monitorVoiceRef.GetType().InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, monitorVoiceRef, new object[] { "", 2 }); } catch { }
                        }
                        // Fire-and-forget monitor speak (async, SPF_ASYNC = 1)
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                monitorVoiceRef.GetType().InvokeMember("Speak",
                                    System.Reflection.BindingFlags.InvokeMethod, null, monitorVoiceRef,
                                    new object[] { text, 1 }); // SPF_ASYNC = 1
                            }
                            catch { /* monitor failures must not affect main TTS */ }
                        });
                    }
                }

                return new TtsResult { Success = true, Error = null, FallbackReason = fallbackReason };
            }
            catch (Exception ex)
            {
                return new TtsResult { Success = false, Error = $"SAPI Speak failed: {ex.Message}" };
            }
        }
    }

    private async Task RenderSapiWavAsync(string text)
    {
        string? temporaryPath = null;
        object? renderVoice = null;
        object? fileStream = null;
        try
        {
            temporaryPath = Path.Combine(Path.GetTempPath(), $"mobmate-sapi-{Guid.NewGuid():N}.tmp");
            await Task.Run(() =>
            {
                var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new InvalidOperationException("SAPI.SpVoice not registered");
                var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream") ?? throw new InvalidOperationException("SAPI.SpFileStream not registered");
                renderVoice = Activator.CreateInstance(voiceType);
                fileStream = Activator.CreateInstance(streamType);
                streamType.InvokeMember("Open", System.Reflection.BindingFlags.InvokeMethod, null, fileStream, new object[] { temporaryPath, 3, false });
                voiceType.InvokeMember("AudioOutputStream", System.Reflection.BindingFlags.SetProperty, null, renderVoice, new[] { fileStream });
                voiceType.InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, renderVoice, new object[] { text, 0 });
                streamType.InvokeMember("Close", System.Reflection.BindingFlags.InvokeMethod, null, fileStream, null);
            }).ConfigureAwait(false);
            var bytes = await File.ReadAllBytesAsync(temporaryPath).ConfigureAwait(false);
            await _wavOutputStore!.SaveWavAsync("windows_sapi", bytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TTS] SAPI WAV render failed");
        }
        finally
        {
            if (fileStream != null && Marshal.IsComObject(fileStream)) try { Marshal.ReleaseComObject(fileStream); } catch { }
            if (renderVoice != null && Marshal.IsComObject(renderVoice)) try { Marshal.ReleaseComObject(renderVoice); } catch { }
            if (temporaryPath != null) try { File.Delete(temporaryPath); } catch { }
        }
    }

    public TtsResult StopAsync()
    {
        lock (_lock)
        {
            if (_disposed)
                return new TtsResult { Success = false, Error = "Disposed" };

            if (_voice == null)
                return new TtsResult { Success = true, Error = null }; // Nothing to stop

            try
            {
                // SAPI.SpVoice.Speak("", SPF_PURGE = 2) clears the queue
                _voice.GetType().InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, _voice, new object[] { "", 2 });
                // Phase 7.2-MON-2: Also stop monitor voice
                if (_monitorVoice != null)
                {
                    try { _monitorVoice.GetType().InvokeMember("Speak", System.Reflection.BindingFlags.InvokeMethod, null, _monitorVoice, new object[] { "", 2 }); } catch { }
                }
                _isSpeaking = false;
                return new TtsResult { Success = true, Error = null };
            }
            catch (Exception ex)
            {
                return new TtsResult { Success = false, Error = $"SAPI Stop failed: {ex.Message}" };
            }
        }
    }

    /// <summary>
    /// Check if the given ASR text might be a loopback of recent TTS output (Phase 7.3-2).
    /// Returns true if the ASR text contains the last TTS text and arrived within 1500ms.
    /// This is informational only; does not auto-drop ASR results.
    /// </summary>
    public bool CheckLoopbackSuspected(string asrText)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_lastTtsText) || _lastTtsStartedAt == default)
                return false;

            var elapsed = (DateTime.UtcNow - _lastTtsStartedAt).TotalMilliseconds;
            if (elapsed > 1500)
                return false;

            // Check if ASR text contains the last TTS text (case-insensitive)
            var asrLower = asrText.ToLowerInvariant();
            var ttsLower = _lastTtsText.ToLowerInvariant();
            return asrLower.Contains(ttsLower);
        }
    }

    private bool EnsureVoice(out string? error)
    {
        if (_voice != null)
        {
            error = null;
            return true;
        }

        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type == null)
            {
                error = "SAPI.SpVoice not registered on this system";
                return false;
            }
            _voice = Activator.CreateInstance(type);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to create SAPI.SpVoice: {ex.Message}";
            return false;
        }
    }

    private bool IsMainOutputSystemDefault(string? targetDeviceId)
    {
        if (string.IsNullOrEmpty(targetDeviceId))
            return true;

        if (!EnsureMonitorVoice(out _))
            return false;

        try
        {
            var audioOutput = _monitorVoice?.GetType().InvokeMember("AudioOutput",
                System.Reflection.BindingFlags.GetProperty, null, _monitorVoice, null);
            var tokenId = audioOutput?.GetType().InvokeMember("ID",
                System.Reflection.BindingFlags.GetProperty, null, audioOutput, null)?.ToString();
            var defaultGuid = string.IsNullOrEmpty(tokenId) ? string.Empty : ExtractGuidFromTokenId(tokenId);
            return string.Equals(defaultGuid, targetDeviceId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensure a second SAPI voice for monitor playback (Phase 7.2-MON-2).
    /// Returns true on success, false on failure (monitor is optional).
    /// </summary>
    private bool EnsureMonitorVoice(out string? error)
    {
        if (_monitorVoice != null)
        {
            error = null;
            return true;
        }

        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type == null)
            {
                error = "SAPI.SpVoice not registered";
                return false;
            }
            _monitorVoice = Activator.CreateInstance(type);
            // Set monitor volume (SAPI Volume: 0-100)
            try
            {
                _monitorVoice.GetType().InvokeMember("Volume",
                    System.Reflection.BindingFlags.SetProperty, null, _monitorVoice,
                    new object[] { _config.MonitorVolumePercent });
            }
            catch { /* Volume may not be available on all systems */ }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to create monitor voice: {ex.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_voice != null)
            {
                try
                {
                    Marshal.ReleaseComObject(_voice);
                }
                catch
                {
                    // Ignore disposal errors
                }
                _voice = null;
            }
            // Phase 7.2-MON-2: Release monitor voice
            if (_monitorVoice != null)
            {
                try
                {
                    Marshal.ReleaseComObject(_monitorVoice);
                }
                catch
                {
                    // Ignore disposal errors
                }
                _monitorVoice = null;
            }
        }
    }
}

public sealed class TtsStatus
{
    public bool Available { get; init; }
    public bool IsSpeaking { get; init; }
    public string? Error { get; init; }
    // Device-related fields (Phase 7.2)
    public string OutputDeviceId { get; init; } = string.Empty;
    public string OutputDeviceName { get; init; } = string.Empty;
    public string OutputDeviceMode { get; init; } = "system_default";
    public bool WirelessLikely { get; init; }
    public int PreSilenceMs { get; init; }
    public string? FallbackReason { get; init; }
    // Queue metrics (Phase 7.2-5)
    public int PendingCount { get; init; }
    public int DroppedCount { get; init; }
    // Loopback detection (Phase 7.3-1)
    public string? LastTtsText { get; init; }
    public string? LastTtsStartedAt { get; init; }
    // Monitor playback (Phase 7.2-MON)
    public bool MonitorEnabled { get; init; }
    public int MonitorVolumePercent { get; init; }
    public bool WavOutputEnabled { get; init; }
    public string? MonitorFallbackReason { get; init; }
    // Phase 7.4: Multi-engine status
    public string Engine { get; init; } = "windows_sapi";
    public string EffectiveEngine { get; init; } = "windows_sapi";
    public bool EngineAvailable { get; init; } = true;
    public string? EngineError { get; init; }
    public string VoicevoxApi { get; init; } = string.Empty;
    public string VoicevoxSpeakerId { get; init; } = "3";
    public bool VoicevoxAutoEmotion { get; init; } = true;
    public bool TtsReflectEmotion { get; init; } = true;
    public string TtsReflectContourStrength { get; init; } = "normal";
    public string TtsReflectToneEmphasis { get; init; } = "normal";
    public string PiperPlusModelId { get; init; } = string.Empty;
    public bool PiperPlusModelInstalled { get; init; }
    public bool PiperPlusRuntimeAvailable { get; init; }
    // Phase 7.4-4: External engine status
    public bool VoicevoxAvailable { get; init; }
    public string? VoicevoxError { get; init; }
    public string? PiperPlusError { get; init; }
    public string? PiperPlusModelPath { get; init; }
    // Kokoro TTS
    public string KokoroModelId { get; init; } = string.Empty;
    public bool KokoroModelInstalled { get; init; }
    public string? KokoroError { get; init; }
    // MOSS TTS
    public string MossModelId { get; init; } = string.Empty;
    public string MossVoiceId { get; init; } = "Ava";
    public bool MossModelInstalled { get; init; }
    public string? MossError { get; init; }
}

/// <summary>
/// Runtime TTS configuration (Phase 7.2).
/// </summary>
public sealed class TtsConfig
{
    public string OutputDeviceId { get; set; } = string.Empty;
    public string OutputDeviceName { get; set; } = string.Empty;
    public int PreSilenceMs { get; set; }
    // Phase 7.3-1: Operation-loop guard settings
    public int MinIntervalMs { get; set; } = 300;
    public int MaxTextLength { get; set; } = 160;
    // Phase 7.2-MON: Monitor playback
    public int MonitorVolumePercent { get; set; } = 0;
    public bool WavOutputEnabled { get; set; } = false;
    // Phase 7.4: Multi-engine support
    public string Engine { get; set; } = "windows_sapi";
    public string VoicevoxApi { get; set; } = string.Empty;
    public string VoicevoxSpeakerId { get; set; } = "3";
    public bool VoicevoxAutoEmotion { get; set; } = true;
    public bool TtsReflectEmotion { get; set; } = true;
    public string TtsReflectContourStrength { get; set; } = "normal";
    public string TtsReflectToneEmphasis { get; set; } = "normal";
    public string PiperPlusExePath { get; set; } = string.Empty;
    public string PiperPlusModelId { get; set; } = string.Empty;
    public string PiperPlusModelPath { get; set; } = string.Empty;
    public bool PiperPlusPersistent { get; set; } = false;
    // Kokoro TTS
    public string KokoroModelId { get; set; } = string.Empty;
    public string KokoroModelDir { get; set; } = string.Empty;
    public int KokoroSpeakerId { get; set; } = 3;
    public float KokoroSpeed { get; set; } = 1.0f;
    // MOSS TTS
    public string MossModelId { get; set; } = string.Empty;
    public string MossModelDir { get; set; } = string.Empty;
    public string MossVoiceId { get; set; } = "Ava";
    public int MossMaxNewFrames { get; set; } = 160;
}

public sealed class TtsResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? FallbackReason { get; init; }

    public static TtsResult Ok() => new() { Success = true };
    public static TtsResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// TTS output device information (Phase 7.2).
/// </summary>
public sealed class TtsDeviceInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public string TokenId { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public bool WirelessLikely { get; init; }
}

/// <summary>
/// Result of device enumeration (Phase 7.2).
/// </summary>
public sealed class TtsDeviceList
{
    public bool Ok { get; init; }
    public TtsDeviceInfo[] Devices { get; init; } = Array.Empty<TtsDeviceInfo>();
    public string? Error { get; init; }
}
