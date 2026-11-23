using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MobMate.Core.VoiceConversion;

public enum VoiceConversionDownloadStatus { Idle, Downloading, Extracting, Done, Error }

public sealed record VoiceConversionDownloadState(
    VoiceConversionDownloadStatus Status,
    double Progress,
    string? Error);

public sealed record VoiceConversionInstallResult(
    bool Ok,
    string Id,
    bool Installed,
    string Message);

public sealed record VoiceConversionModelSetupState(
    string Status,
    string Message,
    string? PthFile = null,
    string? IndexFile = null);

public sealed class VoiceConversionModelDownloadManager : IDisposable
{
    private static readonly string[] BasicRequiredFiles = ["manifest.json", "license.txt"];

    private readonly ConcurrentDictionary<string, VoiceConversionDownloadState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(120) };
    private readonly ILogger? _logger;

    public VoiceConversionModelDownloadManager(ILogger? logger = null)
    {
        _logger = logger;
    }

    public VoiceConversionDownloadState GetState(string id) =>
        _states.TryGetValue(id, out var s) ? s : new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Idle, 0, null);

    public static bool IsInstalled(string id, string modelRoot) => IsModelDirValid(Path.Combine(modelRoot, id));

    public static VoiceConversionModelSetupState GetSetupState(string id, string modelRoot) =>
        GetSetupStateForDirectory(Path.Combine(modelRoot, id));

    public static VoiceConversionModelSetupState GetSetupStateForDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return new VoiceConversionModelSetupState("missing", "model directory is missing");
        if (IsModelDirValid(dir))
            return new VoiceConversionModelSetupState("ready", "ready");

        var pth = Directory.EnumerateFiles(dir, "*.pth", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(pth))
        {
            var index = Directory.EnumerateFiles(dir, "*.index", SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
            return new VoiceConversionModelSetupState(
                "needs_vcclient_export",
                ".pth model detected; export it to ONNX with external VCClient before use",
                ToManifestPath(dir, pth),
                string.IsNullOrWhiteSpace(index) ? null : ToManifestPath(dir, index));
        }

        return new VoiceConversionModelSetupState("incomplete", "required ONNX/manifest files are missing");
    }

    public static int CountInstalled(string modelRoot)
    {
        if (!Directory.Exists(modelRoot)) return 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(modelRoot))
        {
            if (IsModelDirValid(dir)) count++;
        }
        return count;
    }

    public static int ScanAndNormalizeInstalledModels(string modelRoot, ILogger? logger = null)
    {
        if (!Directory.Exists(modelRoot)) return 0;
        var count = 0;
        foreach (var dir in Directory.EnumerateDirectories(modelRoot))
        {
            var id = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(id) || id.StartsWith(".", StringComparison.Ordinal) || !IsSafeId(id))
                continue;
            try
            {
                var entry = new VoiceConversionModelCatalogItem(
                    Id: id,
                    DisplayName: id,
                    Engine: "rvc_onnx",
                    DownloadUrl: string.Empty,
                    SizeBytes: 0,
                    Sha256: string.Empty,
                    License: "User supplied",
                    LicenseUrl: string.Empty,
                    SourcePageUrl: dir,
                    RiskNotice: "User supplied voice conversion model. Verify rights and usage terms before use.",
                    RequiresUserAgreement: false);
                NormalizeSingleRootDirectory(dir);
                NormalizeRvcCompatibleLayout(dir, entry);
                var state = GetSetupStateForDirectory(dir);
                if (state.Status is "ready" or "needs_vcclient_export") count++;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Voice conversion model auto-detect skipped: {Dir}", dir);
            }
        }
        return count;
    }

    public async Task<VoiceConversionInstallResult> DownloadAndInstallAsync(
        VoiceConversionModelCatalogItem entry,
        string modelRoot,
        bool force,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
            throw new InvalidOperationException("model has no downloadUrl");

        var finalDir = Path.Combine(modelRoot, entry.Id);
        if (Directory.Exists(finalDir) && !force && IsModelDirValid(finalDir))
            return new VoiceConversionInstallResult(true, entry.Id, true, "already_installed");

        var workRoot = Path.Combine(modelRoot, ".tmp");
        var tempZip = Path.Combine(workRoot, $"{entry.Id}-{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(workRoot, $"{entry.Id}-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(workRoot, $"{entry.Id}-backup-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(workRoot);
            Directory.CreateDirectory(extractDir);
            _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, 0, null);

            var hfRepo = TryGetHuggingFaceRepo(entry.DownloadUrl);
            if (hfRepo is not null)
            {
                if (string.Equals(entry.Engine, "seedvc_ncnn", StringComparison.OrdinalIgnoreCase))
                    await DownloadSeedVcNcnnHuggingFaceFilesAsync(hfRepo, extractDir, entry, ct);
                else
                    await DownloadHuggingFaceModelFilesAsync(hfRepo, extractDir, entry, ct);
                _logger?.LogWarning("Voice conversion model has no sha256 verification: {Id}", entry.Id);
            }
            else
            {
                await DownloadFileAsync(entry, tempZip, ct);
                if (!string.IsNullOrWhiteSpace(entry.Sha256)) VerifySha256(tempZip, entry.Sha256);
                else _logger?.LogWarning("Voice conversion model has no sha256 verification: {Id}", entry.Id);

                _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.90, null);
                ExtractZipSafe(tempZip, extractDir);
            }
            NormalizeSingleRootDirectory(extractDir);
            NormalizeRvcCompatibleLayout(extractDir, entry);
            ValidateInstallDir(extractDir, entry.Id);

            Directory.CreateDirectory(modelRoot);
            var hadExisting = Directory.Exists(finalDir);
            if (hadExisting) Directory.Move(finalDir, backupDir);
            try
            {
                Directory.Move(extractDir, finalDir);
            }
            catch
            {
                if (hadExisting && Directory.Exists(backupDir) && !Directory.Exists(finalDir))
                    Directory.Move(backupDir, finalDir);
                throw;
            }
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);

            _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Done, 1.0, null);
            return new VoiceConversionInstallResult(true, entry.Id, true, "installed");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Voice conversion model install failed: {Id}", entry.Id);
            _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Error, 0, ex.Message);
            throw;
        }
        finally
        {
            TryDeleteFile(tempZip);
            TryDeleteDirectory(extractDir);
            TryDeleteDirectory(backupDir);
        }
    }

    public Task<VoiceConversionInstallResult> ImportFromPathAsync(
        string sourcePath,
        VoiceConversionModelCatalogItem entry,
        string modelRoot,
        bool force,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidOperationException("sourcePath is required");
        if (string.IsNullOrWhiteSpace(entry.Id) || !IsSafeId(entry.Id))
            throw new InvalidOperationException("id must contain only letters, digits, '.', '_' or '-'");

        var finalDir = Path.Combine(modelRoot, entry.Id);
        if (Directory.Exists(finalDir) && !force && IsModelDirValid(finalDir))
            return Task.FromResult(new VoiceConversionInstallResult(true, entry.Id, true, "already_installed"));

        var workRoot = Path.Combine(modelRoot, ".tmp");
        var extractDir = Path.Combine(workRoot, $"{entry.Id}-import-{Guid.NewGuid():N}");
        var backupDir = Path.Combine(workRoot, $"{entry.Id}-backup-{Guid.NewGuid():N}");

        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(workRoot);
            Directory.CreateDirectory(extractDir);
            _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.25, null);

            if (File.Exists(sourcePath))
            {
                if (!Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("sourcePath file must be a .zip archive");
                ExtractZipSafe(sourcePath, extractDir);
            }
            else if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, extractDir);
            }
            else
            {
                throw new FileNotFoundException("sourcePath not found", sourcePath);
            }

            NormalizeSingleRootDirectory(extractDir);
            NormalizeRvcCompatibleLayout(extractDir, entry);
            ValidateInstallDir(extractDir, entry.Id);

            Directory.CreateDirectory(modelRoot);
            var hadExisting = Directory.Exists(finalDir);
            if (hadExisting) Directory.Move(finalDir, backupDir);
            try
            {
                Directory.Move(extractDir, finalDir);
            }
            catch
            {
                if (hadExisting && Directory.Exists(backupDir) && !Directory.Exists(finalDir))
                    Directory.Move(backupDir, finalDir);
                throw;
            }
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, recursive: true);
            _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Done, 1.0, null);
            return Task.FromResult(new VoiceConversionInstallResult(true, entry.Id, true, "imported"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Voice conversion model import failed: {Id}", entry.Id);
            _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Error, 0, ex.Message);
            throw;
        }
        finally
        {
            TryDeleteDirectory(extractDir);
            TryDeleteDirectory(backupDir);
        }
    }

    public async Task<VoiceConversionInstallResult> ExportPthWithVcClientAsync(
        string id,
        string modelRoot,
        string vcClientBaseUrl,
        int slot,
        bool acceptPthRisk,
        CancellationToken ct = default)
    {
        if (!acceptPthRisk)
            throw new InvalidOperationException("acceptPthRisk=true is required for .pth export");
        if (string.IsNullOrWhiteSpace(id) || !IsSafeId(id))
            throw new InvalidOperationException("invalid model id");
        if (slot < 0 || slot > 999)
            throw new InvalidOperationException("invalid VCClient slot");

        var dir = Path.Combine(modelRoot, id);
        var state = GetSetupStateForDirectory(dir);
        if (state.Status != "needs_vcclient_export" || string.IsNullOrWhiteSpace(state.PthFile))
            throw new InvalidOperationException("model does not need VCClient export");

        var baseUrl = (string.IsNullOrWhiteSpace(vcClientBaseUrl) ? "http://127.0.0.1:18000" : vcClientBaseUrl).TrimEnd('/') + "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("invalid VCClient URL");
        var pthPath = Path.GetFullPath(Path.Combine(dir, state.PthFile));
        var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!pthPath.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(pthPath))
            throw new FileNotFoundException("pth file not found", state.PthFile);
        var indexPath = string.IsNullOrWhiteSpace(state.IndexFile) ? null : Path.GetFullPath(Path.Combine(dir, state.IndexFile));
        if (!string.IsNullOrWhiteSpace(indexPath) && !indexPath.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
            indexPath = null;

        try
        {
            if (await IsSlotManagerVcClientAsync(baseUri, ct))
                return await ExportPthWithVcClientSlotManagerAsync(id, dir, baseUri, slot, pthPath, indexPath, ct);

            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, 0.05, null);
            var pthName = SafeUploadName(Path.GetFileName(pthPath));
            await UploadVcClientFileAsync(baseUri, pthPath, pthName, ct);
            string? indexName = null;
            if (!string.IsNullOrWhiteSpace(indexPath) && File.Exists(indexPath))
            {
                indexName = SafeUploadName(Path.GetFileName(indexPath));
                await UploadVcClientFileAsync(baseUri, indexPath, indexName, ct);
            }

            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.35, null);
            await LoadVcClientRvcModelAsync(baseUri, slot, pthName, indexName, ct);
            await PostVcClientFormAsync(baseUri, "update_settings", new Dictionary<string, string>
            {
                ["key"] = "modelSlotIndex",
                ["val"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, ct);

            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.60, null);
            using var onnxResp = await _http.GetAsync(new Uri(baseUri, "onnx"), ct);
            onnxResp.EnsureSuccessStatusCode();
            using var onnxDoc = JsonDocument.Parse(await onnxResp.Content.ReadAsStringAsync(ct));
            var root = onnxDoc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : string.Empty;
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("VCClient ONNX export failed");
            var filename = root.TryGetProperty("filename", out var f) ? f.GetString() : string.Empty;
            var path = root.TryGetProperty("path", out var p) ? p.GetString() : string.Empty;
            if (string.IsNullOrWhiteSpace(filename)) filename = Path.GetFileNameWithoutExtension(pthName) + ".onnx";
            filename = SafeUploadName(filename);
            if (string.IsNullOrWhiteSpace(path)) path = "/tmp/" + filename;

            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, 0.80, null);
            var onnxUri = Uri.TryCreate(path, UriKind.Absolute, out var abs) ? abs : new Uri(baseUri, path.TrimStart('/'));
            var destOnnx = Path.Combine(dir, filename);
            await DownloadVcClientFileAsync(onnxUri, destOnnx, ct);

            var entry = new VoiceConversionModelCatalogItem(id, id, "rvc_onnx", string.Empty, 0, string.Empty, "User supplied", string.Empty, dir,
                "User supplied voice conversion model. Verify rights and usage terms before use.", false);
            NormalizeRvcCompatibleLayout(dir, entry);
            ValidateInstallDir(dir, id);
            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Done, 1.0, null);
            return new VoiceConversionInstallResult(true, id, true, "exported");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "VCClient RVC export failed: {Id}", id);
            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Error, 0, FriendlyVcClientExportError(ex, id));
            throw;
        }
    }

    private static string FriendlyVcClientExportError(Exception ex, string id)
    {
        if (ex is HttpRequestException or TaskCanceledException)
            return $"VCClient is required and must be running at the configured URL. Put the model .pth and optional .index under models/voiceconv/{id}/, then start VCClient and retry.";
        return ex.Message;
    }

    private async Task<bool> IsSlotManagerVcClientAsync(Uri baseUri, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(new Uri(baseUri, "api/slot-manager/slots"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<VoiceConversionInstallResult> ExportPthWithVcClientSlotManagerAsync(
        string id,
        string dir,
        Uri baseUri,
        int preferredSlot,
        string pthPath,
        string? indexPath,
        CancellationToken ct)
    {
        var slots = await GetVcClientSlotsAsync(baseUri, ct);
        var sourceSlot = PickVcClientSourceSlot(slots, preferredSlot);
        var destSlot = slots.Where(s => s.Empty && s.Index != sourceSlot).OrderBy(s => s.Index).FirstOrDefault();
        if (destSlot.Index < 0)
            throw new InvalidOperationException("VCClient needs at least two empty slots for .pth -> ONNX export");

        var pthName = SafeUploadName(Path.GetFileName(pthPath));
        _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, 0.05, null);
        await UploadVcClientChunkedFileAsync(baseUri, id, pthPath, pthName, 0.05, 0.28, ct);
        string? indexName = null;
        if (!string.IsNullOrWhiteSpace(indexPath) && File.Exists(indexPath))
        {
            indexName = SafeUploadName(Path.GetFileName(indexPath));
            await UploadVcClientChunkedFileAsync(baseUri, id, indexPath, indexName, 0.28, 0.40, ct);
        }

        _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.45, null);
        await PostVcClientJsonAsync(baseUri, "api/slot-manager/slots", new Dictionary<string, object?>
        {
            ["slot_index"] = sourceSlot,
            ["voice_changer_type"] = "RVC",
            ["name"] = Path.GetFileNameWithoutExtension(pthName),
            ["model_file"] = pthName,
            ["index_file"] = indexName,
            ["embedder"] = "hubert_base_l12"
        }, ct);

        _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.60, null);
        await PostVcClientJsonAsync(baseUri, "api/slot-manager/slots/operation/export_onnx", new Dictionary<string, object?>
        {
            ["slot_index"] = sourceSlot
        }, ct);

        _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.72, null);
        await PostVcClientJsonAsync(baseUri, "api/slot-manager/slots/operation/move_exported_onnx_model", new Dictionary<string, object?>
        {
            ["dst"] = destSlot.Index
        }, ct);
        var exportedSampleRate = await GetVcClientSlotSampleRateAsync(baseUri, destSlot.Index, ct) ?? 48000;

        var workRoot = Path.Combine(Path.GetDirectoryName(dir)!, ".tmp");
        var zipPath = Path.Combine(workRoot, $"{id}-vcclient-{Guid.NewGuid():N}.zip");
        var extractDir = Path.Combine(workRoot, $"{id}-vcclient-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(workRoot);
            Directory.CreateDirectory(extractDir);
            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, 0.82, null);
            await DownloadVcClientSlotExportZipAsync(baseUri, destSlot.Index, zipPath, ct);
            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.90, null);
            ExtractZipSafe(zipPath, extractDir);
            NormalizeSingleRootDirectory(extractDir);
            var entry = new VoiceConversionModelCatalogItem(id, id, "rvc_onnx", string.Empty, 0, string.Empty, "User supplied", string.Empty, dir,
                "User supplied voice conversion model. Verify rights and usage terms before use.", false);
            CopyDirectoryOverwrite(extractDir, dir);
            WriteRvcParams(dir, exportedSampleRate);
            NormalizeRvcCompatibleLayout(dir, entry);
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extractDir);
        }

        ValidateInstallDir(dir, id);
        _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Done, 1.0, null);
        return new VoiceConversionInstallResult(true, id, true, "exported");
    }

    private async Task<VcClientSlot[]> GetVcClientSlotsAsync(Uri baseUri, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(new Uri(baseUri, "api/slot-manager/slots"), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var slots = new List<VcClientSlot>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var index = item.TryGetProperty("slot_index", out var idx) && idx.TryGetInt32(out var n) ? n : -1;
            if (index < 0) continue;
            var empty = !item.TryGetProperty("voice_changer_type", out var type)
                || type.ValueKind == JsonValueKind.Null
                || string.IsNullOrWhiteSpace(type.GetString());
            slots.Add(new VcClientSlot(index, empty));
        }
        return [.. slots];
    }

    private async Task<int?> GetVcClientSlotSampleRateAsync(Uri baseUri, int slot, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(new Uri(baseUri, $"api/slot-manager/slots/{slot}"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("sample_rate", out var sr) && sr.TryGetInt32(out var n) ? n : null;
        }
        catch
        {
            return null;
        }
    }

    private static int PickVcClientSourceSlot(VcClientSlot[] slots, int preferredSlot)
    {
        if (preferredSlot >= 0 && slots.Any(s => s.Index == preferredSlot && s.Empty))
            return preferredSlot;
        var firstEmpty = slots.Where(s => s.Empty).OrderBy(s => s.Index).FirstOrDefault();
        if (firstEmpty.Index < 0)
            throw new InvalidOperationException("VCClient has no empty slot for .pth import");
        return firstEmpty.Index;
    }

    private async Task UploadVcClientChunkedFileAsync(Uri baseUri, string id, string path, string uploadName, double start, double end, CancellationToken ct)
    {
        const int chunkSize = 1024 * 1024;
        var length = new FileInfo(path).Length;
        var chunks = Math.Max(1, (int)Math.Ceiling(length / (double)chunkSize));
        await using var fs = File.OpenRead(path);
        var buffer = new byte[chunkSize];
        for (var i = 0; i < chunks; i++)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(0, chunkSize), ct);
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(buffer, 0, read), "file", uploadName);
            content.Add(new StringContent(uploadName, Encoding.UTF8), "filename");
            content.Add(new StringContent(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8), "index");
            using var resp = await _http.PostAsync(new Uri(baseUri, "api/uploader/upload_file_chunk"), content, ct);
            resp.EnsureSuccessStatusCode();
            _states[id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, start + ((end - start) * (i + 1) / chunks), null);
        }

        using var concat = new MultipartFormDataContent();
        concat.Add(new StringContent(uploadName, Encoding.UTF8), "filename");
        concat.Add(new StringContent(chunks.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8), "filename_chunk_num");
        using var concatResp = await _http.PostAsync(new Uri(baseUri, "api/uploader/concat_uploaded_file_chunk"), concat, ct);
        concatResp.EnsureSuccessStatusCode();
    }

    private async Task PostVcClientJsonAsync(Uri baseUri, string relativePath, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(baseUri, relativePath), content, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task DownloadVcClientSlotExportZipAsync(Uri baseUri, int slot, string destPath, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?> { ["slot_index"] = slot }), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri(baseUri, "api/slot-manager/slots/operation/export"), content, ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Open(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
    }

    private readonly record struct VcClientSlot(int Index, bool Empty);

    private async Task UploadVcClientFileAsync(Uri baseUri, string path, string uploadName, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        await using var fs = File.OpenRead(path);
        content.Add(new StreamContent(fs), "file", uploadName);
        content.Add(new StringContent(uploadName, Encoding.UTF8), "filename");
        using var resp = await _http.PostAsync(new Uri(baseUri, "upload_file"), content, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task LoadVcClientRvcModelAsync(Uri baseUri, int slot, string pthName, string? indexName, CancellationToken ct)
    {
        var files = new List<Dictionary<string, string>>
        {
            new() { ["name"] = pthName, ["kind"] = "rvcModel", ["dir"] = string.Empty }
        };
        if (!string.IsNullOrWhiteSpace(indexName))
            files.Add(new() { ["name"] = indexName, ["kind"] = "rvcIndex", ["dir"] = string.Empty });
        var param = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["voiceChangerType"] = "RVC",
            ["slot"] = slot,
            ["isSampleMode"] = false,
            ["sampleId"] = string.Empty,
            ["files"] = files,
            ["params"] = new Dictionary<string, object?>()
        });
        await PostVcClientFormAsync(baseUri, "load_model", new Dictionary<string, string>
        {
            ["slot"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["isHalf"] = "false",
            ["params"] = param
        }, ct);
    }

    private async Task PostVcClientFormAsync(Uri baseUri, string relativePath, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        foreach (var (key, value) in fields)
            content.Add(new StringContent(value, Encoding.UTF8), key);
        using var resp = await _http.PostAsync(new Uri(baseUri, relativePath), content, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task DownloadVcClientFileAsync(Uri uri, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Open(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
    }

    private static string SafeUploadName(string name)
    {
        name = Path.GetFileName(name);
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(name) ? "model.pth" : name;
    }

    private async Task DownloadFileAsync(VoiceConversionModelCatalogItem entry, string destPath, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? entry.SizeBytes;
        long downloaded = 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Open(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[131072];
        int read;
        while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                var progress = Math.Min(0.89, (double)downloaded / total * 0.89);
                _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, progress, null);
            }
        }
    }

    private async Task DownloadHuggingFaceModelFilesAsync(string repo, string destDir, VoiceConversionModelCatalogItem entry, CancellationToken ct)
    {
        _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, 0.02, null);
        using var treeResp = await _http.GetAsync($"https://huggingface.co/api/models/{repo}/tree/main?recursive=true", ct);
        treeResp.EnsureSuccessStatusCode();
        using var treeDoc = JsonDocument.Parse(await treeResp.Content.ReadAsStringAsync(ct));
        var files = treeDoc.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("type", out var t) && string.Equals(t.GetString(), "file", StringComparison.OrdinalIgnoreCase))
            .Select(e => new HfFile(e.GetProperty("path").GetString() ?? string.Empty, e.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0))
            .Where(f => !string.IsNullOrWhiteSpace(f.Path))
            .ToArray();

        var onnxFiles = files.Where(f => f.Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)).ToArray();
        var model = onnxFiles.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("model.onnx", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(model.Path))
            model = onnxFiles.FirstOrDefault(f => !IsContentEncoderPath(f.Path) && !Path.GetFileName(f.Path).Contains("rmvpe", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f.Path).Contains("f0", StringComparison.OrdinalIgnoreCase));
        var content = onnxFiles.FirstOrDefault(f => IsContentEncoderPath(f.Path));
        if (string.IsNullOrWhiteSpace(model.Path) || string.IsNullOrWhiteSpace(content.Path))
            throw new InvalidDataException("HuggingFace repo does not contain model onnx + content encoder onnx");

        Directory.CreateDirectory(destDir);
        await DownloadHfFileAsync(repo, model.Path, Path.Combine(destDir, SafeRelativePath(model.Path)), ct);
        await DownloadHfFileAsync(repo, content.Path, Path.Combine(destDir, SafeRelativePath(content.Path)), ct);
        var config = files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("params.json", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(config.Path))
            config = files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("config.json", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(config.Path))
            await DownloadHfFileAsync(repo, config.Path, Path.Combine(destDir, SafeRelativePath(config.Path)), ct);
        _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.90, null);
    }

    private async Task DownloadSeedVcNcnnHuggingFaceFilesAsync(string repo, string destDir, VoiceConversionModelCatalogItem entry, CancellationToken ct)
    {
        _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, 0.02, null);
        using var treeResp = await _http.GetAsync($"https://huggingface.co/api/models/{repo}/tree/main?recursive=true", ct);
        treeResp.EnsureSuccessStatusCode();
        using var treeDoc = JsonDocument.Parse(await treeResp.Content.ReadAsStringAsync(ct));
        var files = treeDoc.RootElement.EnumerateArray()
            .Where(e => e.TryGetProperty("type", out var t) && string.Equals(t.GetString(), "file", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.GetProperty("path").GetString() ?? string.Empty)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        var manifestPath = files.FirstOrDefault(p => Path.GetFileName(p).Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidDataException("Seed-VC ncnn repo does not contain manifest.json");

        Directory.CreateDirectory(destDir);
        await DownloadHfFileAsync(repo, manifestPath, Path.Combine(destDir, "manifest.json"), ct);
        var required = EnumerateRequiredFiles(destDir);
        var done = 1;
        foreach (var rel in required.Where(p => !p.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hfPath = files.FirstOrDefault(p => p.Replace('\\', '/').Equals(rel.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(p => Path.GetFileName(p).Equals(Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(hfPath))
                throw new InvalidDataException($"Seed-VC ncnn repo missing required file: {rel}");
            await DownloadHfFileAsync(repo, hfPath, Path.Combine(destDir, rel), ct);
            done++;
            _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Downloading, Math.Min(0.89, 0.02 + (0.87 * done / Math.Max(1, required.Length))), null);
        }
        _states[entry.Id] = new VoiceConversionDownloadState(VoiceConversionDownloadStatus.Extracting, 0.90, null);
    }

    private async Task DownloadHfFileAsync(string repo, string hfPath, string destPath, CancellationToken ct)
    {
        var encodedPath = string.Join('/', hfPath.Split('/').Select(Uri.EscapeDataString));
        using var resp = await _http.GetAsync($"https://huggingface.co/{repo}/resolve/main/{encodedPath}?download=true", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Open(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
    }

    private static string? TryGetHuggingFaceRepo(string downloadUrl)
    {
        if (downloadUrl.StartsWith("hf://", StringComparison.OrdinalIgnoreCase))
            return downloadUrl[5..].Trim('/');
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts[2].Equals("archive", StringComparison.OrdinalIgnoreCase))
            return $"{parts[0]}/{parts[1]}";
        return null;
    }

    private static string SafeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"invalid HuggingFace file path: {path}");
        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsSafeId(string id) => id.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ' ');

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        var sourceFull = Path.GetFullPath(sourceDir);
        var destFull = Path.GetFullPath(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceFull, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destFull, Path.GetRelativePath(sourceFull, dir)));
        foreach (var file in Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destFull, Path.GetRelativePath(sourceFull, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void CopyDirectoryOverwrite(string sourceDir, string destDir)
    {
        var sourceFull = Path.GetFullPath(sourceDir);
        var destFull = Path.GetFullPath(destDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceFull, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destFull, Path.GetRelativePath(sourceFull, dir)));
        foreach (var file in Directory.EnumerateFiles(sourceFull, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destFull, Path.GetRelativePath(sourceFull, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static bool IsContentEncoderPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains("contentvec", StringComparison.OrdinalIgnoreCase)
            || name.Contains("hubert", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("vec-", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteRvcParams(string dir, int sampleRate)
    {
        sampleRate = sampleRate > 0 ? sampleRate : 48000;
        File.WriteAllText(Path.Combine(dir, "params.json"), $$"""
{
  "sample_rate": {{sampleRate}}
}
""");
    }

    private readonly record struct HfFile(string Path, long Size);

    private static void VerifySha256(string path, string expectedHex)
    {
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var expected = expectedHex.Trim().ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(actual),
            System.Text.Encoding.ASCII.GetBytes(expected)))
            throw new InvalidDataException("SHA256 mismatch");
    }

    private static void ExtractZipSafe(string zipPath, string destDir)
    {
        var destFull = Path.GetFullPath(destDir);
        if (!destFull.EndsWith(Path.DirectorySeparatorChar)) destFull += Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var targetPath = Path.GetFullPath(Path.Combine(destDir, entry.FullName));
            if (!targetPath.StartsWith(destFull, StringComparison.Ordinal))
                throw new InvalidDataException($"zip entry path traversal blocked: {entry.FullName}");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: false);
        }
    }

    private static void ValidateInstallDir(string dir, string expectedId)
    {
        foreach (var required in EnumerateRequiredFiles(dir))
        {
            if (!File.Exists(Path.Combine(dir, required)))
                throw new InvalidDataException($"required file missing: {required}");
        }

        var manifestPath = Path.Combine(dir, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var manifest = VoiceConversionModelManifest.Load(manifestPath);
            if (!string.Equals(manifest.Id, expectedId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("manifest id does not match catalog id");
        }
    }

    private static void NormalizeRvcCompatibleLayout(string dir, VoiceConversionModelCatalogItem entry)
    {
        if (IsModelDirValidLoose(dir)) return;

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToArray();
        var model = PickFile(files, p => Path.GetFileName(p).Equals("model.onnx", StringComparison.OrdinalIgnoreCase))
            ?? PickFile(files, p => Path.GetExtension(p).Equals(".onnx", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(p).Contains("contentvec", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(p).Contains("hubert", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(p).StartsWith("vec-", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(p).Contains("rmvpe", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(p).Contains("f0", StringComparison.OrdinalIgnoreCase));
        var config = PickFile(files, p => Path.GetFileName(p).Equals("params.json", StringComparison.OrdinalIgnoreCase))
            ?? PickFile(files, p => Path.GetFileName(p).Equals("config.json", StringComparison.OrdinalIgnoreCase));
        var content = PickFile(files, p => Path.GetExtension(p).Equals(".onnx", StringComparison.OrdinalIgnoreCase)
            && (Path.GetFileName(p).Contains("contentvec", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p).Contains("hubert", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p).StartsWith("vec-", StringComparison.OrdinalIgnoreCase)));
        var f0Model = PickFile(files, p => Path.GetExtension(p).Equals(".onnx", StringComparison.OrdinalIgnoreCase)
            && (Path.GetFileName(p).Contains("rmvpe", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(p).Contains("f0", StringComparison.OrdinalIgnoreCase)));
        var index = PickFile(files, p => Path.GetExtension(p).Equals(".index", StringComparison.OrdinalIgnoreCase));

        if (model is null) return;
        if (content is null)
        {
            var sharedContent = PickSharedContentEncoder(dir);
            if (sharedContent is not null)
            {
                content = Path.Combine(dir, "contentvec.onnx");
                File.Copy(sharedContent, content, overwrite: true);
            }
        }
        if (content is null) return;

        if (config is null)
        {
            config = Path.Combine(dir, "params.json");
            File.WriteAllText(config, "{\n  \"sample_rate\": 48000\n}\n");
        }

        var license = PickFile(files, p => Path.GetFileName(p).Equals("license.txt", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(p).Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(p).StartsWith("LICENSE.", StringComparison.OrdinalIgnoreCase));
        if (license is null)
            File.WriteAllText(Path.Combine(dir, "license.txt"), $"Source: {entry.SourcePageUrl}\nLicense: {entry.License}\nLicense URL: {entry.LicenseUrl}\nUser must verify rights and usage terms before use.\n");
        else if (!Path.GetFileName(license).Equals("license.txt", StringComparison.OrdinalIgnoreCase))
            File.Copy(license, Path.Combine(dir, "license.txt"), overwrite: true);

        var modelRel = ToManifestPath(dir, model);
        var contentRel = ToManifestPath(dir, content);
        var configRel = ToManifestPath(dir, config);
        var f0ModelRel = f0Model is null ? string.Empty : ToManifestPath(dir, f0Model);
        var indexRel = index is null ? string.Empty : ToManifestPath(dir, index);
        var configKey = Path.GetFileName(config).Equals("params.json", StringComparison.OrdinalIgnoreCase) ? "params" : "config";
        var sampleRate = ReadRvcSampleRate(config) ?? 48000;
        File.WriteAllText(Path.Combine(dir, "manifest.json"), $$"""
{
  "schemaVersion": "1.1",
  "id": "{{JsonEscape(entry.Id)}}",
  "displayName": "{{JsonEscape(entry.DisplayName)}}",
  "engine": "{{JsonEscape(string.IsNullOrWhiteSpace(entry.Engine) ? "rvc_onnx" : entry.Engine)}}",
  "format": "rvc-compatible",
  "backend": "vcclient-onnx",
  "version": "unknown",
  "author": "HuggingFace",
  "license": "{{JsonEscape(entry.License)}}",
  "licenseUrl": "{{JsonEscape(entry.LicenseUrl)}}",
  "sourceUrl": "{{JsonEscape(entry.SourcePageUrl)}}",
  "sourceSha256": "{{JsonEscape(entry.Sha256)}}",
  "files": { "model": "{{JsonEscape(modelRel)}}", "{{configKey}}": "{{JsonEscape(configRel)}}", "contentEncoder": "{{JsonEscape(contentRel)}}", "f0Model": "{{JsonEscape(f0ModelRel)}}", "index": {{(index is null ? "null" : $"\"{JsonEscape(indexRel)}\"")}} },
  "runtime": { "sampleRate": {{sampleRate}}, "inputSampleRate": 16000, "outputSampleRate": {{sampleRate}}, "contentEncoderSampleRate": 16000, "f0": true, "f0Method": "pitch-input", "usesIndex": {{(index is null ? "false" : "true")}}, "hopMs": 10, "frameMs": 20, "recommendedLatencyMs": 900, "requiresGpu": false, "supportsCpu": true }
}
""");
    }

    private static int? ReadRvcSampleRate(string configPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            if (root.TryGetProperty("sample_rate", out var sampleRate) && sampleRate.TryGetInt32(out var sr)) return sr;
            if (root.TryGetProperty("sampleRate", out sampleRate) && sampleRate.TryGetInt32(out sr)) return sr;
            if (root.TryGetProperty("samplingRate", out sampleRate) && sampleRate.TryGetInt32(out sr)) return sr;
        }
        catch
        {
        }
        return null;
    }

    private static string ToManifestPath(string rootDir, string path) =>
        Path.GetRelativePath(rootDir, path).Replace('\\', '/');

    private static bool IsModelDirValidLoose(string dir) =>
        File.Exists(Path.Combine(dir, "manifest.json"))
        && File.Exists(Path.Combine(dir, "license.txt"));

    private static string? PickSharedContentEncoder(string dir)
    {
        try
        {
            var root = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Directory.EnumerateFiles(root, "*.onnx", SearchOption.AllDirectories)
                .Where(p => !Path.GetFullPath(p).StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
                .Where(p =>
                {
                    var name = Path.GetFileName(p);
                    return name.Contains("contentvec", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("hubert", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("vec-", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(p => p.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? PickFile(string[] files, Func<string, bool> match) =>
        files.OrderBy(p => p.Length).FirstOrDefault(match);

    private static string JsonEscape(string? value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");

    private static void NormalizeSingleRootDirectory(string dir)
    {
        var hasRequiredAtRoot = BasicRequiredFiles.Any(required => File.Exists(Path.Combine(dir, required)));
        if (hasRequiredAtRoot) return;

        var entries = Directory.EnumerateFileSystemEntries(dir).ToArray();
        if (entries.Length != 1 || !Directory.Exists(entries[0])) return;

        var root = entries[0];
        var rootName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var tempRoot = Path.Combine(Path.GetDirectoryName(root)!, $".__unwrap_{Guid.NewGuid():N}");
        Directory.Move(root, tempRoot);
        foreach (var child in Directory.EnumerateFileSystemEntries(tempRoot))
        {
            Directory.Move(child, Path.Combine(dir, Path.GetFileName(child)));
        }
        Directory.Delete(tempRoot, recursive: true);
        _ = rootName;
    }

    private static string[] EnumerateRequiredFiles(string dir)
    {
        var required = new HashSet<string>(BasicRequiredFiles, StringComparer.OrdinalIgnoreCase);
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath)) return [.. required];

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in files.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var path = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal))
                    throw new InvalidDataException($"invalid manifest file path: {path}");
                required.Add(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            }
        }

        return [.. required];
    }

    private static bool IsModelDirValid(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        try
        {
            ValidateInstallDir(dir, Path.GetFileName(dir));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    public void Dispose() => _http.Dispose();
}
