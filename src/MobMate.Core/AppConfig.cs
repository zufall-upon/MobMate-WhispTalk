using System.Text.Json;
using System.Text.Json.Serialization;
using MobMate.Core.Asr;

namespace MobMate.Core;

/// <summary>
/// Application configuration model.
/// </summary>
public sealed class AppConfig
{
    public string NativeExePath { get; set; } =
        "native/mobmate_native_bridge.dll";

    public string DefaultManifestPath { get; set; } =
        string.Empty;

    public string DefaultModelPath { get; set; } =
        "models/whisper/ggml-small-q8_0.bin";

    public string DefaultWhisperDllDir { get; set; } =
        "libs/cpu";

    public string DefaultWhisperRuntimeMode { get; set; } =
        "auto";

    public string DefaultVulkanWhisperDllDir { get; set; } =
        "libs/vulkan";

    public int SteamVulkanDlcAppId { get; set; } = BuildInfo.SteamVulkanDlcAppId;

    public bool EnableSteamDlcGate { get; set; } = true;

    public string DefaultMoonshineDllDir { get; set; } =
        "libs/moonshine";

    public string DefaultMoonshineModelRoot { get; set; } =
        "models/moonshine";

    public string VoicevoxExePath { get; set; } = string.Empty;

    public int DefaultThreads { get; set; } = 4;  /* Phase 6B: Vulkan stability (8 hangs with Vulkan) */

    /// <summary>
    /// Adaptive vocabulary learning options. All fields optional, defaults apply when omitted.
    /// </summary>
    public AdaptiveVocabOptions? AdaptiveVocab { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
