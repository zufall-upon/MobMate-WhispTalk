// Run from the repository root: node tests/MobMate.Core.Tests/joint_request_cancellation_regression.js
// Compile the real Windows public route, manager, catalog and operation gate.
// Only package verification/worker creation are injected; no installed runtime,
// native DLL, download, inference or real profile is used.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const { randomUUID } = require("node:crypto");
const root = path.resolve("temp/ux-improvement/joint-contract", randomUUID());
fs.mkdirSync(root, { recursive: true });
const source = fs.readFileSync("src/MobMate.App.WebHost/Program.cs", "utf8");
const route = source.match(/app\.MapPost\("\/api\/addon-asr\/joint", async \(JsonElement body\) =>\s*\{[\s\S]*?\n\}\);/);
assert.ok(route, "the actual public joint route must be exercised");
fs.writeFileSync(path.join(root, "Program.cs"), `
using Microsoft.AspNetCore.Http;
using MobMate.Core.Asr;
using System.Text.Json;
var fixtureRoot = ${JSON.stringify(root)};
foreach (var enabled in new[] { true, false }) {
  foreach (var requested in new[] { true, false }) await CheckAsync(enabled, requested);
}
async Task CheckAsync(bool enabled, bool requested) {
  var directory = Path.Combine(fixtureRoot, $"settings-{enabled}-{requested}");
  var engine = AddonCatalog.Engines.Single();
  var model = engine.Models.First();
  var packageId = Guid.NewGuid().ToString("N");
  Directory.CreateDirectory(Path.Combine(directory, "packages", packageId));
  var settingsPath = Path.Combine(directory, "active.json");
  File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { EngineId = engine.Id, ModelId = model.Id, PackageId = packageId, Enabled = enabled, JointRequested = requested }));
  var installer = new NoDownloadInstaller();
  var starts = 0;
  AddonManager MakeManager() => new(directory, installer, AddonCatalog.Engines, (package, token) => { starts++; throw new Exception("Cancellation must not start/probe a worker"); });
  await using var addonManager = MakeManager();
  var addonLoad = addonManager.LoadAsync();
  await addonLoad;
  var addonWorkGate = new AddonOperationGate();
  var app = new FixtureApp();
  object ApiMessage(string code, string message) => new { code, message };
  ${route[0]}
  Task<IResult> Call(bool value) => app.Handler!(JsonSerializer.SerializeToElement(new { jointRequested = value }));
  var before = File.ReadAllText(settingsPath);
  Require(!addonManager.Snapshot().Ready && addonManager.Snapshot().Enabled == enabled, "fixture must remain unready with its saved Enabled state");
  Require(Status(await Call(true)) == 409 && File.ReadAllText(settingsPath) == before, "new participation still requires readiness/enabled");
  if (!enabled) {
    try { await addonManager.SetEnabledAsync(true, false); throw new Exception("unready enable was accepted"); }
    catch (InvalidOperationException) { }
    Require(File.ReadAllText(settingsPath) == before, "unready activation changed settings");
  }
  using (addonWorkGate.TryEnterWork()) {
    Require(Status(await Call(false)) == 409 && File.ReadAllText(settingsPath) == before, "recognition/bench reservation must still block mutation");
  }
  using (addonWorkGate.TryEnterAddonOperation()) {
    Require(Status(await Call(false)) == 409 && File.ReadAllText(settingsPath) == before, "existing addon operation must still block mutation");
  }
  Require(Status(await app.Handler!(JsonSerializer.SerializeToElement(new { jointRequested = "false" }))) == 400, "boolean request contract");
  Require(Status(await Call(false)) == 200, "explicit cancellation must succeed without readiness or activation");
  var state = addonManager.Snapshot();
  Require(!state.Ready && !state.RuntimeLoaded && state.Enabled == enabled && !state.JointRequested && state.ModelId == model.Id && state.EngineId == engine.Id, "only the saved Joint request may change");
  using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath))) {
    var value = saved.RootElement;
    Require(value.GetProperty("Enabled").GetBoolean() == enabled && !value.GetProperty("JointRequested").GetBoolean(), "persist cancellation without changing Enabled");
    Require(value.GetProperty("PackageId").GetString() == packageId && value.GetProperty("ModelId").GetString() == model.Id, "package/model metadata preserved");
    Require(value.EnumerateObject().Count() == 5, "existing storage shape preserved");
  }
  Require(Status(await Call(false)) == 200, "an explicit false request remains safely idempotent");
  Require(Status(await Call(true)) == 409, "cancellation must not authorize a new unready participation");
  await using var reloaded = MakeManager(); await reloaded.LoadAsync();
  Require(!reloaded.Snapshot().JointRequested && reloaded.Snapshot().Enabled == enabled && !reloaded.Snapshot().Ready, "saved cancellation survives reload without a worker");
  Require(starts == 0 && installer.Prepared == 0, "no load/probe/download side effects");
  using var released = addonWorkGate.TryEnterWork(); Require(released != null, "route lease must be released");
}
static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
sealed class FixtureApp {
  public Func<JsonElement, Task<IResult>>? Handler;
  public FixtureLifetime Lifetime { get; } = new();
  public void MapPost(string path, Func<JsonElement, Task<IResult>> handler) { if (path != "/api/addon-asr/joint") throw new Exception("wrong route"); Handler = handler; }
}
sealed class FixtureLifetime { public CancellationToken ApplicationStopping => CancellationToken.None; }
sealed class NoDownloadInstaller : IAddonPackageInstaller {
  public int Prepared { get; private set; }
  public Task<PreparedAddonPackage> PrepareAsync(string root, AddonEngine engine, AddonModel model, CancellationToken cancellationToken = default) { Prepared++; throw new Exception("Unexpected setup/download"); }
  public Task VerifyAsync(string root, AddonEngine engine, AddonModel model, PreparedAddonPackage package, CancellationToken cancellationToken = default) {
    if (!Directory.Exists(package.Directory)) throw new Exception("fixture package missing");
    return Task.CompletedTask;
  }
}
`);
const files = ["AddonManager", "AddonCatalog", "AddonPackageInstaller", "AddonRecognizer", "SpeechWorker", "AddonOperationGate"];
const includes = files.map(name => `<Compile Include="${path.relative(root, path.resolve(`src/MobMate.Core/Asr/${name}.cs`)).replaceAll("\\", "/")}" Link="${name}.cs" />`).join("\n");
fs.writeFileSync(path.join(root, "joint-contract.csproj"), `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />${includes}</ItemGroup></Project>`);
fs.writeFileSync(path.join(root, "NuGet.Config"), '<configuration><packageSources><clear /></packageSources></configuration>');
const run = spawnSync("dotnet", ["run", "--project", path.join(root, "joint-contract.csproj"), "--configuration", "Debug", "--no-launch-profile", "-p:NuGetAudit=false", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"], { encoding: "utf8", timeout: 120000 });
fs.writeFileSync(path.join(root, "run.log"), `${run.stdout || ""}\n${run.stderr || ""}\nstatus=${run.status}\nerror=${run.error || ""}\n`);
let removedBytes = 0;
function removeOwned(directory) {
  if (!fs.existsSync(directory)) return;
  assert.equal(fs.lstatSync(directory).isSymbolicLink(), false);
  for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
    const file = path.join(directory, item.name); assert.equal(fs.lstatSync(file).isSymbolicLink(), false);
    if (item.isDirectory()) removeOwned(file); else { removedBytes += fs.statSync(file).size; fs.unlinkSync(file); }
  }
  fs.rmdirSync(directory);
}
removeOwned(path.join(root, "bin")); removeOwned(path.join(root, "obj"));
fs.writeFileSync(path.join(root, "cleanup.json"), JSON.stringify({ removedBytes, remainingBuildDirectories: 0, exit: run.status, preserved: ["Program.cs", "joint-contract.csproj", "NuGet.Config", "run.log", "private settings fixtures, including failures"] }, null, 2));
if (run.status !== 0) { process.stderr.write(run.stdout || ""); process.stderr.write(run.stderr || ""); throw run.error || Error(`joint cancellation fixture failed: ${run.status}; ${root}`); }
console.log(`Joint cancellation contract passed: real route/manager/storage, enabled preservation, no new participation, no worker/download, operation conflicts. Removed ${removedBytes} bytes of owned build intermediates; source/logs preserved.`);
