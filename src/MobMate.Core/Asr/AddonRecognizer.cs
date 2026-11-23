namespace MobMate.Core.Asr;

/// <summary>Minimal adapter boundary; a loaded runtime is not necessarily inference-ready.</summary>
public interface IAddonRecognizer : IAsyncDisposable
{
    bool Initialized { get; }
    bool HasSuccessfulInference { get; }
    bool IsAlive { get; }
    Task<AddonAsrCandidate> TranscribeAsync(byte[] pcm16, int sampleRate, TimeSpan timeout, CancellationToken cancellationToken = default);
}

// Optional internal context: the existing public PCM recognizer contract remains intact.
internal interface IContextualAddonRecognizer
{
    Task<AddonAsrCandidate> TranscribeContextAsync(byte[] pcm16, int sampleRate, string language, string hintMode,
        TimeSpan timeout, CancellationToken cancellationToken);
}

// IsAlive may turn false as soon as shutdown starts. Replacement needs proof of
// process exit, not merely a closed protocol/adapter flag.
internal interface IConfirmedAddonStop
{
    bool StopConfirmed { get; }
}

/// <summary>The candidate and the actual single-engine result are deliberately separate.</summary>
public sealed record AddonRecognitionResult(AddonAsrCandidate? Candidate, AddonAsrCandidate Selected, string? Error);
