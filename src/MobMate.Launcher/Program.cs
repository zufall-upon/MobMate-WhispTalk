using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MobMate.Core;

namespace MobMate.Launcher;

internal enum SupporterEntitlement
{
    Unknown,
    NotOwned,
    Owned,
}

internal interface IEntitlementProvider
{
    SupporterEntitlement GetSupporterEntitlement();
}

internal sealed class FakeEntitlementProvider(SupporterEntitlement entitlement) : IEntitlementProvider
{
    public SupporterEntitlement GetSupporterEntitlement() => entitlement;
}

internal sealed record PayloadContract(string ExecutableName, string[] RequiredFiles);
internal sealed record LaunchTarget(string Channel, string Directory, string ExecutablePath);

internal static class LaunchTargetResolver
{
    public static LaunchTarget? Resolve(string installRoot, SupporterEntitlement entitlement, PayloadContract contract)
    {
        if (entitlement == SupporterEntitlement.Owned)
        {
            var current = CreateTarget(installRoot, "supporter/current", contract);
            if (IsComplete(current.Directory, contract.RequiredFiles))
            {
                return current;
            }
        }

        var stable = CreateTarget(installRoot, "stable", contract);
        return IsComplete(stable.Directory, contract.RequiredFiles) ? stable : null;
    }

    private static LaunchTarget CreateTarget(string installRoot, string channel, PayloadContract contract)
    {
        var directory = Path.GetFullPath(Path.Combine(installRoot, channel));
        return new LaunchTarget(channel, directory, Path.Combine(directory, contract.ExecutableName));
    }

    private static bool IsComplete(string directory, IEnumerable<string> requiredFiles) =>
        requiredFiles.All(relativePath => File.Exists(Path.Combine(directory, relativePath)));
}

internal static class Program
{
    private const int MissingPayloadExitCode = 10;
    private const int LaunchFailureExitCode = 11;
    private const int SelfTestFailureExitCode = 12;

    public static int Main(string[] args)
    {
        _ = SteamLaunchContext.AllowPersistentConnection;
        if (SteamDlcProbe.TryHandle(args, values => values.Length == 1 && values[0] == "supporter"
            ? new NativeSteamSubscriptionBackend(AppContext.BaseDirectory).CheckSubscriptions() : null, out var probeExitCode))
            return probeExitCode;

        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        using var fileLogger = LauncherFileLogger.TryCreate(installRoot);
        ILogger logger = (ILogger?)fileLogger ?? NullLogger.Instance;

        if (args.Length == 1 && args[0] == "--selftest")
        {
            return RunSelfTest(logger);
        }

        try
        {
            return Launch(
                installRoot,
                args,
                new SteamEntitlementProvider(
                    new NativeSteamSubscriptionBackend(installRoot),
                    SignedEntitlementCache.CreateDefault(),
                    WindowsLegacyV1EntitlementToken.CreateDefault(),
                    logger),
                logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Launcher failed before the child process started");
            return LaunchFailureExitCode;
        }
    }

    private static int Launch(string installRoot, string[] args, IEntitlementProvider entitlementProvider, ILogger logger)
    {
        var contract = GetPayloadContract();
        var entitlement = entitlementProvider.GetSupporterEntitlement();
        var target = LaunchTargetResolver.Resolve(installRoot, entitlement, contract);
        if (target is null)
        {
            logger.LogError("No complete payload found under {InstallRoot}", installRoot);
            return MissingPayloadExitCode;
        }

        logger.LogInformation(
            "Selected {Channel}; entitlement={Entitlement}; executable={Executable}",
            target.Channel,
            entitlement,
            target.ExecutablePath);

        Process process;
        try
        {
            process = StartChild(target, args);
        }
        catch (Exception ex) when (target.Channel == "supporter/current")
        {
            logger.LogWarning(ex, "Supporter payload failed to start; falling back to stable");
            target = LaunchTargetResolver.Resolve(installRoot, SupporterEntitlement.NotOwned, contract);
            if (target is null)
            {
                logger.LogError("Stable payload is incomplete under {InstallRoot}", installRoot);
                return MissingPayloadExitCode;
            }

            try
            {
                process = StartChild(target, args);
            }
            catch (Exception stableEx)
            {
                logger.LogError(stableEx, "Stable payload failed to start");
                return LaunchFailureExitCode;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Channel} payload failed to start", target.Channel);
            return LaunchFailureExitCode;
        }

        try
        {
            process.WaitForExit();
            logger.LogInformation("{Channel} exited with code {ExitCode}", target.Channel, process.ExitCode);
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed while waiting for {Channel}; fallback suppressed after process start", target.Channel);
            return LaunchFailureExitCode;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Process StartChild(LaunchTarget target, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = target.ExecutablePath,
            WorkingDirectory = target.Directory,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (OperatingSystem.IsWindows())
            startInfo.Environment[SteamLaunchContext.LauncherContextVariable] = SteamLaunchContext.AllowPersistentConnection ? "steam" : "direct";

        if (!OperatingSystem.IsWindows())
        {
            ConfigureLinuxEnvironment(startInfo, target.Directory);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"Process.Start returned null: {target.ExecutablePath}");
    }

    private static void ConfigureLinuxEnvironment(ProcessStartInfo startInfo, string appDirectory)
    {
        SetDefault(startInfo, "MOBMATE_PIPER_EXE", Path.Combine(appDirectory, "libs/piper_plus/piper_wrapper.sh"));
        SetDefault(startInfo, "MOBMATE_PIPER_MODEL", Path.Combine(appDirectory, "libs/piper_models/en_US-amy-low.onnx"));

        var libraryPaths = new[]
        {
            "libs/voice-neutralizer/linux-x64",
            "mobmate_native_core",
            "libs/piper_plus",
            "libs/piper_plus_cli",
            "libs/piper",
            "libs/sherpa-onnx",
        }.Select(path => Path.Combine(appDirectory, path));
        var inherited = startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var value) ? value : null;
        startInfo.Environment["LD_LIBRARY_PATH"] = string.Join(':', libraryPaths.Append(inherited).Where(path => !string.IsNullOrWhiteSpace(path)));
    }

    private static void SetDefault(ProcessStartInfo startInfo, string name, string value)
    {
        if (!startInfo.Environment.TryGetValue(name, out var existing) || string.IsNullOrWhiteSpace(existing))
        {
            startInfo.Environment[name] = value;
        }
    }

    private static PayloadContract GetPayloadContract() => OperatingSystem.IsWindows()
        ? new PayloadContract("MobMateWhisp.exe", ["MobMateWhisp.exe"])
        : new PayloadContract("MobMateLinux", ["MobMateLinux", "MobMateLinux.dll"]);

    private static int RunSelfTest(ILogger logger)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mobmate-launcher-selftest-{Guid.NewGuid():N}");
        var contract = GetPayloadContract();
        try
        {
            CreatePayload(root, "stable", contract);
            AssertChannel(root, new FakeEntitlementProvider(SupporterEntitlement.Unknown), contract, "stable");

            CreatePayload(root, "supporter/current", contract);
            AssertChannel(root, new FakeEntitlementProvider(SupporterEntitlement.Owned), contract, "supporter/current");

            File.Delete(Path.Combine(root, "supporter/current", contract.RequiredFiles[0]));
            AssertChannel(root, new FakeEntitlementProvider(SupporterEntitlement.Owned), contract, "stable");

            RunEntitlementCacheSelfTest(root, logger);
            logger.LogInformation("Launcher self-test passed");
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Launcher self-test failed");
            return SelfTestFailureExitCode;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to clean self-test directory {Directory}", root);
            }
        }
    }

    private static void CreatePayload(string root, string channel, PayloadContract contract)
    {
        var directory = Path.Combine(root, channel);
        Directory.CreateDirectory(directory);
        foreach (var relativePath in contract.RequiredFiles)
        {
            var path = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }
    }

    private static void AssertChannel(string root, IEntitlementProvider provider, PayloadContract contract, string expected)
    {
        var target = LaunchTargetResolver.Resolve(root, provider.GetSupporterEntitlement(), contract);
        if (target?.Channel != expected)
        {
            throw new InvalidOperationException($"Expected channel={expected}, actual={target?.Channel ?? "none"}");
        }
    }

    private static void RunEntitlementCacheSelfTest(string root, ILogger logger)
    {
        var cachePath = Path.Combine(root, "entitlement-cache.json");
        var cache = new SignedEntitlementCache(cachePath, "selftest-secret", "selftest-machine");
        var now = DateTimeOffset.UtcNow;

        var missingLegacy = new FakeLegacyEntitlementToken(new(false, false, "missing"));
        AssertEntitlement(logger, cache, missingLegacy, new(true, true, true, 1, true, false, "supporter"), SupporterEntitlement.Owned);
        AssertEntitlement(logger, cache, missingLegacy, new(true, true, false, 1, false, false, "offline"), SupporterEntitlement.Owned);
        AssertEntitlement(logger, cache, missingLegacy, new(true, false, true, 1, false, false, "partial failure"), SupporterEntitlement.Owned);
        if (cache.Read(2, now).Allowed)
        {
            throw new InvalidOperationException("Offline cache accepted the wrong SteamID");
        }
        if (cache.Read(1, now.AddDays(31)).Allowed)
        {
            throw new InvalidOperationException("Offline cache accepted an expired token");
        }

        File.AppendAllText(cachePath, "tampered");
        if (cache.Read(1, now).Allowed)
        {
            throw new InvalidOperationException("Offline cache accepted tampered JSON");
        }

        AssertEntitlement(logger, cache, missingLegacy, new(true, true, true, 1, false, true, "legacy"), SupporterEntitlement.Owned);
        AssertEntitlement(logger, cache, missingLegacy, new(true, true, true, 1, false, false, "missing"), SupporterEntitlement.NotOwned);
        AssertEntitlement(logger, cache, missingLegacy, new(true, true, false, 1, false, false, "offline revoked"), SupporterEntitlement.Unknown);
        AssertEntitlement(logger, cache, missingLegacy, new(false, false, false, 0, false, false, "unavailable"), SupporterEntitlement.Unknown);
        AssertEntitlement(logger, cache, missingLegacy, new(true, true, true, 0, true, true, "zero SteamID"), SupporterEntitlement.Unknown);

        var offlineV1 = new FakeLegacyEntitlementToken(new(true, true, "valid transition"));
        AssertEntitlement(logger, cache, offlineV1, new(true, true, false, 3, false, false, "offline V1"), SupporterEntitlement.Owned);
        if (offlineV1.DeleteCount != 0 || File.Exists(cachePath))
        {
            throw new InvalidOperationException("Offline V1 transition modified entitlement storage");
        }

        var expiredV1 = new FakeLegacyEntitlementToken(new(true, false, "expired transition"));
        AssertEntitlement(logger, cache, expiredV1, new(true, true, false, 3, false, false, "offline expired V1"), SupporterEntitlement.Unknown);

        var migratableV1 = new FakeLegacyEntitlementToken(new(true, false, "valid online migration"));
        AssertEntitlement(logger, cache, migratableV1, new(true, false, true, 3, false, false, "partial online V1"), SupporterEntitlement.Owned);
        if (migratableV1.DeleteCount != 1 || !cache.Read(3, DateTimeOffset.UtcNow).Allowed)
        {
            throw new InvalidOperationException("Online V1 migration did not write cache before deletion");
        }
    }

    private static void AssertEntitlement(
        ILogger logger,
        IEntitlementCache cache,
        ILegacyEntitlementToken legacyToken,
        SteamSubscriptionSnapshot snapshot,
        SupporterEntitlement expected)
    {
        var actual = new SteamEntitlementProvider(new FakeSteamSubscriptionBackend(snapshot), cache, legacyToken, logger).GetSupporterEntitlement();
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected entitlement={expected}, actual={actual}, reason={snapshot.Reason}");
        }
    }
}

internal sealed class LauncherFileLogger : ILogger, IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    private LauncherFileLogger(string path)
    {
        _writer = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };
    }

    public static LauncherFileLogger? TryCreate(string installRoot)
    {
        try
        {
            var logDirectory = Path.Combine(installRoot, "logs");
            Directory.CreateDirectory(logDirectory);
            return new LauncherFileLogger(Path.Combine(logDirectory, $"MobMateLauncher_{DateTime.Now:yyyyMMdd}.log"));
        }
        catch
        {
            return null;
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        try
        {
            lock (_sync)
            {
                _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {formatter(state, exception)}");
                if (exception is not null)
                {
                    _writer.WriteLine(exception);
                }
            }
        }
        catch
        {
            // Logging must never prevent stable fallback.
        }
    }

    public void Dispose() => _writer.Dispose();
}
