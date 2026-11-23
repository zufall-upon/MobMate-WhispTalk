using System.Diagnostics;

namespace MobMate.Core.Native;

/// <summary>
/// Runs native CLI commands and captures output.
/// </summary>
public static class NativeCommandRunner
{
    /// <summary>
    /// Runs the native executable with the given arguments.
    /// </summary>
    public static NativeCommandResult Run(string exePath, string[] args, string? workingDirectory = null)
    {
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {exePath}");

        using var stdoutWait = new System.Threading.ManualResetEventSlim(false);
        using var stderrWait = new System.Threading.ManualResetEventSlim(false);

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data == null)
            {
                stdoutWait.Set();
            }
            else
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data == null)
            {
                stderrWait.Set();
            }
            else
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit(120_000);

        stdoutWait.Wait();
        stderrWait.Wait();

        return new NativeCommandResult
        {
            Stdout = stdout.ToString().TrimEnd(),
            Stderr = stderr.ToString().TrimEnd(),
            ExitCode = process.ExitCode,
        };
    }
}
