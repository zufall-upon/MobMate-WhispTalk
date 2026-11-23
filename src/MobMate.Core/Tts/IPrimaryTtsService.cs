namespace MobMate.Core.Tts;

/// <summary>
/// Platform-specific primary TTS abstraction.
/// Windows: WindowsSapiTtsService
/// Linux: NullPrimaryTtsService (Piper is primary via TtsEngineRouter)
/// </summary>
public interface IPrimaryTtsService : IDisposable
{
    bool IsAvailable { get; }
    string EngineName { get; }
    TtsResult SpeakAsync(string text, bool interrupt = true);
    TtsResult StopAsync();
    void SetConfig(TtsConfig config);
    TtsConfig GetConfig();
    TtsStatus GetStatus();
}
