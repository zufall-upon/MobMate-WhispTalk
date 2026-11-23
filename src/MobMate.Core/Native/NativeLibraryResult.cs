namespace MobMate.Core.Native;

/// <summary>
/// Result from a native DLL bridge call.
/// </summary>
public sealed class NativeLibraryResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int ApiVersion { get; init; }
    public string NativeVersion { get; init; } = string.Empty;

    /// <summary>
    /// Transcript text when transcribe succeeds.
    /// </summary>
    public string Transcript { get; init; } = string.Empty;

    /// <summary>
    /// Text selected for downstream handoff. Mirrors Transcript for single-engine ASR.
    /// </summary>
    public string SelectedText { get; init; } = string.Empty;

    public string SelectedEngine { get; init; } = string.Empty;
    public string DecisionReason { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public bool ConfidenceAvailable { get; init; }
    public double? ConfidenceScore { get; init; }
    public string ConfidenceUnavailableReason { get; init; } = string.Empty;

    /// <summary>
    /// Transcription latency in milliseconds.
    /// </summary>
    public int LatencyMs { get; init; }

    /// <summary>
    /// Raw JSON string from the native DLL.
    /// </summary>
    public string RawJson { get; init; } = string.Empty;

    /// <summary>
    /// Error stage (load|wav|transcribe|unknown) when transcribe fails.
    /// </summary>
    public string ErrorStage { get; init; } = string.Empty;
}
