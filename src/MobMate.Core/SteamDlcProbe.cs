using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MobMate.Core;

// Shared with the launcher by source link; no extra executable or public protocol.
internal static class SteamDlcProbe
{
    internal static IntPtr ResolveInterfaceGetter(IntPtr handle, string interfaceName)
    {
        var found = IntPtr.Zero;
        for (var version = 0; version <= 999; version++)
        {
            var name = "SteamAPI_Steam" + interfaceName + "_v" +
                version.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            if (!NativeLibrary.TryGetExport(handle, name, out var candidate) || candidate == IntPtr.Zero)
                continue;
            if (found != IntPtr.Zero)
                throw new EntryPointNotFoundException($"Ambiguous Steam {interfaceName} getter");
            found = candidate;
        }
        return found != IntPtr.Zero ? found :
            throw new EntryPointNotFoundException($"Steam {interfaceName} getter not found");
    }

    private const string Argument = "--mobmate-steam-probe";
    private const int MaximumResponseChars = 16384;
    internal static bool IsChild { get; private set; }

    internal static bool TryHandle(string[] args, Func<string[], object?> check, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != Argument) return false;
        IsChild = true;
        var originalOutput = Console.Out;
        try
        {
            // Legacy single-AppID checks write diagnostics through Console.Out.
            // Keep those diagnostics off the private JSON response stream.
            Console.SetOut(Console.Error);
            if (OperatingSystem.IsWindows() && !SteamLaunchContext.AllowPersistentConnection)
            {
                // Restore the achievement path's identity fallback only in this short-lived child.
                var appId = BuildInfo.IsDemo ? "4448740" : "4261090";
                foreach (var name in new[] { "SteamAppId", "SteamGameId" })
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                        Environment.SetEnvironmentVariable(name, appId);
            }
            var result = check(args[1..]);
            if (result is null) { exitCode = 64; return true; }
            using var output = Console.OpenStandardOutput();
            JsonSerializer.Serialize(output, result, result.GetType());
        }
        catch
        {
            exitCode = 65;
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
        return true;
    }

    internal static T Run<T>(Func<string, T> unavailable, params string[] args) where T : class
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing process path");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add(Argument);
            foreach (var arg in args) start.ArgumentList.Add(arg);
            return RunProcess(start, unavailable);
        }
        catch (Exception ex)
        {
            return unavailable($"Steam probe failed: {ex.Message}");
        }
    }

    internal static T RunProcess<T>(ProcessStartInfo start, Func<string, T> unavailable) where T : class
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return unavailable("Steam probe did not start");
            var output = ReadBounded(process.StandardOutput, timeout.Token);
            var error = ReadBounded(process.StandardError, timeout.Token);
            Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).GetAwaiter().GetResult();
            if (process.ExitCode != 0) return unavailable($"Steam probe exited with code {process.ExitCode}");
            return ReadResult<T>(output.GetAwaiter().GetResult()) ?? unavailable("Invalid Steam probe response");
        }
        catch (Exception ex)
        {
            return unavailable(ex is OperationCanceledException ? "Steam probe timed out" : $"Steam probe failed: {ex.Message}");
        }
        finally
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(1000); }
            }
            catch { }
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > MaximumResponseChars) throw new InvalidDataException("Steam probe response too large");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    internal static T? ReadResult<T>(string json) where T : class
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        var properties = typeof(T).GetProperties();
        var fields = document.RootElement.EnumerateObject().ToArray();
        if (fields.Length != properties.Length || fields.Select(f => f.Name).Distinct().Count() != fields.Length
            || fields.Any(f => f.Value.ValueKind == JsonValueKind.Null)
            || properties.Any(p => !document.RootElement.TryGetProperty(p.Name, out _))) return null;
        var result = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        if (result is null || properties.Any(p => p.PropertyType.IsEnum && !Enum.IsDefined(p.PropertyType, p.GetValue(result)!))) return null;
        return result;
    }
}
