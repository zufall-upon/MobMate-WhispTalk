namespace MobMate.Core.Tts;

/// <summary>
/// Linux / no-SAPI fallback. Always reports unavailable.
/// TtsEngineRouter will route to Piper instead.
/// </summary>
public sealed class NullPrimaryTtsService : IPrimaryTtsService
{
    public bool IsAvailable => false;
    public string EngineName => "none";

    public TtsResult SpeakAsync(string text, bool interrupt = true)
        => TtsResult.Fail("No platform TTS available (use Piper or VOICEVOX)");

    public TtsResult StopAsync() => new TtsResult { Success = false, Error = "No platform TTS" };

    public void SetConfig(TtsConfig config) { }

    public TtsConfig GetConfig() => new TtsConfig();

    public TtsStatus GetStatus() => new TtsStatus
    {
        Available = false,
        IsSpeaking = false,
        Error = "No platform TTS on this OS",
        Engine = "none",
        EffectiveEngine = "none",
        EngineAvailable = false,
    };

    public void Dispose() { }
}
