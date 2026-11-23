using System.Text;

namespace MobMate.Core.Realtime;

public sealed record RealtimeFinalOverride(string Text, string Engine, string DecisionReason);

public sealed record RealtimeFinalOutput(string Text, string Engine, string DecisionReason,
    string? SenseVoiceText, string? PhoneticText);

/// <summary>
/// The caller supplies the existing recognizers and their per-session state. This
/// stage does not speak, learn, read benchmark expectations, or change ASR settings.
/// </summary>
public static class RealtimeFinalOutputProcessor
{
    public static RealtimeFinalOutput Process(RealtimePollResult poll, string? engine, string? language,
        Func<RealtimePollResult, string, string?>? senseVoice,
        Func<RealtimePollResult, string, RealtimeFinalOverride?>? phonetic)
    {
        string? senseVoiceText = null;
        var jointWithSenseVoice = engine == "joint_auto" && language is not null &&
            (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("ja", StringComparison.OrdinalIgnoreCase) ||
             language.Equals("ko", StringComparison.OrdinalIgnoreCase)) &&
            !language.Equals("zh", StringComparison.OrdinalIgnoreCase);
        if (senseVoice is not null && !string.IsNullOrEmpty(poll.Final) &&
            poll.FinalPcm16Le.Length >= 3200 && poll.FinalPcmSampleRate > 0 &&
            (engine == "sense_voice" || jointWithSenseVoice))
        {
            var nativeText = poll.SelectedText ?? poll.Final ?? string.Empty;
            if (!jointWithSenseVoice || nativeText.EnumerateRunes().Count() < 2)
            {
                var candidate = senseVoice(poll, language ?? "auto");
                if (!string.IsNullOrEmpty(candidate)) senseVoiceText = candidate;
            }
        }
        var primary = senseVoiceText ?? poll.SelectedText ?? poll.Final ?? string.Empty;
        var assist = string.IsNullOrEmpty(primary) ? null : phonetic?.Invoke(poll, primary);
        return new RealtimeFinalOutput(assist?.Text ?? primary,
            assist?.Engine ?? (senseVoiceText is not null ? "sense_voice" : poll.SelectedEngine),
            assist?.DecisionReason ?? poll.DecisionReason, senseVoiceText, assist?.Text);
    }
}
