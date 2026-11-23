using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MobMate.Core.Tts;

/// <summary>One entry in rich_tts_catalog.json (Kokoro/multi-lang rich TTS models).</summary>
public sealed record RichVoiceEntry(
    string Id,
    string Name,
    string Engine,        // "kokoro"
    string Language,      // "multi" | "en" | "zh" etc.
    string LanguageDisplay,
    string Quality,
    string Runtime,       // "sherpa_onnx"
    string Platform,      // "all" | "windows"
    string License,
    string SourcePageUrl,
    string TarUrl,        // URL to tar.bz2 tarball
    int SizeMb,
    int DefaultSpeakerId,
    int SpeakerCount,
    RichVoiceSpeaker[] Speakers
);

public sealed record RichVoiceSpeaker(int Id, string Name);

/// <summary>Loads rich_tts_catalog.json.</summary>
public static class RichVoiceCatalog
{
    public static RichVoiceEntry[] Load(string catalogJsonPath)
    {
        if (!File.Exists(catalogJsonPath)) return [];
        try
        {
            var json = File.ReadAllText(catalogJsonPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("voices", out var voices)) return [];
            var list = new List<RichVoiceEntry>();
            foreach (var v in voices.EnumerateArray())
            {
                string G(string k) => v.TryGetProperty(k, out var p) ? p.GetString() ?? "" : "";
                int GI(string k) => v.TryGetProperty(k, out var p) && p.TryGetInt32(out var i) ? i : 0;
                var speakers = new List<RichVoiceSpeaker>();
                if (v.TryGetProperty("speakers", out var spArr) && spArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sp in spArr.EnumerateArray())
                    {
                        int id = sp.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var sid) ? sid : -1;
                        string name = sp.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? string.Empty : string.Empty;
                        if (id >= 0) speakers.Add(new RichVoiceSpeaker(id, string.IsNullOrWhiteSpace(name) ? id.ToString() : name));
                    }
                }
                var entry = new RichVoiceEntry(
                    Id: G("id"), Name: G("name"), Engine: G("engine"),
                    Language: G("language"), LanguageDisplay: G("languageDisplay"),
                    Quality: G("quality"), Runtime: G("runtime"), Platform: G("platform"),
                    License: G("license"), SourcePageUrl: G("sourcePageUrl"),
                    TarUrl: G("tarUrl"), SizeMb: GI("sizeMb"),
                    DefaultSpeakerId: GI("defaultSpeakerId"), SpeakerCount: GI("speakerCount"),
                    Speakers: speakers.ToArray());
                if (!string.IsNullOrWhiteSpace(entry.Id)) list.Add(entry);
            }
            return [.. list];
        }
        catch { return []; }
    }
}

public enum RichDownloadStatus { Idle, Downloading, Extracting, Done, Error }

public sealed record RichDownloadState(
    RichDownloadStatus Status,
    double Progress,
    string? Error
);

/// <summary>
/// Downloads and extracts a Kokoro (sherpa-onnx) model tarball.
/// Progress 0.0–0.9 = download phase, 0.9–1.0 = tar extraction phase.
/// Extraction uses the system `tar` command (available on Windows 10+/Linux).
/// </summary>
public sealed class KokoroModelDownloadManager : IDisposable
{
    private readonly ConcurrentDictionary<string, RichDownloadState> _states = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(90) };
    private readonly Func<string, string, CancellationToken, Task> _extractArchive;

    public KokoroModelDownloadManager() : this(LinuxBzip2Fallback.ExtractAsync) { }

    internal KokoroModelDownloadManager(Func<string, string, CancellationToken, Task> extractArchive) =>
        _extractArchive = extractArchive;

    public RichDownloadState GetState(string id) =>
        _states.TryGetValue(id, out var s) ? s
        : new RichDownloadState(RichDownloadStatus.Idle, 0, null);

    // Management presence is deliberately weaker than native initialization readiness:
    // an incomplete model must remain removable.
    public static bool IsInstalled(string id, string modelRoot)
    {
        var dir = Path.Combine(modelRoot, id);
        return Directory.Exists(dir) &&
            (File.Exists(Path.Combine(dir, "model.int8.onnx")) || File.Exists(Path.Combine(dir, "model.onnx")) ||
             File.Exists(Path.Combine(dir, "voices.bin")) || File.Exists(Path.Combine(dir, "tokens.txt")) ||
             File.Exists(Path.Combine(dir, "mobmate-model.properties")) || Directory.Exists(Path.Combine(dir, "espeak-ng-data")));
    }

    /// <summary>Start background download + extraction. No-op if already in progress.</summary>
    public void StartDownload(RichVoiceEntry entry, string modelRoot, CancellationToken ct = default)
    {
        if (_states.TryGetValue(entry.Id, out var cur) && cur.Status == RichDownloadStatus.Downloading)
            return;
        _states[entry.Id] = new RichDownloadState(RichDownloadStatus.Downloading, 0, null);
        _ = Task.Run(() => DoDownloadAsync(entry, modelRoot, ct), CancellationToken.None);
    }

    private async Task DoDownloadAsync(RichVoiceEntry entry, string modelRoot, CancellationToken ct)
    {
        var tarballPath = Path.Combine(Path.GetTempPath(), $"kokoro-{entry.Id}-{Guid.NewGuid():N}.tar.bz2");
        try
        {
            Directory.CreateDirectory(modelRoot);

            // Phase 1: Download tarball (progress 0.0 – 0.9)
            using var resp = await _http.GetAsync(entry.TarUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Open(tarballPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buf = new byte[131072];
            int read;
            while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                    _states[entry.Id] = new RichDownloadState(RichDownloadStatus.Downloading,
                        Math.Min(0.89, (double)downloaded / total * 0.9), null);
            }
            await dst.FlushAsync(ct);
            dst.Close();

            _states[entry.Id] = new RichDownloadState(RichDownloadStatus.Extracting, 0.90, null);

            // Phase 2: Extract tarball via `tar` (progress 0.9 – 1.0)
            // The tarball extracts to a subdirectory named after the model id.
            // We extract directly into modelRoot so: modelRoot/<id>/...
            await _extractArchive(tarballPath, modelRoot, ct);
            ct.ThrowIfCancellationRequested();

            // Write mobmate-model.properties
            await WritePropertiesAsync(entry, Path.Combine(modelRoot, entry.Id), ct);

            _states[entry.Id] = new RichDownloadState(RichDownloadStatus.Done, 1.0, null);
        }
        catch (Exception ex)
        {
            _states[entry.Id] = new RichDownloadState(RichDownloadStatus.Error, 0, ex.Message);
        }
        finally
        {
            try { if (File.Exists(tarballPath)) File.Delete(tarballPath); } catch { }
        }
    }

    private static async Task WritePropertiesAsync(RichVoiceEntry entry, string destDir, CancellationToken ct)
    {
        static string Esc(string s) => s.Replace(":", "\\:");
        var sb = new StringBuilder();
        sb.AppendLine("#MobMate rich TTS model metadata");
        sb.AppendLine($"id={entry.Id}");
        sb.AppendLine($"display_name={entry.Name}");
        sb.AppendLine($"engine={entry.Engine}");
        sb.AppendLine($"language={entry.Language}");
        sb.AppendLine($"license={entry.License}");
        sb.AppendLine($"source_page_url={Esc(entry.SourcePageUrl)}");
        sb.AppendLine($"tar_url={Esc(entry.TarUrl)}");
        sb.AppendLine($"runtime={entry.Runtime}");
        sb.AppendLine($"platform={entry.Platform}");
        sb.AppendLine($"default_speaker_id={entry.DefaultSpeakerId}");
        sb.AppendLine($"speaker_count={entry.SpeakerCount}");
        if (entry.Speakers.Length > 0)
            sb.AppendLine($"speakers={string.Join(",", entry.Speakers.Select(s => $"{s.Id}:{Esc(s.Name)}"))}");
        Directory.CreateDirectory(destDir);
        await File.WriteAllTextAsync(Path.Combine(destDir, "mobmate-model.properties"), sb.ToString(), Encoding.UTF8, ct);
    }

    public void Delete(string id, string modelRoot)
    {
        var dir = Path.Combine(modelRoot, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        _states.TryRemove(id, out _);
    }

    public void Dispose() => _http.Dispose();
}
