namespace MobMate.Core.Asr;

/// <summary>A pinned artifact; its length and digest are checked before it is used.</summary>
public sealed record AddonArtifact(string Url, long Bytes, string Sha256);
public sealed record AddonNotice(string FileName, AddonArtifact Artifact);

public sealed record AddonRuntime(
    string Id, string Version, string License, AddonArtifact Artifact,
    string Executable, IReadOnlyList<string> Files, long MaximumExpandedBytes, string? SourcePageUrl = null,
    IReadOnlyList<AddonNotice>? Notices = null);

public sealed record AddonModel(
    string Id, string Name, string FileName, string Format, string Revision,
    string License, IReadOnlyList<string> Languages, AddonArtifact Artifact, string? SourcePageUrl = null,
    IReadOnlyList<AddonNotice>? Notices = null);

public sealed record AddonEngine(
    string Id, string Name, string Platform, AddonRuntime Runtime, IReadOnlyList<AddonModel> Models);

/// <summary>Built-in catalog only; this is not an arbitrary-URL plugin registry.</summary>
public static partial class AddonCatalog
{
    public const string HaasId = "haas_ja";
    private const string ModelRevision = "6b965d5007ba780585de51549d57a8c1c0d67c7e";
    private const string ModelUrl = "https://huggingface.co/sakasegawa/Haas-ja-v1.0-GGUF/resolve/" + ModelRevision + "/";
    private static readonly IReadOnlyList<string> Japanese = Array.AsReadOnly(new[] { "ja" });

    private static AddonModel Model(string quantization, long bytes, string sha256)
    {
        var file = $"Haas-616M-ja-v1.0-{quantization}.gguf";
        return new AddonModel(quantization.ToLowerInvariant(), $"Haas-ja-v1.0 {quantization}", file,
            "speech.cpp GGUF", ModelRevision, "Apache-2.0", Japanese,
            new AddonArtifact(ModelUrl + file, bytes, sha256),
            "https://huggingface.co/sakasegawa/Haas-ja-v1.0-GGUF/tree/" + ModelRevision,
            new[] { new AddonNotice("NOTICE-model-apache.txt", new AddonArtifact(ModelUrl + "LICENSE", 11358,
                "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30")) });
    }

    public static IReadOnlyList<AddonEngine> Engines { get; } = CreateEngines(
        new AddonEngine(HaasId, "Haas-ja-v1.0", "windows-x64",
            new AddonRuntime("speech.cpp", "0.10.1", "MIT",
                new AddonArtifact(
                    "https://github.com/nyosegawa/speech.cpp/releases/download/v0.10.1/speech-0.10.1-windows-x64-vulkan.zip",
                    29548632, "a8fe0e4b4f463c460e7ace9d38c363bec08b254cac5a7f764b7967af35d4fa1a"),
                "speech.exe", Array.AsReadOnly(new[] { "speech.exe", "speech.dll", "speech.h", "speech.lib" }),
                256L * 1024 * 1024, "https://github.com/nyosegawa/speech.cpp/releases/tag/v0.10.1",
                new[]
                {
                    new AddonNotice("NOTICE-speech-mit.txt", new AddonArtifact(
                        "https://raw.githubusercontent.com/nyosegawa/speech.cpp/03555100287c118b41d1b475044e5ab1e07baa8c/LICENSE",
                        1067, "688f8aca0abe167966d277ed00bd594a5a51b028a4fbc1516ff9de7ef05c521a")),
                    new AddonNotice("NOTICE-ggml-mit.txt", new AddonArtifact(
                        "https://raw.githubusercontent.com/nyosegawa/ggml/e1a9ac12edbb57402618fd6936598603f5ee6266/LICENSE",
                        1078, "94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d"))
                }),
            Array.AsReadOnly(new[]
            {
                Model("Q8_0", 656381472, "cd5f60b2f1c411404b83fca4a65e3ce30a26d9e951b6a346529e65f22758afeb"),
                Model("Q6_K", 508898848, "19b33195b691abda293648a02702086754e1392728871d0dafcd3d50682f5dc3"),
                Model("Q5_K", 428021280, "880ee2d3068b98834d88cff5794442a7f36f1808a6b365a5f6188a060b689658"),
                Model("Q4_K", 351901216, "d24d53f29b75214a933ff482b0de1217e0a800f4118cca39ad6cf03609a89d6f"),
                Model("F16", 1233128832, "f752e02f68c1dfff795900e40aa82e5302976fb5360080fcbd330d8b3c992cd8")
            })));

    private static IReadOnlyList<AddonEngine> CreateEngines(AddonEngine haas) =>
        Array.AsReadOnly(new[] { haas }.Concat(AdditionalEngines(haas.Runtime)).ToArray());

    internal static bool IsAddonEngine(string? id) => Engines.Any(engine => engine.Id == id);
}
