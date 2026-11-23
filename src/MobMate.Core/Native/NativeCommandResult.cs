namespace MobMate.Core.Native;

/// <summary>
/// Result from running a native CLI command.
/// </summary>
public sealed class NativeCommandResult
{
    public string Stdout { get; init; } = string.Empty;
    public string Stderr { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public bool Success => ExitCode == 0;
}
