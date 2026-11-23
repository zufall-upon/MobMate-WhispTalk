using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MobMate.Core.Tts;

/// <summary>
/// Downloads MOSS TTS model files from HuggingFace one file at a time.
/// Directory structure after download:
///   destRoot/{id}/MOSS-TTS-Nano-100M-ONNX/  (TTS files)
///   destRoot/{id}/MOSS-Audio-Tokenizer-Nano-ONNX/  (Codec files)
/// The TTS model dir is passed to MossTtsService.SetConfig.
/// </summary>
public sealed class MossModelDownloadManager : IDisposable
{
    // Approximate total bytes for each known model (for progress reporting)
    private const long MossNano100MApproxBytes = 763L * 1024 * 1024; // ~728MB + some overhead

    private static readonly Dictionary<string, ModelDef> KnownModels
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["moss-tts-nano-100m-onnx"] = new ModelDef(
            TtsRepo: "OpenMOSS-Team/MOSS-TTS-Nano-100M-ONNX",
            TtsSubDir: "MOSS-TTS-Nano-100M-ONNX",
            TtsFiles:
            [
                "browser_poc_manifest.json", "tts_browser_onnx_meta.json", "tokenizer.model",
                "moss_tts_prefill.onnx", "moss_tts_decode_step.onnx", "moss_tts_local_decoder.onnx",
                "moss_tts_local_cached_step.onnx", "moss_tts_local_fixed_sampled_frame.onnx",
                "moss_tts_global_shared.data", "moss_tts_local_shared.data"
            ],
            CodecRepo: "OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX",
            CodecSubDir: "MOSS-Audio-Tokenizer-Nano-ONNX",
            CodecFiles:
            [
                "codec_browser_onnx_meta.json",
                "moss_audio_tokenizer_decode_full.onnx", "moss_audio_tokenizer_decode_step.onnx",
                "moss_audio_tokenizer_decode_shared.data",
                "moss_audio_tokenizer_encode.onnx", "moss_audio_tokenizer_encode.data"
            ],
            ApproxTotalBytes: MossNano100MApproxBytes
        ),
        // int8 quantized variant (~274MB total): smaller TTS files, same codec
        ["moss-tts-nano-100m-onnx-int8"] = new ModelDef(
            TtsRepo: "REALBITS/MOSS-TTS-Nano-100M-ONNX-int8",
            TtsSubDir: "MOSS-TTS-Nano-100M-ONNX-int8",
            TtsFiles:
            [
                "browser_poc_manifest.json", "tts_browser_onnx_meta.json", "tokenizer.model",
                "moss_tts_prefill.onnx", "moss_tts_decode_step.onnx",
                "moss_tts_local_fixed_sampled_frame.onnx",
                "moss_tts_global_shared_int8.data", "moss_tts_local_fixed_sampled_frame_int8.data"
            ],
            CodecRepo: "OpenMOSS-Team/MOSS-Audio-Tokenizer-Nano-ONNX",
            CodecSubDir: "MOSS-Audio-Tokenizer-Nano-ONNX",
            CodecFiles:
            [
                "codec_browser_onnx_meta.json",
                "moss_audio_tokenizer_decode_full.onnx", "moss_audio_tokenizer_decode_step.onnx",
                "moss_audio_tokenizer_decode_shared.data",
                "moss_audio_tokenizer_encode.onnx", "moss_audio_tokenizer_encode.data"
            ],
            ApproxTotalBytes: 280L * 1024 * 1024
        )
    };

    private readonly ConcurrentDictionary<string, RichDownloadState> _states = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(120) };

    public RichDownloadState GetState(string id) =>
        _states.TryGetValue(id, out var s) ? s
        : new RichDownloadState(RichDownloadStatus.Idle, 0, null);

    public static bool IsInstalled(string id, string modelRoot)
    {
        var outer = Path.Combine(modelRoot, id);
        // Check any direct subdirectory that is a valid MOSS model dir
        if (Directory.Exists(outer))
        {
            foreach (var sub in Directory.EnumerateDirectories(outer))
                if (MossTtsService.IsModelDirValid(sub)) return true;
        }
        // Fallback: flat structure (manually placed)
        return MossTtsService.IsModelDirValid(outer);
    }

    /// <summary>Start background download. No-op if already in progress.</summary>
    public void StartDownload(string id, string modelRoot, CancellationToken ct = default)
    {
        if (!KnownModels.ContainsKey(id))
            throw new InvalidOperationException($"Unknown MOSS model id: '{id}'");
        if (_states.TryGetValue(id, out var cur) && cur.Status == RichDownloadStatus.Downloading)
            return;
        _states[id] = new RichDownloadState(RichDownloadStatus.Downloading, 0, null);
        _ = Task.Run(() => DoDownloadAsync(id, modelRoot, ct), CancellationToken.None);
    }

    private async Task DoDownloadAsync(string id, string modelRoot, CancellationToken ct)
    {
        var def = KnownModels[id];
        var outerDir = Path.Combine(modelRoot, id);
        var ttsDir = Path.Combine(outerDir, def.TtsSubDir);
        var codecDir = Path.Combine(outerDir, def.CodecSubDir);
        try
        {
            Directory.CreateDirectory(ttsDir);
            Directory.CreateDirectory(codecDir);

            long totalBytes = def.ApproxTotalBytes;
            long downloadedBytes = 0L;

            foreach (var filename in def.TtsFiles)
            {
                ct.ThrowIfCancellationRequested();
                var url = $"https://huggingface.co/{def.TtsRepo}/resolve/main/{filename}";
                downloadedBytes += await DownloadFileAsync(url, Path.Combine(ttsDir, filename), downloadedBytes, totalBytes, id, ct);
            }
            foreach (var filename in def.CodecFiles)
            {
                ct.ThrowIfCancellationRequested();
                var url = $"https://huggingface.co/{def.CodecRepo}/resolve/main/{filename}";
                downloadedBytes += await DownloadFileAsync(url, Path.Combine(codecDir, filename), downloadedBytes, totalBytes, id, ct);
            }

            WriteProperties(id, outerDir);
            _states[id] = new RichDownloadState(RichDownloadStatus.Done, 1.0, null);
        }
        catch (Exception ex)
        {
            _states[id] = new RichDownloadState(RichDownloadStatus.Error, 0, ex.Message);
        }
    }

    private async Task<long> DownloadFileAsync(
        string url, string destPath,
        long downloadedSoFar, long totalBytes,
        string id, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Open(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[131072];
        int read;
        long fileBytes = 0L;
        while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            fileBytes += read;
            if (totalBytes > 0)
            {
                var progress = Math.Min(0.99, (double)(downloadedSoFar + fileBytes) / totalBytes);
                _states[id] = new RichDownloadState(RichDownloadStatus.Downloading, progress, null);
            }
        }
        await dst.FlushAsync(ct);
        return fileBytes;
    }

    private static void WriteProperties(string id, string outerDir)
    {
        var isInt8 = id.EndsWith("-int8", StringComparison.OrdinalIgnoreCase);
        var lines = new[]
        {
            "#MobMate MOSS TTS model metadata",
            $"id={id}",
            isInt8 ? "display_name=MOSS-TTS-Nano 100M ONNX int8 (smaller)" : "display_name=MOSS-TTS-Nano 100M ONNX",
            "engine=moss",
            "language=multi",
            isInt8 ? "license=see-source" : "license=Apache-2.0 (model weights)",
            isInt8 ? "source_page_url=https://huggingface.co/REALBITS/MOSS-TTS-Nano-100M-ONNX-int8"
                   : "source_page_url=https://huggingface.co/OpenMOSS-Team/MOSS-TTS-Nano-100M-ONNX",
            "voices=Junhao,Zhiming,Weiguo,Xiaoyu,Yuewen,Lingyu,Trump,Ava,Bella,Adam,Nathan,Soyo,Saki,Mortis,Umiri,Mei,Anon,Arisa"
        };
        File.WriteAllLines(Path.Combine(outerDir, "mobmate-model.properties"), lines, Encoding.UTF8);
    }

    public void Delete(string id, string modelRoot)
    {
        var dir = Path.Combine(modelRoot, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        _states.TryRemove(id, out _);
    }

    public void Dispose() => _http.Dispose();

    private sealed record ModelDef(
        string TtsRepo, string TtsSubDir, string[] TtsFiles,
        string CodecRepo, string CodecSubDir, string[] CodecFiles,
        long ApproxTotalBytes);
}
