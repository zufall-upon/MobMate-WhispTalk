using System.Text.Json.Nodes;

namespace MobMate.Core.VoiceConversion;

// Reuse the existing settings document; no additional HTTP contract.
public static class MobfyOnsetSelection
{
    public static bool IsSelected(UiSettingsDocument document)
        => document.Settings.GetValueOrDefault("mobmate.mode") == "mobfy";

    public static string Language(UiSettingsDocument document)
        => document.Settings.GetValueOrDefault("mobmate.realtimeLanguage") ?? "ja";

    public static bool Enabled(UiSettingsDocument document)
        => document.Settings.GetValueOrDefault("mobmate.fillerEnabled") != "false";

    public static bool Changed(UiSettingsDocument before, UiSettingsDocument after)
        => SelectionKeys.Any(key => before.Settings.GetValueOrDefault(key) != after.Settings.GetValueOrDefault(key));

    private static readonly string[] SelectionKeys =
    [
        "mobmate.mode", "mobmate.vc.modelId", "mobmate.vc.rawMode",
        "mobmate.realtimeLanguage", "mobmate.captureDeviceId",
        "mobmate.ttsOutputDeviceId", "mobmate.ttsMonitorVolume"
    ];

    public static string Options(UiSettingsDocument document)
    {
        // Mirror vcGetOptions' applied defaults, not unused legacy slider values.
        var settings = document.Settings;
        var options = new JsonObject
        {
            ["modelId"] = settings.GetValueOrDefault("mobmate.vc.modelId") ?? "",
            ["inputDeviceId"] = settings.GetValueOrDefault("mobmate.captureDeviceId") ?? "",
            ["outputDeviceId"] = settings.GetValueOrDefault("mobmate.ttsOutputDeviceId") ?? "",
            ["f0ShiftSemitones"] = 1, ["speakerId"] = 0,
            ["prosodyNeutralizerEnabled"] = true, ["neutralizerPreset"] = "flat",
            ["neutralizerStrength"] = 1.0, ["pressureSuppressorStrength"] = 0.8,
            ["noiseCleanup"] = "dtln", ["f0Flatten"] = 0.5,
            ["energyCompression"] = 0.95, ["sharpnessSuppression"] = 0.75,
            ["worldWet"] = 2, ["pressureEstimatorEnabled"] = true,
            ["adaptiveConversionStrength"] = true, ["outputVolumePercent"] = 140,
            ["monitorVolumePercent"] = double.TryParse(settings.GetValueOrDefault("mobmate.ttsMonitorVolume"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var monitor)
                && double.IsFinite(monitor) ? Math.Clamp(monitor, 0, 100) : 0,
            ["vadStartThreshold"] = 0.54, ["vadContinueThreshold"] = 0.36,
            ["wavOutputEnabled"] = false
        };
        if (settings.GetValueOrDefault("mobmate.vc.rawMode") == "true") options["mode"] = "rvc_raw";
        return options.ToJsonString();
    }
}
