using System.Text.Json;

namespace MobMate.Core.VoiceConversion;

public sealed record VoiceConversionModelManifest(
    string SchemaVersion,
    string Id,
    string DisplayName,
    string Engine,
    string Version,
    string Author,
    string License,
    VoiceConversionManifestFiles Files,
    VoiceConversionManifestRuntime Runtime,
    VoiceConversionManifestUi? Ui = null,
    string? Format = null,
    string? Backend = null,
    string? LicenseUrl = null,
    string? SourceUrl = null,
    string? SourceSha256 = null)
{
    public static VoiceConversionModelManifest Load(string manifestPath)
    {
        var json = File.ReadAllText(manifestPath);
        return JsonSerializer.Deserialize<VoiceConversionModelManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("manifest.json is empty or invalid");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record VoiceConversionManifestFiles(
    string Model,
    string ContentEncoder,
    string? Config = null,
    string? Params = null,
    string? F0Model = null,
    string? Index = null,
    string Prosody = "");

public sealed record VoiceConversionManifestRuntime(
    int SampleRate = 0,
    int InputSampleRate = 0,
    int OutputSampleRate = 0,
    int ContentEncoderSampleRate = 16000,
    string F0Method = "pitch-input",
    bool F0 = true,
    bool UsesIndex = false,
    double HopMs = 10,
    double FrameMs = 20,
    int RecommendedLatencyMs = 900,
    bool RequiresGpu = false,
    bool SupportsCpu = true);

public sealed record VoiceConversionManifestUi(
    bool DefaultProsodyNeutralizer,
    bool DefaultPressureEstimator,
    double DefaultAdaptiveConversionStrength,
    double DefaultOutputGainDb);
