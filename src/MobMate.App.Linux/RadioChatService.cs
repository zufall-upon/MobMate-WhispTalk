using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed class RadioChatService : IDisposable
{
    private static readonly Regex SlotRegex = new("^p([0-2])\\.([1-9])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HexColorRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private readonly object _lock = new();
    private readonly string _appRoot;
    private readonly string _configPath;
    private readonly string _defaultLang;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastCommandsMtimeUtc = DateTime.MinValue;
    private System.Threading.Timer? _previewAutoHideTimer;
    private Func<PendingSpeechStatus?>? _pendingStatusProvider;
    private Process? _previewProcess;
    private bool _overlayVisible;
    private RadioChatConfig _config;

    public RadioChatService(string appRoot, string defaultLang, ILogger logger)
    {
        _appRoot = appRoot;
        _defaultLang = NormalizeLang(defaultLang);
        _logger = logger;
        _configPath = Path.Combine(appRoot, "radio_chat.json");
        _config = LoadOrDefaultConfig();
        EnsureCommandsFile();
        ReloadCommandsIfUpdated(force: true);
    }

    public RadioChatStatus GetStatus() => new()
    {
        Enabled = _config.Enabled,
        OverlayVisible = _overlayVisible,
        CommandCount = _commands.Count,
        CommandsPath = _config.CommandsPath,
        HotkeyRegistered = false,
        Platform = "linux",
        TriggerCmd = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "radio-chat-trigger.sh") + " toggle"
    };

    public RadioChatConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    public void ApplyConfig(RadioChatConfig config)
    {
        lock (_lock)
        {
            _config = NormalizeConfig(config);
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions), new UTF8Encoding(false));
        }
        EnsureCommandsFile();
        ReloadCommandsIfUpdated(force: true);
    }

    public void SetPendingStatusProvider(Func<PendingSpeechStatus?> provider)
    {
        _pendingStatusProvider = provider;
    }

    public IReadOnlyDictionary<string, string> GetCommands()
    {
        ReloadCommandsIfUpdated(force: false);
        return new Dictionary<string, string>(_commands, StringComparer.OrdinalIgnoreCase);
    }

    public void UpsertCommand(int page, int digit, string text)
    {
        if (page < 0 || page > 2 || digit < 1 || digit > 9) return;
        var key = $"p{page}.{digit}";
        _commands[key] = text ?? string.Empty;
        SaveCommands();
    }

    public async Task<bool> SpeakSlotAsync(int page, int digit, Func<string, CancellationToken, Task> speakAsync, CancellationToken ct)
    {
        if (page < 0 || page > 2 || digit < 1 || digit > 9) return false;
        ReloadCommandsIfUpdated(force: false);
        var key = $"p{page}.{digit}";
        if (!_commands.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return false;
        await speakAsync(text, ct);
        return true;
    }

    public void ReloadCommandsIfUpdated(bool force = false)
    {
        var path = ResolveCommandsPath();
        if (!File.Exists(path)) return;
        var mtime = File.GetLastWriteTimeUtc(path);
        if (!force && mtime <= _lastCommandsMtimeUtc) return;

        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith("//") || line.StartsWith(';')) continue;
            var sep = line.IndexOf('=');
            if (sep <= 0) continue;
            var key = line[..sep].Trim();
            var value = line[(sep + 1)..].Trim();
            var m = SlotRegex.Match(key);
            if (!m.Success) continue;
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                value = value[1..^1];
            next[key.ToLowerInvariant()] = value;
        }
        _commands.Clear();
        foreach (var kv in next) _commands[kv.Key] = kv.Value;
        _lastCommandsMtimeUtc = mtime;
    }

    public void ShowPreview(Func<PendingSpeechStatus?>? pendingStatusProvider = null)
    {
        _pendingStatusProvider = pendingStatusProvider;
        _overlayVisible = true;
        TryOpenPreviewWindow();
        _logger.LogInformation("[RadioChat] preview requested (Linux overlay window)");
    }

    public void ShowPreviewFor(TimeSpan duration, Func<PendingSpeechStatus?>? pendingStatusProvider = null)
    {
        ShowPreview(pendingStatusProvider);
        if (duration <= TimeSpan.Zero) return;
        _previewAutoHideTimer ??= new System.Threading.Timer(_ => HidePreview(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _previewAutoHideTimer.Change(duration, Timeout.InfiniteTimeSpan);
    }

    public void HidePreview()
    {
        _previewAutoHideTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _overlayVisible = false;
        try { if (_previewProcess is { HasExited: false }) _previewProcess.Kill(); } catch { }
        // Host-launched overlay (Steam pressure-vessel) runs as a transient user unit.
        try { TryStopHostUserUnit("mobmate-radio-overlay", _logger); } catch { }
    }

    public void Dispose()
    {
        HidePreview();
        try { if (_previewProcess is { HasExited: false }) _previewProcess.Kill(); } catch { }
        _previewAutoHideTimer?.Dispose();
    }

    private void TryOpenPreviewWindow()
    {
        try
        {
            if (_previewProcess is { HasExited: false }) return;
            _previewProcess = null;
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "scripts", "linux", "open-radio-chat-window.py");
            if (!File.Exists(scriptPath))
            {
                _logger.LogWarning("[RadioChat] overlay script not found: {Path}", scriptPath);
                return;
            }

            var systemdEnv = ReadSystemdUserEnvironment();
            string? EnvValue(string name) => systemdEnv.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : Environment.GetEnvironmentVariable(name);
            var runtimeDir = EnvValue("XDG_RUNTIME_DIR")
                ?? $"/run/user/{Environment.GetEnvironmentVariable("UID") ?? "1000"}";
            var psi = new ProcessStartInfo(ResolveHostCommand("python3"), $"\"{scriptPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            SanitizeSteamRuntimeEnvironment(psi.Environment);
            psi.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
            var bus = EnvValue("DBUS_SESSION_BUS_ADDRESS");
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = string.IsNullOrWhiteSpace(bus)
                ? $"unix:path={runtimeDir}/bus"
                : bus;
            var xauth = EnvValue("XAUTHORITY");
            if (!string.IsNullOrWhiteSpace(xauth))
                psi.Environment["XAUTHORITY"] = xauth;
            var display = EnvValue("DISPLAY");
            if (!string.IsNullOrWhiteSpace(display))
                psi.Environment["DISPLAY"] = display;
            var waylandDisplay = EnvValue("WAYLAND_DISPLAY");
            if (string.IsNullOrWhiteSpace(waylandDisplay))
                waylandDisplay = File.Exists(Path.Combine(runtimeDir, "wayland-0")) ? "wayland-0" : null;
            if (!string.IsNullOrWhiteSpace(waylandDisplay))
                psi.Environment["WAYLAND_DISPLAY"] = waylandDisplay;

            // Prefer XWayland when current X auth is valid: overlay positioning/keep-above
            // are more reliable than native Wayland for this small command palette.
            if (!string.IsNullOrWhiteSpace(display) && !string.IsNullOrWhiteSpace(xauth) && File.Exists(xauth))
            {
                psi.Environment["GDK_BACKEND"] = "x11";
            }
            else if (string.IsNullOrWhiteSpace(waylandDisplay))
            {
                psi.Environment["GDK_BACKEND"] = "x11";
                psi.Environment["DISPLAY"] = File.Exists("/tmp/.X11-unix/X0")
                    ? ":0"
                    : (display ?? ":0");
            }
            if (TryStartHostUserCommand("mobmate-radio-overlay", "/usr/bin/python3", new[] { scriptPath }, psi.Environment, _logger))
            {
                _logger.LogInformation("[RadioChat] overlay opened via host user service");
                return;
            }
            _previewProcess = Process.Start(psi);
            if (_previewProcess is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var output = await _previewProcess.StandardOutput.ReadToEndAsync();
                        var error = await _previewProcess.StandardError.ReadToEndAsync();
                        await _previewProcess.WaitForExitAsync();
                        if (!string.IsNullOrWhiteSpace(output) || !string.IsNullOrWhiteSpace(error))
                            AppendHelperDebug($"radio direct exit={_previewProcess.ExitCode} out={output.Trim()} err={error.Trim()}");
                    }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RadioChat] overlay open failed");
        }
    }

    private static Dictionary<string, string> ReadSystemdUserEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var psi = new ProcessStartInfo(ResolveHostCommand("systemctl"), "--user show-environment")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            SanitizeSteamRuntimeEnvironment(psi.Environment);
            using var process = Process.Start(psi);
            if (process is null) return result;
            if (!process.WaitForExit(1000))
            {
                try { process.Kill(); } catch { }
                return result;
            }
            var output = process.StandardOutput.ReadToEnd();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = line.IndexOf('=');
                if (index <= 0) continue;
                result[line[..index]] = line[(index + 1)..].TrimEnd('\r');
            }
        }
        catch
        {
            // Best-effort only; parent process environment remains the fallback.
        }
        return result;
    }

    internal static void SanitizeSteamRuntimeEnvironment(IDictionary<string, string?> env)
    {
        env.Remove("LD_PRELOAD");
        env.Remove("LD_AUDIT");
        FilterSteamRuntimePaths(env, "LD_LIBRARY_PATH");
        FilterSteamRuntimePaths(env, "PATH");
        if (Directory.Exists("/run/host/usr/bin"))
        {
            env["PATH"] = "/run/host/usr/bin:/run/host/bin:/usr/bin:/bin";
            env["LD_LIBRARY_PATH"] = "/run/host/usr/lib/x86_64-linux-gnu/systemd:/run/host/usr/lib/x86_64-linux-gnu:/run/host/lib/x86_64-linux-gnu:/run/host/usr/lib:/run/host/lib";
            env["PYTHONHOME"] = "/run/host/usr";
            env["PYTHONPATH"] = "/run/host/usr/lib/python3/dist-packages";
            env["GI_TYPELIB_PATH"] = "/run/host/usr/lib/x86_64-linux-gnu/girepository-1.0:/run/host/usr/lib/girepository-1.0";
            // WebKitGTK spawns helper processes (WebKitNetworkProcess/WebKitWebProcess) from its
            // libexec dir. Inside pressure-vessel that compiled-in path (/usr/lib/.../webkit2gtk-4.1)
            // does not exist; point it at the host copy so the subtitle/radio overlay can load.
            foreach (var dir in new[]
                     {
                         "/run/host/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1",
                         "/run/host/usr/libexec/webkit2gtk-4.1",
                         "/run/host/usr/lib/webkit2gtk-4.1"
                     })
            {
                if (Directory.Exists(dir))
                {
                    env["WEBKIT_EXEC_PATH"] = dir;
                    break;
                }
            }
        }
        if (!env.TryGetValue("PATH", out var path) || string.IsNullOrWhiteSpace(path))
            env["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
    }

    internal static string ResolveHostCommand(string command)
    {
        var hostPath = Path.Combine("/run/host/usr/bin", command);
        return File.Exists(hostPath) ? hostPath : command;
    }

    internal static bool TryStartHostUserCommand(string unitPrefix, string command, IReadOnlyList<string> args, IDictionary<string, string?> env, ILogger logger)
    {
        var uid = Environment.GetEnvironmentVariable("UID") ?? "1000";
        var hostRuntimeDir = $"/run/user/{uid}";
        // Inside the Steam pressure-vessel container (scout-on-soldier) the WebKitGTK helper
        // processes do not exist at the compiled-in path, so the overlay must run on the HOST.
        // The container exposes the host user/session bus only through a filtered proxy at
        // /run/user/<uid>/bus -> /run/pressure-vessel/bus. Drive the host systemd user manager
        // through that proxy so the unit (and its WebKit helpers) start in the host namespace.
        var isPv = Directory.Exists("/run/host");
        // Plain bus path (non-PV) requires a real local user bus socket to exist.
        if (!isPv && !File.Exists($"{hostRuntimeDir}/bus")) return false;
        // Stable unit name (no pid/timestamp): a second open replaces/reuses the same window
        // instead of stacking duplicate overlay processes.
        var unitName = unitPrefix;
        // Inside PV: systemd-run --user cannot work because it requires the host user manager's
        // private socket ($XDG_RUNTIME_DIR/systemd/private), which the container does not expose.
        // The session-bus proxy however forwards org.freedesktop.systemd1, so drive the host user
        // manager via gdbus StartTransientUnit instead.
        if (isPv)
            return TryStartTransientUnitViaProxy(unitName, command, args, NormalizeHostServiceEnvironment(env, hostRuntimeDir), hostRuntimeDir, logger);

        var systemdRun = ResolveHostCommand("systemd-run");
        if (!File.Exists(systemdRun)) return false;
        try
        {
            AppendHelperDebug($"try {unitPrefix}: pv={isPv} command={command} args={string.Join(' ', args)}");
            var psi = new ProcessStartInfo(systemdRun)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment.Clear();
            psi.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            psi.Environment["XDG_RUNTIME_DIR"] = hostRuntimeDir;
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={hostRuntimeDir}/bus";
            foreach (var name in new[] { "HOME", "LANG", "USER", "LOGNAME" })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) psi.Environment[name] = value;
            }

            psi.ArgumentList.Add("--user");
            psi.ArgumentList.Add("--collect");
            psi.ArgumentList.Add($"--unit={unitName}");
            var serviceEnv = NormalizeHostServiceEnvironment(env, hostRuntimeDir);
            foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "GDK_BACKEND" })
            {
                if (serviceEnv.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                    psi.ArgumentList.Add($"--setenv={name}={value}");
            }
            psi.ArgumentList.Add(command);
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null) { AppendHelperDebug($"{unitPrefix}: systemd-run process null"); return false; }
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                AppendHelperDebug($"{unitPrefix}: systemd-run timeout");
                return false;
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            AppendHelperDebug($"{unitPrefix}: exit={process.ExitCode} out={output} err={error}");
            if (process.ExitCode == 0) return true;
            if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase)) return true;
            logger.LogWarning("[LinuxHost] systemd-run exit={ExitCode}: {Error}", process.ExitCode, error);
        }
        catch (Exception ex)
        {
            AppendHelperDebug($"{unitPrefix}: exception={ex}");
            logger.LogWarning(ex, "[LinuxHost] systemd-run launch failed");
        }
        return false;
    }

    private static Dictionary<string, string?> NormalizeHostServiceEnvironment(IDictionary<string, string?> env, string hostRuntimeDir)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in new[] { "DISPLAY", "WAYLAND_DISPLAY", "GDK_BACKEND" })
        {
            if (env.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) result[name] = value;
        }
        result["XDG_RUNTIME_DIR"] = hostRuntimeDir;
        result["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={hostRuntimeDir}/bus";

        var xauthority = env.TryGetValue("XAUTHORITY", out var xauth) ? xauth : null;
        if (string.IsNullOrWhiteSpace(xauthority) || xauthority.Contains("/run/pressure-vessel", StringComparison.Ordinal) || !File.Exists($"/run/host{xauthority}"))
        {
            try
            {
                xauthority = Directory.EnumerateFiles($"/run/host{hostRuntimeDir}", ".mutter-Xwaylandauth.*")
                    .Select(path => path.StartsWith("/run/host", StringComparison.Ordinal) ? path[9..] : path)
                    .FirstOrDefault();
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(xauthority)) result["XAUTHORITY"] = xauthority;
        return result;
    }

    // Stop a transient overlay unit started via TryStartHostUserCommand. Mirrors the start path:
    // inside PV the StopUnit call rides the session-bus proxy to the host user manager; otherwise
    // it uses the local user bus. No-op (returns false) when the helper was not unit-launched.
    internal static bool TryStopHostUserUnit(string unitPrefix, ILogger logger)
    {
        var uid = Environment.GetEnvironmentVariable("UID") ?? "1000";
        var hostRuntimeDir = $"/run/user/{uid}";
        var isPv = Directory.Exists("/run/host");
        if (!isPv && !File.Exists($"{hostRuntimeDir}/bus")) return false;
        string gdbus, gdbusLdStop;
        if (isPv)
        {
            (gdbus, gdbusLdStop) = ResolvePvGdbusPair();
        }
        else
        {
            gdbus = ResolveHostCommand("gdbus");
            gdbusLdStop = "";
        }
        if (!File.Exists(gdbus)) return false;
        try
        {
            var psi = new ProcessStartInfo(gdbus)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment.Clear();
            psi.Environment["PATH"] = isPv ? "/run/host/usr/bin:/run/host/bin:/usr/bin:/bin" : "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            if (!string.IsNullOrEmpty(gdbusLdStop))
                psi.Environment["LD_LIBRARY_PATH"] = gdbusLdStop;
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={hostRuntimeDir}/bus";
            psi.ArgumentList.Add("call");
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add("--dest");
            psi.ArgumentList.Add("org.freedesktop.systemd1");
            psi.ArgumentList.Add("--object-path");
            psi.ArgumentList.Add("/org/freedesktop/systemd1");
            psi.ArgumentList.Add("--method");
            psi.ArgumentList.Add("org.freedesktop.systemd1.Manager.StopUnit");
            psi.ArgumentList.Add($"{unitPrefix}.service");
            psi.ArgumentList.Add("replace");
            using var process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(3000)) { try { process.Kill(); } catch { } return false; }
            var error = process.StandardError.ReadToEnd().Trim();
            AppendHelperDebug($"{unitPrefix}: StopUnit exit={process.ExitCode} err={error}");
            // "not loaded" = already gone; treat as success.
            if (process.ExitCode == 0 || error.Contains("not loaded", StringComparison.OrdinalIgnoreCase)) return true;
            logger.LogWarning("[LinuxHost] StopUnit exit={ExitCode}: {Error}", process.ExitCode, error);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LinuxHost] StopUnit failed");
        }
        return false;
    }

    // PV path: ask the host systemd user manager (reachable through the session-bus proxy) to
    // start a transient unit. gdbus honours DBUS_SESSION_BUS_ADDRESS so the call rides the proxy;
    // the resulting unit runs in the host namespace where the WebKitGTK helper processes exist.
    private static bool TryStartTransientUnitViaProxy(string unitName, string command, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> serviceEnv, string hostRuntimeDir, ILogger logger)
    {
        try
        {
            var (gdbus, gdbusLd) = ResolvePvGdbusPair();
            if (!File.Exists(gdbus)) { AppendHelperDebug($"{unitName}: gdbus not found"); return false; }

            static string Q(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
            static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";

            // The unit runs in the host namespace, but we cannot read the host's rotating
            // .mutter-Xwaylandauth cookie filename from inside PV (/run/host/run/user/<uid> is
            // not exposed). Resolve XAUTHORITY at runtime on the host via a shell wrapper so the
            // XWayland X11 backend can authorize; otherwise GTK fails with "Authorization required".
            var execParts = new List<string> { command };
            execParts.AddRange(args);
            var execShell = string.Join(" ", execParts.Select(Sh));
            // XWayland auth cookie filename differs by desktop: GNOME/mutter uses
            // .mutter-Xwaylandauth.*, KDE Plasma / SDDM (e.g. Bazzite) uses xauth_*. Glob both and
            // take the newest (-t) so the X11 backend can authorize on either; otherwise GTK aborts
            // with "Authorization required, but no authorization protocol specified".
            var inner = $"XAUTHORITY=\"$(ls -1t {hostRuntimeDir}/.mutter-Xwaylandauth.* {hostRuntimeDir}/xauth_* 2>/dev/null | head -n1)\"; export XAUTHORITY; exec {execShell}";
            var argvText = $"[{Q("/bin/sh")}, {Q("-c")}, {Q(inner)}]";
            var execStart = $"('ExecStart', <[({Q("/bin/sh")}, {argvText}, false)]>)";

            var envEntries = serviceEnv
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value) && kv.Key != "XAUTHORITY")
                .Select(kv => Q($"{kv.Key}={kv.Value}"))
                .ToList();
            var envText = envEntries.Count > 0
                ? $"('Environment', <[{string.Join(", ", envEntries)}]>)"
                : "('Environment', <@as []>)";

            var props = "[" + string.Join(", ", new[]
            {
                $"('Description', <{Q("MobMate " + unitName)}>)",
                "('Type', <'simple'>)",
                execStart,
                envText,
                "('CollectMode', <'inactive-or-failed'>)"
            }) + "]";

            var psi = new ProcessStartInfo(gdbus)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment.Clear();
            psi.Environment["PATH"] = "/run/host/usr/bin:/run/host/bin:/usr/bin:/bin";
            if (!string.IsNullOrEmpty(gdbusLd))
                psi.Environment["LD_LIBRARY_PATH"] = gdbusLd;
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={hostRuntimeDir}/bus";
            psi.ArgumentList.Add("call");
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add("--dest");
            psi.ArgumentList.Add("org.freedesktop.systemd1");
            psi.ArgumentList.Add("--object-path");
            psi.ArgumentList.Add("/org/freedesktop/systemd1");
            psi.ArgumentList.Add("--method");
            psi.ArgumentList.Add("org.freedesktop.systemd1.Manager.StartTransientUnit");
            psi.ArgumentList.Add($"{unitName}.service");
            psi.ArgumentList.Add("replace");
            psi.ArgumentList.Add(props);
            psi.ArgumentList.Add("@a(sa(sv)) []");

            using var process = Process.Start(psi);
            if (process is null) { AppendHelperDebug($"{unitName}: gdbus null"); return false; }
            if (!process.WaitForExit(4000))
            {
                try { process.Kill(); } catch { }
                AppendHelperDebug($"{unitName}: gdbus timeout");
                return false;
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            AppendHelperDebug($"{unitName}: StartTransientUnit exit={process.ExitCode} out={output} err={error}");
            if (process.ExitCode == 0) return true;
            // Unit already active = overlay window already up; treat as success (idempotent).
            if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase)) return true;
            logger.LogWarning("[LinuxHost] StartTransientUnit exit={ExitCode}: {Error}", process.ExitCode, error);
        }
        catch (Exception ex)
        {
            AppendHelperDebug($"{unitName}: gdbus exception={ex.Message}");
            logger.LogWarning(ex, "[LinuxHost] StartTransientUnit via proxy failed");
        }
        return false;
    }

    // Diagnostic: check whether the pressure-vessel session-bus proxy forwards calls to the
    // host user manager (org.freedesktop.systemd1). gdbus honours DBUS_SESSION_BUS_ADDRESS,
    // unlike systemd-run --user which requires the host private socket (not exposed in PV).
    private static void ProbeProxyBusSystemd1(string hostRuntimeDir)
    {
        try
        {
            var (gdbus, gdbusLdProbe) = ResolvePvGdbusPair();
            if (!File.Exists(gdbus)) { AppendHelperDebug($"probe: gdbus not found at {gdbus}"); return; }
            AppendHelperDebug($"probe: using gdbus={gdbus}");
            var psi = new ProcessStartInfo(gdbus)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment.Clear();
            psi.Environment["PATH"] = "/run/host/usr/bin:/run/host/bin:/usr/bin:/bin";
            if (!string.IsNullOrEmpty(gdbusLdProbe))
                psi.Environment["LD_LIBRARY_PATH"] = gdbusLdProbe;
            psi.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={hostRuntimeDir}/bus";
            psi.ArgumentList.Add("call");
            psi.ArgumentList.Add("--session");
            psi.ArgumentList.Add("--dest");
            psi.ArgumentList.Add("org.freedesktop.systemd1");
            psi.ArgumentList.Add("--object-path");
            psi.ArgumentList.Add("/org/freedesktop/systemd1");
            psi.ArgumentList.Add("--method");
            psi.ArgumentList.Add("org.freedesktop.systemd1.Manager.GetUnit");
            psi.ArgumentList.Add("dbus.service");
            using var p = Process.Start(psi);
            if (p is null) { AppendHelperDebug("probe: gdbus null"); return; }
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } AppendHelperDebug("probe: gdbus timeout"); return; }
            var o = p.StandardOutput.ReadToEnd().Trim();
            var e = p.StandardError.ReadToEnd().Trim();
            AppendHelperDebug($"probe systemd1 via proxy: exit={p.ExitCode} out={o} err={e}");
        }
        catch (Exception ex) { AppendHelperDebug($"probe exception={ex.Message}"); }
    }

    // Resolve the gdbus binary to use inside a PV (pressure-vessel) container.
    // Prefer the PV-native gdbus (/usr/bin/gdbus, part of the Steam runtime) because it is
    // always linked against the Steam runtime's own GLib. The host binary (/run/host/usr/bin/gdbus)
    // may use newer GLib symbols (e.g. g_string_free_and_steal added in GLib 2.76) that are absent
    // from the older Steam runtime libglib, causing "symbol lookup error" on distros like Bazzite/Fedora.
    // When using the PV-native binary the host LD_LIBRARY_PATH is NOT needed (returns "").
    private static (string gdbusPath, string ldLibraryPath) ResolvePvGdbusPair()
    {
        const string pvNative = "/usr/bin/gdbus";
        if (File.Exists(pvNative)) return (pvNative, "");
        // Fallback: host gdbus. The host binary links against the host's own GLib, which must be
        // reachable via LD_LIBRARY_PATH. Include BOTH the Debian/Ubuntu multiarch dir
        // (/usr/lib/x86_64-linux-gnu) AND the Fedora/Bazzite layout (/usr/lib64). On Fedora the
        // multiarch dirs do not exist, so without lib64 the host gdbus resolves the older Steam
        // runtime GLib and fails with "undefined symbol: g_string_free_and_steal".
        const string hostPath = "/run/host/usr/bin/gdbus";
        const string hostLd = "/run/host/usr/lib/x86_64-linux-gnu:/run/host/lib/x86_64-linux-gnu:/run/host/usr/lib64:/run/host/lib64:/run/host/usr/lib:/run/host/lib";
        return (hostPath, hostLd);
    }

    internal static void AppendHelperDebug(string message)
    {
        try { File.AppendAllText("/tmp/mobmate-helper-debug.log", $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch { }
    }

    private static void FilterSteamRuntimePaths(IDictionary<string, string?> env, string name)
    {
        if (!env.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) return;
        var filtered = value.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !IsSteamRuntimePath(part))
            .ToArray();
        if (filtered.Length == 0) env.Remove(name);
        else env[name] = string.Join(':', filtered);
    }

    private static bool IsSteamRuntimePath(string path)
    {
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        return lower.Contains("/steam-runtime", StringComparison.Ordinal)
            || lower.Contains("steamlinuxruntime", StringComparison.Ordinal)
            || lower.Contains("/ubuntu12_32", StringComparison.Ordinal)
            || lower.Contains("/ubuntu12_64", StringComparison.Ordinal)
            || lower.Contains("pressure-vessel", StringComparison.Ordinal)
            || lower.Contains("gameoverlayrenderer", StringComparison.Ordinal);
    }

    private void EnsureCommandsFile()
    {
        var target = ResolveCommandsPath();
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? AppContext.BaseDirectory);
        var preset = Path.Combine(_appRoot, "libs", "preset", $"_radiocmd_{_defaultLang}.txt");
        if (!File.Exists(preset)) preset = Path.Combine(_appRoot, "libs", "preset", "_radiocmd_en.txt");
        if (File.Exists(preset)) File.Copy(preset, target, overwrite: false);
        else File.WriteAllText(target, "# Radio command presets\n", new UTF8Encoding(false));
    }

    private void SaveCommands()
    {
        var path = ResolveCommandsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        var lines = _commands.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key} = \"{kv.Value.Replace("\"", "\\\"")}\"");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        _lastCommandsMtimeUtc = File.GetLastWriteTimeUtc(path);
    }

    private string ResolveCommandsPath()
    {
        var rel = string.IsNullOrWhiteSpace(_config.CommandsPath) ? "_radiocmd.txt" : _config.CommandsPath.Trim();
        return Path.IsPathFullyQualified(rel) ? rel : Path.Combine(_appRoot, rel);
    }

    private RadioChatConfig LoadOrDefaultConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var parsed = JsonSerializer.Deserialize<RadioChatConfig>(File.ReadAllText(_configPath, Encoding.UTF8), JsonOptions);
                return NormalizeConfig(parsed ?? new RadioChatConfig());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RadioChat] config load failed; using default");
        }
        var cfg = NormalizeConfig(new RadioChatConfig());
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath) ?? AppContext.BaseDirectory);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, JsonOptions), new UTF8Encoding(false));
        return cfg;
    }

    private static RadioChatConfig NormalizeConfig(RadioChatConfig src)
    {
        src.CommandsPath = string.IsNullOrWhiteSpace(src.CommandsPath) ? "_radiocmd.txt" : src.CommandsPath;
        src.Overlay ??= new RadioOverlayConfig();
        src.Overlay.Position = NormalizePosition(src.Overlay.Position);
        src.Overlay.FontSize = Math.Clamp(src.Overlay.FontSize, 10, 32);
        src.Overlay.Opacity = Math.Clamp(src.Overlay.Opacity, 0.2, 1.0);
        src.Overlay.Margin = Math.Clamp(src.Overlay.Margin, 0, 120);
        src.Overlay.MaxLines = Math.Clamp(src.Overlay.MaxLines, 4, 20);
        src.Overlay.Background = NormalizeColor(src.Overlay.Background, "#1D6F5A");
        src.Overlay.Foreground = NormalizeColor(src.Overlay.Foreground, "#FFFFFF");
        src.Overlay.PageChangeKey = string.IsNullOrWhiteSpace(src.Overlay.PageChangeKey) ? "Q" : src.Overlay.PageChangeKey.Trim().ToUpperInvariant();
        src.Shortcut ??= new RadioShortcut();
        src.Shortcut.Display = string.IsNullOrWhiteSpace(src.Shortcut.Display) ? "Ctrl+Shift+F4" : src.Shortcut.Display;
        src.Shortcut.Modifier = string.IsNullOrWhiteSpace(src.Shortcut.Modifier) ? "ctrl+shift" : src.Shortcut.Modifier;
        src.Shortcut.Key = string.IsNullOrWhiteSpace(src.Shortcut.Key) ? "F4" : src.Shortcut.Key;
        return src;
    }

    private static string NormalizePosition(string? position)
    {
        var x = (position ?? "top_left").Trim().ToLowerInvariant().Replace('-', '_');
        return x is "top_left" or "top_right" or "bottom_left" or "bottom_right" ? x : "top_left";
    }

    private static string NormalizeColor(string? color, string fallback)
    {
        var x = (color ?? string.Empty).Trim();
        return HexColorRegex.IsMatch(x) ? x.ToUpperInvariant() : fallback;
    }

    private static string NormalizeLang(string lang)
    {
        var x = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return x switch { "ja" or "en" or "ko" or "zh_cn" or "zh_tw" => x, _ => "en" };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

internal sealed class RadioChatConfig
{
    public bool Enabled { get; set; } = false;
    public string CommandsPath { get; set; } = "_radiocmd.txt";
    public RadioShortcut Shortcut { get; set; } = new();
    public RadioOverlayConfig Overlay { get; set; } = new();
}

internal sealed class RadioOverlayConfig
{
    public string Position { get; set; } = "top_left";
    public int FontSize { get; set; } = 16;
    public double Opacity { get; set; } = 0.78;
    public int Margin { get; set; } = 12;
    public int MaxLines { get; set; } = 12;
    public string Background { get; set; } = "#1D6F5A";
    public string Foreground { get; set; } = "#FFFFFF";
    public string PageChangeKey { get; set; } = "Q";
}

internal sealed class RadioShortcut
{
    public string Display { get; set; } = "Ctrl+Shift+F4";
    public string Modifier { get; set; } = "ctrl+shift";
    public string Key { get; set; } = "F4";
}

internal sealed class RadioChatStatus
{
    public bool Enabled { get; set; }
    public bool OverlayVisible { get; set; }
    public int CommandCount { get; set; }
    public string CommandsPath { get; set; } = "_radiocmd.txt";
    public bool HotkeyRegistered { get; set; }
    public string? Platform { get; set; }
    public string? TriggerCmd { get; set; }
}
