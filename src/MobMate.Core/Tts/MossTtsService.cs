using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace MobMate.Core.Tts;

public sealed class MossTtsService : IDisposable
{
    private readonly object _lock = new();
    private string _modelDir = string.Empty;
    private string _voiceId = "Ava";
    private int _maxNewFrames = 160;
    private MossRuntime? _runtime;
    private MossCodecEncoder? _codecEncoder;
    private List<int[]>? _referencePromptAudioCodes;
    private int _referenceOperations;

    // A custom reference overrides the configured built-in voice for body
    // synthesis. Filler cannot reuse a built-in voice while that is possible.
    internal event Action? ReferenceChanging;
    internal bool HasOnlyBuiltInReference
    {
        get { lock (_lock) return _referenceOperations == 0 && _referencePromptAudioCodes == null; }
    }

    public string? LastError { get; private set; }
    public string ModelDir => _modelDir;
    public bool IsModelInstalled => IsModelDirValid(_modelDir);

    public void SetConfig(string? modelDir, string? voiceId, int maxNewFrames = 160)
    {
        lock (_lock)
        {
            var nextDir = modelDir ?? string.Empty;
            var nextVoice = string.IsNullOrWhiteSpace(voiceId) ? "Ava" : voiceId.Trim();
            var nextMaxFrames = Math.Clamp(maxNewFrames, 20, 400);
            if (!string.Equals(_modelDir, nextDir, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_voiceId, nextVoice, StringComparison.Ordinal)
                || _maxNewFrames != nextMaxFrames)
            {
                _runtime?.Dispose();
                _runtime = null;
                _codecEncoder?.Dispose();
                _codecEncoder = null;
                _referencePromptAudioCodes = null;
                _modelDir = nextDir;
                _voiceId = nextVoice;
                _maxNewFrames = nextMaxFrames;
                LastError = null;
            }
        }
    }

    public Task<bool> SetReferencePcmAsync(byte[] pcm16LeMono, int sampleRate, CancellationToken ct = default)
    {
        if (pcm16LeMono.Length < 3200 || sampleRate <= 0) return Task.FromResult(false);
        if (ct.IsCancellationRequested) return Task.FromCanceled<bool>(ct);
        var pcm = pcm16LeMono.ToArray();
        lock (_lock) _referenceOperations++;
        try { ReferenceChanging?.Invoke(); }
        catch { lock (_lock) _referenceOperations--; throw; }
        return Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                MossCodecEncoder encoder;
                lock (_lock)
                {
                    if (!IsModelDirValid(_modelDir)) return false;
                    _codecEncoder ??= new MossCodecEncoder(_modelDir, threadCount: 2);
                    encoder = _codecEncoder;
                }

                var codes = encoder.EncodePcm16Mono(pcm, sampleRate, ct);
                lock (_lock) _referencePromptAudioCodes = codes;
                return codes.Count > 0;
            }
            catch
            {
                return false;
            }
            finally { lock (_lock) _referenceOperations--; }
        }, CancellationToken.None);
    }

    public Task<bool> WarmUpAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    if (!IsModelDirValid(_modelDir)) return false;
                    _runtime ??= new MossRuntime(_modelDir, threadCount: 4, maxNewFrames: _maxNewFrames);
                    LastError = null;
                    return true;
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }, ct);
    }

    // 自声 F0 カーブを匿名ボイスへ転写するときの抑揚強度(肉耳確認で確定)。
    private const double ProsodyContourGain = 1.5;

    public Task<MossResult> SynthesizeAsync(string text, CancellationToken ct = default, TtsProsodyProfile? prosodyProfile = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(MossResult.Fail("empty text"));
        return Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                MossRuntime runtime;
                string voiceId;
                List<int[]>? referenceCodes;
                lock (_lock)
                {
                    if (!IsModelDirValid(_modelDir))
                    {
                        LastError = "MOSS model not installed";
                        return MossResult.Fail(LastError);
                    }
                    _runtime ??= new MossRuntime(_modelDir, threadCount: 4, maxNewFrames: _maxNewFrames);
                    runtime = _runtime;
                    voiceId = _voiceId;
                    referenceCodes = _referencePromptAudioCodes?.Select(r => r.ToArray()).ToList();
                }

                var sw = Stopwatch.StartNew();
                var waveform = runtime.Synthesize(text, voiceId, ct, referenceCodes);
                var samples = ApplyProsodyContour(waveform.Samples, waveform.Channels, prosodyProfile);
                var wav = WritePcm16Wav(samples, waveform.SampleRate, waveform.Channels);
                sw.Stop();
                LastError = null;
                return MossResult.Ok(wav, waveform.FrameCount, sw.Elapsed);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return MossResult.Fail(ex.Message);
            }
        }, ct);
    }

    // 自声 F0 カーブ(PitchContour, semitone 偏差列)を生成波形へ time-varying pitch-shift で転写する。
    // 声色は MOSS の固定ボイスのまま、抑揚の全体トレンドのみを反映する(音素境界は取れないため細部は粗い)。
    private static float[] ApplyProsodyContour(float[] interleaved, int channels, TtsProsodyProfile? profile)
    {
        if (profile is not { } p || !p.HasPitchContour() || channels < 1 || interleaved.Length == 0)
            return interleaved;
        int frames = interleaved.Length / channels;
        if (frames < 64) return interleaved;

        var contour = p.PitchContour;
        var scaled = new float[contour.Length];
        for (int i = 0; i < contour.Length; i++) scaled[i] = (float)(contour[i] * ProsodyContourGain);

        var result = new float[interleaved.Length];
        var channel = new float[frames];
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < frames; i++) channel[i] = interleaved[(i * channels) + c];
            var shifted = PitchShifter.ShiftVarying(channel, scaled, 0.0);
            int m = Math.Min(shifted.Length, frames);
            for (int i = 0; i < m; i++) result[(i * channels) + c] = shifted[i];
        }
        return result;
    }

    public static bool IsModelDirValid(string? modelDir)
    {
        if (string.IsNullOrWhiteSpace(modelDir) || !Directory.Exists(modelDir)) return false;
        var manifest = Path.Combine(modelDir, "browser_poc_manifest.json");
        var ttsMeta = Path.Combine(modelDir, "tts_browser_onnx_meta.json");
        var tokenizer = Path.Combine(modelDir, "tokenizer.model");
        if (!File.Exists(manifest) || !File.Exists(ttsMeta) || !File.Exists(tokenizer)) return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            var codecRel = doc.RootElement.GetProperty("model_files").GetProperty("codec_meta").GetString();
            return !string.IsNullOrWhiteSpace(codecRel) && File.Exists(Path.GetFullPath(Path.Combine(modelDir, codecRel)));
        }
        catch { return false; }
    }

    private static byte[] WritePcm16Wav(float[] interleaved, int sampleRate, int channels)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        var dataBytes = interleaved.Length * 2;
        bw.Write("RIFF"u8.ToArray());
        bw.Write(36 + dataBytes);
        bw.Write("WAVEfmt "u8.ToArray());
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * 2);
        bw.Write((short)(channels * 2));
        bw.Write((short)16);
        bw.Write("data"u8.ToArray());
        bw.Write(dataBytes);
        foreach (var sample in interleaved)
            bw.Write((short)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767f));
        bw.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _runtime?.Dispose();
            _runtime = null;
            _codecEncoder?.Dispose();
            _codecEncoder = null;
            _referencePromptAudioCodes = null;
        }
    }

    private sealed class MossCodecEncoder : IDisposable
    {
        private const int TargetSampleRate = 48000;
        private const int TargetChannels = 2;
        private const int Nvq = 16;
        private const double MaxReferenceSeconds = 3.0;
        private const double MinReferenceSeconds = 0.35;
        private const double ReferencePadSeconds = 0.12;
        private readonly JsonDocument _codecMetaDoc;
        private readonly InferenceSession _encode;

        public MossCodecEncoder(string modelDir, int threadCount)
        {
            using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "browser_poc_manifest.json"), Encoding.UTF8));
            var codecMetaPath = Path.GetFullPath(Path.Combine(modelDir, manifestDoc.RootElement.GetProperty("model_files").GetProperty("codec_meta").GetString() ?? string.Empty));
            _codecMetaDoc = JsonDocument.Parse(File.ReadAllText(codecMetaPath, Encoding.UTF8));
            var codecDir = Path.GetDirectoryName(codecMetaPath) ?? modelDir;
            var encodeFile = TryFileName(_codecMetaDoc.RootElement, "encode")
                ?? TryFileName(_codecMetaDoc.RootElement, "encoder")
                ?? "moss_audio_tokenizer_encode.onnx";
            _encode = Open(Path.Combine(codecDir, encodeFile), threadCount);
        }

        public List<int[]> EncodePcm16Mono(byte[] pcm16LeMono, int sourceSampleRate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var mono = Pcm16LeToMonoFloat(pcm16LeMono);
            mono = TrimAndCapReference(mono, sourceSampleRate);
            if (mono.Length < Math.Max(1, (int)Math.Round(sourceSampleRate * MinReferenceSeconds))) return [];
            var resampled = ResampleLinear(mono, sourceSampleRate, TargetSampleRate);
            var length = resampled.Length;
            if (length <= 0) return [];
            var waveform = new DenseTensor<float>([1, TargetChannels, length]);
            for (var i = 0; i < length; i++)
            {
                waveform[0, 0, i] = resampled[i];
                waveform[0, 1, i] = resampled[i];
            }
            using var outputs = _encode.Run([
                NamedOnnxValue.CreateFromTensor("waveform", waveform),
                NamedOnnxValue.CreateFromTensor("input_lengths", Vector([length])),
            ]);
            var map = outputs.ToDictionary(v => v.Name, v => v);
            var codes = ReadTensorAsIntArray(map["audio_codes"]);
            var frames = ReadCodeFrameCount(map["audio_codes"], codes.Length / Nvq);
            if (map.TryGetValue("audio_code_lengths", out var lenValue))
                frames = Math.Min(frames, (int)ReadFirstInt64(lenValue));
            var result = new List<int[]>(frames);
            for (var f = 0; f < frames; f++)
            {
                var row = new int[Nvq];
                for (var q = 0; q < Nvq; q++) row[q] = codes[(f * Nvq) + q];
                result.Add(row);
            }
            return result;
        }

        private static float[] TrimAndCapReference(float[] source, int sampleRate)
        {
            if (source.Length == 0 || sampleRate <= 0) return [];
            var maxSamples = Math.Max(1, (int)Math.Round(sampleRate * MaxReferenceSeconds));
            var minSamples = Math.Max(1, (int)Math.Round(sampleRate * MinReferenceSeconds));
            var padSamples = Math.Max(0, (int)Math.Round(sampleRate * ReferencePadSeconds));
            if (source.Length <= maxSamples && source.Length <= sampleRate * 4) return source;

            var frameSamples = Math.Max(1, sampleRate / 50); // 20ms
            var frameCount = Math.Max(1, (source.Length + frameSamples - 1) / frameSamples);
            var rms = new double[frameCount];
            var maxRms = 0.0;
            for (var frame = 0; frame < frameCount; frame++)
            {
                var start = frame * frameSamples;
                var end = Math.Min(source.Length, start + frameSamples);
                var sum = 0.0;
                for (var i = start; i < end; i++) sum += source[i] * source[i];
                var value = Math.Sqrt(sum / Math.Max(1, end - start));
                rms[frame] = value;
                if (value > maxRms) maxRms = value;
            }
            if (maxRms < 0.003) return [];
            var threshold = Math.Max(0.004, maxRms * 0.12);
            var first = -1;
            var last = -1;
            for (var frame = 0; frame < frameCount; frame++)
            {
                if (rms[frame] < threshold) continue;
                if (first < 0) first = frame;
                last = frame;
            }
            if (first < 0 || last < first) return [];

            var trimStart = Math.Max(0, (first * frameSamples) - padSamples);
            var trimEnd = Math.Min(source.Length, ((last + 1) * frameSamples) + padSamples);
            if (trimEnd - trimStart < minSamples) return [];
            if (trimEnd - trimStart > maxSamples)
            {
                var bestFrame = first;
                var bestRms = 0.0;
                for (var frame = first; frame <= last; frame++)
                {
                    if (rms[frame] <= bestRms) continue;
                    bestRms = rms[frame];
                    bestFrame = frame;
                }
                var center = (bestFrame * frameSamples) + (frameSamples / 2);
                trimStart = Math.Clamp(center - (maxSamples / 2), 0, Math.Max(0, source.Length - maxSamples));
                trimEnd = Math.Min(source.Length, trimStart + maxSamples);
            }
            var length = trimEnd - trimStart;
            if (length <= 0) return [];
            var result = new float[length];
            Array.Copy(source, trimStart, result, 0, length);
            return result;
        }

        private static float[] Pcm16LeToMonoFloat(byte[] pcm)
        {
            var samples = pcm.Length / 2;
            var result = new float[samples];
            for (var i = 0; i < samples; i++)
                result[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
            return result;
        }

        private static float[] ResampleLinear(float[] source, int sourceRate, int targetRate)
        {
            if (source.Length == 0) return [];
            if (sourceRate == targetRate) return source;
            var targetLength = Math.Max(1, (int)Math.Round(source.Length * (double)targetRate / sourceRate));
            var result = new float[targetLength];
            var scale = sourceRate / (double)targetRate;
            for (var i = 0; i < targetLength; i++)
            {
                var pos = i * scale;
                var left = Math.Min(source.Length - 1, (int)pos);
                var right = Math.Min(source.Length - 1, left + 1);
                var frac = (float)(pos - left);
                result[i] = source[left] + ((source[right] - source[left]) * frac);
            }
            return result;
        }

        private static InferenceSession Open(string path, int threadCount)
        {
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, threadCount), InterOpNumThreads = 1 };
            return new InferenceSession(path, options);
        }

        private static DenseTensor<int> Vector(int[] values)
        {
            var tensor = new DenseTensor<int>([values.Length]);
            for (var i = 0; i < values.Length; i++) tensor[i] = values[i];
            return tensor;
        }

        private static string? TryFileName(JsonElement meta, string key)
            => meta.TryGetProperty("files", out var files) && files.TryGetProperty(key, out var value) ? value.GetString() : null;

        private static long ReadFirstInt64(DisposableNamedOnnxValue value)
        {
            if (value.Value is Tensor<long> longTensor) return longTensor.ToArray()[0];
            if (value.Value is Tensor<int> intTensor) return intTensor.ToArray()[0];
            throw new InvalidOperationException($"Unsupported scalar type for {value.Name}: {value.Value?.GetType().FullName}");
        }

        private static int[] ReadTensorAsIntArray(DisposableNamedOnnxValue value)
        {
            if (value.Value is Tensor<int> intTensor) return intTensor.ToArray();
            if (value.Value is Tensor<long> longTensor) return longTensor.ToArray().Select(v => (int)v).ToArray();
            throw new InvalidOperationException($"Unsupported code tensor type for {value.Name}: {value.Value?.GetType().FullName}");
        }

        private static int ReadCodeFrameCount(DisposableNamedOnnxValue value, int fallback)
        {
            if (value.Value is Tensor<int> intTensor && intTensor.Dimensions.Length >= 3) return intTensor.Dimensions[1];
            if (value.Value is Tensor<long> longTensor && longTensor.Dimensions.Length >= 3) return longTensor.Dimensions[1];
            return fallback;
        }

        public void Dispose()
        {
            _encode.Dispose();
            _codecMetaDoc.Dispose();
        }
    }

    private sealed class MossRuntime : IDisposable
    {
        private readonly JsonDocument _manifestDoc;
        private readonly JsonDocument _ttsMetaDoc;
        private readonly JsonDocument _codecMetaDoc;
        private readonly JsonElement _manifest;
        private readonly JsonElement _ttsMeta;
        private readonly JsonElement _codecMeta;
        private readonly InferenceSession _prefill;
        private readonly InferenceSession _decode;
        private readonly InferenceSession _localFixed;
        private readonly InferenceSession _codecDecode;
        private readonly SentencePieceTokenizer _tokenizer;
        private readonly Random _random = new(1);
        private readonly int _maxNewFrames;

        public MossRuntime(string modelDir, int threadCount, int maxNewFrames)
        {
            _maxNewFrames = Math.Clamp(maxNewFrames, 20, 400);
            _manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "browser_poc_manifest.json"), Encoding.UTF8));
            _ttsMetaDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "tts_browser_onnx_meta.json"), Encoding.UTF8));
            _manifest = _manifestDoc.RootElement;
            _ttsMeta = _ttsMetaDoc.RootElement;
            var codecMetaPath = ResolveRelative(modelDir, _manifest.GetProperty("model_files").GetProperty("codec_meta").GetString() ?? string.Empty);
            _codecMetaDoc = JsonDocument.Parse(File.ReadAllText(codecMetaPath, Encoding.UTF8));
            _codecMeta = _codecMetaDoc.RootElement;
            var codecDir = Path.GetDirectoryName(codecMetaPath) ?? modelDir;
            _prefill = Open(Path.Combine(modelDir, FileName(_ttsMeta, "prefill")), threadCount);
            _decode = Open(Path.Combine(modelDir, FileName(_ttsMeta, "decode_step")), threadCount);
            _localFixed = Open(Path.Combine(modelDir, FileName(_ttsMeta, "local_fixed_sampled_frame")), threadCount);
            _codecDecode = Open(Path.Combine(codecDir, FileName(_codecMeta, "decode_full")), threadCount);
            using var tokenizerStream = File.OpenRead(Path.Combine(modelDir, "tokenizer.model"));
            _tokenizer = SentencePieceTokenizer.Create(tokenizerStream, false, false, null);
        }

        public MossWaveform Synthesize(string text, string voiceId, CancellationToken ct, List<int[]>? promptAudioCodes = null)
        {
            var normalized = NormalizeText(text);
            var tokenIds = _tokenizer.EncodeToIds(normalized, false, false).ToArray();
            var frameCap = ComputeFrameCap(normalized);
            var rows = BuildVoiceCloneRows(promptAudioCodes ?? GetPromptAudioCodes(voiceId), tokenIds);
            var rowWidth = Nvq + 1;
            var inputIds = new DenseTensor<int>([1, rows.Count, rowWidth]);
            for (var i = 0; i < rows.Count; i++)
            for (var j = 0; j < rowWidth; j++)
                inputIds[0, i, j] = rows[i][j];
            var attentionMask = new DenseTensor<int>([1, rows.Count]);
            for (var i = 0; i < rows.Count; i++) attentionMask[0, i] = 1;

            using var prefillOutputs = _prefill.Run([
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
            ]);
            var outputs = prefillOutputs.ToDictionary(v => v.Name, v => v);
            var globalHidden = ExtractLastHidden(outputs["global_hidden"].AsTensor<float>());
            var past = new Dictionary<string, Tensor<float>>();
            foreach (var name in StringArray(_ttsMeta.GetProperty("onnx").GetProperty("prefill_output_names")).Skip(1))
                past[name.Replace("present_", "past_")] = CloneTensor(outputs[name].AsTensor<float>());

            var pastValidLength = rows.Count;
            var generated = new List<int[]>();
            var previousSets = Enumerable.Range(0, Nvq).Select(_ => new HashSet<int>()).ToArray();
            for (var step = 0; step < frameCap; step++)
            {
                ct.ThrowIfCancellationRequested();
                var frame = RunFixedFrame(globalHidden, previousSets);
                if (frame is null) break;
                generated.Add(frame);
                for (var i = 0; i < Nvq; i++) previousSets[i].Add(frame[i]);

                var nextRow = new DenseTensor<int>([1, 1, rowWidth]);
                for (var j = 0; j < rowWidth; j++) nextRow[0, 0, j] = AudioPad;
                nextRow[0, 0, 0] = AudioAssistantSlot;
                for (var i = 0; i < frame.Length; i++) nextRow[0, 0, i + 1] = frame[i];
                var feeds = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", nextRow),
                    NamedOnnxValue.CreateFromTensor("past_valid_lengths", Vector([pastValidLength])),
                };
                foreach (var name in StringArray(_ttsMeta.GetProperty("onnx").GetProperty("decode_input_names")).Skip(2))
                    feeds.Add(NamedOnnxValue.CreateFromTensor(name, past[name]));

                using var decodeOutputs = _decode.Run(feeds);
                var map = decodeOutputs.ToDictionary(v => v.Name, v => v);
                globalHidden = ExtractLastHidden(map["global_hidden"].AsTensor<float>());
                pastValidLength++;
                past = new Dictionary<string, Tensor<float>>();
                foreach (var name in StringArray(_ttsMeta.GetProperty("onnx").GetProperty("decode_output_names")).Skip(1))
                    past[name.Replace("present_", "past_")] = CloneTensor(map[name].AsTensor<float>());
            }
            return Decode(generated);
        }

        private static DenseTensor<float> CloneTensor(Tensor<float> source)
        {
            var clone = new DenseTensor<float>(source.Dimensions.ToArray());
            source.ToArray().AsSpan().CopyTo(clone.Buffer.Span);
            return clone;
        }

        private int[]? RunFixedFrame(DenseTensor<float> globalHidden, HashSet<int>[] previousSets)
        {
            const int codebook = 1024;
            var seen = new DenseTensor<int>([1, Nvq, codebook]);
            for (var ch = 0; ch < Nvq; ch++)
            foreach (var id in previousSets[ch])
                if (id >= 0 && id < codebook) seen[0, ch, id] = 1;
            var assistantRandom = new DenseTensor<float>([1]);
            assistantRandom[0] = NextFloat();
            var audioRandom = new DenseTensor<float>([1, Nvq]);
            for (var i = 0; i < Nvq; i++) audioRandom[0, i] = NextFloat();
            using var outputs = _localFixed.Run([
                NamedOnnxValue.CreateFromTensor("global_hidden", globalHidden),
                NamedOnnxValue.CreateFromTensor("repetition_seen_mask", seen),
                NamedOnnxValue.CreateFromTensor("assistant_random_u", assistantRandom),
                NamedOnnxValue.CreateFromTensor("audio_random_u", audioRandom),
            ]);
            var map = outputs.ToDictionary(v => v.Name, v => v);
            return ReadFirstInt64(map["should_continue"]) != 0 ? ReadIntArray(map["frame_token_ids"]) : null;
        }

        private MossWaveform Decode(List<int[]> frames)
        {
            if (frames.Count == 0) throw new InvalidOperationException("MOSS generated no frames");
            var audioCodes = new DenseTensor<int>([1, frames.Count, Nvq]);
            for (var i = 0; i < frames.Count; i++)
            for (var j = 0; j < Nvq; j++)
                audioCodes[0, i, j] = frames[i][j];
            using var outputs = _codecDecode.Run([
                NamedOnnxValue.CreateFromTensor("audio_codes", audioCodes),
                NamedOnnxValue.CreateFromTensor("audio_code_lengths", Vector([frames.Count])),
            ]);
            var map = outputs.ToDictionary(v => v.Name, v => v);
            var audio = map["audio"].AsTensor<float>();
            var length = (int)ReadFirstInt64(map["audio_lengths"]);
            var channels = _codecMeta.GetProperty("codec_config").GetProperty("channels").GetInt32();
            var sampleRate = _codecMeta.GetProperty("codec_config").GetProperty("sample_rate").GetInt32();
            var interleaved = new float[length * channels];
            var offset = 0;
            for (var s = 0; s < length; s++)
            for (var ch = 0; ch < channels; ch++)
                interleaved[offset++] = audio[0, ch, s];
            return new MossWaveform(interleaved, sampleRate, channels, frames.Count);
        }

        // 短文で should_continue(EOS) が出ず maxNewFrames まで幻覚生成する MOSS の暴走を防ぐ。
        // 発話文字数に比例した生成frame上限。長文は _maxNewFrames を上限に従来どおり should_continue で停止。
        private int ComputeFrameCap(string normalized)
        {
            // デバッグ用: 明示cap上書き(チューニング専用、本番は未設定)
            var ov = Environment.GetEnvironmentVariable("MOSS_FRAME_CAP");
            if (!string.IsNullOrWhiteSpace(ov) && int.TryParse(ov, out var capOv) && capOv > 0)
                return Math.Min(capOv, _maxNewFrames);
            int speechChars = 0;
            foreach (var ch in normalized)
            {
                if (char.IsWhiteSpace(ch) || IsSentenceEnd(ch) || ch is '、' or ',' or '「' or '」' or '・') continue;
                speechChars++;
            }
            // 短文の幻覚抑制を優先し1文字あたり2frame(@codec ~80ms/frame)。最低4frame。
            // 長文は本来 should_continue(EOS)で cap到達前に停止する(例: speech10 で約16frame停止 < cap20)。
            int cap = speechChars * 2;
            return Math.Clamp(cap, 4, _maxNewFrames);
        }

        private static string NormalizeText(string text)
        {
            var normalized = string.Join(' ', (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (normalized.Length == 0) throw new ArgumentException("text is empty", nameof(text));
            if (ContainsCjk(normalized))
            {
                if (!IsSentenceEnd(normalized[^1])) normalized += "。";
                return normalized;
            }
            if (normalized[0] >= 'a' && normalized[0] <= 'z') normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];
            if (char.IsLetterOrDigit(normalized[^1])) normalized += ".";
            if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 5) normalized = "        " + normalized;
            return normalized;
        }

        private static bool ContainsCjk(string text) => text.Any(c =>
            (c >= '\u4e00' && c <= '\u9fff')
            || (c >= '\u3400' && c <= '\u4dbf')
            || (c >= '\u3040' && c <= '\u30ff')
            || (c >= '\uac00' && c <= '\ud7af'));

        private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '。' or '！' or '？' or '…' or '；' or ';';

        private DenseTensor<float> ExtractLastHidden(Tensor<float> source)
        {
            if (source.Dimensions.Length == 2)
            {
                var result2 = new DenseTensor<float>([source.Dimensions[0], source.Dimensions[1]]);
                source.ToArray().AsSpan().CopyTo(result2.Buffer.Span);
                return result2;
            }
            var hidden = source.Dimensions[2];
            var last = source.Dimensions[1] - 1;
            var result = new DenseTensor<float>([1, hidden]);
            for (var i = 0; i < hidden; i++) result[0, i] = source[0, last, i];
            return result;
        }

        private List<int[]> BuildVoiceCloneRows(List<int[]> promptAudioCodes, int[] textTokenIds)
        {
            var rows = new List<int[]>();
            rows.AddRange(BuildTextRows(IntArray(_manifest.GetProperty("prompt_templates").GetProperty("user_prompt_prefix_token_ids")).Concat([AudioStart]).ToArray()));
            rows.AddRange(BuildAudioRows(promptAudioCodes));
            var suffix = new[] { AudioEnd }
                .Concat(IntArray(_manifest.GetProperty("prompt_templates").GetProperty("user_prompt_after_reference_token_ids")))
                .Concat(textTokenIds)
                .Concat(IntArray(_manifest.GetProperty("prompt_templates").GetProperty("assistant_prompt_prefix_token_ids")))
                .Concat([AudioStart])
                .ToArray();
            rows.AddRange(BuildTextRows(suffix));
            return rows;
        }

        private IEnumerable<int[]> BuildTextRows(int[] tokenIds)
        {
            foreach (var tokenId in tokenIds)
            {
                var row = Enumerable.Repeat(AudioPad, Nvq + 1).ToArray();
                row[0] = tokenId;
                yield return row;
            }
        }

        private IEnumerable<int[]> BuildAudioRows(List<int[]> codes)
        {
            foreach (var codeRow in codes)
            {
                var row = Enumerable.Repeat(AudioPad, Nvq + 1).ToArray();
                row[0] = AudioUserSlot;
                for (var i = 0; i < Math.Min(codeRow.Length, Nvq); i++) row[i + 1] = codeRow[i];
                yield return row;
            }
        }

        private List<int[]> GetPromptAudioCodes(string voiceId)
        {
            foreach (var voice in _manifest.GetProperty("builtin_voices").EnumerateArray())
            {
                if (!string.Equals(voice.GetProperty("voice").GetString(), voiceId, StringComparison.Ordinal)) continue;
                return voice.GetProperty("prompt_audio_codes").EnumerateArray()
                    .Select(row => row.EnumerateArray().Select(v => v.GetInt32()).ToArray())
                    .ToList();
            }
            throw new InvalidOperationException($"MOSS built-in voice not found: {voiceId}");
        }

        private int Nvq => _manifest.GetProperty("tts_config").GetProperty("n_vq").GetInt32();
        private int AudioPad => _manifest.GetProperty("tts_config").GetProperty("audio_pad_token_id").GetInt32();
        private int AudioStart => _manifest.GetProperty("tts_config").GetProperty("audio_start_token_id").GetInt32();
        private int AudioEnd => _manifest.GetProperty("tts_config").GetProperty("audio_end_token_id").GetInt32();
        private int AudioUserSlot => _manifest.GetProperty("tts_config").GetProperty("audio_user_slot_token_id").GetInt32();
        private int AudioAssistantSlot => _manifest.GetProperty("tts_config").GetProperty("audio_assistant_slot_token_id").GetInt32();
        private float NextFloat() => Math.Min(0.99999994f, Math.Max(0f, (float)_random.NextDouble()));

        private static InferenceSession Open(string path, int threadCount)
        {
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(1, threadCount), InterOpNumThreads = 1 };
            return new InferenceSession(path, options);
        }

        private static string ResolveRelative(string baseDir, string relativePath) => Path.GetFullPath(Path.Combine(baseDir, relativePath));
        private static string FileName(JsonElement meta, string key) => meta.GetProperty("files").GetProperty(key).GetString() ?? throw new InvalidOperationException(key);
        private static int[] IntArray(JsonElement element) => element.EnumerateArray().Select(x => x.GetInt32()).ToArray();
        private static string[] StringArray(JsonElement element) => element.EnumerateArray().Select(x => x.GetString()!).ToArray();
        private static DenseTensor<int> Vector(int[] values)
        {
            var tensor = new DenseTensor<int>([values.Length]);
            for (var i = 0; i < values.Length; i++) tensor[i] = values[i];
            return tensor;
        }
        private static long ReadFirstInt64(DisposableNamedOnnxValue value)
        {
            if (value.Value is Tensor<long> longTensor) return longTensor.ToArray()[0];
            if (value.Value is Tensor<int> intTensor) return intTensor.ToArray()[0];
            if (value.Value is Tensor<bool> boolTensor) return boolTensor.ToArray()[0] ? 1 : 0;
            throw new InvalidOperationException($"Unsupported scalar type for {value.Name}: {value.Value?.GetType().FullName}");
        }
        private static int[] ReadIntArray(DisposableNamedOnnxValue value)
        {
            if (value.Value is Tensor<long> longTensor) return longTensor.ToArray().Select(x => (int)x).ToArray();
            if (value.Value is Tensor<int> intTensor) return intTensor.ToArray();
            throw new InvalidOperationException($"Unsupported int array type for {value.Name}: {value.Value?.GetType().FullName}");
        }

        public void Dispose()
        {
            _prefill.Dispose();
            _decode.Dispose();
            _localFixed.Dispose();
            _codecDecode.Dispose();
            _manifestDoc.Dispose();
            _ttsMetaDoc.Dispose();
            _codecMetaDoc.Dispose();
        }
    }
}

public sealed class MossResult
{
    public bool Success { get; init; }
    public byte[]? WavBytes { get; init; }
    public string? Error { get; init; }
    public int FrameCount { get; init; }
    public TimeSpan Elapsed { get; init; }

    public static MossResult Ok(byte[] wavBytes, int frameCount, TimeSpan elapsed) => new() { Success = true, WavBytes = wavBytes, FrameCount = frameCount, Elapsed = elapsed };
    public static MossResult Fail(string error) => new() { Success = false, Error = error };
}

internal readonly record struct MossWaveform(float[] Samples, int SampleRate, int Channels, int FrameCount);
