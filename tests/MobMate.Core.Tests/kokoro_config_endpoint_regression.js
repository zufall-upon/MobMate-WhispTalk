// Run from the repository root. Execute both real config handlers with safe native/runtime doubles.
// Generated build inputs stay in temp/; no model, audio device or application process is used.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const section = (file, from, to) => {
  const source = fs.readFileSync(file, "utf8");
  const start = source.indexOf(from), end = source.indexOf(to, start);
  assert.ok(start >= 0 && end > start, file);
  return source.slice(start, end);
};
const linux = section("src/MobMate.App.Linux/Program.cs", 'app.MapPost("/api/tts/config",', "// ---- Virtual mic setup");
const windows = section("src/MobMate.App.WebHost/Program.cs", 'app.MapPost("/api/tts/config",', 'app.MapGet("/api/tts/devices",');
const config = section("src/MobMate.Core/Tts/WindowsSapiTtsService.cs", "public sealed class TtsConfig", "public sealed class TtsResult");
const dir = "temp/kokoro-linux-report/config-endpoint-regression";
fs.mkdirSync(dir, { recursive: true });
fs.writeFileSync(path.join(dir, "ConfigEndpoints.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
</Project>`);
fs.writeFileSync(path.join(dir, "Program.cs"), `
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MobMate.Core.Tts;

namespace MobMate.Core.Tts {
${config}
// Runtime failure is injected after the file guard succeeds, without entering native code.
public sealed class KokoroTtsService {
    public string? ModelDir { get; private set; }
    public int SpeakerId { get; private set; }
    public float Speed { get; private set; }
    public bool IsModelInstalled { get; private set; }
    public string? LastError => IsModelInstalled ? null : "injected initialization failure";
    public static bool IsModelDirValid(string dir) => Path.GetFileName(dir) is "good" or "runtime-fail";
    public void SetConfig(string? dir, int speakerId = 3, float speed = 1) {
        ModelDir = dir; SpeakerId = speakerId; Speed = speed;
        IsModelInstalled = Path.GetFileName(dir) == "good";
    }
}}

class Program {
    static async Task Main() {
        var failures = new List<string>();
        foreach (var linux in new[] { true, false })
        foreach (var scenario in new[] { "engine-only", "runtime-fail", "healthy", "other-engine" }) {
            try { await new Fixture(linux).Check(scenario); }
            catch (Exception ex) { failures.Add((linux ? "Linux" : "Windows") + "/" + scenario + ": " + ex.Message); }
        }
        if (failures.Count > 0) throw new Exception(string.Join("\\n", failures));
    }
}

class Fixture {
    readonly bool linux;
    readonly App app = new();
    TtsConfig linuxTtsConfig = Initial();
    readonly Primary ttsService = new();
    readonly KokoroTtsService kokoroService = new();
    readonly Piper piperService = new();
    readonly Router ttsRouter;
    readonly string kokoroModelRoot = "models", mossModelRoot = "moss", piperExe = "piper", piperModel = "piper-model", nativeExe = "unused", ttsNativeExe = "unused";
    string ttsOutputSinkName = "old-output";
    string? linuxMonitorFallbackReason;
    int monitorChanges;
    const string TtsEngineKokoro = "kokoro";
    readonly string[] TtsEngineValues = ["auto", "windows_sapi", "piper", "piper_plus", "kokoro", "moss", "voicevox"];
    public Fixture(bool linux) {
        this.linux = linux;
        ttsRouter = new Router(ttsService, kokoroService);
        kokoroService.SetConfig("models/good", 8, 1.2f);
        if (linux) RegisterLinux(); else RegisterWindows();
    }
    static TtsConfig Initial() => new() { Engine = "piper", KokoroModelId = "good", KokoroModelDir = "models/good", KokoroSpeakerId = 8, KokoroSpeed = 1.2f,
        OutputDeviceId = "old-output", OutputDeviceName = "old-output", MonitorVolumePercent = 30, WavOutputEnabled = true,
        PiperPlusExePath = "piper", PiperPlusModelPath = "piper-model", VoicevoxApi = "http://127.0.0.1:50021" };
    public async Task Check(string scenario) {
        if (scenario == "engine-only") {
            linuxTtsConfig.KokoroModelId = ttsService.Config.KokoroModelId = "";
            linuxTtsConfig.KokoroModelDir = ttsService.Config.KokoroModelDir = "";
            kokoroService.SetConfig("");
        }
        if (scenario == "other-engine") {
            linuxTtsConfig.KokoroModelId = ttsService.Config.KokoroModelId = "missing";
            linuxTtsConfig.KokoroModelDir = ttsService.Config.KokoroModelDir = "models/missing";
            kokoroService.SetConfig("models/missing", 8, 1.2f);
        }
        var previous = JsonSerializer.Serialize(linux ? linuxTtsConfig : ttsService.Config);
        var previousModel = kokoroService.ModelDir;
        var previousSpeaker = kokoroService.SpeakerId;
        var previousSpeed = kokoroService.Speed;
        var env = Environment.GetEnvironmentVariable("PULSE_SINK");
        NativeLibraryBridge.OutputChanges = 0;
        try {
            var body = new Dictionary<string, object> { ["engine"] = scenario == "other-engine" ? "piper" : "kokoro", ["outputDeviceId"] = "new-output", ["monitorVolumePercent"] = 90, ["wavOutputEnabled"] = false, ["kokoroSpeakerId"] = 4 };
            if (scenario is "runtime-fail" or "healthy") body["kokoroModelId"] = scenario == "healthy" ? "good" : "runtime-fail";
            var ctx = new DefaultHttpContext();
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
            ctx.Request.ContentLength = ctx.Request.Body.Length;
            var result = await app.Handler!(ctx.Request);
            var rejected = scenario is "engine-only" or "runtime-fail";
            Require(((IStatusCodeHttpResult)result).StatusCode == (rejected ? 400 : 200), "HTTP outcome");
            if (rejected) {
                Require(JsonSerializer.Serialize(linux ? linuxTtsConfig : ttsService.Config) == previous, "previous settings changed");
                Require(ttsRouter.ConfigChanges == 0 && piperService.ConfigChanges == 0, "other service settings changed");
                Require(NativeLibraryBridge.OutputChanges == 0 && monitorChanges == 0 && ttsOutputSinkName == "old-output" && Environment.GetEnvironmentVariable("PULSE_SINK") == env, "output side effect");
                Require(kokoroService.ModelDir == previousModel && kokoroService.SpeakerId == previousSpeaker && kokoroService.Speed == previousSpeed, "previous Kokoro state lost");
                var error = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
                Require(error.Contains("tts.kokoro_unavailable"), "existing error code lost");
            } else {
                var applied = linux ? linuxTtsConfig : ttsService.Config;
                Require(applied.OutputDeviceId == "new-output" && applied.MonitorVolumePercent == 90 && !applied.WavOutputEnabled, "normal controls not applied");
                Require(applied.KokoroModelId == (scenario == "healthy" ? "good" : "missing"), "selection preference lost");
                Require(ttsRouter.ConfigChanges == 1 && NativeLibraryBridge.OutputChanges == 1, "normal apply side effects");
                Require(scenario != "healthy" || kokoroService.IsModelInstalled, "healthy auto-config failed");
            }
        } finally { Environment.SetEnvironmentVariable("PULSE_SINK", env); }
    }
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    void RegisterLinux() {
${linux}
    }
    void RegisterWindows() {
${windows}
    }
    static object ApiError(string message, string code = "error") => new { ok = false, code, message };
    static object ApiMessage(string code, string message) => new { code, message };
    static Dictionary<string, object?> ApiProblemCode(string code) => new() { ["code"] = code };
    static bool IsLiteralValue(string value, string[] values) => values.Contains(value);
    static string NormalizeTtsOutputSink(string value) => value;
    static string ResolvePiperExeForEngine(string engine) => "piper";
    static string ResolveDefaultPiperPlusExe(TtsConfig config) => "piper";
    static Model? ResolvePiperPlusModel(string? id) => new("piper-model");
    static string? ResolvePiperPlusModelPathById(string? id) => "piper-model";
    static string? ResolvePiperRuntimeById(string? id) => "piper";
    static string? ResolveKokoroModelDir(string root, string? id) => string.IsNullOrWhiteSpace(id) ? null : Path.Combine(root, id);
    static string? ResolveMossModelDir(string root, string? id) => null;
    static void WarmUpMossIfSelected(TtsConfig config) { }
    Task EnsureLinuxMonitorPlaybackAsync(string sink, int volume) { monitorChanges++; return Task.CompletedTask; }
    sealed record Model(string ModelPath);
    sealed class App {
        public Func<HttpRequest, Task<IResult>>? Handler;
        public void MapPost(string path, Func<HttpRequest, Task<IResult>> handler) => Handler = handler;
    }
    sealed class Primary {
        public TtsConfig Config = Initial();
        public TtsConfig GetConfig() => JsonSerializer.Deserialize<TtsConfig>(JsonSerializer.Serialize(Config))!;
        public DeviceList EnumerateDevices() => new();
    }
    sealed class DeviceList { public Device[] Devices { get; } = []; }
    sealed record Device(string Id, string Name);
    sealed class Router(Primary primary, KokoroTtsService kokoro) {
        public int ConfigChanges;
        public void SetConfig(TtsConfig config) { ConfigChanges++; primary.Config = config; kokoro.SetConfig(config.KokoroModelDir, config.KokoroSpeakerId, config.KokoroSpeed); }
        public Status GetStatus() => new(primary.Config.Engine);
    }
    sealed record Status(string EffectiveEngine);
    sealed class Piper {
        public int ConfigChanges;
        public void SetConfig(string? exePath = null, string? modelPath = null) => ConfigChanges++;
    }
}
static class NativeLibraryBridge {
    public static int OutputChanges;
    public static void SetTtsOutputDevice(string exe, string name, string? id = null) => OutputChanges++;
}
`);
for (const args of [
  ["build", path.join(dir, "ConfigEndpoints.csproj"), "-c", "Debug", "--ignore-failed-sources", "-p:NuGetAudit=false", "--verbosity", "quiet"],
  [path.join(dir, "bin/Debug/net8.0/ConfigEndpoints.dll")],
]) {
  const result = spawnSync("dotnet", args, { stdio: "inherit" });
  assert.equal(result.status, 0, args.join(" "));
}
console.log("Kokoro config endpoints: rejected selections preserve settings/output; normal and other-engine controls apply on both OS paths.");
