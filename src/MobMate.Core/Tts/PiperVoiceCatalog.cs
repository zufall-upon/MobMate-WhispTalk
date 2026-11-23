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

/// <summary>Piper voice catalog entry (one row in piper_voice_catalog.json).</summary>
public sealed record PiperVoiceEntry(
    string Id,
    string Name,
    string Language,
    string LanguageDisplay,
    string Quality,
    string Runtime,       // "piper" | "piper_plus"
    string Platform,      // "all" | "windows"
    string License,
    string SourcePageUrl,
    string OnnxUrl,
    string ConfigUrl,
    int SizeMb
);

public enum VoiceDownloadStatus { Idle, Downloading, Done, Error }

public sealed record VoiceDownloadState(
    VoiceDownloadStatus Status,
    double Progress,   // 0.0 – 1.0
    string? Error
);

/// <summary>Manages async voice model downloads. One instance per server lifetime.</summary>
public sealed class PiperVoiceDownloadManager : IDisposable
{
    private readonly ConcurrentDictionary<string, VoiceDownloadState> _states = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(60) };

    public VoiceDownloadState GetState(string id) =>
        _states.TryGetValue(id, out var s) ? s
            : new VoiceDownloadState(VoiceDownloadStatus.Idle, 0, null);

    /// <summary>Returns true if <paramref name="id"/> is already installed (directory + *.onnx present).</summary>
    public static bool IsInstalled(string id, string modelRoot)
    {
        var dir = Path.Combine(modelRoot, id);
        if (!Directory.Exists(dir)) return false;
        foreach (var f in Directory.EnumerateFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly))
        {
            if (!Path.GetFileName(f).Contains(".cpu.opt.", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Start a background HTTP download for a regular Piper voice. No-op if already in progress.</summary>
    public void StartDownload(PiperVoiceEntry entry, string modelRoot, CancellationToken ct = default)
    {
        if (_states.TryGetValue(entry.Id, out var cur) && cur.Status == VoiceDownloadStatus.Downloading)
            return;
        _states[entry.Id] = new VoiceDownloadState(VoiceDownloadStatus.Downloading, 0, null);
        _ = Task.Run(() => DoDownloadAsync(entry, modelRoot, ct), CancellationToken.None);
    }

    private async Task DoDownloadAsync(PiperVoiceEntry entry, string modelRoot, CancellationToken ct)
    {
        var destDir = Path.Combine(modelRoot, entry.Id);
        try
        {
            Directory.CreateDirectory(destDir);

            // onnx file
            var onnxFile = Path.Combine(destDir, UrlFileName(entry.OnnxUrl));
            await DownloadFileAsync(entry.OnnxUrl, onnxFile, 0.0, string.IsNullOrEmpty(entry.ConfigUrl) ? 1.0 : 0.9, entry.Id, ct);

            // config json (optional)
            if (!string.IsNullOrEmpty(entry.ConfigUrl))
            {
                var cfgFile = Path.Combine(destDir, UrlFileName(entry.ConfigUrl));
                await DownloadFileAsync(entry.ConfigUrl, cfgFile, 0.9, 1.0, entry.Id, ct);
            }

            // write mobmate-model.properties
            await WritePropertiesAsync(entry, destDir, ct);

            _states[entry.Id] = new VoiceDownloadState(VoiceDownloadStatus.Done, 1.0, null);
        }
        catch (Exception ex)
        {
            _states[entry.Id] = new VoiceDownloadState(VoiceDownloadStatus.Error, 0, ex.Message);
            try { if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true); } catch { /* ignore */ }
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, double pStart, double pEnd, string id, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        long downloaded = 0L;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Open(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buf = new byte[131072]; // 128 KB
        int read;
        while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                var fp = (double)downloaded / total;
                _states[id] = new VoiceDownloadState(VoiceDownloadStatus.Downloading,
                    pStart + fp * (pEnd - pStart), null);
            }
        }
    }

    private static async Task WritePropertiesAsync(PiperVoiceEntry entry, string destDir, CancellationToken ct)
    {
        static string Esc(string s) => s.Replace(":", "\\:");
        var sb = new StringBuilder();
        sb.AppendLine("#MobMate piper voice model metadata");
        sb.AppendLine($"id={entry.Id}");
        sb.AppendLine($"display_name={entry.Name}");
        sb.AppendLine($"language={entry.Language}");
        sb.AppendLine($"license={entry.License}");
        sb.AppendLine($"source_page_url={Esc(entry.SourcePageUrl)}");
        sb.AppendLine($"model_url={Esc(entry.OnnxUrl)}");
        if (!string.IsNullOrEmpty(entry.ConfigUrl))
            sb.AppendLine($"config_url={Esc(entry.ConfigUrl)}");
        sb.AppendLine($"runtime={entry.Runtime}");
        sb.AppendLine($"platform={entry.Platform}");
        await File.WriteAllTextAsync(Path.Combine(destDir, "mobmate-model.properties"), sb.ToString(), Encoding.UTF8, ct);
    }

    /// <summary>Delete the model directory. No-op if not installed.</summary>
    public void Delete(string id, string modelRoot)
    {
        var dir = Path.Combine(modelRoot, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        _states.TryRemove(id, out _);
    }

    private static string UrlFileName(string url)
    {
        var raw = url.Split('?')[0];
        var slash = raw.LastIndexOf('/');
        return slash >= 0 ? raw[(slash + 1)..] : raw;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Loads the piper_voice_catalog.json catalog file.</summary>
public static class PiperVoiceCatalog
{
    /// <summary>Load catalog entries from the JSON file. Returns empty array on any error.</summary>
    public static PiperVoiceEntry[] Load(string catalogJsonPath)
    {
        if (!File.Exists(catalogJsonPath)) return [];
        try
        {
            var json = File.ReadAllText(catalogJsonPath, Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("voices", out var voices)) return [];

            var list = new List<PiperVoiceEntry>();
            foreach (var v in voices.EnumerateArray())
            {
                string G(string k) => v.TryGetProperty(k, out var p) ? p.GetString() ?? "" : "";
                int GI(string k) => v.TryGetProperty(k, out var p) && p.TryGetInt32(out var i) ? i : 0;
                var entry = new PiperVoiceEntry(
                    Id: G("id"),
                    Name: G("name"),
                    Language: G("language"),
                    LanguageDisplay: G("languageDisplay"),
                    Quality: G("quality"),
                    Runtime: G("runtime"),
                    Platform: G("platform"),
                    License: G("license"),
                    SourcePageUrl: G("sourcePageUrl"),
                    OnnxUrl: G("onnxUrl"),
                    ConfigUrl: G("configUrl"),
                    SizeMb: GI("sizeMb")
                );
                if (!string.IsNullOrWhiteSpace(entry.Id)) list.Add(entry);
            }
            return [.. list];
        }
        catch
        {
            return [];
        }
    }
}
