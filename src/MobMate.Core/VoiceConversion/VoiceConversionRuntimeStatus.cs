namespace MobMate.Core.VoiceConversion;

public sealed record VoiceConversionRuntimeStatus(
    bool Enabled,
    bool Running,
    string Mode,
    string Message,
    int InstalledModelCount)
{
    public static VoiceConversionRuntimeStatus Phase1(int installedModelCount) => new(
        Enabled: false,
        Running: false,
        Mode: "voice_conversion",
        Message: "Voice Conversion Phase 1: model management API only; audio conversion is not implemented.",
        InstalledModelCount: installedModelCount);
}
