// Exercise the actual public start handler with an existing session, without native/audio/model activity.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const { createHash } = require("node:crypto");
// Prefer the caller's explicit SDK root, or the installed per-user SDK on Linux.
// GUI-launched node does not necessarily inherit dotnet in PATH.
const dotnetName = process.platform === "win32" ? "dotnet.exe" : "dotnet";
const dotnetRoot = process.env.DOTNET_ROOT || (process.env.HOME && path.join(process.env.HOME, ".dotnet"));
const dotnet = dotnetRoot && fs.existsSync(path.join(dotnetRoot, dotnetName)) ? path.join(dotnetRoot, dotnetName) : dotnetName;

module.exports = function checkAddonStartStateContract() {
  const source = fs.readFileSync("src/MobMate.App.WebHost/Program.cs", "utf8");
  const start = source.indexOf('app.MapPost("/api/realtime/start",');
  const end = source.indexOf('app.MapGet("/api/realtime/filler",', start);
  const helpersStart = source.indexOf("bool AddonAvailable()");
  const helpersEnd = source.indexOf("app.Lifetime.ApplicationStopping.Register", helpersStart);
  const languagesStart = source.indexOf("IReadOnlyList<string> DetectMoonshineLanguages()");
  const languagesEnd = source.indexOf("object RealtimeLanguageResponse(", languagesStart);
  assert.ok(start >= 0 && end > start && helpersStart >= 0 && helpersEnd > helpersStart && languagesStart >= 0 && languagesEnd > languagesStart);
  // 正当な自動適用を残し、準備失敗だけ拒否。
  // 実source式の限定評価。C#型検査・HTTP/Windows/worker/性能の実証ではない。生成前に終了する。
  if (process.env.MOBMATE_START_PROFILE_STATIC_ONLY === "1") {
    const begin = source.indexOf("    var startAddon = addonManager.Describe();", start);
    const finish = source.indexOf("    lock (realtimeLock)", begin);
    assert.ok(begin > start && finish > begin);
    const early = source.indexOf("    var requestedProfile =", start);
    let body = ((early < 0 ? "" : source.slice(early, source.indexOf("    var adaptivePrompt", early))) + source.slice(begin, finish)).replace(/\bvar /g, "let ");
    // Translate the specific C# collection/string operations to their JS equivalents.
    // Fail on source drift rather than silently skipping an input guard.
    for (const [csharp, js] of [
      [".Engines.First(", ".Engines.find("], [".Models.Any(", ".Models.some("],
      [".Languages.Contains(", ".Languages.includes("], ["whisperLanguageLabels.ContainsKey(", "whisperLanguageLabels.has("],
      ["initialPrompt.Length", "initialPrompt.length"]
    ]) {
      assert.ok(body.includes(csharp), `static fixture needs review: ${csharp}`);
      body = body.replace(csharp, js);
    }
    const run = new Function("addonManager", "useLanguageProfile", "engine", "language", "addonLanguageMode",
      "IsAddonEngine", "IsRealtimeLanguageSupported", "Results", "ApiMessage", "RealtimeEngineJointAuto", "AddonCatalog", "ValidAddonMode",
      "IsLiteralValue", "scene", "RealtimeSceneValues", "prefilterProfile", "RealtimePrefilterValues", "initialPrompt", "whisperLanguageLabels",
      body + ";return {engine, addonLanguageMode};");
    const catalog = { QwenId: "qwen3_asr_ja", Engines: [{ Id: "qwen3_asr_ja", Models: [{ Languages: ["ja", "en"] }] }] };
    const literal = (value, values) => values.some(item => item.toLowerCase() === value.toLowerCase());
    const labels = new Map([["ja", "Japanese"], ["en", "English"]]);
    const validMode = (m, e, used) => used && e === "qwen3_asr_ja" ? ["requested", "auto"].includes(m) : m == null;
    function evaluate(profile, ready, enabled, use, engine, supported, language, hint, scene = "short_command", prefilter = "auto", prompt = "") {
      const desired = profile === null ? null : { Enabled: !!profile, JointRequested: profile === "joint", EngineId: "qwen3_asr_ja", LanguageMode: "auto" };
      const snapshot = { Desired: desired, State: { Ready: ready, Enabled: enabled, EngineId: "qwen3_asr_ja" }, Language: "en" };
      const before = JSON.stringify(snapshot);
      const result = run({ Describe: () => snapshot },
        use, engine, language, hint, value => value === "qwen3_asr_ja", () => supported,
        { Conflict: x => ({ ...x, status: 409 }), BadRequest: x => ({ ...x, status: 400 }) },
        (code, message) => ({ code, message }), "joint_auto", catalog, validMode,
        literal, scene, ["auto", "short_command", "talk_clean", "talk_noisy", "talk_low_voice", "desktop_hearing"],
        prefilter, ["auto", "normal", "strong", "off"], prompt, labels);
      assert.equal(JSON.stringify(snapshot), before, "static source slices must not mutate the profile fixture");
      return result;
    }
    for (const [profile, ready, enabled, use, engine, supported, expected, language = "en", hint = null] of [
      ...["", "invalid", "requested", "auto"].map(h => [true, true, true, false, "qwen3_asr_ja", true,
        ["requested", "auto"].includes(h) ? "qwen3_asr_ja" : "addon.invalid_language_mode", "en", h]),
      [null, false, false, false, "whisper", true, "addon.invalid_language_mode", "en", "auto"],
      [true, true, true, true, "whisper", true, "qwen3_asr_ja", "en", "invalid"],
      [null, false, false, true, "whisper", true, "whisper"],
      [false, false, false, true, "whisper", true, "whisper"],
      [true, false, true, true, "whisper", true, "addon.not_ready"],
      [true, false, true, true, "whisper", true, "whisper", "auto"],
      [true, true, true, true, "whisper", true, "qwen3_asr_ja"],
      ["joint", true, true, true, "whisper", true, "joint_auto"],
      ["joint", false, true, true, "joint_auto", true, "addon.not_ready"],
      [true, false, true, false, "whisper", true, "whisper"],
      [true, true, true, false, "whisper", true, "whisper"],
      [true, false, true, false, "qwen3_asr_ja", true, "addon.not_ready"],
      [false, true, false, true, "qwen3_asr_ja", true, "addon.not_ready"],
      [true, true, true, false, "qwen3_asr_ja", true, "qwen3_asr_ja"],
      [null, false, false, true, "whisper", false, "realtime.unsupported_language"]]) {
      const result = evaluate(profile, ready, enabled, use, engine, supported, language, hint);
      assert.equal(result.code || result.engine, expected);
      if (result.code) assert.equal(result.status, expected === "addon.not_ready" ? 409 : 400);
      if (use && profile && ready) assert.equal(result.addonLanguageMode, "auto");
    }
    for (const [result, code] of [
      [evaluate(null, false, false, false, "qwen3_asr_ja", true, "ko", "auto"), "realtime.unsupported_language"],
      [evaluate(null, false, false, false, "whisper", true, "en", null, "invalid"), "realtime.unsupported_scene"],
      [evaluate(null, false, false, false, "whisper", true, "en", null, "short_command", "invalid"), "realtime.unsupported_prefilter"],
      [evaluate(null, false, false, false, "whisper", true, "en", null, "short_command", "auto", "x".repeat(2001)), "realtime.initial_prompt_too_long"]
    ]) {
      assert.equal(result.code, code);
      assert.equal(result.status, 400);
    }
    assert.equal(evaluate(null, false, false, false, "whisper", true, "en", null, "short_command", "auto", "x".repeat(2000)).engine, "whisper");
    assert.ok(early > start && early < source.indexOf("    var startLease", start));
    console.log("限定静的PASS: 保存profile/単独/Joint/未Ready/disabled/未設定/legacy/言語/hint早期拒否。実HTTP等は未実行。");
    return;
  }
  // Stable, content-bound inputs: running this check must not add new source paths after earlier checks.
  const identity = createHash("sha256").update(source).update(fs.readFileSync(__filename)).digest("hex");
  const root = path.resolve(process.env.MOBMATE_START_STATE_ROOT || "temp/asr-candidates/continuation/resume/start-state-contract", identity);
  fs.mkdirSync(root, { recursive: true });
  for (const [relative, names] of [
    ["ja/quantized/base-ja", ["tokenizer.bin", "encoder_model.ort", "decoder_model_merged.ort"]],
    ["en/quantized", ["tokenizer.bin", "streaming_config.json", "encoder.ort", "adapter.ort", "cross_kv.ort", "decoder_kv.ort", "frontend.ort"]],
    ["runtime-whisper", ["whisper.dll"]], ["runtime-moonshine", ["moonshine.dll"]], ["whisper-model", ["model.bin"]]
  ]) {
    const directory = path.join(root, "model-fixture", relative);
    fs.mkdirSync(directory, { recursive: true });
    for (const name of names) {
      const file = path.join(directory, name);
      if (fs.existsSync(file)) assert.equal(fs.readFileSync(file, "utf8"), "", `fixture input changed: ${file}`);
      else fs.writeFileSync(file, "", { flag: "wx" });
    }
  }
  function writeInput(name, content) {
    const file = path.join(root, name);
    if (fs.existsSync(file)) assert.equal(fs.readFileSync(file, "utf8"), content, `fixture input changed: ${file}`);
    else fs.writeFileSync(file, content);
  }
  writeInput("Program.cs", `
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MobMate.Core.Asr;
using System.Text;
using System.Text.Json;
// Platform-only seam: exercise the Windows handler on Linux, not Windows runtime parity.
using OperatingSystem = StartFixtureOperatingSystem;
using AddonState = StartFixtureState;

foreach (var enabledTts in new[] { false, true }) {
  foreach (var scenario in new[] { "language", "missing", "empty", "invalid", "typed", "joint-missing", "unused", "disabled", "requested", "auto", "joint-en", "plain",
    "profile", "profile-joint", "profile-unready", "probe-failed", "profile-joint-unready", "profile-disabled", "profile-unset", "legacy-false", "joint-no-consent", "saved-hint", "joint-no-model", "joint-no-whisper", "joint-no-whisper-model", "joint-no-moonshine-runtime", "joint-legacy-windows",
    "profile-joint-no-model", "profile-joint-no-whisper-model", "profile-standalone-no-model", "profile-optout-no-model" })
    await CheckAsync(scenario, enabledTts);
}
async Task CheckAsync(string scenario, bool enabledTts) {
  var app = new StartFixtureApp();
  var realtimeLogger = NullLogger.Instance;
  const string RealtimeEngineJointAuto = "joint_auto", RealtimeEngineMoonshine = "moonshine", RealtimeEngineSenseVoice = "sense_voice", RealtimeEngineSafeAuto = "safe_auto", RealtimeEngineParaformerZh = "paraformer_zh";
  const string RealtimeSceneShortCommand = "short_command", RealtimePrefilterAuto = "auto", RealtimePrefilterOff = "off";
  string[] RealtimeEngineValues = ["whisper", "joint_auto", "moonshine", "safe_auto"];
  string[] RealtimeSceneValues = ["short_command", "talk_clean"], RealtimePrefilterValues = ["auto", "off"];
  var addonManager = new StartFixtureAddon(scenario);
  var addonLoad = Task.CompletedTask;
  var whisperLanguageLabels = new Dictionary<string, string> { ["ja"] = "Japanese", ["en"] = "English" };
  ${source.slice(helpersStart, helpersEnd)}
  var addonWorkGate = new AddonOperationGate();
  var oldLease = addonWorkGate.TryEnterWork();
  IDisposable? realtimeWorkLease = oldLease;
  var oldSession = new RealtimeSessionController("old-session");
  RealtimeSessionController? realtimeSession = oldSession;
  var realtimeSessionService = new StartFixtureSessionService { Current = oldSession };
  var addonTranscriber = new StartFixtureTranscriber();
  var adaptivePromptBuilder = new StartFixturePromptBuilder();
  var realtimeLock = new object(); var phoneticAssistPrefetchLock = new object();
  using var realtimeMutationGate = new SemaphoreSlim(1, 1);
  var realtimeOutputEpoch = new StartFixtureEpoch();
  var currentAdaptiveIpa = "previous-ipa"; var lastPhoneticAssistDebugKey = "previous-debug";
  long lastTtsFinalDetectedAtMs = 1234;
  var realtimeStartRecoveryRequired = false;
  (string? engine, string? language, string? scene, string? prefilterProfile, int initialPromptLength) lastStartOptions = ("joint_auto", "ja", "talk_clean", "off", 3);
  var lastPhoneticAssistAcceptedKey = "accepted-key"; var lastPhoneticAssistAcceptedText = "accepted-text"; var lastPhoneticAssistAcceptedReason = "accepted-reason";
  var phoneticAssistPrefetchKey = "pending-key"; Task<string?>? phoneticAssistPrefetchTask = Task.FromResult<string?>("pending-result");
  var realtimeTtsEnabled = enabledTts; var lastTtsFinalText = "already-spoken";
  object? lastRealtimeTextFilterResult = new object(); var lastFilteredFinalSource = "already-filtered";
  var appRoot = "unused"; var pendingStarts = 0;
  StartFixtureConfig LoadAppConfig() => new() { DefaultMoonshineModelRoot = scenario is "joint-no-model" or "profile-joint-no-model" or "profile-standalone-no-model" or "profile-optout-no-model" ? "missing-models" : ${JSON.stringify(path.join(root, "model-fixture"))} };
  int PreferredLanguageOrder(string code) => WhisperLanguageCatalog.PreferredOrder(code);
  ${source.slice(languagesStart, languagesEnd)}
  long onsetControlGeneration = 0; int onsetInputChanged = 0; bool onsetExplicitInput = false, onsetIdleKnown = false;
  string onsetCaptureId = "", onsetNativeExe = "";
  void BindOnset(bool enabled, bool newSession = false, long? expectedGeneration = null) { }
  var previous = State();
  var previousFilter = lastRealtimeTextFilterResult; var previousTask = phoneticAssistPrefetchTask;
  string State() => JsonSerializer.Serialize(new { lastStartOptions.engine, lastStartOptions.language, lastStartOptions.scene, lastStartOptions.prefilterProfile, lastStartOptions.initialPromptLength,
    currentAdaptiveIpa, lastPhoneticAssistDebugKey, lastTtsFinalDetectedAtMs,
    lastPhoneticAssistAcceptedKey, lastPhoneticAssistAcceptedText, lastPhoneticAssistAcceptedReason, phoneticAssistPrefetchKey, realtimeTtsEnabled, lastTtsFinalText, lastFilteredFinalSource });
  bool IsDemoSessionExpired() => false;
  IResult DemoSessionExpiredResult() => throw new Exception("unexpected demo check");
  StartFixtureConfig LoadConfigWithSelectedWhisperModel() => new() {
    DefaultWhisperDllDir = ${JSON.stringify(path.join(root, "model-fixture", "runtime-whisper"))} + (scenario == "joint-no-whisper" ? "-missing" : ""),
    DefaultMoonshineDllDir = ${JSON.stringify(path.join(root, "model-fixture", "runtime-moonshine"))} + (scenario == "joint-no-moonshine-runtime" ? "-missing" : ""),
    DefaultModelPath = ${JSON.stringify(path.join(root, "model-fixture", "whisper-model", "model.bin"))} + (scenario is "joint-no-whisper-model" or "profile-joint-no-whisper-model" ? "-missing" : "") };
  bool IsLegacyWindowsCpuOnlyRuntime(StartFixtureConfig config) => scenario == "joint-legacy-windows";
  string ResolveDefaultRealtimeEngine(StartFixtureConfig config) => "joint_auto";
  bool IsSafeAuto(string engine) => engine == RealtimeEngineSafeAuto;
  bool IsLiteralValue(string value, string[] values) => values.Contains(value);
  bool IsRealtimeLanguageSupported(string engine, string language) => GetRealtimeLanguageCodes(engine).Contains(language, StringComparer.OrdinalIgnoreCase);
  string ResolveRealtimeEngineForRuntime(string engine, StartFixtureConfig config, ILogger log) => engine;
  bool IsJointAutoZh(string engine, string language) => false;
  object ApiMessage(string code, string message) => new { code, message };
  Dictionary<string, object?> ApiProblemCode(string code) => new() { ["code"] = code };
  void MarkRealtimeStartPending(string engine, string clientSource) { pendingStarts++; }
  void ClearRealtimeStartPending() { }
  void UnlockSteamAchievement(string value) { }
  ${source.slice(start, end)}
  var usesProfile = (scenario.StartsWith("profile") && scenario != "profile-optout-no-model") || scenario == "probe-failed";
  var engine = scenario is "joint-missing" or "joint-en" or "joint-no-consent" or "joint-no-model" or "joint-no-whisper" or "joint-no-whisper-model" or "joint-no-moonshine-runtime" or "joint-legacy-windows" ? "joint_auto" :
    usesProfile || scenario.StartsWith("profile") || scenario is "unused" or "plain" or "legacy-false" ? "whisper" : AddonCatalog.QwenId;
  var body = new Dictionary<string, object> { ["engine"] = engine, ["language"] = scenario == "language" ? "xx" : "en", ["scene"] = "short_command", ["ttsEnabled"] = !enabledTts };
  body["useLanguageProfile"] = usesProfile;
  if (scenario == "joint-missing") body["language"] = "ja";
  if (scenario is "empty" or "invalid" or "typed" or "requested" or "auto" or "unused" or "disabled")
    body["addonLanguageMode"] = scenario == "empty" ? "" : scenario == "typed" ? 7 : scenario is "unused" or "disabled" ? "auto" : scenario;
  var context = new DefaultHttpContext();
  context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
  context.Request.ContentLength = context.Request.Body.Length;
  NativeLibraryBridge.Registrations = 0;
  var result = await app.Handler!(context.Request);
  // No profile cannot be Ready; an explicitly saved hint/JA Joint consent remains usable.
  // The pre-profile fixture's blanket 400 for missing fields did not express those distinct inputs.
  var unavailable = scenario is "missing" or "disabled" or "profile-unready" or "probe-failed" or "profile-joint-unready";
  var accepted = scenario is "requested" or "auto" or "joint-en" or "joint-missing" or "plain" or "profile" or "profile-joint" or "profile-disabled" or "profile-unset" or "legacy-false" or "joint-no-consent" or "saved-hint" or "profile-standalone-no-model" or "profile-optout-no-model";
  var status = ((IStatusCodeHttpResult)result).StatusCode;
  Require(status == (accepted ? 200 : unavailable ? 409 : 400), scenario + ": HTTP outcome " + status);
  if (!accepted) {
    var value = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
    // The consulted readiness policy now reports 409 addon.not_ready, never substitutes a base engine.
    var expected = scenario is "language" or "joint-no-model" or "joint-no-whisper" or "joint-no-whisper-model" or "joint-no-moonshine-runtime" or "joint-legacy-windows" or "profile-joint-no-model" or "profile-joint-no-whisper-model" ? "realtime.unsupported_language" : unavailable ? "addon.not_ready" : "addon.invalid_language_mode";
    Require(value.Contains(expected), scenario + ": error identity");
    Require(State() == previous && ReferenceEquals(previousFilter, lastRealtimeTextFilterResult) && ReferenceEquals(previousTask, phoneticAssistPrefetchTask), scenario + ": rejected start changed existing final-processing/TTS state");
    if (!unavailable)
      Require(ReferenceEquals(realtimeSession, oldSession) && ReferenceEquals(realtimeWorkLease, oldLease) && !oldSession.Disposed && realtimeSessionService.Starts == 0 && pendingStarts == 0 && addonManager.Applies == 0 && realtimeOutputEpoch.Invalidations == 0, scenario + ": rejected start replaced the existing session/lease");
    else
      Require(realtimeSession is null && realtimeWorkLease is null && oldSession.Disposed && realtimeSessionService.Current is null && realtimeSessionService.Starts == 0 && pendingStarts == 0 && realtimeOutputEpoch.Begins == 0 && NativeLibraryBridge.Registrations == 0, scenario + ": preparation failure substituted/revived a session");
  } else {
    var expectedEngine = scenario is "profile" or "profile-standalone-no-model" ? AddonCatalog.QwenId : scenario == "profile-joint" ? "joint_auto" : engine;
    var expectedLanguage = scenario == "joint-missing" ? "ja" : "en";
    Require(lastStartOptions.engine == expectedEngine && lastStartOptions.language == expectedLanguage && realtimeTtsEnabled == !enabledTts && lastTtsFinalText == "", scenario + ": normal start did not apply caller settings");
    Require(oldSession.Disposed && realtimeSessionService.Starts == 1 && !ReferenceEquals(realtimeSession, oldSession), scenario + ": normal session replacement");
    using var options = JsonDocument.Parse(realtimeSession!.Options);
    Require(options.RootElement.GetProperty("language").GetString() == expectedLanguage, scenario + ": app language changed with hint");
    Require(options.RootElement.GetProperty("engine").GetString() == expectedEngine && addonManager.Applies == 1 && realtimeOutputEpoch.Begins == 1, scenario + ": wrong start route");
    if (scenario is "requested" or "auto") Require(options.RootElement.GetProperty("addonLanguageMode").GetString() == scenario && NativeLibraryBridge.Registrations == 1, scenario + ": valid Qwen hint lost");
    else if (scenario is "profile" or "profile-joint" or "profile-standalone-no-model") Require(options.RootElement.GetProperty("addonLanguageMode").GetString() == "auto" && options.RootElement.GetProperty("addonJointRequested").GetBoolean() == (scenario == "profile-joint") && NativeLibraryBridge.Registrations == 1, scenario + ": saved profile/explicit Joint consent lost");
    else if (scenario is "joint-missing" or "saved-hint") Require(options.RootElement.GetProperty("addonLanguageMode").GetString() == (scenario == "joint-missing" ? "requested" : "auto") && options.RootElement.GetProperty("addonJointRequested").GetBoolean() == (scenario == "joint-missing") && NativeLibraryBridge.Registrations == 1, scenario + ": explicit saved hint/JA consent lost");
    else Require(!options.RootElement.TryGetProperty("addonLanguageMode", out _) && !options.RootElement.GetProperty("addonJointRequested").GetBoolean(), scenario + ": unused addon acquired a hint/participation");
  }
  Require(addonManager.Profiles["ja"].JointRequested && (scenario != "joint-en" || !addonManager.Profiles.ContainsKey("en")), scenario + ": JA consent inherited/mutated");
  realtimeSessionService.Current?.Dispose(); realtimeWorkLease?.Dispose();
  using var released = addonWorkGate.TryEnterAddonOperation();
  Require(released is not null, scenario + ": start lease leaked");
}
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
sealed class StartFixtureApp {
  public Func<HttpRequest, Task<IResult>>? Handler;
  public void MapPost(string route, Func<HttpRequest, Task<IResult>> handler) { if (route != "/api/realtime/start") throw new Exception("wrong route"); Handler = handler; }
}
sealed class StartFixtureAddon {
  public Dictionary<string, StartFixtureProfile> Profiles = new() { ["ja"] = new(true, true) };
  public string Language = "ja";
  public int Applies;
  readonly bool ready;
  public StartFixtureAddon(string scenario) {
    ready = scenario is not ("profile-unready" or "probe-failed" or "profile-joint-unready");
    if (scenario is not ("missing" or "joint-en" or "profile-unset"))
      Profiles["en"] = new(scenario is not ("disabled" or "profile-disabled"), scenario is "profile-joint" or "profile-joint-unready" or "profile-joint-no-model" or "profile-joint-no-whisper-model" or "profile-optout-no-model", "auto");
  }
  public StartFixtureDescription Describe(string? language = null) {
    language ??= Language;
    Profiles.TryGetValue(language, out var desired);
    return new(language, desired, new(AddonCatalog.QwenId, "q8_0", desired is not null && ready, desired?.Enabled == true, desired?.JointRequested == true),
      language == Language && ready && desired?.Enabled == true && desired.JointRequested);
  }
  public StartFixtureState Snapshot() => Describe().State;
  public Task ApplyLanguageAsync(string language, CancellationToken token) { Language = language; Applies++; return Task.CompletedTask; }
  public Task StopAsync(CancellationToken token) => Task.CompletedTask;
}
sealed record StartFixtureProfile(bool Enabled, bool JointRequested, string LanguageMode = "requested") { public string EngineId => AddonCatalog.QwenId; }
sealed record StartFixtureDescription(string Language, StartFixtureProfile? Desired, StartFixtureState State, bool JointEligible);
sealed record StartFixtureState(string EngineId, string ModelId, bool Ready, bool Enabled, bool JointRequested);
static class StartFixtureOperatingSystem { public static bool IsWindows() => true; }
static class SpeechLinuxRuntime { public static bool Available => false; public static AddonRuntime ForQwen(AddonRuntime runtime) => runtime; }
sealed class StartFixtureEpoch { public int Invalidations, Begins; public void Invalidate() { Invalidations++; } public void Begin() { Begins++; } }
sealed class StartFixtureTranscriber { public int CallbackV2 => 1; public int JointCallbackV2 => 2; }
sealed class StartFixturePromptBuilder { public StartFixturePrompt Build(string prompt, string language) => new(prompt, false, 0); }
sealed record StartFixturePrompt(string Prompt, bool Injected, int VocabCount);
sealed class StartFixtureConfig {
  public string NativeExePath = "unused", DefaultModelPath = "unused", DefaultMoonshineDllDir = "unused", DefaultMoonshineModelRoot = "unused", DefaultWhisperDllDir = "unused";
  public int DefaultThreads = 1;
}
static class AppConfigLoader { public static StartFixtureRuntime ResolveWhisperRuntime(StartFixtureConfig config, string root) => new(config.DefaultWhisperDllDir); }
sealed class StartFixtureRuntime(string dir) { public bool UseGpu => true; public string WhisperDllDir => dir; }
static class SteamAchievementService { public const string FirstSessionStart = "unused"; }
sealed class RealtimeSessionController(string options) {
  public string Options => options; public bool Disposed;
  public void Dispose() { Disposed = true; }
  public static RealtimeSessionController Create(string exe, string options, ILogger log) => new(options);
}
sealed class StartFixtureSessionService {
  public RealtimeSessionController? Current; public int Starts;
  public void Start(Func<RealtimeSessionController> create) { Current?.Dispose(); Current = create(); Starts++; }
  public void Dispose() { Current?.Dispose(); Current = null; }
}
static class NativeLibraryBridge {
  public static int Registrations;
  public static void RegisterAddonTranscriberV2(string exe, int callback, bool joint = false) { Registrations++; }
}
`);
  const includes = ["AddonCatalog.cs", "AddonCatalog.Additions.cs", "AddonOperationGate.cs", "WhisperLanguageCatalog.cs", "MoonshineModelCatalog.cs"].map(file =>
    `<Compile Include="${path.relative(root, path.resolve("src/MobMate.Core/Asr", file)).replaceAll("\\", "/")}" Link="${file}" />`).join("\n");
  writeInput("StartState.csproj", `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />${includes}</ItemGroup></Project>`);
  writeInput("NuGet.Config", '<configuration><packageSources><clear /></packageSources></configuration>');
  const run = spawnSync(dotnet, ["run", "--project", path.join(root, "StartState.csproj"), "--configuration", "Debug", "--no-launch-profile", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"], { encoding: "utf8", timeout: 120000 });
  // 成功時の一時ログは増やさない。以前の記録と今回の失敗ログは保全する。
  if (run.status !== 0 || run.error) {
    const attempt = fs.mkdtempSync(path.join(root, "attempt-"));
    fs.writeFileSync(path.join(attempt, "run.log"), `${run.stdout || ""}\n${run.stderr || ""}\nstatus=${run.status}\nerror=${run.error || ""}\n`, { flag: "wx" });
  }
  // Remove only build products owned by this invocation. Preserve source, inputs and failure logs.
  function clean(directory) {
    if (!fs.existsSync(directory)) return;
    assert.equal(fs.lstatSync(directory).isSymbolicLink(), false);
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const file = path.join(directory, entry.name); assert.equal(fs.lstatSync(file).isSymbolicLink(), false);
      if (entry.isDirectory()) clean(file); else fs.unlinkSync(file);
    }
    fs.rmdirSync(directory);
  }
  clean(path.join(root, "bin")); clean(path.join(root, "obj"));
  assert.equal(run.status, 0, `public start state regression failed: ${root}\n${run.stdout || ""}\n${run.stderr || ""}\n${run.error || ""}`);
  console.log("PASS 開始handler・言語検出実compile/実行: 31scenario×既存TTS状態2種。依存とWindows OS判定はfake、実音声/推論/実Windows/性能の検証ではない。");
};

// Linux開始profile専用。上のWindows fixture本体は変更せず、成功証拠を再利用する。
module.exports.linux = function checkLinuxStartProfileContract() {
  const source = fs.readFileSync("src/MobMate.App.Linux/Program.cs", "utf8");
  const start = source.indexOf('app.MapPost("/api/realtime/start",');
  const end = source.indexOf('app.MapGet("/api/realtime/filler",', start);
  const manager = fs.readFileSync("src/MobMate.Core/Asr/AddonLanguageManager.cs", "utf8");
  const resolveStart = manager.indexOf("    public string ResolveRecognitionEngine(");
  const resolveEnd = manager.indexOf("    public AddonLanguageState Describe(", resolveStart);
  assert.ok(start >= 0 && end > start && resolveStart >= 0 && resolveEnd > resolveStart);
  const root = path.resolve(process.env.MOBMATE_START_STATE_ROOT || "temp/asr-candidates/continuation/resume/start-state-contract",
    "linux-" + createHash("sha256").update(source).update(manager).update(fs.readFileSync(__filename)).digest("hex"));
  fs.mkdirSync(root, { recursive: true });
  const models = path.join(root, "model-fixture", "en", "quantized");
  fs.mkdirSync(models, { recursive: true });
  for (const name of ["tokenizer.bin", "streaming_config.json", "encoder.ort", "adapter.ort", "cross_kv.ort", "decoder_kv.ort", "frontend.ort"]) {
    const file = path.join(models, name);
    if (fs.existsSync(file)) assert.equal(fs.readFileSync(file, "utf8"), "", `fixture input changed: ${file}`);
    else fs.writeFileSync(file, "", { flag: "wx" });
  }
  for (const [dir, name] of [["runtime-moonshine", "libmoonshine.so"], ["runtime-whisper", "libwhisper.so"], ["whisper-model", "model.bin"]]) {
    const directory = path.join(root, dir); fs.mkdirSync(directory, { recursive: true });
    const file = path.join(directory, name);
    if (!fs.existsSync(file)) fs.writeFileSync(file, "", { flag: "wx" });
  }
  const readinessStart = source.indexOf("bool JointRuntimeAvailable()");
  const readinessEnd = source.indexOf(";", readinessStart) + 1;
  assert.ok(readinessStart >= 0 && readinessEnd > readinessStart);
  const program = `
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MobMate.Core.Asr;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
var errors = new List<string>();
foreach (var oldTts in new[] { false, true })
  foreach (var scenario in new[] { "ready", "unready", "joint-unready", "disabled", "unset", "legacy-false", "joint-ja-only", "joint-ready", "typed", "invalid",
    "missing-joint", "missing-joint-profile", "missing-standalone-profile", "unavailable-whisper-profile", "missing-whisper-runtime", "missing-whisper-model", "missing-moonshine-runtime" }) {
    try { await Check(scenario, oldTts); }
    catch (Exception ex) { errors.Add(scenario + ": " + ex.Message); }
  }
if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
async Task Check(string scenario, bool oldTts) {
  var app = new FixtureApp();
  var addonWorkGate = new AddonOperationGate();
  var oldLease = addonWorkGate.TryEnterWork(); IDisposable? realtimeWorkLease = oldLease;
  using var realtimeMutationGate = new SemaphoreSlim(1, 1);
  var addonManager = new FixtureAddon(scenario); var addonLoad = Task.CompletedTask;
  var oldSession = new RealtimeSessionController("old");
  RealtimeSessionController? realtimeSession = oldSession;
  var realtimeSessionService = new FixtureSessions { Current = oldSession };
  var realtimeOutputEpoch = new FixtureEpoch(); var addonTranscriber = new FixtureTranscriber();
  var logger = NullLogger.Instance; var adaptiveVocabLogger = NullLogger.Instance;
  var adaptivePromptBuilder = new FixturePrompts();
  var phoneticAssistPrefetchLock = new object();
  var nativeExe = "unused"; var moonshineDllDir = ${JSON.stringify(path.join(root, "runtime-moonshine"))}; var moonshineModelRoot = ${JSON.stringify(path.join(root, "model-fixture"))};
  if (scenario is "missing-joint" or "missing-joint-profile" or "missing-standalone-profile") moonshineModelRoot = "";
  if (scenario == "missing-moonshine-runtime") moonshineDllDir += "-missing";
  var whisperDllDir = ${JSON.stringify(path.join(root, "runtime-whisper"))}; var whisperCpuDllDir = "unused"; var whisperModelPath = ${JSON.stringify(path.join(root, "whisper-model", "model.bin"))};
  if (scenario == "missing-whisper-runtime") whisperDllDir += "-missing";
  if (scenario == "missing-whisper-model") whisperModelPath += "-missing";
  if (scenario == "unavailable-whisper-profile") whisperDllDir = "";
  ${source.slice(readinessStart, readinessEnd)}
  var whisperUseGpu = true; var sileroVadModelPath = ""; var onnxRuntimeDllDir = "";
  long onsetControlGeneration = 0; int onsetInputChanged = 0; bool onsetExplicitInput = false, onsetIdleKnown = false;
  string onsetCaptureId = "";
  void BindOnset(bool enabled, bool newSession = false, long? expectedGeneration = null) { }
  var lastRealtimeEngine = "old-engine"; var lastRealtimeLanguage = "ja"; var realtimeTtsEnabled = oldTts;
  var currentAdaptiveIpa = "old-ipa"; var lastPhoneticAssistDebugKey = "old-debug";
  var lastPhoneticAssistAcceptedKey = "old-key"; var lastPhoneticAssistAcceptedText = "old-text"; var lastPhoneticAssistAcceptedReason = "old-reason";
  var lastRealtimeTtsFinal = "old-final"; var lastRealtimeStatusFinalEmitted = "old-status";
  var phoneticAssistPrefetchKey = "old-prefetch"; Task<string?>? phoneticAssistPrefetchTask = Task.FromResult<string?>("old-result");
  bool IsDemoSessionExpired() => false;
  IResult DemoSessionExpiredResult() => throw new Exception("unexpected demo");
  object ApiError(string message, string code) => new { error = message, code };
  void UnlockSteamAchievement(string key) { }
  ${source.slice(start, end)}
  var requestedEngine = scenario is "joint-ja-only" or "missing-joint" or "missing-standalone-profile" or "missing-whisper-runtime" or "missing-whisper-model" or "missing-moonshine-runtime" ? "joint_auto" : "whisper";
  var body = new Dictionary<string, object> { ["engine"] = requestedEngine, ["language"] = "en", ["ttsEnabled"] = !oldTts,
    ["useLanguageProfile"] = scenario is not ("legacy-false" or "joint-ja-only" or "missing-joint" or "missing-whisper-runtime" or "missing-whisper-model" or "missing-moonshine-runtime") };
  if (scenario is "typed" or "invalid") body["addonLanguageMode"] = scenario == "typed" ? 7 : "invalid";
  var context = new DefaultHttpContext();
  context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
  NativeLibraryBridge.Registrations = 0;
  var result = await app.Handler!(context.Request);
  try {
    var rejectedHint = scenario is "typed" or "invalid";
    var missingJoint = scenario is "missing-joint" or "missing-joint-profile" or "unavailable-whisper-profile" or "missing-whisper-runtime" or "missing-whisper-model" or "missing-moonshine-runtime";
    var unavailable = scenario is "unready" or "joint-unready";
    var status = ((IStatusCodeHttpResult)result).StatusCode;
    var value = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
    Require(status == (rejectedHint || missingJoint ? 400 : unavailable ? 409 : 200), "HTTP=" + status + " body=" + value + " started=" + realtimeSessionService.Starts + " engine=" + lastRealtimeEngine);
    Require(addonManager.Enabled == (scenario != "disabled") && addonManager.JointForJa, "saved selection/JA consent changed");
    if (rejectedHint || missingJoint) {
      Require(value.Contains(missingJoint ? "realtime.unsupported_language" : "addon.invalid_language_mode"), "wrong rejection error");
      Require(ReferenceEquals(realtimeSession, oldSession) && ReferenceEquals(realtimeWorkLease, oldLease) && !oldSession.Disposed &&
        addonManager.Applies == 0 && realtimeOutputEpoch.Invalidations == 0 && realtimeSessionService.Starts == 0 &&
        lastRealtimeEngine == "old-engine" && lastRealtimeLanguage == "ja" && realtimeTtsEnabled == oldTts && lastRealtimeTtsFinal == "old-final", "bad hint destroyed existing session/state");
    } else if (unavailable) {
      Require(value.Contains("addon.not_ready") && realtimeSession is null && realtimeWorkLease is null && oldSession.Disposed &&
        realtimeSessionService.Current is null && realtimeSessionService.Starts == 0 && realtimeOutputEpoch.Begins == 0 && NativeLibraryBridge.Registrations == 0,
        "preparation failure substituted/revived a session");
    } else {
      var expectedEngine = scenario is "ready" or "missing-standalone-profile" ? AddonCatalog.QwenId : scenario == "joint-ready" ? "joint_auto" : requestedEngine;
      Require(oldSession.Disposed && realtimeSessionService.Starts == 1 && lastRealtimeEngine == expectedEngine && realtimeTtsEnabled == !oldTts && realtimeOutputEpoch.Begins == 1,
        "normal profile/legacy route changed");
      using var options = JsonDocument.Parse(realtimeSession!.Options);
      Require(options.RootElement.GetProperty("engine").GetString() == expectedEngine && options.RootElement.GetProperty("language").GetString() == "en", "options route/language changed");
      var usesAddon = scenario is "ready" or "joint-ready" or "missing-standalone-profile";
      Require(NativeLibraryBridge.Registrations == (usesAddon ? 1 : 0), "unconsented registration");
      if (usesAddon) Require(options.RootElement.GetProperty("addonJointRequested").GetBoolean() == (scenario == "joint-ready") &&
        options.RootElement.GetProperty("addonJointLanguage").GetString() == "en" && options.RootElement.GetProperty("addonLanguageMode").GetString() == "auto", "explicit EN consent/hint lost");
      else Require(!options.RootElement.TryGetProperty("addonJointRequested", out _) && !options.RootElement.TryGetProperty("addonLanguageMode", out _), "JA consent inherited by EN/base");
    }
  } finally {
    realtimeSessionService.Dispose(); realtimeWorkLease?.Dispose(); oldLease?.Dispose();
  }
  using var released = addonWorkGate.TryEnterAddonOperation();
  Require(released is not null && realtimeMutationGate.CurrentCount == 1, "lease/gate leak");
}
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
sealed class FixtureApp { public Func<HttpRequest, Task<IResult>>? Handler; public void MapPost(string route, Func<HttpRequest, Task<IResult>> handler) { Handler = handler; } }
sealed class FixtureAddon(string scenario) {
  public bool Enabled => scenario != "disabled";
  public bool JointForJa => true;
  public int Applies; string language = "ja";
  public FixtureDescription Describe(string? requested = null) {
    var profile = scenario == "unset" ? null : new FixtureProfile(Enabled, scenario is "joint-ready" or "joint-unready" or "missing-joint-profile");
    return new(requested ?? language, profile, new(profile is not null && scenario is not ("unready" or "joint-unready"), profile?.Enabled == true), profile?.Enabled == true && profile.JointRequested && scenario != "joint-unready");
  }
  public Task ApplyLanguageAsync(string value, CancellationToken token) { language = value; Applies++; return Task.CompletedTask; }
  public Task StopAsync(CancellationToken token) => Task.CompletedTask;
  ${manager.slice(resolveStart, resolveEnd)}
}
sealed record FixtureProfile(bool Enabled, bool JointRequested) { public string EngineId => AddonCatalog.QwenId; public string ModelId => "q8_0"; public string LanguageMode => "auto"; }
sealed record FixtureDescription(string Language, FixtureProfile? Desired, FixtureState State, bool JointEligible);
sealed record FixtureState(bool Ready, bool Enabled);
static class SpeechLinuxRuntime { public static bool Available => true; public static AddonRuntime ForQwen(AddonRuntime runtime) => runtime; }
sealed class FixtureEpoch { public int Invalidations, Begins; public void Invalidate() { Invalidations++; } public void Begin() { Begins++; } }
sealed class FixtureTranscriber { public int CallbackV2 => 1; public int JointCallbackV2 => 2; }
sealed class FixturePrompts { public FixturePrompt Build(string text, string lang) => new(text, false, 0); }
sealed record FixturePrompt(string Prompt, bool Injected, int VocabCount);
sealed class RealtimeSessionController(string options) { public string Options => options; public bool Disposed; public void Dispose() { Disposed = true; } public static RealtimeSessionController Create(string exe, string body, ILogger log) => new(body); }
sealed class FixtureSessions { public RealtimeSessionController? Current; public int Starts; public void Dispose() { Current?.Dispose(); Current = null; } public void Start(Func<RealtimeSessionController> create) { Dispose(); Current = create(); Starts++; } }
static class NativeLibraryBridge { public static int Registrations; public static void RegisterAddonTranscriberV2(string exe, int callback, bool joint = false) { Registrations++; } }
static class SteamAchievementService { public const string FirstSessionStart = "unused"; }
`;
  function writeInput(name, content) {
    const file = path.join(root, name);
    if (fs.existsSync(file)) assert.equal(fs.readFileSync(file, "utf8"), content, `fixture input changed: ${file}`);
    else fs.writeFileSync(file, content, { flag: "wx" });
  }
  writeInput("Program.cs", program);
  const includes = ["src/MobMate.Core/Asr/AddonCatalog.cs", "src/MobMate.Core/Asr/AddonCatalog.Additions.cs",
    "src/MobMate.Core/Asr/AddonOperationGate.cs", "src/MobMate.Core/Asr/WhisperLanguageCatalog.cs",
    "src/MobMate.Core/Asr/MoonshineModelCatalog.cs", "src/MobMate.App.Linux/RealtimeLanguagePolicy.cs"].map(file =>
    `<Compile Include="${path.relative(root, path.resolve(file))}" />`).join("\n");
  writeInput("StartState.csproj", `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />${includes}</ItemGroup></Project>`);
  writeInput("NuGet.Config", '<configuration><packageSources><clear /></packageSources></configuration>');
  const run = spawnSync(dotnet, ["run", "--project", path.join(root, "StartState.csproj"), "-c", "Debug", "--no-launch-profile", "-p:NuGetAudit=false", "-p:UseSharedCompilation=false", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"], { encoding: "utf8", timeout: 120000 });
  assert.equal(run.status, 0, `Linux start profile regression failed:\n${run.stdout || ""}\n${run.stderr || ""}\n${run.error || ""}`);
  console.log("PASS Linux実開始handler＋実ResolveRecognitionEngine: 17scenario×既存TTS状態2種。worker/native/音声はfake。モデル不足時とprofile切替の状態も確認。");
};
