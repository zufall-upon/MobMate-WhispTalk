using System.Globalization;
using System.Diagnostics;
using System.Text.Json;

// Owns only the loopback created by this process. Pactl module IDs and
// source-output indexes are different namespaces.
internal sealed class LinuxMonitorLoopback
{
    private readonly Func<string, CancellationToken, Task<string?>> _pactl;
    private uint? _moduleId;
    private string? _marker;
    private string? _key;
    private volatile bool _stopped;
    private bool _loadUnconfirmed;

    internal LinuxMonitorLoopback(Func<string, CancellationToken, Task<string?>> pactl) => _pactl = pactl;

    internal string? CleanupError { get; private set; }

    internal async Task<string?> EnsureAsync(string sourceSink, string defaultSink, int volumePercent, CancellationToken ct = default)
    {
        if (_stopped) return "linux_monitor_stopped";
        ct.ThrowIfCancellationRequested();
        if (_loadUnconfirmed)
        {
            var modules = await _pactl("list short modules", ct);
            ct.ThrowIfCancellationRequested();
            if (_stopped) return "linux_monitor_stopped";
            if (modules == null || _marker == null || !TryFindOwnedModule(modules, null, _marker, out var foundId))
                return "linux_monitor_loopback_load_unconfirmed";

            _moduleId = foundId;
            _loadUnconfirmed = false;
            if (!foundId.HasValue)
            {
                _marker = null;
                _key = null;
            }
        }
        CleanupError = null;
        var key = $"{sourceSink}->{defaultSink}";
        if (_moduleId.HasValue && !string.Equals(_key, key, StringComparison.Ordinal))
        {
            var error = await UnloadAsync(ct);
            if (error != null) return error;
        }

        if (!_moduleId.HasValue)
        {
            if (_stopped) return "linux_monitor_stopped";
            ct.ThrowIfCancellationRequested();
            var marker = "MobMateTtsMonitorPlayback-" + Guid.NewGuid().ToString("N");
            var args = $"load-module module-loopback source={sourceSink}.monitor sink={defaultSink} latency_msec=30 source_dont_move=true sink_dont_move=true source_output_properties=media.name={marker}";
            // A cancelled load may already have reached the server even if its
            // module ID never reaches us. Do not claim that cleanup succeeded.
            _marker = marker;
            _key = key;
            _loadUnconfirmed = true;
            var result = await _pactl(args, ct);
            if (!TryId(result, out var moduleId)) return "linux_monitor_loopback_load_failed";
            _moduleId = moduleId;
            _loadUnconfirmed = false;
        }

        ct.ThrowIfCancellationRequested();
        if (_stopped) return "linux_monitor_stopped";
        var output = await _pactl("--format=json list source-outputs", ct);
        ct.ThrowIfCancellationRequested();
        if (_stopped) return "linux_monitor_stopped";
        var errorReason = output == null ? "linux_monitor_source_output_list_failed" : null;
        uint streamId = 0;
        if (errorReason == null && !TryFindSourceOutput(output!, _moduleId.Value, _marker!, out streamId))
            errorReason = "linux_monitor_source_output_not_unique";
        if (errorReason == null && await _pactl($"set-source-output-volume {streamId} {volumePercent}%", ct) == null)
            errorReason = "linux_monitor_volume_set_failed";
        if (errorReason == null) return null;

        CleanupError = await UnloadAsync(ct);
        return errorReason;
    }

    // This flag is set before waiting for the host gate, including when an
    // older configuration is still inside it.
    internal void RequestStop() => _stopped = true;

    internal Task<string?> StopAsync(CancellationToken ct = default)
    {
        RequestStop();
        return UnloadAsync(ct);
    }

    internal async Task<string?> StopWithinAsync(SemaphoreSlim gate, TimeSpan timeout)
    {
        RequestStop();
        using var deadline = new CancellationTokenSource();
        var cleanup = StopUnderGateAsync(gate, deadline.Token);
        if (await Task.WhenAny(cleanup, Task.Delay(timeout)) != cleanup)
        {
            // Cancel synchronously so the owned-child termination callback has
            // run before shutdown returns, without waiting for a broken service.
            deadline.Cancel();
            return "linux_monitor_shutdown_timed_out";
        }
        return await cleanup;
    }

    private async Task<string?> StopUnderGateAsync(SemaphoreSlim gate, CancellationToken ct)
    {
        try
        {
            await gate.WaitAsync(ct);
            try { return await StopAsync(ct); }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return "linux_monitor_shutdown_timed_out";
        }
        catch (Exception)
        {
            return "linux_monitor_shutdown_failed";
        }
    }

    internal async Task<string?> UnloadAsync(CancellationToken ct = default)
    {
        if (!_moduleId.HasValue && !_loadUnconfirmed) return null;
        ct.ThrowIfCancellationRequested();
        // Pactl's JSON module list does not include module indexes. Its short
        // tab-separated list does, and also retains the original arguments.
        var modules = await _pactl("list short modules", ct);
        ct.ThrowIfCancellationRequested();
        if (modules == null || _marker == null || !TryFindOwnedModule(modules, _moduleId, _marker, out var foundId))
            return _loadUnconfirmed ? "linux_monitor_loopback_load_unconfirmed" : "linux_monitor_loopback_ownership_unverified";
        if (foundId.HasValue && await _pactl($"unload-module {foundId.Value}", ct) == null)
            return "linux_monitor_loopback_unload_failed";
        ct.ThrowIfCancellationRequested();
        _moduleId = null;
        _marker = null;
        _key = null;
        _loadUnconfirmed = false;
        return null;
    }

    // The Process instance is the child started by this operation, never a
    // process discovered by name or ID. Cancellation must not leave it running.
    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunChildAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("pactl process did not start");
        var terminationGate = new object();
        var terminationRequested = false;
        Exception? killFailure = null;
        void TerminateChild()
        {
            lock (terminationGate)
            {
                if (terminationRequested) return;
                try
                {
                    if (!child.HasExited) child.Kill();
                    terminationRequested = true;
                    killFailure = null;
                }
                catch (Exception ex) { killFailure = ex; }
            }
        }
        using var cancelChild = ct.Register(TerminateChild);
        try
        {
            var stdout = child.StandardOutput.ReadToEndAsync(ct);
            var stderr = child.StandardError.ReadToEndAsync(ct);
            await child.WaitForExitAsync(ct);
            return (child.ExitCode, await stdout.WaitAsync(ct), await stderr.WaitAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The await continuation can race the cancellation callback. It
            // must not unregister that callback before termination is asked for.
            TerminateChild();
            cancelChild.Dispose();
            if (killFailure != null) throw new InvalidOperationException("could not terminate owned pactl process", killFailure);
            throw;
        }
    }

    private static bool TryId(string? value, out uint id)
        => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id != uint.MaxValue;

    private static bool TryId(JsonElement value, out uint id)
    {
        id = 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetUInt32(out id) && id != uint.MaxValue,
            JsonValueKind.String => TryId(value.GetString(), out id),
            _ => false
        };
    }

    private static bool TryFindSourceOutput(string json, uint moduleId, string marker, out uint streamId)
    {
        streamId = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            var count = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var index) || !TryId(index, out var id) ||
                    !item.TryGetProperty("owner_module", out var owner)) return false;
                if (owner.ValueKind == JsonValueKind.Null) continue;
                if (!TryId(owner, out var ownerId)) return false;
                if (ownerId != moduleId) continue;
                count++;
                if (!item.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                    !properties.TryGetProperty("media.name", out var name) || name.ValueKind != JsonValueKind.String ||
                    !string.Equals(name.GetString(), marker, StringComparison.Ordinal)) return false;
                streamId = id;
            }
            return count == 1;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryFindOwnedModule(string modules, uint? expectedId, string marker, out uint? foundId)
    {
        foundId = null;
        var ids = new HashSet<uint>();
        var property = "source_output_properties=media.name=" + marker;
        foreach (var line in modules.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 2 || !TryId(parts[0], out var id) || !ids.Add(id) || string.IsNullOrWhiteSpace(parts[1])) return false;
            if (parts[1] == "module-loopback" && parts.Length < 3) return false;
            var args = parts.Length < 3 ? "" : parts[2];
            var properties = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(arg => arg.StartsWith("source_output_properties=", StringComparison.Ordinal)).ToArray();
            var matches = properties.Count(arg => string.Equals(arg, property, StringComparison.Ordinal));
            if (matches == 0)
            {
                if (expectedId == id || args.Contains(marker, StringComparison.Ordinal)) return false;
                continue;
            }
            if (matches != 1 || properties.Length != 1 || parts[1] != "module-loopback" || foundId.HasValue ||
                (expectedId.HasValue && expectedId.Value != id)) return false;
            foundId = id;
        }
        return true;
    }
}
