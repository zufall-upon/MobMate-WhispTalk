using Microsoft.Extensions.Logging;
using System.Text;

namespace MobMate.Core;

public static class AppLogging
{
    private static bool s_initialized;

    private const long MaxLogBytes = 6 * 1024 * 1024; // 6 MB
    private const int KeepLogDays = 10;

    public static string Initialize(string appRoot, string buildChannel)
    {
        if (s_initialized)
        {
            return string.Empty;
        }

        var logDir = Path.Combine(appRoot, "logs");
        Directory.CreateDirectory(logDir);

        PruneOldLogs(logDir);
        var logPath = ResolveLogPath(logDir, DateTime.Now);
        var logWriter = TextWriter.Synchronized(new StreamWriter(File.Open(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        });

        Console.SetOut(new TeeTextWriter(Console.Out, logWriter));
        Console.SetError(new TeeTextWriter(Console.Error, logWriter));
        s_initialized = true;

        Console.WriteLine($"[Startup] log file: {logPath}");
        Console.WriteLine($"[Startup] build channel: {buildChannel}");
        return logPath;
    }

    private static void PruneOldLogs(string logDir)
    {
        var cutoff = DateTime.Now.AddDays(-KeepLogDays);
        foreach (var file in Directory.EnumerateFiles(logDir, "MobMateWhisp_*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch { }
        }
    }

    private static string ResolveLogPath(string logDir, DateTime now)
    {
        var baseName = $"MobMateWhisp_{now:yyyyMMdd}";
        var path = Path.Combine(logDir, baseName + ".log");
        if (!File.Exists(path) || new FileInfo(path).Length < MaxLogBytes)
            return path;
        for (var i = 1; i <= 99; i++)
        {
            var rotated = Path.Combine(logDir, $"{baseName}_{i}.log");
            if (!File.Exists(rotated) || new FileInfo(rotated).Length < MaxLogBytes)
                return rotated;
        }
        return path; // fallback: 99世代超えたら末尾に追記
    }

    /// <summary>
    /// ビルドチャンネルに応じた ILoggerFactory を生成する。
    /// 実出力は Console.SetOut が tee しているため、Console.WriteLine を経由する独自プロバイダーを使う。
    /// debug: Debug 以上、release: Information 以上。
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(string buildChannel)
    {
        var minLevel = buildChannel == "release" ? LogLevel.Information : LogLevel.Debug;
        return LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(minLevel)
                   .AddProvider(new SimpleConsoleLoggerProvider(minLevel)));
    }

    private sealed class SimpleConsoleLoggerProvider(LogLevel minLevel) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new SimpleConsoleLogger(categoryName, minLevel);
        public void Dispose() { }
    }

    private sealed class SimpleConsoleLogger(string category, LogLevel minLevel) : ILogger
    {
        public bool IsEnabled(LogLevel level) => level >= minLevel;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel level, EventId _, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var prefix = level switch
            {
                LogLevel.Debug       => "[DBG]",
                LogLevel.Information => "[INF]",
                LogLevel.Warning     => "[WRN]",
                LogLevel.Error       => "[ERR]",
                LogLevel.Critical    => "[CRT]",
                _                    => "[???]"
            };
            var msg = formatter(state, ex);
            if (ex != null) msg += $"\n  {ex}";
            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {prefix} {msg}");
        }
    }

    private sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _first;
        private readonly TextWriter _second;

        public TeeTextWriter(TextWriter first, TextWriter second)
        {
            _first = first;
            _second = second;
        }

        public override Encoding Encoding => _first.Encoding;

        public override void Write(char value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void Write(string? value)
        {
            _first.Write(value);
            _second.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _first.WriteLine(value);
            _second.WriteLine(value);
        }

        public override void Flush()
        {
            _first.Flush();
            _second.Flush();
        }
    }
}
