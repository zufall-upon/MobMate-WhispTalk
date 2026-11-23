namespace MobMate.Core.Asr;

public static partial class AddonCatalog
{
    public const string ParakeetId = "parakeet_ja";
    public const string QwenId = "qwen3_asr_ja";
    public const string NemotronId = "nemotron_3_5";
    private const string ParakeetUrl = "https://huggingface.co/sakasegawa/parakeet-tdt_ctc-0.6b-ja-GGUF/resolve/f7cb5387e1e76385456b7a792054565ecf177417/";
    private const string QwenUrl = "https://huggingface.co/kznkzn/Qwen3-ASR-1.7B-JA-speechcpp-GGUF/resolve/c0a433564995cdca43c8b19aa605cd57142ef4c7/";
    private const string NemoPrefix = "nemo-speech-0.3.0-windows-x86_64-vulkan";

    private static AddonNotice Notice(string name, string url, long bytes, string sha) => new(name, new(url, bytes, sha));
    private static IEnumerable<AddonEngine> AdditionalEngines(AddonRuntime speech)
    {
        yield return new(ParakeetId, "Parakeet TDT-CTC 0.6B JA (TDT)", "windows-x64", speech,
            new[] { new AddonModel("q8_0", "Parakeet JA Q8_0", "parakeet-tdt_ctc-0.6B-ja-Q8_0.gguf", "speech.cpp GGUF",
                "f7cb5387e1e76385456b7a792054565ecf177417", "CC-BY-4.0", new[] { "ja" },
                new(ParakeetUrl + "parakeet-tdt_ctc-0.6B-ja-Q8_0.gguf", 661012416, "6ff90c5240f0778dfb44816916eb3c8e634645e120170d6ae43c0f50a11d79d0"), ParakeetUrl,
                new[] {
                    Notice("NOTICE-cc-by-4.0.txt", "https://creativecommons.org/licenses/by/4.0/legalcode.txt", 18657, "9ba9550ad48438d0836ddab3da480b3b69ffa0aac7b7878b5a0039e7ab429411"),
                    Notice("NOTICE-model-card.txt", "https://huggingface.co/nvidia/parakeet-tdt_ctc-0.6b-ja/resolve/44edb27eea9317daf89333e75eb830db4b1cc298/README.md", 8311, "1e7d6cfbca749e10dce9786b7c44e977766257cb12f418d35af4a64fbb6a2234"),
                    Notice("NOTICE-conversion.txt", ParakeetUrl + "README.md", 6865, "0b6590f429d16000a021743ffd820f5265c99881a5fbd97b6a50b22e0fdb2ea4") }) });
        yield return new(QwenId, "Neosophie Qwen3-ASR-1.7B-JA", SpeechLinuxRuntime.Available ? "linux-x64" : "windows-x64",
            SpeechLinuxRuntime.Available ? SpeechLinuxRuntime.ForQwen(speech) : speech,
            new[] { new AddonModel("q8_0", "Qwen3-ASR-1.7B-JA Q8_0", "Qwen3-ASR-1.7B-JA-Q8_0.gguf", "speech.cpp GGUF",
                "c0a433564995cdca43c8b19aa605cd57142ef4c7", "Apache-2.0", new[] { "ja", "en" },
                new(QwenUrl + "Qwen3-ASR-1.7B-JA-Q8_0.gguf", 2176109280, "cfe17bfad6708fd4ea243ade250634b7191becb491579ae8f19f423fb2516676"), QwenUrl,
                new[] {
                    Notice("NOTICE-model-apache.txt", QwenUrl + "LICENSE", 11343, "a44a6081c73ad75f0255bb2bb5cab74ef1829565a895a24e53a4f11290ab7655"),
                    Notice("NOTICE-model-attribution.txt", QwenUrl + "NOTICE", 1448, "7b3a077b34502622b7ab89194e78d562143fee1b0034ee4483073990af52c196"),
                    Notice("NOTICE-conversion.txt", QwenUrl + "model-manifest.json", 614, "2e42a5a2d179504ed78de0a721ba851e6229b61e99e7165ccd350adc57ca3717") }) });
        var nemo = new AddonRuntime("nemo-speech", "0.3.0", "Apache-2.0",
            new("https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/v0.3.0/" + NemoPrefix + ".zip", 22417032,
                "72b3b8b41bfe2be0320eff4e9d310594e4f99deefcde4a157799a039656963ce"),
            NemoPrefix + "/bin/nemo-speech.exe",
            Array.AsReadOnly(NemoFiles.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => NemoPrefix + "/" + x).ToArray()),
            256L * 1024 * 1024, "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/tag/v0.3.0");
        const string modelUrl = "https://huggingface.co/nvidia/nemotron-3.5-asr-streaming-0.6b/resolve/ea30d66debe3740a08b573244286791d423d6b3e/";
        yield return new(NemotronId, "Nemotron 3.5 ASR Streaming", "windows-x64", nemo,
            new[] { new AddonModel("q8_0", "Nemotron 3.5 Q8_0", "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf", "NeMo-Speech.cpp GGUF",
                "ea30d66debe3740a08b573244286791d423d6b3e", "OpenMDW-1.1", new[] { "ja", "en", "ko" },
                new(modelUrl + "nemotron-3.5-asr-streaming-0.6b.q8_0.gguf", 742090464, "3fc991d3badad7277c11030a7519832cddaf2057aafed6d4b25147e953a070b1"), modelUrl,
                new[] {
                    Notice("NOTICE-model-card.txt", modelUrl + "README.md", 54239, "a3344caadf796c084c6b90a9fa5978068fd45e3a019790bebe50489bb3c0f7b7"),
                    Notice("NOTICE-model-openmdw.html", "https://openmdw.ai/license/1-1/", 54212, "499a1d1a7b7ff278498e0634abcdae9fa522dc5d36f9ce38549bec60a9762f22") }) });
    }

    // Exact members of the approved, SHA-256-pinned official archive, including all notices.
    private const string NemoFiles = """
        bin/concrt140.dll
        bin/ggml-base.dll
        bin/ggml-cpu.dll
        bin/ggml-vulkan.dll
        bin/ggml.dll
        bin/llama-common.dll
        bin/llama.dll
        bin/msvcp140.dll
        bin/msvcp140_1.dll
        bin/msvcp140_2.dll
        bin/msvcp140_atomic_wait.dll
        bin/msvcp140_codecvt_ids.dll
        bin/nemo-speech.exe
        bin/nemo_speech_asr.dll
        bin/nemo_speech_asr_c.dll
        bin/nemo_speech_nmt.dll
        bin/nemo_speech_nmt_c.dll
        bin/nemo_speech_speechlm.dll
        bin/nemo_speech_tts.dll
        bin/vcomp140.dll
        bin/vcruntime140.dll
        bin/vcruntime140_1.dll
        lib/nemo_speech_asr_c.lib
        lib/nemo_speech_nmt_c.lib
        lib/nemo_speech_tts.lib
        include/nemo_speech/asr.h
        include/nemo_speech/diar.h
        include/nemo_speech/nmt.h
        include/nemo_speech/tts.h
        share/nemo-speech/model-index.json
        lib/cmake/NeMoSpeech/NeMoSpeechConfig.cmake
        lib/cmake/NeMoSpeech/NeMoSpeechConfigVersion.cmake
        share/doc/nemo-speech/CONTRIBUTING.md
        share/doc/nemo-speech/README.md
        share/licenses/nemo-speech/LICENSE
        share/licenses/nemo-speech/NOTICE
        share/licenses/nemo-speech/THIRD_PARTY_NOTICES.md
        share/nemo-speech/config/asr.example.yaml
        share/nemo-speech/config/diar.example.yaml
        share/nemo-speech/config/nmt.example.yaml
        share/nemo-speech/config/README.md
        share/nemo-speech/config/server.example.yaml
        share/nemo-speech/config/tts.example.yaml
        share/nemo-speech/config/voicechat.yaml
        share/doc/nemo-speech/docs/api.md
        share/doc/nemo-speech/docs/build.md
        share/doc/nemo-speech/docs/cli.md
        share/doc/nemo-speech/docs/clients.md
        share/doc/nemo-speech/docs/install.md
        share/doc/nemo-speech/docs/model-conversion.md
        share/doc/nemo-speech/docs/README.md
        share/doc/nemo-speech/docs/sdk.md
        share/doc/nemo-speech/docs/server.md
        share/doc/nemo-speech/docs/troubleshooting.md
        share/doc/nemo-speech/docs/asr/configuration.md
        share/doc/nemo-speech/docs/asr/customization.md
        share/doc/nemo-speech/docs/asr/models.md
        share/doc/nemo-speech/docs/development/asr-batching.md
        share/doc/nemo-speech/docs/development/cublas-shim.md
        share/doc/nemo-speech/docs/development/diagnostics.md
        share/doc/nemo-speech/docs/development/README.md
        share/doc/nemo-speech/docs/development/releasing.md
        share/doc/nemo-speech/docs/development/windows-build.md
        share/doc/nemo-speech/docs/nmt/configuration.md
        share/doc/nemo-speech/docs/nmt/models.md
        share/doc/nemo-speech/docs/s2s/clients.md
        share/doc/nemo-speech/docs/s2s/configuration.md
        share/doc/nemo-speech/docs/s2s/docker.md
        share/doc/nemo-speech/docs/s2s/models.md
        share/doc/nemo-speech/docs/s2s/README.md
        share/doc/nemo-speech/docs/salm/configuration.md
        share/doc/nemo-speech/docs/salm/models.md
        share/doc/nemo-speech/docs/salm/README.md
        share/doc/nemo-speech/docs/tts/configuration.md
        share/doc/nemo-speech/docs/tts/models.md
        share/licenses/nemo-speech/third_party/cpp-httplib/LICENSE
        share/licenses/nemo-speech/third_party/ggml/LICENSE
        share/licenses/nemo-speech/third_party/llama.cpp/LICENSE
        share/licenses/nemo-speech/third_party/miniaudio/LICENSE
        share/licenses/nemo-speech/third_party/vcpkg/abseil/LICENSE
        share/licenses/nemo-speech/third_party/vcpkg/protobuf/LICENSE
        share/licenses/nemo-speech/third_party/vcpkg/sentencepiece/LICENSE
        share/licenses/nemo-speech/third_party/vcpkg/utf8-range/LICENSE
        """;
}
