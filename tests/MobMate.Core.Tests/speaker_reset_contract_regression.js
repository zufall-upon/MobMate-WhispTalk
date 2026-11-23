// Run from the repository root: node tests/MobMate.Core.Tests/speaker_reset_contract_regression.js
// Compile the exact Windows and Linux public route bodies with a fault-injected
// native bridge and the real vocabulary store, never a user profile/native DLL.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const { randomUUID } = require("node:crypto");
const root = path.resolve("temp/ux-improvement/reset-contract", randomUUID());
fs.mkdirSync(root, { recursive: true });
const bodies = ["MobMate.App.WebHost", "MobMate.App.Linux"].map(name => {
  const source = fs.readFileSync(`src/${name}/Program.cs`, "utf8");
  const route = source.match(/app\.MapPost\("\/api\/speaker\/reset", \(\) =>\s*\{[\s\S]*?\n\}\);/);
  assert.ok(route, `${name}: real public route`);
  return route[0];
});
// Windows alone supplies a persisted speakerProfilePath at session creation.
// Its optional confirmation block must not change the shared reset contract.
const persistenceGuard = /    \/\/ Confirm the Windows profile[\s\S]*?    \/\/ End Windows profile confirmation\.\r?\n/;
assert.equal(bodies[0].replace(persistenceGuard, "").replace(/\r/g, ""), bodies[1].replace(/\r/g, ""), "shared reset behavior must have Linux parity");
const functions = bodies.map((body, index) => `
void Check${index}() {
    var app = new FixtureApp();
    var vocabPath = Path.Combine(${JSON.stringify(root)}, "vocab-${index}.json");
    var adaptiveVocabStore = new AdaptiveVocabStore(vocabPath);
    var profilePath = Path.Combine(AppContext.BaseDirectory, "speaker_profile.json");
    var adaptiveRmsLock = new object();
    double adaptiveRmsAverage = 17.5;
    int adaptiveRmsCount = 8;
    ${body}
    var modes = new List<string> { "native-failed", "native-threw", "success", "already-empty" };
    if (${index} == 0) modes.AddRange(new[] { "cold-profile-retained", "remove-failed-profile-retained" });
    foreach (var mode in modes) {
        File.Delete(profilePath);
        var profileRetained = mode.EndsWith("profile-retained");
        if (profileRetained) File.WriteAllText(profilePath, "old enrolled speaker data");
        if (mode != "already-empty") File.WriteAllText(vocabPath, "fixture learned data");
        NativeLibraryBridge.Result = mode == "native-failed" ? 9 : 0;
        NativeLibraryBridge.Throw = mode == "native-threw";
        NativeLibraryBridge.Calls = 0;
        adaptiveRmsAverage = 17.5;
        adaptiveRmsCount = 8;
        IResult? result = null;
        try { result = app.Handler!(); }
        catch (InvalidOperationException) { if (mode != "native-threw") throw; }
        Require(NativeLibraryBridge.Calls == 1, "native reset must execute once");
        var failed = mode.StartsWith("native-") || profileRetained;
        if (failed) {
            Require(File.ReadAllText(vocabPath) == "fixture learned data", "unconfirmed reset must preserve vocabulary: " + mode);
            Require(adaptiveRmsAverage == 17.5 && adaptiveRmsCount == 8, "unconfirmed reset must preserve RMS: " + mode);
        } else {
            Require(!File.Exists(vocabPath), "successful opt-in reset must clear vocabulary");
            Require(adaptiveRmsAverage == 0 && adaptiveRmsCount == 0, "successful reset must clear RMS");
        }
        if (result != null) {
            var json = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
            Require(json.GetProperty("success").GetBoolean() == !failed, "success contract: " + mode);
            Require(json.GetProperty("adaptiveVocabReset").GetBoolean() == (mode == "success"), "absent file is not an error");
            Require(json.GetProperty("code").GetString() == (mode == "native-failed" || profileRetained ? "speaker.reset_failed" : null), "error code contract");
        }
        if (profileRetained) Require(File.ReadAllText(profilePath) == "old enrolled speaker data", "confirmation must not expand deletion scope");
    }
    File.Delete(profilePath);
}
`);
fs.writeFileSync(path.join(root, "Program.cs"), `using Microsoft.AspNetCore.Http;
using MobMate.Core.Asr;
using System.Text.Json;
${functions.join("\n")}
Check0(); Check1();
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
sealed class FixtureApp {
    public Func<IResult>? Handler;
    public void MapPost(string path, Func<IResult> handler) { if(path != "/api/speaker/reset") throw new Exception("wrong route"); Handler = handler; }
}
static class NativeLibraryBridge {
    public static int Result, Calls;
    public static bool Throw;
    public static int mmv2_speaker_reset() { Calls++; if (Throw) throw new InvalidOperationException("injected native error"); return Result; }
}
// The real native function returns zero both before it has learned a profile
// path and when std::remove fails. Reproduce that public result with a retained
// private file, not a user profile or the installed native DLL.
static class AppContext { public static string BaseDirectory => ${JSON.stringify(root)}; }
`);
const relative = file => path.relative(root, path.resolve(file)).replaceAll("\\", "/");
fs.writeFileSync(path.join(root, "reset-contract.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup>
  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" />
    <Compile Include="${relative("src/MobMate.Core/Asr/AdaptiveVocabStore.cs")}" Link="AdaptiveVocabStore.cs" />
    <Compile Include="${relative("src/MobMate.Core/Asr/SimpleTokenizer.cs")}" Link="SimpleTokenizer.cs" />
  </ItemGroup>
</Project>`);
fs.writeFileSync(path.join(root, "NuGet.Config"), '<configuration><packageSources><clear /></packageSources></configuration>');
const run = spawnSync("dotnet", ["run", "--project", path.join(root, "reset-contract.csproj"), "--configuration", "Debug", "--no-launch-profile", "-p:NuGetAudit=false", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"], { encoding: "utf8", timeout: 120000 });
fs.writeFileSync(path.join(root, "run.log"), `${run.stdout || ""}\n${run.stderr || ""}\nstatus=${run.status}\nerror=${run.error || ""}\n`);
// These intermediates belong only to this new random fixture. Keep source and
// logs, including failures. Never traverse a reparse point to clean a tree.
let removedBytes = 0;
function removeOwnedTree(directory) {
  if (!fs.existsSync(directory)) return;
  for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
    const file = path.join(directory, item.name);
    assert.equal(fs.lstatSync(file).isSymbolicLink(), false, "do not follow cleanup links");
    if (item.isDirectory()) removeOwnedTree(file); else { removedBytes += fs.statSync(file).size; fs.unlinkSync(file); }
  }
  fs.rmdirSync(directory);
}
removeOwnedTree(path.join(root, "bin")); removeOwnedTree(path.join(root, "obj"));
assert.equal(fs.existsSync(path.join(root, "bin")), false); assert.equal(fs.existsSync(path.join(root, "obj")), false);
fs.writeFileSync(path.join(root, "cleanup.json"), JSON.stringify({ removedBytes, remainingBuildDirectories: 0, preserved: ["Program.cs", "reset-contract.csproj", "NuGet.Config", "run.log"], exit: run.status }, null, 2));
if (run.status !== 0) { process.stderr.write(run.stdout || ""); process.stderr.write(run.stderr || ""); throw run.error || Error(`reset fixture failed: ${run.status}; ${root}`); }
console.log(`Speaker reset contract passed: both real route bodies, native failure/throw, zero result with retained Windows profile, successful deletion and already-absent vocabulary. Removed ${removedBytes} bytes of owned fixture build intermediates; source/logs retained.`);
