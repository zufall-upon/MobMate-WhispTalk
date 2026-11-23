"""Run focused real-source status/poll/client regressions, without capture or models.

Uses the existing Debug build and source-handler isolation pattern. All generated
inputs/logs stay in the requested temp directory; owned binaries are removed.
"""
import argparse
import ast
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument("--run-dir", required=True)
parser.add_argument("--tts-caller-only", action="store_true", help="Run real-source TTS caller/client fixtures only; no native/device tests")
parser.add_argument("--windows-final-segment-only", action="store_true", help="Run only the offline Windows final-segment caller fixture")
parser.add_argument("--standalone-failure-only", action="store_true", help="Run the offline standalone addon failure regression")
parser.add_argument("--core-dll", type=Path, default=ROOT / "src/MobMate.Core/bin/Debug/net8.0/MobMate.Core.dll")
parser.add_argument("--dotnet-root", type=Path, required=True, help="Existing .NET SDK root")
parser.add_argument("--node", help="Node.js executable, required outside the sandbox modes")
parser.add_argument("--native-build-dir", type=Path, help="Existing native build directory for the full mode")
parser.add_argument("--budget-json", type=Path, help="Original high-water budget record")
parser.add_argument("--pulse-json", type=Path, help="Original prior capacity-charge record")
parser.add_argument("--windows-reservations", type=Path, help="Windows reservation directory under this checkout's temp")
parser.add_argument("--standalone-reservations", type=Path, help="Standalone reservation directory under this checkout's temp")
parser.add_argument("--capped-output", type=Path, help="Original capped-output helper")
parser.add_argument("--retained-tts-seconds", help="Original separate TTS operation usage and limit")
parser.add_argument("--native-archive", type=Path, help="Existing native static archive for standalone mode")
parser.add_argument("--sherpa-dir", type=Path, help="Existing sherpa native library directory for standalone mode")
args = parser.parse_args()
dotnet_root = args.dotnet_root.resolve()
dotnet = dotnet_root / "dotnet"
if not dotnet.is_file():
    parser.error("--dotnet-root must contain the existing dotnet executable")
if args.standalone_failure_only or args.windows_final_segment_only:
    required = ("budget_json", "pulse_json", "windows_reservations", "capped_output", "retained_tts_seconds")
    if args.standalone_failure_only:
        required += ("standalone_reservations",)
        if os.environ.get("MOBMATE_STANDALONE_BEFORE") != "1":
            required += ("native_archive", "sherpa_dir")
    missing = [name.replace("_", "-") for name in required if getattr(args, name) is None]
    if missing:
        parser.error("missing sandbox inputs: " + ", ".join("--" + name for name in missing))
    for directory in (args.windows_reservations, args.standalone_reservations):
        if directory is not None and (not directory.is_dir() or not directory.resolve().is_relative_to(ROOT / "temp")):
            parser.error("existing reservation directories must be under this checkout's temp")
else:
    if not args.node or (not args.tts_caller_only and args.native_build_dir is None):
        parser.error("--node and, for full mode, --native-build-dir are required")


def standalone_failure_only():
    """Run the selected source branches with fake callbacks in a capped namespace."""
    assert not args.tts_caller_only and not args.windows_final_segment_only
    before = os.environ.get("MOBMATE_STANDALONE_BEFORE") == "1"
    caller_only = os.environ.get("MOBMATE_STANDALONE_CALLER_PREFLIGHT") == "1"
    assert not (before and caller_only)
    run = Path(args.run_dir).resolve()
    base = args.standalone_reservations.resolve()
    assert run.parent == base
    native = (ROOT / "native/mobmate_native_core/src/native_api.cpp").read_text()
    fresh = native[native.index("    /* fresh segment transcript - no accumulation from previous segments */"):
                   native.index("    const std::vector<int16_t>* use_samples = &pcm_samples;")]
    begin = native.index("    auto start = std::chrono::steady_clock::now();\n    if (use_addon)")
    branch = native[begin:native.index("    if (use_paraformer_zh) {", begin)]
    fixture = r'''
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
enum class RealtimeState { kProcessing, kRecording, kCompleted };
struct PcmStats {};
struct RealtimeSession {
    std::string realtime_engine = "haas_ja", addon_engine_id = "haas_ja", language = "ja", addon_language_mode = "requested";
    std::string final_text, selected_engine, decision_reason, addon_candidate_text;
    bool has_transcript = false, addon_candidate_attempted = false, addon_candidate_success = false, copy_final_to_clipboard = true;
    int latency_ms = 0; double vad_start_prob = 0;
    int64_t addon_candidate_ms = -1, addon_candidate_wall_ms = -1, addon_wall_ms = -1;
    RealtimeState state = RealtimeState::kRecording;
};
using V1 = int (*)(const int16_t*, int, int, char*, int, int64_t*);
using V2 = int (*)(const char*, const char*, const char*, const int16_t*, int, int, char*, int, int64_t*);
std::atomic<V1> g_addon_transcriber{nullptr}; std::atomic<V2> g_addon_transcriber_v2{nullptr};
int alternative_calls = 0, clipboard_calls = 0, callback_calls = 0; bool fail = false;
int v1(const int16_t*, int, int, char* text, int, int64_t* ms) {
    ++callback_calls; if (fail) return -1; std::strcpy(text, "Previous final"); *ms = 17; return 0;
}
int v2(const char*, const char*, const char*, const int16_t* p, int n, int rate, char* t, int cap, int64_t* ms) {
    return v1(p, n, rate, t, cap, ms);
}
void mmv2_clipboard_set_text(const char*) { ++clipboard_calls; }
bool should_accept_realtime_final_text(const std::string&, const PcmStats&, const std::string&, const std::string&, std::string&, const std::vector<int16_t>*, double) { return true; }
int transcribe_realtime_samples(RealtimeSession* s, const std::vector<int16_t>&, bool keep, int, double, const PcmStats*) {
    ++alternative_calls; s->final_text = "Unselected fallback"; s->has_transcript = true;
    s->selected_engine = "moonshine"; s->decision_reason = "single_engine";
    s->state = keep ? RealtimeState::kRecording : RealtimeState::kCompleted; return 0;
}
int actual_branch(RealtimeSession* session, const std::vector<int16_t>& pcm_samples, bool keep_recording, int seg_id, double capture_rms, const PcmStats* stats) {
    const bool use_addon = true; const auto addon_wall_start = std::chrono::steady_clock::now();
    /*FRESH*/
    const auto& padded_samples = pcm_samples;
    /*BRANCH*/
    return -99;
}
void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
int main(int argc, char** argv) {
    const bool before = argc == 2 && std::string(argv[1]) == "before";
    const std::vector<int16_t> pcm(32, 1000); PcmStats stats;
    try {
        for (bool use_v2 : {false, true}) for (bool keep : {false, true}) {
            RealtimeSession s; callback_calls = alternative_calls = clipboard_calls = 0;
            g_addon_transcriber.store(use_v2 ? nullptr : v1); g_addon_transcriber_v2.store(use_v2 ? v2 : nullptr);
            fail = false;
            require(actual_branch(&s, pcm, keep, 1, 0, &stats) == 0 && s.final_text == "Previous final", "successful previous final");
            fail = true;
            require(actual_branch(&s, pcm, keep, 2, 0, &stats) == 0, "failure return");
            require(callback_calls == 2 && clipboard_calls == 1, "callback/clipboard counts");
            if (before) {
                require(alternative_calls == 1 && s.final_text == "Unselected fallback" && s.selected_engine == "moonshine", "old recursive fallback not reproduced");
            } else {
                require(alternative_calls == 0 && s.final_text.empty() && !s.has_transcript && s.selected_engine == "none" &&
                        s.decision_reason == "addon_inference_failed" && s.latency_ms == 0, "failure emitted alternative/stale result");
                require(s.state == (keep ? RealtimeState::kRecording : RealtimeState::kCompleted), "failure changed session state");
            }
            require(s.realtime_engine == s.addon_engine_id, "stored selection changed");
            std::ofstream("/scratch/native-failure.txt") << s.final_text;
        }
        std::cout << (before ? "BEFORE: alternative called once; unselected final emitted; previous final cleared" :
            "PASS: V1/V2 failed standalone has no alternative, no stale final or clipboard write; state and selection retained") << '\n';
        return 0;
    } catch (const std::exception& e) { std::cerr << "FAIL portable Windows branch: " << e.what() << '\n'; return 1; }
}
'''.replace("/*FRESH*/", fresh).replace("/*BRANCH*/", branch)

    host = (ROOT / "src/MobMate.App.WebHost/Program.cs").read_text()
    def section(begin, end):
        start = host.index(begin)
        return host[start:host.index(end, start)]
    # Reuse the existing caller fixture's actual fake seams, but run only this
    # failure contract rather than its already-completed twelve-case suite.
    tree = ast.parse(Path(__file__).read_text())
    old = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "windows_final_segment_only")
    caller = next(n.value.value for n in old.body if isinstance(n, ast.Assign) and
                  any(isinstance(t, ast.Name) and t.id == "fixture" for t in n.targets))
    main_start = caller.index("    static void Main() {")
    main_end = caller.index("\n}\n\nsealed class Fixture", main_start)
    main = r'''
    static void Main() {
        var nativeFinal = System.IO.File.ReadAllText("/scratch/native-failure.txt");
        var poll = new RealtimePollResult { State = "recording", Final = nativeFinal, SelectedText = nativeFinal,
            SelectedEngine = "none", DecisionReason = "addon_inference_failed", ConfidenceAvailable = true,
            ConfidenceScore = 0.99, FinalPcm16Le = new byte[3200], FinalPcmSampleRate = 16000 };
        int overlay = 0, phonetic = 0;
        var output = RealtimeFinalOutputProcessor.Process(poll, "haas_ja", "ja",
            (p, lang) => { overlay++; return "forbidden overlay"; },
            (p, text) => { phonetic++; return new RealtimeFinalOverride("forbidden assist", "other", "other"); });
        Require(output.Text == "" && output.Engine == "none" && overlay == 0 && phonetic == 0,
            "failed standalone reached managed recognizer or assist");
        using (var f = new Fixture()) {
            f.Reset(true); f.vrchatOscService.Enabled = true;
            f.Poll("Previous final", 1000); f.WaitSpoken(1);
            f.Learn(poll, output.Text); f.Poll(output.Text, 2000);
            Require(!f.SpeakCalled.WaitOne(150) && f.Spoken.Length == 1 && f.Osc.Length == 1 &&
                f.lastTtsFinalText == "Previous final" && f.Learned == 0,
                "failure replayed previous final or created TTS/OSC/learning");
            f.Manual(); f.WaitSpoken(2);
            Require(f.Spoken[1] == "manual speech", "failure canceled manual speech");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.vrchatOscService.Enabled = true; f.translationService.Block = true;
            f.Poll("Already authorized", 1000);
            Require(f.translationService.Started.Wait(3000), "prior translation did not start");
            f.Poll(output.Text, 2000); f.translationService.Release.Set(); f.WaitSpoken(1);
            Require(f.Spoken[0] == "TR:Already authorized" && f.Osc.SequenceEqual(new[] { "TR:Already authorized" }),
                "failure canceled an already authorized speech");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.vrchatOscService.Enabled = true; f.translationService.Block = true;
            f.Poll("Late prior work", 1000);
            Require(f.translationService.Started.Wait(3000), "late translation did not start");
            f.realtimeOutputEpoch.Invalidate(); f.Poll(output.Text, 2000); f.translationService.Release.Set();
            Require(f.SpeakAttempted.WaitOne(3000) && f.Spoken.Length == 0 && f.Osc.Length == 0,
                "invalidated generation emitted output");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.vrchatOscService.Enabled = true; f.translationService.Block = true;
            f.Poll("Obsolete generation", 1000);
            Require(f.translationService.Started.Wait(3000), "obsolete translation did not start");
            f.Poll(output.Text, 1500); f.realtimeOutputEpoch.Begin();
            f.Poll("Latest generation", 2000); f.translationService.Release.Set(); f.WaitSpoken(1);
            Require(f.Spoken.SequenceEqual(new[] { "TR:Latest generation" }) &&
                f.Osc.SequenceEqual(new[] { "TR:Latest generation" }), "latest generation lost or obsolete generation emitted");
        }
        System.Console.WriteLine("PASS real output processor, learning guard and Windows TTS/OSC caller; failure empty, prior/manual preserved, invalidation and latest-wins");
    }
'''
    caller = caller[:main_start] + main + caller[main_end:]
    learning = r'''
    string lastAdaptiveVocabLearnedTextKey = "", lastAdaptiveFinalText = "", lastAdaptiveFinalIpa = "", currentAdaptiveIpa = "";
    DateTimeOffset lastAdaptiveVocabLearnedAt, lastAdaptiveFinalAt;
    readonly object adaptiveRmsLock = new(); int adaptiveRmsCount; double adaptiveRmsAverage;
    readonly FakeAdaptiveOptions adaptiveVocabOptions = new();
    readonly FakeAdaptiveStore adaptiveVocabStore = new(); ILogger adaptiveVocabLogger = NullLogger.Instance;
    static string NormalizeAdaptiveHotwordText(string s) => s;
    public int Learned => adaptiveVocabStore.Calls;
    public void Learn(RealtimePollResult poll, string text) => MaybeLearnAdaptiveAsrText(poll, text);
    public void Manual() { SpeakDirectAsync("manual speech", CancellationToken.None).GetAwaiter().GetResult(); SpeakAttempted.Set(); }
    /*ACTUAL_LEARNING*/
'''
    caller = caller.replace("    /*ACTUAL_PENDING*/", learning + "\n    /*ACTUAL_PENDING*/")
    caller += r'''
sealed class FakeAdaptiveOptions { public double EmphaticRmsMultiplier = 2, RespeakSamePollToleranceSeconds = 1, RespeakWindowSeconds = 10; }
sealed class FakeAdaptiveStore { public int Calls; public int AddText(string t, string l, string s) { Calls++; return 1; } public void AdjustPriority(string t, int p) { Calls++; } }
static class AdaptiveVocabSources { public const string Asr = "asr", AsrEmphatic = "asr_emphatic"; }
static class PhoneticAssist { public static double IpaSimilarity(string a, string b) => 0; }
'''
    boundary = next(line.strip() for line in host.splitlines() if line.startswith("long lastTtsFinalDetectedAtMs ="))
    pending = section("Task SpeakWithPendingAsync(", "pendingSpeechService.OnTimeoutSpeakAsync")
    pending = pending.replace("Task SpeakWithPendingAsync(", "Task CapturedSpeakWithPendingAsync(", 1)
    for marker, value in (("BOUNDARY_DECLARATION", boundary), ("ACTUAL_HELPERS", section("static string GetNewFinalText(", "void MaybeLearnAdaptiveAsrText(")),
                          ("ACTUAL_LEARNING", section("void MaybeLearnAdaptiveAsrText(", "void MaybeSpeakRealtimeFinal(")),
                          ("ACTUAL_PENDING", pending), ("ACTUAL_CALLER", section("void MaybeSpeakRealtimeFinal(", "(string? Text, string? Engine, string? DecisionReason) TryApplyPhoneticAssist(")),
                          ("ACTUAL_RESET", section("realtimeTtsEnabled = ttsEnabled;", "realtimeLogger.LogInformation(\"[Realtime] op="))):
        caller = caller.replace("/*" + marker + "*/", value)

    mib = 1024 * 1024
    cap = 256 * 1024 if before else 128 * 1024 if caller_only else 1920 * 1024
    # The previous caller attempts already reserved 1 MiB for their persistent
    # exports. Share that reservation only after accounting for both trees.
    exports = mib
    high = json.loads(args.budget_json.read_text())["budget"]["high_water_conservative"]
    prior = json.loads(args.pulse_json.read_text())["capacity_charge_bytes"]
    windows = args.windows_reservations.resolve()
    prior += sum(json.loads(p.read_text())["per_attempt_tmpfs_cap_bytes"] for p in windows.glob("*/reservation.json"))
    prior += sum(json.loads(p.read_text())["per_attempt_tmpfs_cap_bytes"] for p in base.glob("*/reservation.json")) if base.exists() else 0
    retained = sum(p.stat().st_blocks * 512 for folder in (windows, base) if folder.exists()
                   for p in folder.glob("*/*") if p.is_file() and not p.is_symlink())
    assert retained + 128 * 1024 <= exports, "shared persistent export reserve exhausted"
    charge = prior + cap + exports
    assert high["additional"] + charge <= 320 * mib, "additional capacity exhausted"
    assert high["new"] + charge + 64 * mib <= 1024 * mib, "new capacity exhausted"
    assert high["total"] + charge + 64 * mib <= 4096 * mib, "private capacity exhausted"
    fs = os.statvfs(ROOT); root_free = fs.f_bavail * fs.f_frsize
    assert root_free >= (1024 + 64) * mib + exports, "root free-space floor"
    def memory():
        return {x.split(":")[0]: int(x.split()[1]) * 1024 for x in Path("/proc/meminfo").read_text().splitlines()
                if x.startswith(("MemAvailable:", "SwapTotal:", "SwapFree:"))}
    mem_before = memory()
    assert mem_before["MemAvailable"] >= 512 * mib + cap, "RAM guard"
    core = args.core_dll.resolve()
    assert core.is_file(), core
    run.mkdir(parents=True, exist_ok=False)
    (run / "reservation.json").write_text(json.dumps({"historical_high_water": high, "previous_charge_bytes": prior,
        "per_attempt_tmpfs_cap_bytes": cap, "all_attempts_and_exports_charge_bytes": charge, "export_reserve_bytes": exports,
        "shared_exports_retained_bytes": retained,
        "root_available_before": root_free, "memory_before": mem_before, "retained_tts_seconds": args.retained_tts_seconds}, indent=2) + "\n")
    (run / "branch.cpp").write_text(fixture)
    if not before:
        (run / "caller.cs").write_text(caller)
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("capped_output", args.capped_output.resolve())
    capped = importlib.util.module_from_spec(spec); spec.loader.exec_module(capped)
    child = r'''
import glob, importlib.util, json, os, pathlib, subprocess, sys
root, run, cap, phase, core, archive, sherpa = sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4], sys.argv[5], sys.argv[6], sys.argv[7]
spec = importlib.util.spec_from_file_location("cap", "/fixtures/capped-output.py")
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
info = m.mount_check(cap)
print("ISOLATION tmpfs_capacity_bytes=", info["tmpfs_capacity_bytes"], "audio=false gpu=false network=false", flush=True)
os.mkdir('/scratch/tmp'); os.mkdir('/scratch/home')
def call(name, argv):
    result = subprocess.run(argv, cwd='/scratch')
    print(name, 'exit', result.returncode, flush=True)
    if result.returncode: sys.exit(result.returncode)
if phase != 'caller':
    call('WINDOWS_BRANCH_COMPILE', ['/usr/bin/c++', '-std=c++17', '-O0', '-g0', '-pipe', run + '/branch.cpp', '-o', '/scratch/branch'])
    call('WINDOWS_BRANCH_RUN', ['/scratch/branch'] + (['before'] if phase == 'before' else []))
else:
    pathlib.Path('/scratch/native-failure.txt').write_text('')
if phase != 'before':
    pathlib.Path('/scratch/Fixture.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {'tfm': 'net8.0', 'frameworks': [
        {'name': 'Microsoft.NETCore.App', 'version': '8.0.33'}, {'name': 'Microsoft.AspNetCore.App', 'version': '8.0.33'}]}}))
    os.symlink(core, '/scratch/MobMate.Core.dll')
    refs = sorted(glob.glob('/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.33/ref/net8.0/*.dll'))
    refs += ['/dotnet/packs/Microsoft.AspNetCore.App.Ref/8.0.33/ref/net8.0/Microsoft.Extensions.Logging.Abstractions.dll', core]
    assert len(refs) > 100
    compiler = ['/dotnet/dotnet', '/dotnet/sdk/8.0.427/Roslyn/bincore/csc.dll', '-nologo',
        '-noconfig', '-nostdlib+', '-langversion:latest', '-nullable:enable', '-nowarn:0436', '-target:exe', '-out:/scratch/Fixture.dll']
    compiler += ['-r:' + ref for ref in refs]
    compiler += [run + '/caller.cs', root + '/src/MobMate.Core/Text/RealtimeTextFilter.cs',
        root + '/src/MobMate.Core/Realtime/RealtimeOutputEpoch.cs', root + '/src/MobMate.Core/Realtime/RealtimeFinalOutputProcessor.cs']
    call('CALLER_COMPILE', compiler)
    call('CALLER_RUN', ['/dotnet/dotnet', '/scratch/Fixture.dll'])
    # Leave no caller binaries competing with the native compiler's bounded space.
    if phase != 'caller':
        for path in ('/scratch/branch', '/scratch/Fixture.dll', '/scratch/Fixture.runtimeconfig.json', '/scratch/MobMate.Core.dll'):
            os.unlink(path)
        call('LINUX_CURRENT_SOURCE_COMPILE', ['/usr/bin/c++', '-std=c++20', '-Os', '-g0', '-pipe', '-pthread',
            '-ffunction-sections', '-fdata-sections', '-DSTANDALONE_FAILURE_REGRESSION', '-I', root + '/native/mobmate_native_core/include',
            '-I', root + '/libs/sherpa-onnx/include', '-I', root + '/libs/onnxruntime/include', '-I', root + '/native/vendor',
            root + '/tests/MobMate.Core.Tests/addon_linux_connection_regression.cpp', archive, sherpa,
            '-ldl', '-lm', '-Wl,--gc-sections', '-Wl,-rpath,' + str(pathlib.Path(sherpa).parent), '-s', '-o', '/scratch/linux-regression'])
        call('LINUX_CURRENT_SOURCE_RUN', ['/scratch/linux-regression', '--standalone-failure-only'])
print('TMPFS_USED_BEFORE_DESTROY', (os.statvfs('/scratch').f_blocks-os.statvfs('/scratch').f_bfree)*os.statvfs('/scratch').f_frsize, flush=True)
'''
    command = ['/usr/bin/bwrap', '--die-with-parent', '--unshare-all', '--unshare-user', '--disable-userns', '--clearenv',
               '--ro-bind', '/usr', '/usr', '--ro-bind', '/lib', '/lib', '--ro-bind', '/lib64', '/lib64',
               '--ro-bind', '/etc', '/etc', '--symlink', '/usr/bin', '/bin', '--ro-bind', str(ROOT), str(ROOT),
               '--ro-bind', str(dotnet_root), '/dotnet', '--dir', '/fixtures',
               '--ro-bind', str(args.capped_output.resolve()), '/fixtures/capped-output.py',
               '--ro-bind', str(core), '/fixtures/MobMate.Core.dll', '--size', str(cap), '--tmpfs', '/scratch',
               '--symlink', '/scratch/tmp', '/tmp', '--symlink', '/scratch/run', '/run', '--dir', '/dev',
               '--symlink', '/scratch/shm', '/dev/shm', '--symlink', '/proc/self/fd', '/dev/fd',
               '--symlink', '/proc/self/fd/0', '/dev/stdin', '--symlink', '/proc/self/fd/1', '/dev/stdout',
               '--symlink', '/proc/self/fd/2', '/dev/stderr']
    archive = args.native_archive.resolve() if args.native_archive else None
    if not before:
        sherpa_dir = args.sherpa_dir.resolve()
        sherpa = sherpa_dir / 'libsherpa-onnx-c-api.so'
        assert archive.is_file() and sherpa.is_file(), 'existing auxiliary native dependencies missing'
        command += ['--ro-bind', str(sherpa_dir), '/fixtures/sherpa',
                    '--ro-bind', str(archive), '/fixtures/native-core.a']
    for name in ('null', 'zero', 'random', 'urandom'):
        command += ['--dev-bind', '/dev/' + name, '/dev/' + name]
    command += ['--proc', '/proc', '--remount-ro', '/proc', '--remount-ro', '/', '--chdir', '/scratch']
    for key, value in {'HOME': '/scratch/home', 'TMPDIR': '/scratch/tmp', 'DOTNET_ROOT': '/dotnet',
                       'DOTNET_CLI_HOME': '/scratch/home', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
                       'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_EnableDiagnostics': '0', 'LANG': 'C.UTF-8',
                       'PATH': '/usr/bin:/bin', 'PYTHONDONTWRITEBYTECODE': '1'}.items():
        command += ['--setenv', key, value]
    phase = 'before' if before else 'caller' if caller_only else 'after'
    command += ['--', '/usr/bin/python3', '-B', '-c', child, str(ROOT), str(run), str(cap), phase,
                '/fixtures/MobMate.Core.dll', '/fixtures/native-core.a', '/fixtures/sherpa/libsherpa-onnx-c-api.so']
    result = capped.receive(command, time.monotonic() + 180)
    output = (result.get('stdout_tail') or '') + (result.get('stderr') or '')
    (run / 'fixture.log').write_text(output[-65536:])
    result.pop('command', None)
    result.update({'scope': 'portable extracted Windows branch/caller and Linux current native_api.cpp; fake callbacks; not Windows native execution',
                   'native_sha256': hashlib.sha256(native.encode()).hexdigest(),
                   'host_sha256': hashlib.sha256(host.encode()).hexdigest(),
                   'core_sha256': hashlib.sha256(core.read_bytes()).hexdigest(),
                   'auxiliary_archive': str(archive),
                   'root_available_after': os.statvfs(ROOT).f_bavail * os.statvfs(ROOT).f_frsize,
                   'memory_after': memory(), 'owned_pid_reaped': not Path('/proc', str(result['pid'])).exists(),
                   'tmpfs_visible_in_parent': any(line.split()[4] == '/scratch' for line in Path('/proc/self/mountinfo').read_text().splitlines())})
    (run / 'results.json').write_text(json.dumps(result, indent=2) + '\n')
    print(output, flush=True)
    print('namespace_exit', result['exit'], 'elapsed_seconds', result['elapsed_seconds'],
          'exited', result['namespace_process_exited'], 'parent_tmpfs', result['tmpfs_visible_in_parent'], flush=True)
    assert result['namespace_process_exited'] and result['owned_pid_reaped'] and not result['tmpfs_visible_in_parent']
    return 0 if result['exit'] == 0 else 1


if args.standalone_failure_only:
    raise SystemExit(standalone_failure_only())


def windows_final_segment_only():
    """Compile the extracted caller only, in the existing single capped-bwrap pattern."""
    assert not args.tts_caller_only
    run = Path(args.run_dir).resolve()
    windows = args.windows_reservations.resolve()
    assert run.parent == windows
    source_path = ROOT / "src/MobMate.App.WebHost/Program.cs"
    source = source_path.read_text()

    def section(begin, end):
        first = source.index(begin)
        return source[first:source.index(end, first)]

    actual_helpers = section("static string GetNewFinalText(", "void MaybeLearnAdaptiveAsrText(")
    actual_pending = section("Task SpeakWithPendingAsync(", "pendingSpeechService.OnTimeoutSpeakAsync")
    actual_pending = actual_pending.replace("Task SpeakWithPendingAsync(", "Task CapturedSpeakWithPendingAsync(", 1)
    actual_caller = section("void MaybeSpeakRealtimeFinal(", "(string? Text, string? Engine, string? DecisionReason) TryApplyPhoneticAssist(")
    actual_reset = section("realtimeTtsEnabled = ttsEnabled;", "realtimeLogger.LogInformation(\"[Realtime] op=")
    boundary_declaration = next((line.strip() for line in source.splitlines()
                                 if line.startswith("long lastTtsFinalDetectedAtMs =")),
                                "long lastTtsFinalDetectedAtMs = 0; // fixture observation before the fix")

    # Retained high-water and charge are never reset. Each attempted namespace
    # is conservatively charged at its recorded cap, even after destruction.
    previous = json.loads(args.budget_json.read_text())
    pulse = json.loads(args.pulse_json.read_text())
    high = previous["budget"]["high_water_conservative"]
    prior_charge = pulse["capacity_charge_bytes"]
    mib = 1024 * 1024
    cap = 2 * mib
    exports = mib
    reservations = [json.loads(path.read_text()) for path in windows.glob("*/reservation.json")] if windows.exists() else []
    attempts = len(reservations)
    charge = prior_charge + sum(item["per_attempt_tmpfs_cap_bytes"] for item in reservations) + cap + exports
    assert high["additional"] + charge <= 320 * mib, "additional capacity exhausted"
    assert high["new"] + charge + 64 * mib <= 1024 * mib, "new capacity exhausted"
    assert high["total"] + charge + 64 * mib <= 4096 * mib, "private capacity exhausted"
    retained = sum(path.stat().st_blocks * 512 for path in windows.glob("*/*") if path.is_file()) if windows.exists() else 0
    assert retained + 128 * 1024 <= exports, "persistent result reserve exhausted"
    fs = os.statvfs(ROOT)
    root_free = fs.f_bavail * fs.f_frsize
    assert root_free >= (1024 + 64) * mib + exports, "root free-space floor"
    memory = {line.split(":")[0]: int(line.split()[1]) * 1024 for line in Path("/proc/meminfo").read_text().splitlines()
              if line.startswith(("MemAvailable:", "SwapTotal:", "SwapFree:"))}
    assert memory["MemAvailable"] >= 512 * mib + cap, "RAM guard"
    core = args.core_dll.resolve()
    assert core.is_file(), core

    run.mkdir(parents=True, exist_ok=False)
    reservation = {"historical_high_water": high, "previous_charge_bytes": prior_charge,
                   "attempt_number": attempts + 1, "per_attempt_tmpfs_cap_bytes": cap,
                   "all_attempts_and_exports_charge_bytes": charge, "export_reserve_bytes": exports,
                   "root_available_before": root_free, "memory_before": memory,
                   "retained_tts_seconds": args.retained_tts_seconds}
    (run / "reservation.json").write_text(json.dumps(reservation, indent=2) + "\n")

    fixture = r'''
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MobMate.Core.Realtime;
using MobMate.Core.Text;
using MobMate.Core.Tts;

static class Program {
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Main() {
        var boundaryFailures = new List<string>();
        try {
            using var f = new Fixture();
            f.Reset(true); f.vrchatOscService.Enabled = true;
            f.Poll("Line one\nLine two", 10); f.WaitSpoken(1);
            Require(f.Spoken[0] == "Line one\nLine two" && f.Osc.SequenceEqual(new[] { "Line one\nLine two" }),
                "first known segment lost lines: TTS=[" + f.Spoken[0] + "] OSC=[" + string.Join("|", f.Osc) + "]");
            f.Reset(true); f.Poll("Reset first\nReset second", 20); f.WaitSpoken(2);
            Require(f.Spoken[1] == "Reset first\nReset second" && f.Osc[1] == "Reset first\nReset second", "Start reset lost first segment lines");
            f.Reset(true); f.Filter.NgWords = "blocked phrase";
            f.Poll("blocked phrase\nAllowed tail", 30);
            Require(!f.SpeakCalled.WaitOne(150) && f.Spoken.Length == 2 && f.Osc.Length == 2 && f.lastRealtimeTextFilterResult!.Dropped,
                "first segment NG filtering ignored an earlier line");
        } catch (Exception error) { boundaryFailures.Add(error.Message); }
        try {
            using var f = new Fixture();
            f.Reset(true); f.vrchatOscService.Enabled = true;
            f.Poll("Open the map", 1000); f.WaitSpoken(1);
            f.ttsRouter.Status = new TtsStatus { IsSpeaking = true };
            f.Poll("Open the map", 0);
            Require(!f.SpeakCalled.WaitOne(150) && f.Osc.Length == 1, "retained final dispatched while speaking");
            f.ttsRouter.Status = new TtsStatus();
            f.Poll("Open the map now.", 2000); f.WaitSpoken(2);
            Require(f.Spoken[1] == "Open the map now." && f.Osc[1] == "Open the map now.",
                "retained final during playback erased the boundary: TTS=[" + f.Spoken[1] + "] OSC=[" + f.Osc[1] + "]");
        } catch (Exception error) { boundaryFailures.Add(error.Message); }
        Require(boundaryFailures.Count == 0, string.Join("; ", boundaryFailures));
        using (var f = new Fixture()) {
            f.Reset(true);
            f.Poll("Open the map", 1000); f.WaitSpoken(1);
            f.Poll("Open the map now.", 2000); f.WaitSpoken(2);
            Require(f.Spoken[1] == "Open the map now.", "distinct boundary lost prefix: actual=[" + f.Spoken[1] + "]");
            Require(f.FinalTimes.SequenceEqual(new long[] { 1000, 2000 }), "final timestamp did not reach fake TTS");
            f.Poll("Open the map now.", 3000);
            Require(!f.SpeakCalled.WaitOne(150), "identical final repeated at another boundary");
            f.Poll("", 3000);
            f.Poll("Open the map now. again", 3000); f.WaitSpoken(3);
            Require(f.Spoken[2] == "Open the map now. again", "empty or duplicate final advanced the text boundary");
            f.Reset(true);
            Require(f.lastTtsFinalText == "" && f.lastTtsFinalDetectedAtMs == 0, "actual Start reset did not clear the boundary");
            f.Poll("Open the map", 4000); f.WaitSpoken(4);
            Require(f.Spoken[3] == "Open the map", "Start retained previous text");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.Poll("Alpha", 0); f.WaitSpoken(1);
            f.Poll("Alpha beta", 0); f.WaitSpoken(2);
            Require(f.Spoken[1] == "beta", "missing timestamp cumulative compatibility");
            f.Poll("Alpha beta gamma", 9000); f.WaitSpoken(3);
            Require(f.Spoken[2] == "gamma", "first positive timestamp must retain cumulative compatibility");
            f.Poll("Alpha beta gamma delta", 9000); f.WaitSpoken(4);
            Require(f.Spoken[3] == "delta", "same boundary cumulative compatibility");
            f.Poll("Alpha beta gamma delta epsilon", 0); f.WaitSpoken(5);
            Require(f.Spoken[4] == "epsilon", "zero after positive timestamp cumulative compatibility");
            f.Poll("Alpha beta gamma delta epsilon zeta", 9100); f.WaitSpoken(6);
            Require(f.Spoken[5] == "zeta", "positive after zero timestamp cumulative compatibility");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.Poll("Line one\nLine two", 10); f.WaitSpoken(1);
            f.Poll("Line one\nLine two next", 20); f.WaitSpoken(2);
            Require(f.Spoken[1] == "Line one\nLine two next", "distinct multiline final must stay whole");
            f.Override("raw", "sense", "chosen", 30); f.WaitSpoken(3);
            f.Override("raw next", "sense next", "chosen next", 40); f.WaitSpoken(4);
            Require(f.Spoken[2] == "chosen" && f.Spoken[3] == "chosen next", "final override precedence or boundary changed");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.ttsRouter.Status = new TtsStatus { IsSpeaking = true };
            f.Poll("playing base", 10);
            Require(f.lastTtsFinalText == "playing base" && !f.SpeakCalled.WaitOne(150), "IsSpeaking suppression changed");
            f.ttsRouter.Status = new TtsStatus(); f.Poll("playing base next", 20); f.WaitSpoken(1);
            Require(f.Spoken[0] == "playing base next", "IsSpeaking failed to retain boundary");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.ttsRouter.Status = new TtsStatus { LastTtsText = "echo base", LastTtsStartedAt = DateTime.UtcNow.ToString("O") };
            f.Poll("echo base", 10);
            Require(f.lastTtsFinalText == "echo base" && !f.SpeakCalled.WaitOne(150), "loopback suppression changed");
            f.ttsRouter.Status = new TtsStatus(); f.Poll("echo base next", 20); f.WaitSpoken(1);
            Require(f.Spoken[0] == "echo base next", "loopback failed to retain boundary");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.Filter.NgWords = "blocked phrase"; f.Poll("blocked phrase", 10);
            Require(f.lastRealtimeTextFilterResult!.Dropped && f.lastTtsFinalText == "blocked phrase" && !f.SpeakCalled.WaitOne(150), "NG filter changed");
            f.Poll("blocked phrase extended", 20);
            Require(f.lastRealtimeTextFilterResult!.Dropped && !f.SpeakCalled.WaitOne(150), "NG must inspect the entire distinct final");
            f.Filter.NgWords = ""; f.Poll("blocked phrase allowed", 30); f.WaitSpoken(1);
            Require(f.Spoken[0] == "blocked phrase allowed", "NG drop failed to retain boundary");
            f.Filter.LaughReplacementEnabled = true; f.Filter.ManualLaughTokens = "hehe";
            f.Filter.AutoLaughTokens = ""; f.Filter.LaughReplacementWords = "LAUGH";
            f.Poll("before hehe", 40); f.WaitSpoken(2);
            Require(f.Spoken[1] == "before LAUGH" && f.lastRealtimeTextFilterResult!.LaughReplaced, "filter transformation changed");
            f.Filter.LaughReplacementEnabled = false; f.Poll(new string('x', 520), 50); f.WaitSpoken(3);
            Require(f.Spoken[2] == new string('x', 500), "500-character cap changed");
        }
        using (var f = new Fixture()) {
            f.Reset(false); f.Poll("both disabled", 10);
            Require(f.lastTtsFinalText == "" && !f.SpeakCalled.WaitOne(150) && f.Osc.Length == 0, "disabled TTS/OSC dispatched");
            f.vrchatOscService.Enabled = true; f.Poll("osc only", 20);
            Require(f.Osc.SequenceEqual(new[] { "osc only" }) && !f.SpeakCalled.WaitOne(150), "OSC requires explicit enable; TTS must remain opt-in");
            f.vrchatOscService.Enabled = false; f.realtimeTtsEnabled = true; f.Poll("tts only", 30); f.WaitSpoken(1);
            Require(f.Spoken[0] == "tts only" && f.Osc.Length == 1, "TTS or OSC settings changed");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.vrchatOscService.Enabled = true; f.translationService.Block = true;
            f.Poll("translate me", 10); Require(f.translationService.Started.Wait(3000), "fake translation did not start");
            f.realtimeTtsEnabled = false; f.vrchatOscService.Enabled = false; f.translationService.Release.Set();
            Require(SpinWait.SpinUntil(() => f.Osc.Length == 1, 3000), "captured OSC setting lost");
            Require(f.Osc[0] == "TR:translate me" && !f.SpeakCalled.WaitOne(150), "current TTS setting or captured OSC setting changed");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.translationService.Block = true;
            f.Poll("translate later", 10); Require(f.translationService.Started.Wait(3000), "fake translation did not start");
            f.vrchatOscService.Enabled = true; f.translationService.Release.Set(); f.WaitSpoken(1);
            Require(f.Spoken[0] == "TR:translate later" && f.Osc.Length == 0, "OSC must use captured, not current, setting");
        }
        using (var f = new Fixture()) {
            f.Reset(false); f.vrchatOscService.Enabled = true; f.translationService.Block = true;
            f.Poll("tts enabled later", 10); Require(f.translationService.Started.Wait(3000), "fake translation did not start");
            f.realtimeTtsEnabled = true; f.vrchatOscService.Enabled = false; f.translationService.Release.Set(); f.WaitSpoken(1);
            Require(f.Spoken[0] == "TR:tts enabled later" && f.Osc.SequenceEqual(new[] { "TR:tts enabled later" }), "current TTS enable or captured OSC enable changed");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.vrchatOscService.Enabled = true; f.translationService.Block = true; f.translationService.Fail = true;
            f.Poll("translation failure", 10); Require(f.translationService.Started.Wait(3000), "fake translation did not start");
            f.translationService.Release.Set(); f.WaitSpoken(1);
            Require(f.Spoken[0] == "translation failure" && f.Osc.SequenceEqual(new[] { "translation failure" }), "existing translation-error output changed");
        }
        using (var f = new Fixture()) {
            f.Reset(true); f.vrchatOscService.Enabled = true; f.translationService.Block = true;
            f.Poll("stale translation", 10); Require(f.translationService.Started.Wait(3000), "fake translation did not start");
            f.realtimeOutputEpoch.Invalidate(); f.translationService.Release.Set();
            Require(f.SpeakAttempted.WaitOne(3000), "stale real pending helper was not reached");
            Require(f.Spoken.Length == 0 && f.Osc.Length == 0, "invalidated generation produced output");
        }
    }
}

sealed class Fixture : IDisposable {
    public bool realtimeTtsEnabled;
    public string lastTtsFinalText = string.Empty;
    public /*BOUNDARY_DECLARATION*/
    string lastTtsProsodyFinalText = string.Empty; byte[] lastTtsProsodyPcm16Le = Array.Empty<byte>(); int lastTtsProsodySampleRate;
    string reviewLastCountedFinalText = ""; object reviewPromptLock = new(); int reviewSessionAsrSuccessCount;
    public RealtimeTextFilterResult? lastRealtimeTextFilterResult; string lastFilteredFinalSource = "";
    string lastTranslatedFinalText = "", lastTranslatedFinalSource = "";
    (string? language, int unused) lastStartOptions = ("en", 0);
    public readonly RealtimeOutputEpoch realtimeOutputEpoch = new();
    public readonly FakeTtsRouter ttsRouter = new(); public readonly FakeOsc vrchatOscService = new();
    readonly FakePendingSpeech pendingSpeechService = new();
    public readonly FakeTranslation translationService = new();
    public readonly RealtimeTextFilterConfig Filter = new() { LaughReplacementEnabled = false };
    readonly ConcurrentQueue<(string Text, long Detected)> spoken = new(); public string[] Spoken => spoken.Select(item => item.Text).ToArray();
    public long[] FinalTimes => spoken.Select(item => item.Detected).ToArray(); public string[] Osc => vrchatOscService.Output.ToArray();
    public readonly AutoResetEvent SpeakCalled = new(false);
    public readonly AutoResetEvent SpeakAttempted = new(false);
    ILogger logger = NullLogger.Instance, translationLogger = NullLogger.Instance;
    const bool EnableMossAsrReferenceAudio = false;
    public Fixture() { realtimeOutputEpoch.Begin(); }
    public void Dispose() { translationService.Release.Set(); realtimeOutputEpoch.Dispose(); SpeakCalled.Dispose(); SpeakAttempted.Dispose(); }
    public void Reset(bool ttsEnabled) { /*ACTUAL_RESET*/ }
    public void Poll(string text, long detected) => MaybeSpeakRealtimeFinal(new RealtimePollResult { Final = text, SelectedText = text, FinalDetectedAtMs = detected });
    public void Override(string raw, string sense, string selected, long detected) => MaybeSpeakRealtimeFinal(new RealtimePollResult { Final = raw, SelectedText = raw, FinalDetectedAtMs = detected }, sense, selected);
    public void WaitSpoken(int count) { if (!SpeakCalled.WaitOne(3000) || !SpeakAttempted.WaitOne(3000) || spoken.Count < count) throw new Exception("fake TTS timed out"); }
    RealtimeTextFilterConfig SnapshotTextFilterConfig(string? language) => Filter;
    FakeTranslationConfig LoadTranslationConfig() => new() { TalkTarget = translationService.Block ? "fake-target" : "OFF" };
    void UnlockSteamAchievement(string value) { }
    Task SpeakWithPendingAsync(string text, CancellationToken ct, TtsProsodyProfile? prosody, long detected, RealtimeOutputTicket ticket) {
        var result = CapturedSpeakWithPendingAsync(text, ct, prosody, detected, ticket);
        SpeakAttempted.Set(); return result;
    }
    Task SpeakDirectAsync(string text, CancellationToken ct, TtsProsodyProfile? prosody = null, long detected = 0) {
        ct.ThrowIfCancellationRequested(); spoken.Enqueue((text, detected)); SpeakCalled.Set(); return Task.CompletedTask;
    }
    /*ACTUAL_PENDING*/
    /*ACTUAL_HELPERS*/
    /*ACTUAL_CALLER*/
}
sealed class FakeTtsRouter {
    public TtsStatus Status = new(); public TtsStatus GetStatus() => Status;
    public Task<bool> SetMossReferencePcmAsync(byte[] pcm, int rate, CancellationToken token) => Task.FromResult(false);
}
sealed class FakePendingSpeech {
    public Task EnqueueOrSpeakAsync(string text, Func<string, CancellationToken, Task> speak, CancellationToken token, CancellationToken origin = default)
        => speak(text, token);
}
sealed class FakeOsc {
    public bool Enabled; public readonly ConcurrentQueue<string> Output = new();
    public FakeOsc GetConfig() => this; public void EnqueueText(string text) => Output.Enqueue(text);
}
sealed class FakeTranslationConfig { public string TalkTarget = "OFF"; }
sealed class FakeTranslation {
    public bool Block, Fail; public bool IsLoaded => true;
    public readonly ManualResetEventSlim Started = new(false), Release = new(false);
    public string Translate(string text, string source, string target) { Started.Set(); if (!Release.Wait(3000)) throw new Exception("fake translation barrier timeout"); if (Fail) throw new InvalidOperationException("fake translation error"); return "TR:" + text; }
}
static class SteamAchievementService { public const string FirstTranslatedOutput = "fixture"; }
'''
    for marker, value in (("BOUNDARY_DECLARATION", boundary_declaration), ("ACTUAL_HELPERS", actual_helpers), ("ACTUAL_PENDING", actual_pending),
                          ("ACTUAL_CALLER", actual_caller), ("ACTUAL_RESET", actual_reset)):
        fixture = fixture.replace("/*" + marker + "*/", value)
    (run / "Program.cs").write_text(fixture)

    # Reuse the previously validated bwrap receive/mount guards, not the old TTS
    # runner, build, 96 MiB reservation, daemon, or its elapsed-time budget.
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location("capped_output", args.capped_output.resolve())
    capped = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(capped)
    child = r'''
import glob, importlib.util, json, os, pathlib, resource, subprocess, sys
root, run, core, cap = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4])
resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
spec = importlib.util.spec_from_file_location("cap", "/fixtures/capped-output.py")
module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
isolation = module.mount_check(cap)
print("ISOLATION tmpfs_capacity_bytes=" + str(isolation["tmpfs_capacity_bytes"]) + " writable=/scratch audio=false gpu=false network=false", flush=True)
os.mkdir("/scratch/tmp"); os.mkdir("/scratch/home")
pathlib.Path("/scratch/Fixture.runtimeconfig.json").write_text(json.dumps({"runtimeOptions":{"tfm":"net8.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"8.0.33"},{"name":"Microsoft.AspNetCore.App","version":"8.0.33"}]}}))
os.symlink(core, "/scratch/MobMate.Core.dll")
refs = sorted(glob.glob("/dotnet/packs/Microsoft.NETCore.App.Ref/8.0.33/ref/net8.0/*.dll"))
refs += ["/dotnet/packs/Microsoft.AspNetCore.App.Ref/8.0.33/ref/net8.0/Microsoft.Extensions.Logging.Abstractions.dll", core]
assert len(refs) > 100
# CS0436 is expected: the two linked current source files intentionally take
# precedence over their older types in the read-only MobMate.Core DLL.
compiler = ["/dotnet/dotnet", "/dotnet/sdk/8.0.427/Roslyn/bincore/csc.dll", "-nologo", "-noconfig", "-nostdlib+", "-langversion:latest", "-nullable:enable", "-nowarn:0436", "-target:exe", "-out:/scratch/Fixture.dll"]
compiler += ["-r:" + ref for ref in refs]
compiler += [run + "/Program.cs", root + "/src/MobMate.Core/Text/RealtimeTextFilter.cs", root + "/src/MobMate.Core/Realtime/RealtimeOutputEpoch.cs"]
try:
    result = subprocess.run(compiler, cwd="/scratch")
    print("FIXTURE_COMPILE_EXIT", result.returncode, flush=True)
    if result.returncode == 0:
        result = subprocess.run(["/dotnet/dotnet", "/scratch/Fixture.dll"], cwd="/scratch")
        print("FIXTURE_EXECUTION_EXIT", result.returncode, flush=True)
        if result.returncode == 0:
            print("PASS windows-final-segment-only: real caller/helper, reset, duplicates, cumulative boundaries, filters, opt-in, OSC settings, translation errors and cancellation; all audio/ASR/TTS/translation fake", flush=True)
    code = result.returncode
finally:
    fs = os.statvfs("/scratch")
    print("TMPFS_USED_BEFORE_DESTROY", (fs.f_blocks - fs.f_bfree) * fs.f_frsize, flush=True)
sys.exit(code)
'''
    command = ['/usr/bin/bwrap', '--die-with-parent', '--unshare-all', '--unshare-user', '--disable-userns', '--clearenv',
               '--ro-bind', '/usr', '/usr', '--ro-bind', '/lib', '/lib', '--ro-bind', '/lib64', '/lib64',
               '--ro-bind', '/etc', '/etc', '--symlink', '/usr/bin', '/bin', '--ro-bind', str(ROOT), str(ROOT),
               '--ro-bind', str(dotnet_root), '/dotnet', '--dir', '/fixtures',
               '--ro-bind', str(args.capped_output.resolve()), '/fixtures/capped-output.py',
               '--ro-bind', str(core), '/fixtures/MobMate.Core.dll', '--size', str(cap), '--tmpfs', '/scratch',
               '--symlink', '/scratch/tmp', '/tmp', '--symlink', '/scratch/run', '/run', '--dir', '/dev',
               '--symlink', '/scratch/shm', '/dev/shm', '--symlink', '/proc/self/fd', '/dev/fd',
               '--symlink', '/proc/self/fd/0', '/dev/stdin', '--symlink', '/proc/self/fd/1', '/dev/stdout',
               '--symlink', '/proc/self/fd/2', '/dev/stderr']
    for name in ('null', 'zero', 'random', 'urandom'):
        command += ['--dev-bind', '/dev/' + name, '/dev/' + name]
    command += ['--proc', '/proc', '--remount-ro', '/proc', '--remount-ro', '/', '--chdir', '/scratch']
    settings = {'HOME': '/scratch/home', 'TMPDIR': '/scratch/tmp', 'DOTNET_ROOT': '/dotnet',
                'DOTNET_CLI_HOME': '/scratch/home', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE': '1',
                'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_EnableDiagnostics': '0', 'LANG': 'C.UTF-8',
                'PATH': '/usr/bin:/bin', 'PYTHONDONTWRITEBYTECODE': '1'}
    command += [part for key, value in settings.items() for part in ('--setenv', key, value)]
    command += ['--', '/usr/bin/python3', '-B', '-c', child, str(ROOT), str(run), '/fixtures/MobMate.Core.dll', str(cap)]
    result = capped.receive(command, time.monotonic() + 90)
    output = (result.get('stdout_tail') or '') + (result.get('stderr') or '')
    (run / "fixture.log").write_text(output)
    result.pop('command', None)
    result.update({"scope": "Windows source-extracted caller; fake ASR, translation, TTS and audio; not Windows execution or performance",
                   "source_sha256": hashlib.sha256(source.encode()).hexdigest(),
                   "core_sha256": hashlib.sha256(core.read_bytes()).hexdigest(), "core_dll": str(core),
                   "root_available_after": os.statvfs(ROOT).f_bavail * os.statvfs(ROOT).f_frsize,
                   "memory_after": {line.split(":")[0]: int(line.split()[1]) * 1024 for line in Path("/proc/meminfo").read_text().splitlines()
                                    if line.startswith(("MemAvailable:", "SwapTotal:", "SwapFree:"))},
                   "owned_pid_reaped": not Path('/proc', str(result['pid'])).exists(),
                   "tmpfs_visible_in_parent": any(line.split()[4] == '/scratch' for line in Path('/proc/self/mountinfo').read_text().splitlines())})
    (run / "results.json").write_text(json.dumps(result, indent=2) + "\n")
    print(output, flush=True)
    print("namespace_exit", result['exit'], "elapsed_seconds", result['elapsed_seconds'],
          "exited", result['namespace_process_exited'], "parent_tmpfs", result['tmpfs_visible_in_parent'], flush=True)
    assert result['namespace_process_exited'] and result['owned_pid_reaped'] and not result['tmpfs_visible_in_parent']
    return 0 if result['exit'] == 0 else 1


if args.windows_final_segment_only:
    raise SystemExit(windows_final_segment_only())

RUN = Path(args.run_dir).resolve()
assert RUN.is_relative_to(ROOT / "temp")
RUN.mkdir(parents=True, exist_ok=False)
source = (ROOT / "src/MobMate.App.Linux/Program.cs").read_text()
begin = source.index('app.MapGet("/api/realtime/status",')
end = source.index('app.MapPost("/api/realtime/destroy",', begin)
handler = source[begin:end]
poll_begin = source.index('app.MapGet("/api/realtime/poll",')
poll_handler = source[poll_begin:begin]
start_begin = source.index('app.MapPost("/api/realtime/start",')
stop_begin = source.index('app.MapPost("/api/realtime/stop",', start_begin)
start_handler = source[start_begin:stop_begin]
stop_handler = source[stop_begin:poll_begin]
suppress = source[source.index('(bool ttsSuppressed, bool loopbackSuspected) GetRealtimeTtsSuppressState('):source.index('TtsProsodyProfile? AnalyzeRealtimeSourceProsody(')]
echo_constants = source[source.index('const int TtsCooldownAfterSpeakMs ='):source.index('static bool IsShortPhoneticAssistPcm(')]
speakable = next(line for line in source.splitlines() if line.startswith('static bool HasSpeakableText('))
core = args.core_dll.resolve()
assert core.is_file(), core
steps = []
(RUN / "tmp").mkdir()
env = os.environ.copy()
env.update({"DOTNET_ROOT": str(dotnet_root), "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false", "DOTNET_CLI_HOME": str(RUN / "cli-home"),
            "DOTNET_CLI_USE_MSBUILD_SERVER": "0", "MSBUILDDISABLENODEREUSE": "1", "TMPDIR": str(RUN / "tmp")})


def command(name, argv, overrides=None):
    started = time.monotonic()
    result = subprocess.run(argv, cwd=ROOT, env={**env, **(overrides or {})}, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
    (RUN / (name + ".log")).write_text(result.stdout)
    steps.append({"name": name, "exit": result.returncode, "elapsed_seconds": time.monotonic() - started})
    print(name, result.returncode, result.stdout[-8000:], flush=True)
    return result.returncode == 0


fixture = r'''
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MobMate.Core.Asr;
using MobMate.Core.Realtime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using MobMate.Core.Text;
using RealtimeSessionController = LinuxStatusFixtureSession;

var failed = new List<string>();
var boundaryPasses = new List<string>();
var clientRequests = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
foreach (var entry in AddonCatalog.Engines)
    foreach (var language in entry.Models.SelectMany(m => m.Languages).Distinct())
        Check(entry.Id, language, "Current native", entry.Id, "Current native", false);
// Retain existing base and Joint/SenseVoice behavior, not a blanket overlay ban.
Check("joint_auto", "ja", "Current native", "whisper", "Current native", false);
Check("joint_auto", "ja", "あ", "whisper", "Managed replacement", true);
Check("sense_voice", "en", "Current native", "whisper", "Managed replacement", true);
// The Linux native PCM-ready path can contain no native transcript at all.
// Explicit SenseVoice still consumes that final once; Auto and native routes do not.
Check("sense_voice", "en", "", "none", "Managed replacement", true);
Check("sense_voice", "en", "", "none", "", false, pcmBytes: 0);
Check("sense_voice", "en", "", "none", "", false, sampleRate: 0);
// Paraformer is also managed on Linux: native supplies final PCM without text.
Check("paraformer_zh", "zh", "", "none", "Managed replacement", true);
Check("paraformer_zh", "zh", "", "none", "", false, pcmBytes: 0);
Check("paraformer_zh", "zh", "", "none", "", false, sampleRate: 0);
Check("paraformer_zh", "zh", "", "none", "", false, paraAvailable: false);
Check("paraformer_zh", "zh", "Current native", "paraformer_zh", "Current native", false, paraAvailable: false);
Check("joint_auto", "en", "", "none", "", false);
Check("safe_auto", "en", "", "none", "", false);
Check("whisper", "en", "", "none", "", false);
Check("moonshine", "en", "", "none", "", false);
Check("whisper", "en", "Current native", "whisper", "Current native", false);
Check("moonshine", "en", "Current native", "moonshine", "Current native", false);
Check("joint_auto", "en", "Previously displayed", "whisper", "Previously displayed", false, true);
CheckPoll(false); CheckPoll(true);
foreach (var route in new[] { AddonCatalog.QwenId, "sense_voice" })
    foreach (var scenario in new[] { "off-on", "off-on-playing", "playback", "filter", "same-final", "stop-during-poll" })
        Check(route, "en", "First utterance", route, "First utterance", route == "sense_voice", scenario: scenario);
File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[0])!, "status-cases.json"), JsonSerializer.Serialize(boundaryPasses));
if (failed.Count != 0) throw new Exception(string.Join("\n", failed));

void CheckPoll(bool captureLost) {
    var app = new LinuxPollFixtureApp();
    var realtimeMutationGate = new SemaphoreSlim(1, 1);
    using var realtimeOutputEpoch = new RealtimeOutputEpoch(); realtimeOutputEpoch.Begin();
    var before = realtimeOutputEpoch.Capture();
    var realtimeSession = new LinuxStatusFixtureSession(new RealtimePollResult {
        State = captureLost ? "failed" : "recording", Final = "Previously displayed", SelectedText = "Previously displayed",
        Error = captureLost ? "capture_device_lost" : null });
    var learned = 0;
    void MaybeLearnAdaptiveAsrText(RealtimePollResult result, string text) { learned++; }
    /*ACTUAL_POLL_HANDLER*/
    var output = JsonSerializer.SerializeToElement(((IValueHttpResult)app.Handler!()).Value);
    Require(learned == (captureLost ? 0 : 1), "poll: learning crossed loss boundary");
    Require(before.Cancellation.IsCancellationRequested == captureLost, "poll: output epoch state");
    if (captureLost) Require(output.GetProperty("Error").GetString() == "capture_device_lost", "poll: missing loss error");
}

void Check(string engine, string language, string nativeText, string nativeEngine, string expected, bool expectOverlay, bool captureLost = false, string? scenario = null, int pcmBytes = 3200, int sampleRate = 16000, bool paraAvailable = true) {
    try {
        var app = new LinuxStatusFixtureApp();
        var logger = NullLogger.Instance;
        var senseVoiceLogger = NullLogger.Instance; var paraformerLogger = NullLogger.Instance;
        var realtimeMutationGate = new SemaphoreSlim(1, 1);
        using var realtimeOutputEpoch = new RealtimeOutputEpoch(); realtimeOutputEpoch.Begin();
        var beforeLoss = realtimeOutputEpoch.Capture();
        var input = new RealtimePollResult { State = "recording", Final = nativeText, SelectedText = nativeText,
            SelectedEngine = nativeEngine, DecisionReason = "native", Language = language,
            Error = captureLost ? "capture_device_lost" : null,
            FinalPcm16Le = new byte[pcmBytes], FinalPcmSampleRate = sampleRate };
        LinuxStatusFixtureSession? realtimeSession = new(input);
        var lastRealtimeEngine = engine; var lastRealtimeLanguage = language;
        var senseVoiceService = new LinuxStatusFixtureRecognizer();
        var paraformerZhService = new LinuxStatusFixtureRecognizer { IsAvailable = paraAvailable };
        var lastRealtimeStatusFinalEmitted = ""; var lastRealtimeTtsFinal = "";
        var lastTranslatedFinalSource = ""; var lastTranslatedFinalText = "";
        var realtimeTtsEnabled = true; var ttsSuppressUntil = DateTimeOffset.MinValue;
        var lastTtsSpeakingSnapshot = false;
        var ttsRouter = new LinuxFixtureTtsRouter();
        var filterConfig = RealtimeTextFilterConfig.CreateDefault(language);
        /*ACTUAL_ECHO_CONSTANTS*/
        var spoken = new List<string>();
        var prefetchCalls = 0;
        void StartPhoneticAssistPrefetch(RealtimePollResult poll, string text) { prefetchCalls++; }
        float[] PcmToFloat32(byte[] bytes) => new float[bytes.Length / 2];
        bool SenseVoiceTextPlausibleForLanguage(string text, string lang) => true;
        (string? Text, string? Engine, string? DecisionReason) TryApplyPhoneticAssist(RealtimePollResult poll, string text) => (null, null, null);
        void LogRealtimeAsrReady(RealtimePollResult poll, string text, string? selected, string? reason, string origin) { }
        int GetLastFinalToTtsSpeakMs(long detected) => 0;
        int GetLastFinalToPlaybackMs(long detected) => 0;
        RealtimeTextFilterConfig SnapshotTextFilterConfig(string lang) => filterConfig;
        /*ACTUAL_SUPPRESS*/
        /*ACTUAL_SPEAKABLE*/
        object AnalyzeRealtimeSourceProsody(string text, RealtimePollResult poll) => new();
        void TriggerRealtimeSpeak(string text, string key, string lang, object prosody, long detected) {
            spoken.Add(text);
            ttsRouter.Status.LastTtsText = text;
            ttsRouter.Status.LastTtsStartedAt = DateTimeOffset.UtcNow.ToString("O");
        }
        /*ACTUAL_HANDLER*/
        if (scenario is not null) {
            var addonWorkGate = new AddonOperationGate();
            IDisposable? realtimeWorkLease = null;
            var realtimeSessionService = new LinuxFixtureSessionService();
            var addonManager = new LinuxFixtureAddonManager();
            var addonLoad = Task.CompletedTask;
            var addonTranscriber = new LinuxFixtureTranscriber();
            string nativeExe = "fixture-no-native", whisperDllDir = "", whisperCpuDllDir = "", whisperModelPath = "";
            string moonshineDllDir = "", moonshineModelRoot = "", sileroVadModelPath = "", onnxRuntimeDllDir = "";
            bool whisperUseGpu = false;
            string currentAdaptiveIpa = "", lastPhoneticAssistDebugKey = "", lastPhoneticAssistAcceptedKey = "";
            string lastPhoneticAssistAcceptedText = "", lastPhoneticAssistAcceptedReason = "", phoneticAssistPrefetchKey = "";
            object phoneticAssistPrefetchLock = new();
            Task<string?>? phoneticAssistPrefetchTask = null;
            var adaptivePromptBuilder = new LinuxFixturePromptBuilder();
            var adaptiveVocabLogger = NullLogger.Instance;
            bool IsDemoSessionExpired() => false;
            IResult DemoSessionExpiredResult() => throw new Exception("unexpected demo path");
            object ApiError(string message, string code) => new { message, code };
            void UnlockSteamAchievement(string value) { }
            /*ACTUAL_START*/
            /*ACTUAL_STOP*/
            JsonElement Poll(string text, long detected = 1000) {
                realtimeSession!.Input = new RealtimePollResult { State = "recording", Final = text, SelectedText = text,
                    SelectedEngine = engine, Language = language, FinalDetectedAtMs = detected,
                    FinalPcm16Le = new byte[3200], FinalPcmSampleRate = 16000 };
                senseVoiceService.NextText = text;
                var value = JsonSerializer.SerializeToElement(((IValueHttpResult)app.Handler!()).Value);
                Require(!value.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null, "status returned error: " + value);
                return value;
            }
            void Start(bool enabled) {
                // Payloads come from the actual wizard/main clients, not an invented live toggle.
                var payload = JsonNode.Parse(clientRequests.EnumerateArray().First(x => x.GetProperty("url").GetString() == "/api/realtime/start" &&
                    x.GetProperty("body").GetProperty("ttsEnabled").GetBoolean() == enabled).GetProperty("body").GetRawText())!.AsObject();
                payload["engine"] = engine; payload["language"] = language;
                var context = new DefaultHttpContext();
                context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload.ToJsonString()));
                var response = app.Start!(context.Request).GetAwaiter().GetResult();
                var value = JsonSerializer.SerializeToElement(((IValueHttpResult)response).Value);
                Require(((IStatusCodeHttpResult)response).StatusCode == 200 && value.GetProperty("ok").GetBoolean(), "start rejected: " + value);
                Require(realtimeTtsEnabled == enabled && lastRealtimeTtsFinal == "" && lastRealtimeStatusFinalEmitted == "", "start failed to reset transcript/opt-in state");
            }
            void Stop() {
                var result = app.Stop!(false).GetAwaiter().GetResult();
                Require(JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value).GetProperty("ok").GetBoolean(), "stop rejected");
            }
            if (scenario == "off-on") {
                Start(false); Poll("Disabled utterance");
                Require(spoken.Count == 0 && ttsRouter.Status.LastTtsStartedAt is null, "OFF dispatched TTS");
                Stop(); Start(true);
                Thread.Sleep(1500); // A new utterance plus the normal 1000ms end-silence fits inside 2500ms.
                Poll("New enabled utterance", 2000);
                Require(spoken.SequenceEqual(new[] { "New enabled utterance" }), "OFF history suppressed the first ON utterance after real Stop/Start");
            } else if (scenario == "off-on-playing") {
                Start(true); Poll("Already playing");
                ttsRouter.Status.IsSpeaking = true;
                Start(false); Poll("Wizard recognition", 2000);
                Stop(); Start(true);
                var during = Poll("Main during playback", 3000);
                Require(spoken.Count == 1 && during.GetProperty("loopbackSuspected").GetBoolean(), "OFF/ON cleared existing playback suppression");
                ttsRouter.Status.IsSpeaking = false;
                var after = Poll("Main after playback", 4000);
                Require(spoken.Count == 1 && after.GetProperty("loopbackSuspected").GetBoolean(), "OFF/ON cleared playback-finish suppression");
            } else if (scenario == "playback") {
                Start(true); Poll("First utterance");
                ttsRouter.Status.IsSpeaking = true;
                var during = Poll("Different utterance", 2000);
                Require(spoken.Count == 1 && during.GetProperty("loopbackSuspected").GetBoolean(), "playback did not suppress a different utterance");
                ttsRouter.Status.IsSpeaking = false;
                var after = Poll("After playback", 3000);
                Require(spoken.Count == 1 && after.GetProperty("loopbackSuspected").GetBoolean(), "playback-finish cooldown was lost");
                Thread.Sleep(TtsRecentStartedEchoMs + 100);
                var later = Poll("Later utterance", 4000);
                Require(spoken.SequenceEqual(new[] { "First utterance", "Later utterance" }) && !later.GetProperty("loopbackSuspected").GetBoolean(), "normal speech did not resume after the unchanged safety windows");
            } else if (scenario == "filter") {
                Start(true); filterConfig.NgWords = "blocked phrase";
                var dropped = Poll("blocked phrase");
                Require(dropped.GetProperty("textFilter").GetProperty("dropped").GetBoolean() && spoken.Count == 0, "real NG filter did not suppress output");
                Poll("Allowed utterance", 2000);
                Require(spoken.SequenceEqual(new[] { "Allowed utterance" }), "filtered input created a phantom TTS cooldown");
            } else if (scenario == "same-final") {
                Start(true); Poll("Same utterance");
                var repeat = Poll("Same utterance");
                Require(spoken.Count == 1 && repeat.GetProperty("final").GetString() == "", "retained final repeated TTS");
                var distinct = Poll("Same utterance", 2000);
                Require(spoken.Count == 1 && distinct.GetProperty("final").GetString() == "", "existing content-only duplicate policy changed");
                // A fresh timestamp still does not establish non-echo origin. Record, do not relax this policy here.
            } else if (scenario == "stop-during-poll") {
                Start(true);
                Task<IResult>? stopping = null;
                realtimeSession!.OnPoll = () => stopping = app.Stop!(true);
                var late = Poll("Late utterance");
                stopping!.GetAwaiter().GetResult();
                Require(late.GetProperty("discarded").GetBoolean() && spoken.Count == 0 && realtimeSession is null, "late status escaped the real Stop intent");
            }
            if (realtimeSession is not null) Stop();
            using var drained = addonWorkGate.TryEnterAddonOperation();
            Require(drained is not null, "fixture leaked a work lease");
            boundaryPasses.Add(engine + ":" + scenario);
            return;
        }
        var result = app.Handler!();
        var output = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
        if (nativeText == "" && !expectOverlay) {
            var final = output.TryGetProperty("final", out var lowerFinal) ? lowerFinal : output.GetProperty("Final");
            Require(final.GetString() == "" && senseVoiceService.Calls == 0 && paraformerZhService.Calls == 0 && spoken.Count == 0,
                "empty/invalid final invoked a recognizer or TTS");
            return;
        }
        if (captureLost) {
            Require(output.GetProperty("Error").GetString() == "capture_device_lost" && beforeLoss.Cancellation.IsCancellationRequested,
                "loss did not return the error and invalidate the output epoch");
            Require(senseVoiceService.Calls == 0 && paraformerZhService.Calls == 0 && spoken.Count == 0 && prefetchCalls == 0,
                "loss performed an overlay, prefetch or TTS side effect");
            return;
        }
        var selected = output.TryGetProperty("selectedEngine", out var camel) ? camel : output.GetProperty("selected_engine");
        var expectedOverlayEngine = engine == "paraformer_zh" ? "paraformer_zh" : "sense_voice";
        Require(output.GetProperty("final").GetString() == expected && selected.GetString() == (expectOverlay ? expectedOverlayEngine : nativeEngine),
            "status replaced the requested route's transcript/engine");
        Require(senseVoiceService.Calls == (expectOverlay && expectedOverlayEngine == "sense_voice" ? 1 : 0) &&
                paraformerZhService.Calls == (expectOverlay && expectedOverlayEngine == "paraformer_zh" ? 1 : 0),
            "unexpected managed recognizer execution");
        Require(spoken.SequenceEqual(new[] { expected }), "TTS did not preserve the selected route");
        System.Diagnostics.Trace.WriteLine("PASS " + engine + "/" + language);
    } catch (Exception error) { failed.Add(engine + "/" + language + "/" + scenario + ": " + error.Message); }
}
static void Require(bool value, string message) { if (!value) throw new Exception(message); }
sealed class LinuxStatusFixtureApp {
    public Func<IResult>? Handler;
    public Func<HttpRequest, Task<IResult>>? Start;
    public Func<bool?, Task<IResult>>? Stop;
    public void MapGet(string path, Func<IResult> handler) { if (path != "/api/realtime/status") throw new Exception(path); Handler = handler; }
    public void MapPost(string path, Func<HttpRequest, Task<IResult>> handler) { if (path != "/api/realtime/start") throw new Exception(path); Start = handler; }
    public void MapPost(string path, Func<bool?, Task<IResult>> handler) { if (path != "/api/realtime/stop") throw new Exception(path); Stop = handler; }
}
sealed class LinuxPollFixtureApp {
    public Func<IResult>? Handler;
    public void MapGet(string path, Func<IResult> handler) { if (path != "/api/realtime/poll") throw new Exception(path); Handler = handler; }
}
sealed class LinuxStatusFixtureSession(RealtimePollResult input) {
    public RealtimePollResult Input = input;
    public Action? OnPoll;
    public RealtimePollResult Poll() { OnPoll?.Invoke(); return Input; }
    public static LinuxStatusFixtureSession Create(string exe, string options, ILogger logger) => new(new RealtimePollResult());
}
sealed class LinuxStatusFixtureRecognizer {
    public bool IsAvailable = true; public int Calls;
    public string NextText = "Managed replacement";
    public string Transcribe(float[] pcm, int rate, string? language = null) { ++Calls; return NextText; }
}
sealed class LinuxFixtureTtsRouter {
    public LinuxFixtureTtsStatus Status = new();
    public LinuxFixtureTtsStatus GetStatus() => Status;
}
sealed class LinuxFixtureTtsStatus { public bool IsSpeaking; public string? LastTtsStartedAt; public string? LastTtsText; }
sealed class LinuxFixtureSessionService {
    public LinuxStatusFixtureSession? Current;
    public void Dispose() { Current = null; }
    public void Start(Func<LinuxStatusFixtureSession> create) { Current = create(); }
    public void Stop() { Current = null; }
}
sealed class LinuxFixtureAddonManager {
    private string language = "en";
    public LinuxFixtureAddonDescription Describe(string? requested = null) => new(requested ?? language);
    public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    public Task ApplyLanguageAsync(string value, CancellationToken token) { language = value; return Task.CompletedTask; }
    public string ResolveRecognitionEngine(string engine, bool follow) => engine;
}
sealed class LinuxFixtureAddonDescription(string language) {
    public string Language => language;
    public LinuxFixtureAddonDesired Desired => new();
    public LinuxFixtureAddonStatus State => new();
    public bool JointEligible => false;
}
sealed class LinuxFixtureAddonDesired {
    public bool Enabled => true;
    public string EngineId => AddonCatalog.QwenId;
    public string LanguageMode => "requested";
    public string ModelId => AddonCatalog.Engines.Single(e => e.Id == EngineId).Models[0].Id;
}
sealed class LinuxFixtureAddonStatus { public bool Ready => true; public bool Enabled => true; }
sealed class LinuxFixtureTranscriber { public int CallbackV2 => 1; public int JointCallbackV2 => throw new Exception("Joint is OFF"); }
static class NativeLibraryBridge { public static void RegisterAddonTranscriberV2(string exe, int callback, bool joint) { if (joint) throw new Exception("Joint is OFF"); } }
sealed class LinuxFixturePromptBuilder { public LinuxFixturePrompt Build(string prompt, string language) => new(prompt); }
sealed record LinuxFixturePrompt(string Prompt) { public bool Injected => false; public int VocabCount => 0; }
static class SteamAchievementService { public const string FirstSessionStart = "fixture"; }
'''
status_dir = RUN / "status"
status_dir.mkdir()
for marker, content in (("ACTUAL_HANDLER", handler), ("ACTUAL_POLL_HANDLER", poll_handler),
                        ("ACTUAL_ECHO_CONSTANTS", echo_constants), ("ACTUAL_SUPPRESS", suppress),
                        ("ACTUAL_SPEAKABLE", speakable), ("ACTUAL_START", start_handler), ("ACTUAL_STOP", stop_handler)):
    fixture = fixture.replace("/*" + marker + "*/", content)
(status_dir / "Program.cs").write_text(fixture)
(status_dir / "Status.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
<ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /><Reference Include="MobMate.Core"><HintPath>{core}</HintPath></Reference>
<Compile Include="{ROOT / 'src/MobMate.Core/Text/RealtimeTextFilter.cs'}" Link="RealtimeTextFilter.cs" />
<Compile Include="{ROOT / 'src/MobMate.App.Linux/RealtimeLanguagePolicy.cs'}" Link="RealtimeLanguagePolicy.cs" /></ItemGroup></Project>''')
(status_dir / "NuGet.Config").write_text("<configuration><packageSources><clear /></packageSources></configuration>")
client_fixture = r'''
const fs = require("node:fs"), vm = require("node:vm"), assert = require("node:assert/strict");
const source = fs.readFileSync("web/app.js", "utf8"), requests = [], elements = {};
function slice(begin, end) {
  const a = source.indexOf(begin), b = source.indexOf(end, a);
  assert.ok(a >= 0 && b > a); return source.slice(a, b);
}
const element = id => elements[id] ??= { value: id === "rt-language-select" ? "en" : "", checked: true,
  options: [], textContent: "", className: "", style: {}, classList: { toggle() {}, contains: () => true } };
const noop = () => {};
const context = vm.createContext({ console, document: { getElementById: element },
  localStorage: { getItem: () => null, setItem: noop }, defaultRealtimeEngine: "whisper",
  normalizeRealtimeEngine: x => x, normalizeRealtimeLanguage: x => x, addonHintSettings: () => ({}),
  normalizeSetting: (x, values, fallback) => x || fallback, REALTIME_SCENES: [], REALTIME_PREFILTERS: [],
  setupMicBlocksOtherCapture: () => false, rejectUnavailableCaptureInput: () => false,
  stopMobfyDirectForModeSwitch: async () => {}, realtimePollToken: 0, t: x => x, apiMessage: () => "fixture error",
  setRecordingButtons: noop, resetRealtimeLiveUi: noop, setRealtimeWasRecording: noop,
  startNoiseWarmupUi: noop, clearNoiseWarmupUi: noop, meterStart: noop, spkStart: noop, pollRealtime: noop,
  meterStop: noop, spkStop: noop, waveClear: noop, setText: noop, setRealtimeGuide: noop,
  updateRealtimeResults: noop, checkReviewPrompt: noop, formatRealtimeLatencyMs: () => "-",
  setupEl: element, captureInputUnavailable: () => false, captureInputSelectionChanged: noop,
  renderSetupMicStopState: noop, setInterval: () => 1, clearInterval: noop, isMobfyMode: () => false,
  setupMicTesting: false, setupMicStarting: null, setupMicStopping: null, setupMicUnconfirmed: false,
  setupClosing: false, setupCurrentStep: 0, setupMicCaptureGeneration: 0, setupMicPollSeq: 0,
  setupMicTimer: null, setupMicInitSeq: 0,
  fetch: async (url, init) => {
    requests.push({ url, body: init?.body ? JSON.parse(init.body) : null });
    return { ok: true, json: async () => ({ ok: true }) };
  } });
vm.runInContext(slice("function getRealtimeSettings()", "function restoreRealtimeSettings()") +
  slice("const RealtimeSessionClient =", "let audioInputRefreshSeq") +
  slice("async function startRealtime(", "// The drawer and setup wizard") +
  slice("async function startSetupMicTest()", "async function refreshSetupVirtualAudio()"), context);
(async () => {
  // Wizard explicitly starts with TTS OFF; normal main Start keeps its existing ON setting.
  // Do not manufacture a live toggle of the hidden rt-tts-enabled control.
  await context.startSetupMicTest();
  assert.equal(requests.length, 1); assert.equal(requests[0].body.ttsEnabled, false);
  await context.pollSetupMic();
  assert.equal(await context.stopSetupMicTest(), true);
  await context.startRealtime();
  assert.deepEqual(requests.map(x => x.url), ["/api/realtime/start", "/api/realtime/status", "/api/realtime/stop", "/api/realtime/start"]);
  assert.equal(requests[3].body.ttsEnabled, true);
  assert.equal(requests[0].body.clientSource, "wizard"); assert.equal(requests[3].body.clientSource, "main");
  fs.writeFileSync(process.argv[2], JSON.stringify(requests, null, 2));
  console.log("PASS actual wizard OFF/status/Stop -> main ON Start requests; all audio/network seams disabled");
})().catch(error => { console.error(error); process.exitCode = 1; });
'''
(RUN / "client-tts.js").write_text(client_fixture)
client_requests = RUN / "client-requests.json"
binary = RUN / "linux-poll-regression"
capture_binary = RUN / "capture-explicit-device"
passed = False
try:
    client_tts_ok = command("client-tts", [args.node, str(RUN / "client-tts.js"), str(client_requests)])
    status_ok = client_tts_ok and command("status", [str(dotnet), "run", "--project", str(status_dir / "Status.csproj"),
        "--configuration", "Debug", "--no-launch-profile", "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false",
        "-p:NuGetAudit=false", "-p:MobMateIsDemo=false", "-p:UpdatePackage=false", "-p:UseSharedCompilation=false",
        "--", str(client_requests)])
    passed = client_tts_ok and status_ok
    if not args.tts_caller_only:
        native_dir = (ROOT / args.native_build_dir).resolve()
        cache = (native_dir / "CMakeCache.txt").read_text()
        sherpa = Path(next(line.split("=", 1)[1] for line in cache.splitlines() if line.startswith("SHERPA_ONNX_LIB:FILEPATH=")))
        native_build_ok = command("native-build", ["/usr/bin/c++", "-std=c++20", "-O0", "-g", "-pthread",
            "-I", str(ROOT / "native/mobmate_native_core/include"), "-I", str(ROOT / "libs/sherpa-onnx/include"),
            "-I", str(ROOT / "libs/onnxruntime/include"), str(ROOT / "tests/MobMate.Core.Tests/addon_linux_connection_regression.cpp"),
            str(native_dir / "mobmate_native_core/libmobmate_native_core.a"), str(sherpa), "-ldl", "-lm",
            "-Wl,-rpath," + str(sherpa.parent), "-o", str(binary)])
        native_ok = command("native", [str(binary)]) if native_build_ok else False
        capture_build_ok = command("capture-build", ["/usr/bin/c++", "-std=c++17", "-O0", "-pthread",
            "-I", str(ROOT / "native/mobmate_native_core/include"), str(ROOT / "tests/MobMate.Core.Tests/capture_explicit_device_smoke.cpp"),
            "-ldl", "-o", str(capture_binary)])
        capture_ok = command("capture", [str(capture_binary)]) if capture_build_ok else False
        poll_ui_ok = command("poll-ui", [args.node, str(ROOT / "tests/MobMate.Core.Tests/addon_asr_ui_regression.js")],
            {"MOBMATE_POLL_ONLY": "1"})
        client_ok = command("client", [args.node, str(ROOT / "tests/MobMate.Core.Tests/addon_language_stop_regression.js")])
        settings_ok = command("existing-settings", [args.node, str(ROOT / "tests/MobMate.Core.Tests/settings_interaction_regression.js")])
        passed = passed and native_ok and capture_ok and poll_ui_ok and client_ok and settings_ok
finally:
    # Only this invocation's disposable binaries/intermediates. Keep failing logs and generated source inputs.
    for directory in [status_dir / "bin", status_dir / "obj", RUN / "cli-home", RUN / "tmp"]:
        if directory.exists():
            assert not directory.is_symlink()
            shutil.rmtree(directory)
    if binary.exists():
        binary.unlink()
    if capture_binary.exists():
        capture_binary.unlink()
    (RUN / "results.json").write_text(json.dumps({"passed": passed, "steps": steps,
        "status_source_sha256": hashlib.sha256(source.encode()).hexdigest(),
        "client_source_sha256": hashlib.sha256((ROOT / "web/app.js").read_bytes()).hexdigest(),
        "filter_source_sha256": hashlib.sha256((ROOT / "src/MobMate.Core/Text/RealtimeTextFilter.cs").read_bytes()).hexdigest(),
        "language_policy_source_sha256": hashlib.sha256((ROOT / "src/MobMate.App.Linux/RealtimeLanguagePolicy.cs").read_bytes()).hexdigest(),
        "core_dll": str(core), "core_sha256": hashlib.sha256(core.read_bytes()).hexdigest(),
        "scope": "tts-caller-only" if args.tts_caller_only else "full", "capture_started": False}, indent=2) + "\n")
raise SystemExit(0 if passed else 1)
