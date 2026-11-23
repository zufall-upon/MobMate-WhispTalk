using MobMate.Core.Asr;

if (args.Length != 1) throw new ArgumentException("Provide a new fixture directory");
var root = Path.Combine(Path.GetFullPath(args[0]), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
void Files(string relative, params string[] files)
{
    var directory = Path.Combine(root, relative);
    Directory.CreateDirectory(directory);
    foreach (var name in files) File.WriteAllText(Path.Combine(directory, name), "");
}
void Check(bool value, string message) { if (!value) throw new Exception(message); }
string[] Joint() => RealtimeLanguagePolicy.Catalog(true, root)["joint_auto"].Select(x => x.Code).ToArray();

Check(Joint().Length == 0 && RealtimeLanguagePolicy.RejectsExplicit("joint_auto", "ja", true, root),
    "missing models must not advertise/accept JointAUTO");
Files("ja/other", "tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort");
Check(Joint().Length == 0, "files in the wrong model directory must not count");
Files("ja/quantized/base-ja", "tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort");
Files("ko/quantized/tiny-ko", "tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort");
Files("zh/quantized/base-zh", "tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort");
Files("vi/quantized/base-vi", "tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort");
Files("en/quantized", "tokenizer.bin", "streaming_config.json", "encoder.ort", "adapter.ort", "decoder_kv.ort", "frontend.ort");
Check(!Joint().Contains("en"), "incomplete streaming model must not advertise English");
Files("en/quantized", "cross_kv.ort");
Check(Joint().SequenceEqual(new[] { "ja", "en", "zh", "ko", "vi" }), "exact native model layouts/order");
Check(MoonshineModelCatalog.Detect(root).SequenceEqual(Joint()), "Windows detector and Linux catalog disagree");
Check(Joint().All(code => !RealtimeLanguagePolicy.RejectsExplicit("joint_auto", code, true, root)) &&
    RealtimeLanguagePolicy.RejectsExplicit("joint_auto", "yue", true, root), "catalog and explicit Start disagree");
Check(RealtimeLanguagePolicy.Catalog(true, root)["moonshine"].Select(x => x.Code).SequenceEqual(Joint()) &&
    RealtimeLanguagePolicy.Catalog(true, root)["safe_auto"].Select(x => x.Code).SequenceEqual(Joint()), "Moonshine route parity");
Check(RealtimeLanguagePolicy.Catalog(false, root)["whisper"].Length == 0 &&
    RealtimeLanguagePolicy.RejectsExplicit("whisper", "en", false, root) &&
    !RealtimeLanguagePolicy.RejectsExplicit("unknown", "en", true, root) &&
    !RealtimeLanguagePolicy.RejectsExplicit("joint_auto", "auto", true, root), "other route/default contract");
Check(RealtimeLanguagePolicy.Catalog(true, root, false)["joint_auto"].Length == 0 &&
    RealtimeLanguagePolicy.RejectsExplicit("joint_auto", "ja", true, root, false) &&
    RealtimeLanguagePolicy.RejectsExplicit("joint_auto", "auto", true, root, false) &&
    RealtimeLanguagePolicy.Catalog(true, root, false)["moonshine"].Length > 0,
    "missing joint runtime/model must not disable standalone moonshine");
var splitRoot = Path.Combine(root, "split");
var split = Path.Combine(splitRoot, "en", "quantized");
Directory.CreateDirectory(split);
foreach (var name in new[] { "tokenizer.bin", "streaming_config.json", "encoder.ort", "adapter.ort", "cross_kv.ort",
             "decoder_kv.ort", "frontend.model.ort", "frontend.weights.ort" })
    File.WriteAllText(Path.Combine(split, name), "");
Check(MoonshineModelCatalog.Detect(splitRoot).SequenceEqual(new[] { "en" }), "split frontend pair");
Console.WriteLine("PASS model-layout/catalog/explicit-start policy; empty fixtures only, no native inference");
