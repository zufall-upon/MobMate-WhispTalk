#!/usr/bin/python3
"""実Piper sourceを依存なしでcompileし、session所有・取消し・設定境界を検証する。"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--run-dir', required=True)
parser.add_argument('--dotnet', default='dotnet', help='Path to the .NET 8 SDK executable')
parser.add_argument('--router-before', action='store_true', help='pre-change caller oracle from HEAD')
parser.add_argument('--prepare-only', action='store_true', help='Generate inputs and build Debug binary; do not run assertions')
args = parser.parse_args()
DOTNET = args.dotnet
run = Path(args.run_dir).resolve()
assert run.is_relative_to(ROOT / 'temp')
assert not run.is_symlink()
base = run
run = base / ('compiled-inputs-before' if args.router_before else 'compiled-inputs-result-generation')
exe = run / 'fake-piper'
envdir = run / '.sortie-env'
env = dict(os.environ, DOTNET_CLI_HOME=str(envdir), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
           DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1', NUGET_PACKAGES=str(envdir / 'packages'))
source_paths = [ROOT / 'src/MobMate.Core/Tts' / name for name in
                ('PiperPlusTtsService.cs', 'PiperPlusPersistentSessionManager.cs',
                 'TtsProsodyAnalyzer.cs', 'TtsEngineRouter.cs')]

if not args.prepare_only:
    # Run only the binary produced by the separately observed preparation/build.
    # These byte checks reject obsolete source/recipe; native binding remains the
    # authority for the source, generated inputs and actual binary being checked.
    assert run.is_dir() and not run.is_symlink(), 'run --prepare-only first'
    assert (run / 'helper-at-build.py').read_bytes() == Path(__file__).read_bytes(), 'recipe changed after build'
    for path in source_paths:
        current = (subprocess.check_output(['git', 'show', 'HEAD:src/MobMate.Core/Tts/TtsEngineRouter.cs'], cwd=ROOT)
                   if args.router_before and path.name == 'TtsEngineRouter.cs' else path.read_bytes())
        assert (run / (path.name + '.source-at-build')).read_bytes() == current, 'source changed after build: ' + path.name
    binary = run / 'bin/Debug/net8.0/PiperSessionIsolation.dll'
    assert binary.is_file() and not binary.is_symlink() and exe.is_file() and not exe.is_symlink()
    output = base / ('run-' + str(time.time_ns()))
    output.mkdir(parents=True, exist_ok=False)
    scratch = output / 'tmp'
    scratch.mkdir()
    log = output / 'calls.jsonl'
    print('固定入力/binary:', run, '実行結果:', output, flush=True)
    try:
        subprocess.run([DOTNET, 'exec', str(binary), str(run), str(exe), str(log)],
                       cwd=run, env=dict(env, TMPDIR=str(scratch)), check=True)
    finally:
        pids = {json.loads(line)['pid'] for line in log.read_text().splitlines()} if log.exists() else set()
        for pid in pids:
            try:
                command = Path('/proc', str(pid), 'cmdline').read_bytes().split(b'\0')
            except (FileNotFoundError, ProcessLookupError):
                continue
            assert os.fsencode(exe) not in command, 'owned fake Piper has not exited'
        mounts = [line.split()[4] for line in Path('/proc/self/mountinfo').read_text().splitlines()]
        assert not scratch.is_symlink() and not any(m == str(scratch) or m.startswith(str(scratch) + '/') for m in mounts)
        shutil.rmtree(scratch)
    print('Piper session isolation regression: PASS')
    raise SystemExit(0)

# Preparation owns input generation and restore/build, but never executes tests.
print('準備/build保存先:', run, flush=True)
run.mkdir(parents=True, exist_ok=False)
envdir.mkdir()
(run / 'tmp').mkdir()
(run / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>\n')
(run / 'PiperSessionIsolation.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <Compile Include="{ROOT}/src/MobMate.Core/Tts/PiperPlusTtsService.cs" Link="PiperPlusTtsService.cs" />
    <Compile Include="{ROOT}/src/MobMate.Core/Tts/PiperPlusPersistentSessionManager.cs" Link="PiperPlusPersistentSessionManager.cs" />
    <Compile Include="{ROOT}/src/MobMate.Core/Tts/TtsProsodyAnalyzer.cs" Link="TtsProsodyAnalyzer.cs" />
  </ItemGroup>
</Project>\n''')
exe = run / 'fake-piper'
exe.write_text(r'''#!/usr/bin/python3
import json, os, pathlib, sys, time
args = sys.argv[1:]
log = pathlib.Path(os.environ['PIPER_TEST_LOG'])
def event(kind, **data):
    with log.open('a') as f:
        f.write(json.dumps(dict(event=kind, pid=os.getpid(), args=args, **data))+'\n')
def wav(text):
    return b'RIFF'+(248).to_bytes(4,'little')+b'WAVE'+text.encode().ljust(244,b'!')
event('start')
if '--json-input' in args:
    out = pathlib.Path(args[args.index('--output-dir')+1])
    for line in sys.stdin:
        request = json.loads(line)
        event('request', text=request['text'])
        if request['text'].startswith('delay'):
            time.sleep(2)
        (out / request['output_file']).write_bytes(wav(request['text']))
else:
    text = args[args.index('--text')+1]
    event('oneshot', text=text)
    if text == 'ordinary-failure':
        sys.exit(2)
    if '--output_raw' in args:
        sys.stdout.buffer.write(b'\0\0' * 128)
    else:
        pathlib.Path(args[args.index('--output_file')+1]).write_bytes(wav(text))
''')
exe.chmod(0o700)
(run / 'Program.cs').write_text(r'''
using System.Text.Json;
using MobMate.Core.Tts;

var root = args[0]; var exe = args[1]; var log = args[2];
Environment.SetEnvironmentVariable("PIPER_TEST_LOG", log);
string Model(string name) {
    var d = Path.Combine(root, name);
    var p = Path.Combine(d, "voice.onnx");
    if (File.ReadAllText(p) != "model" || File.ReadAllText(Path.Combine(d, "config.json")) != "{\"audio\":{\"sample_rate\":22050}}")
        throw new Exception("prepared model/config changed");
    return p;
}
var m1 = Model("a"); var m2 = Model("b");
var opt = new PiperPlusPersistentSessionManager.SynthesisOptions("ja", "1.00", "0", "0.67", "0.80");
void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
JsonElement[] Events() {
    if (!File.Exists(log)) return Array.Empty<JsonElement>();
    return File.ReadAllLines(log).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
}
async Task WaitRequest(string text) {
    var until = DateTime.UtcNow.AddSeconds(10);
    while (DateTime.UtcNow < until) {
        if (Events().Any(e => e.GetProperty("event").GetString() == "request" && e.GetProperty("text").GetString() == text)) return;
        await Task.Delay(20);
    }
    throw new Exception("request not observed: " + text);
}
int failed = 0;
async Task Check(string name, Func<Task> test) {
    try { await test(); Console.Error.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
}
await Check("distinct-key-same-dir-preserves-wav", async () => {
    await using var manager = new PiperPlusPersistentSessionManager();
    var a = await manager.SynthesizeAsync(exe, m1, null, "first", opt);
    var bytes = File.ReadAllBytes(a.WavPath);
    var b = await manager.SynthesizeAsync(exe, m2, null, "second", opt);
    Require(a.WavPath != b.WavPath && File.Exists(a.WavPath) && File.ReadAllBytes(a.WavPath).SequenceEqual(bytes), "different keys destroyed or overwrote live WAV");
});
await Check("same-key-different-manager-preserves-wav", async () => {
    await using var a = new PiperPlusPersistentSessionManager();
    await using var b = new PiperPlusPersistentSessionManager();
    var x = await a.SynthesizeAsync(exe, m1, null, "manager-a", opt);
    var bytes = File.ReadAllBytes(x.WavPath);
    var y = await b.SynthesizeAsync(exe, m1, null, "manager-b", opt);
    Require(x.WavPath != y.WavPath && File.Exists(x.WavPath) && File.ReadAllBytes(x.WavPath).SequenceEqual(bytes), "other manager destroyed or overwrote live WAV");
});
await Check("caller-cancel-does-not-regenerate", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    using var cancel = new CancellationTokenSource();
    var task = service.SynthesizeAsync("delay-cancel", cancel.Token);
    await WaitRequest("delay-cancel"); cancel.Cancel();
    try { var r = await task; Require(!r.Success, "cancel returned success"); }
    catch (OperationCanceledException) { }
    await Task.Delay(400); // A wrongly launched fallback may log after its parent returns.
    Require(!Events().Any(e => e.GetProperty("event").GetString() == "oneshot" && e.GetProperty("text").GetString() == "delay-cancel"), "cancelled text regenerated by fallback");
});
await Check("configuration-change-invalidates-pending", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    var task = service.SynthesizeAsync("delay-config");
    await WaitRequest("delay-config"); service.SetConfig(modelPath: m2);
    try { var r = await task; Require(!r.Success, "obsolete configuration produced WAV"); }
    catch (OperationCanceledException) { }
    Require(!Events().Any(e => e.GetProperty("event").GetString() == "oneshot" && e.GetProperty("text").GetString() == "delay-config"), "obsolete text regenerated by fallback");
    var next = await service.SynthesizeAsync("new-config");
    Require(next.Success && next.WavBytes is { Length: > 128 }, "new configuration did not synthesize");
});
await Check("queued-request-retains-original-generation", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    var current = service.SynthesizeAsync("delay-queue-current");
    await WaitRequest("delay-queue-current");
    var queued = service.SynthesizeAsync("queued-old-mode");
    service.SetConfig(usePersistent: false);
    foreach (var task in new[] { current, queued }) {
        try { var r = await task; Require(!r.Success, "obsolete queued/current call succeeded"); }
        catch (OperationCanceledException) { }
    }
    Require(!Events().Any(e => e.TryGetProperty("text", out var t) && t.GetString() == "queued-old-mode"), "old queued request used new mode");
    var after = await service.SynthesizeAsync("new-off-mode");
    Require(after.Success && Events().Any(e => e.GetProperty("event").GetString() == "oneshot" && e.GetProperty("text").GetString() == "new-off-mode"), "new OFF request did not use OFF mode");
});
await Check("unchanged-config-keeps-session", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    var task = service.SynthesizeAsync("delay-unchanged");
    await WaitRequest("delay-unchanged");
    service.SetConfig(exePath: exe, modelPath: m1, usePersistent: true, reflectEmotion: false);
    Require((await task).Success && (await service.SynthesizeAsync("same-config-next")).Success, "unchanged configuration invalidated work");
    var events = Events().Where(e => e.GetProperty("event").GetString() == "request" &&
        new[] { "delay-unchanged", "same-config-next" }.Contains(e.GetProperty("text").GetString())).ToArray();
    Require(events.Length == 2 && events[0].GetProperty("pid").GetInt32() == events[1].GetProperty("pid").GetInt32(), "unchanged settings replaced session");
});
await Check("empty-language-uses-cli-default", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    Require((await service.SynthesizeAsync("empty-language")).Success, "default-language synthesis failed");
    var request = Events().Single(e => e.GetProperty("event").GetString() == "request" && e.GetProperty("text").GetString() == "empty-language");
    Require(!request.GetProperty("args").EnumerateArray().Any(a => a.GetString() == "--language"), "empty --language rejected by real CLI");
});
await Check("explicit-off-on-and-reuse", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(reflectEmotion: false);
    var off = await service.SynthesizeAsync("off-default"); Require(off.Success, "default OFF synthesis");
    service.SetConfig(usePersistent: true);
    var first = await service.SynthesizeAsync("on-first"); var second = await service.SynthesizeAsync("on-second");
    Require(first.Success && second.Success, "persistent output");
    var events = Events();
    var r1 = events.Single(e => e.GetProperty("event").GetString() == "request" && e.GetProperty("text").GetString() == "on-first");
    var r2 = events.Single(e => e.GetProperty("event").GetString() == "request" && e.GetProperty("text").GetString() == "on-second");
    Require(r1.GetProperty("pid").GetInt32() == r2.GetProperty("pid").GetInt32(), "not same persistent process");
    Require(events.Any(e => e.GetProperty("event").GetString() == "oneshot" && e.GetProperty("text").GetString() == "off-default"), "default was not one-shot");
});
await Check("real-router-cancellation-does-not-speak", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    var caller = new CallerFixture(service);
    using var cancel = new CancellationTokenSource();
    var task = caller.Run("delay-router", cancel.Token);
    await WaitRequest("delay-router"); cancel.Cancel();
    try { await task; } catch (OperationCanceledException) { }
    Require(caller.FallbackCalls == 0 && caller.PlaybackCalls == 0, "cancelled caller reached fallback or playback");
});
await Check("real-router-config-change-does-not-speak", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    var caller = new CallerFixture(service);
    var task = caller.Run("delay-router-config", CancellationToken.None);
    await WaitRequest("delay-router-config"); service.SetConfig(modelPath: m2);
    try { await task; } catch (OperationCanceledException) { }
    Require(caller.FallbackCalls == 0 && caller.PlaybackCalls == 0, "obsolete caller reached fallback or playback");
});
await Check("ordinary-engine-failure-retains-fallback", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    var caller = new CallerFixture(service);
    var result = await caller.Run("ordinary-failure", CancellationToken.None);
    Require(result.Success && caller.FallbackCalls == 1 && caller.PlaybackCalls == 0, "ordinary failure fallback changed");
});
// Result-consumption regressions: synthesis has finished, but output has not started.
await Check("completed-result-config-change-before-playback", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    var caller = new CallerFixture(service);
    caller.BeforeOutputConfig = () => service.SetConfig(modelPath: m2);
    try { await caller.Run("completed-before-config", CancellationToken.None); }
    catch (OperationCanceledException) { }
    Require(caller.FallbackCalls == 0 && caller.PlaybackCalls == 0 && caller.StartedCalls == 0, "completed obsolete WAV reached output");
});
await Check("completed-failure-config-change-before-fallback", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    var caller = new CallerFixture(service);
    using var held = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var blocker = Task.Run(() => { lock (caller.ResultLock) { held.Set(); release.Wait(); } });
    Require(held.Wait(10000), "result lock was not held");
    Task<TtsResult>? pending = null;
    try {
        pending = caller.Run("ordinary-failure", CancellationToken.None);
        // The gate cannot admit this barrier until the failed synthesis has returned.
        Require((await service.SynthesizeAsync("after-failure-barrier")).Success, "synthesis barrier failed");
        service.SetConfig(modelPath: m2);
    } finally { release.Set(); await blocker; }
    try { await pending!; } catch (OperationCanceledException) { }
    Require(caller.FallbackCalls == 0 && caller.PlaybackCalls == 0 && caller.StartedCalls == 0, "completed obsolete failure reached fallback");
});
await Check("completed-result-unchanged-config-still-plays", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(usePersistent: true, reflectEmotion: false);
    var caller = new CallerFixture(service);
    caller.BeforeOutputConfig = () => service.SetConfig(modelPath: m1, usePersistent: true, reflectEmotion: false);
    var result = await caller.Run("completed-unchanged-config", CancellationToken.None);
    Require(result.Success && caller.PlaybackCalls == 1 && caller.FallbackCalls == 0 && caller.StartedCalls == 1, "unchanged result was discarded");
});
await Check("completed-result-caller-cancel-before-playback", async () => {
    using var service = new PiperPlusTtsService(exe, m1);
    service.SetConfig(reflectEmotion: false);
    var caller = new CallerFixture(service);
    using var cancel = new CancellationTokenSource();
    caller.BeforeOutputConfig = cancel.Cancel;
    try { await caller.Run("completed-before-cancel", cancel.Token); }
    catch (OperationCanceledException) { }
    Require(caller.FallbackCalls == 0 && caller.PlaybackCalls == 0 && caller.StartedCalls == 0, "completed cancelled WAV reached output");
});
// End result-consumption regressions.
if (failed != 0) Environment.ExitCode = 1;
''')
router = (subprocess.check_output(['git', 'show', 'HEAD:src/MobMate.Core/Tts/TtsEngineRouter.cs'], cwd=ROOT).decode()
          if args.router_before else (ROOT / 'src/MobMate.Core/Tts/TtsEngineRouter.cs').read_text())
start = router.index('    private async Task<TtsResult> TryPiperAsync(')
end = router.index('    private async Task<TtsResult> TryMossAsync(', start)
with (run / 'Program.cs').open('a') as f:
    f.write(r'''
sealed class FakeConfig { public int MonitorVolumePercent => 0; }
sealed class FakePrimary {
    public Action? BeforeRead;
    public FakeConfig GetConfig() { var action = BeforeRead; BeforeRead = null; action?.Invoke(); return new(); }
}
sealed class FakePlayback {
    public bool IsPlaying => false; public int Calls;
    public void Stop() { }
    public (bool Success, string Message) PlayWav(byte[] bytes, int volume, long time) { Calls++; return (true, ""); }
}
readonly struct TtsResult {
    public bool Success { get; init; }
    public static TtsResult Ok() => new() { Success = true };
    public static TtsResult Fail(string error) => new() { Success = false };
}
sealed class CallerFixture {
    readonly PiperPlusTtsService? _piperService;
    readonly FakePlayback _playbackService = new(); readonly FakePrimary _primaryService = new();
    readonly object _lock = new(); string? _piperError;
    public int FallbackCalls, StartedCalls; public int PlaybackCalls => _playbackService.Calls;
    public CallerFixture(PiperPlusTtsService service) { _piperService = service; }
    public object ResultLock => _lock;
    public Action? BeforeOutputConfig { set => _primaryService.BeforeRead = value; }
    public Task<TtsResult> Run(string text, CancellationToken ct) => TryPiperAsync(text, false, ct);
    TtsResult TrySapiAsync(string text, bool interrupt) { FallbackCalls++; return TtsResult.Ok(); }
    void SaveExternalWav(string engine, byte[] bytes) { }
    void MarkExternalTtsStarted(string text) { StartedCalls++; }
''')
    f.write(router[start:end])
    f.write('}\n')
for name in ('a', 'b'):
    model = run / name
    model.mkdir()
    (model / 'voice.onnx').write_text('model')
    (model / 'config.json').write_text('{"audio":{"sample_rate":22050}}')
for path in source_paths:
    content = router.encode() if args.router_before and path.name == 'TtsEngineRouter.cs' else path.read_bytes()
    (run / (path.name + '.source-at-build')).write_bytes(content)
(run / 'helper-at-build.py').write_bytes(Path(__file__).read_bytes())
subprocess.run([DOTNET, 'restore', str(run / 'PiperSessionIsolation.csproj'), '--configfile', str(run / 'NuGet.Config'), '-p:NuGetAudit=false'],
               cwd=run, env=dict(env, TMPDIR=str(run / 'tmp')), check=True)
subprocess.run([DOTNET, 'build', str(run / 'PiperSessionIsolation.csproj'), '--no-restore', '-c', 'Debug', '-p:Demo=false', '-p:UpdatePackage=false'],
               cwd=run, env=dict(env, TMPDIR=str(run / 'tmp')), check=True)
shutil.rmtree(run / 'tmp')
print('入力生成・Debug build完了。15挙動は未実行。binary/入力はReviewまで保持。')
