"""Build/run VC control against its pinned baseline, without audio devices.

Only VAD probabilities, the recognizer interface and model inference are mocked.
Both binaries include the full production VC translation unit. The baseline gets
the same compile-time test seams, never a rewritten gate or release predicate.
When both Moonshine directories are supplied, the candidate additionally runs
the same device-free pipeline with real ASR and synthetic zero PCM.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "native/mobmate_native_core/src/voice_conversion.cpp"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree_bytes_without_links(path):
    pending, total = [path], 0
    while pending:
        for entry in os.scandir(pending.pop()):
            info = entry.stat(follow_symlinks=False)
            if entry.is_symlink() or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError(f"cleanup refused reparse/symlink: {entry.path}")
            if entry.is_dir(follow_symlinks=False):
                pending.append(Path(entry.path))
            else:
                total += info.st_size
    return total


def private_path(value):
    path = ROOT / value
    # Check the unresolved ancestry too: resolve() alone would hide a junction.
    for ancestor in (path, *path.parents):
        if ancestor.exists():
            info = ancestor.lstat()
            if ancestor.is_symlink() or getattr(info, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError(f"reparse/symlink output forbidden: {ancestor}")
        if ancestor == ROOT:
            break
    path = path.resolve()
    if not path.is_relative_to(ROOT / "temp/mobfy-native") or path == ROOT / "temp/mobfy-native":
        raise ValueError("outputs must be children of this checkout's temp/mobfy-native")
    return path


def instrument_baseline(text):
    replacements = [
        ('#include "../../vendor/miniaudio.h"', '#include "miniaudio.h"'),
        ('                                      VcRawOutputGainState* raw_output_gain = nullptr) {',
         '                                      VcRawOutputGainState* raw_output_gain = nullptr) {\n'
         '#ifdef MOBMATE_VC_BOUNDARY_TEST\n    return vc_boundary_test_convert(samples, num_samples, sr);\n#endif'),
        ('    void laugh_asr_worker_loop() {\n        mobmate::MoonshineBridge bridge;',
         '    void laugh_asr_worker_loop() {\n        VcBoundaryAsrBridge bridge;'),
        ('            const int64_t asr_done_ms = vc_steady_now_ms();',
         '            const int64_t asr_done_ms = vc_steady_now_ms();\n'
         '            vc_boundary_test_event("asr_ready", "baseline_unclassified", decision.c_str());'),
        ('                if (stopping) {\n                    if (rvc_future.valid()) (void)rvc_future.get();',
         '                if (stopping) {\n                    if (rvc_future.valid()) (void)rvc_future.get();\n'
         '                    vc_boundary_test_event("stopping", "baseline_unclassified", decision.c_str());'),
        ('                        laugh_asr_pending.store(!laugh_asr_jobs.empty(), std::memory_order_relaxed);\n'
         '                    }\n                    continue;',
         '                        laugh_asr_pending.store(!laugh_asr_jobs.empty(), std::memory_order_relaxed);\n'
         '                    }\n                    vc_boundary_test_event("cancelled", "baseline_unclassified", decision.c_str());\n'
         '                    continue;'),
        ('                laugh_asr_pending.store(!laugh_asr_jobs.empty(), std::memory_order_relaxed);\n'
         '            }\n        }\n    }\n\n    void score_vad_frame()',
         '                laugh_asr_pending.store(!laugh_asr_jobs.empty(), std::memory_order_relaxed);\n'
         '            }\n            vc_boundary_test_event("job_done", "baseline_unclassified", decision.c_str());\n'
         '        }\n    }\n\n    void score_vad_frame()'),
        ('        double prob = silero_vad_score(vad, vad_16k_buf, 512, 16000);',
         '        double prob = vc_boundary_test_vad();'),
    ]
    for old, new in replacements:
        if text.count(old) != 1:
            raise ValueError(f"baseline instrumentation anchor changed: {old[:100]!r}")
        text = text.replace(old, new, 1)
    return text


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-dir", required=True)
    parser.add_argument("--run-dir", required=True)
    parser.add_argument("--cmake", required=True, help="Path to the existing CMake executable")
    parser.add_argument("--baseline-ref", required=True, help="Original pinned baseline commit, if available")
    parser.add_argument("--baseline-sha256", required=True, help="SHA-256 of the original baseline source")
    parser.add_argument("--keep-build", action="store_true", help="development only; final check removes intermediates")
    parser.add_argument("--moonshine-runtime", help="optional real Moonshine runtime directory")
    parser.add_argument("--moonshine-model", help="optional real Japanese tiny streaming model directory")
    args = parser.parse_args()
    if (args.moonshine_runtime is None) != (args.moonshine_model is None):
        parser.error("--moonshine-runtime and --moonshine-model must be supplied together")
    real_asr = args.moonshine_runtime is not None
    if real_asr and (not args.moonshine_runtime.strip() or not args.moonshine_model.strip()):
        parser.error("Moonshine runtime/model directories must not be empty")
    runtime = (ROOT / args.moonshine_runtime).resolve() if real_asr else None
    model = (ROOT / args.moonshine_model).resolve() if real_asr else None
    build, run = private_path(args.build_dir), private_path(args.run_dir)
    if build == run or build in run.parents or run in build.parents:
        raise ValueError("build and results must be disjoint")
    if run.exists():
        run = run / ("attempt-" + str(time.time_ns()))
    run.mkdir(parents=True, exist_ok=False)
    build.mkdir(parents=True, exist_ok=True)
    cmake = Path(args.cmake)
    if not cmake.is_file():
        raise FileNotFoundError(f"configured CMake missing: {cmake}; no installation attempted")
    steps = []

    def command(name, argv, timeout=180):
        start = time.monotonic()
        process = subprocess.run([str(x) for x in argv], cwd=ROOT, stdout=subprocess.PIPE,
                                 stderr=subprocess.PIPE, timeout=timeout)
        (run / (name + ".log")).write_bytes(process.stdout)
        (run / (name + ".stderr.log")).write_bytes(process.stderr)
        steps.append({"name": name, "argv": [str(x) for x in argv], "exit": process.returncode,
                      "elapsed_seconds": round(time.monotonic() - start, 3)})
        print(name, "exit", process.returncode, "seconds", steps[-1]["elapsed_seconds"], flush=True)
        if process.returncode:
            print(process.stdout.decode("utf-8", errors="replace"), flush=True)
            print(process.stderr.decode("utf-8", errors="replace"), flush=True)
            raise RuntimeError(f"{name} failed; see {run / (name + '.log')}")
        return process.stdout

    receipt = {"kind": "synthetic_native_control_regression", "configuration": "Debug",
               "capture_started": False, "playback_started": False, "models_loaded": None if real_asr else False,
               "real_speech_used": False, "baseline_ref": args.baseline_ref, "steps": steps}
    try:
        baseline_bytes = command("baseline-source", ["git", "show", args.baseline_ref + ":native/mobmate_native_core/src/voice_conversion.cpp"])
        if hashlib.sha256(baseline_bytes).hexdigest() != args.baseline_sha256.lower():
            raise ValueError("baseline source SHA-256 mismatch")
        baseline_text = baseline_bytes.decode("utf-8-sig").replace("\r\n", "\n")
        adapted = build / "baseline-voice_conversion.cpp"
        adapted.write_text(instrument_baseline(baseline_text), encoding="utf-8")
        receipt["baseline_original_sha256"] = hashlib.sha256(baseline_bytes).hexdigest()
        receipt["baseline_instrumented_sha256"] = sha(adapted)
        receipt["head"] = command("head", ["git", "rev-parse", "HEAD"]).decode().strip()
        configure = [cmake, "-S", ROOT / "native", "-B", build, "-DBUILD_TESTING=OFF",
                     "-DMOBMATE_VC_BASELINE_SOURCE=" + adapted.as_posix()]
        if os.name == "nt":
            configure += ["-G", "Visual Studio 17 2022", "-A", "x64"]
        else:
            configure += ["-DCMAKE_BUILD_TYPE=Debug"]
        command("configure", configure)
        command("build", [cmake, "--build", build, "--config", "Debug", "--target",
                          "mobmate_native_bridge", "mobmate_mobfy_segment_regression",
                          "mobmate_mobfy_segment_baseline", "--parallel", "2"], 600)
        outputs = {}
        for name, target in (("baseline", "mobmate_mobfy_segment_baseline"),
                             ("candidate", "mobmate_mobfy_segment_regression")):
            binary = build / "mobmate_native_core" / ("Debug/" + target + ".exe" if os.name == "nt" else target)
            retained = run / binary.name
            shutil.copy2(binary, retained)
            receipt[name + "_binary_sha256"] = sha(retained)
            argv = [retained]
            if name == "candidate" and real_asr:
                argv += [runtime, model]
            data = command(name, argv, 120)
            outputs[name] = [json.loads(line) for line in data.decode("utf-8").splitlines() if line.startswith("{")]
            if name == "candidate" and os.name != "nt":
                context_data = command("context-init-failure", [retained, "--context-init-failure"], 120)
                assert context_data.decode("utf-8").splitlines() == [
                    'PASS context_init_failure {"laughAsrAssistEnabled":false}',
                    'PASS context_init_failure {}',
                    'PASS context_init_failure {"mode":"rvc_raw","laughAsrAssistEnabled":false}',
                ], "context failure modes missing"
                lifecycle_data = command("lifecycle-boundary", [retained, "--lifecycle-boundary"], 120)
                assert lifecycle_data.decode("utf-8").splitlines() == [
                    "PASS lifecycle_load_unload_stop_reload",
                    "PASS lifecycle_passthrough",
                    "PASS lifecycle_assist_off",
                    "PASS lifecycle_unsupported_live_engine",
                    "PASS lifecycle_raw_conversion",
                    "PASS lifecycle_file_caller_fail_closed",
                    "PASS lifecycle_unreadable_selection_fail_closed",
                    "PASS lifecycle_passthrough_retained_tokens",
                ], "device-free lifecycle cases missing"
        before, candidate_rows = outputs["baseline"], outputs["candidate"]
        after, selection_rows = candidate_rows[:25], candidate_rows[25:]
        if real_asr:
            assert selection_rows and selection_rows[-1]["case"] == "real_tiny_zero_mobfy", "real ASR case missing"
            real_row = selection_rows.pop()
            assert real_row["outcome"] == "normal_empty" and real_row["decision"] == "reject_no_asr_text"
            assert real_row["asr_calls"] == real_row["vc_calls"] == real_row["gate_rejects"] == 1
            assert real_row["gate_accepts"] == real_row["releases"] == real_row["nonzero_samples"] == 0
            assert real_row["sink"] == "device_free_on_playback" and real_row["playback_callbacks"] > 0
            assert any(e.startswith("asr_ready:normal_empty:reject_no_asr_text:") for e in real_row["events"])
            receipt["models_loaded"] = True
            receipt["real_asr"] = {"runtime_dir": str(runtime), "model_dir": str(model), "arch": 2,
                                   "load_and_options_asserted": True, "zero_pcm_at_asr_and_mock_rvc": True,
                                   "row": real_row}
        expected_selection = {"selection_default_ja_base", "selection_tiny_text", "selection_tiny_empty",
                              "selection_tiny_load_failure", "selection_tiny_invalid_path", "selection_other_zh",
                              "selection_other_ko", "selection_other_en", "selection_other_fr",
                              "selection_loaded_worker_retains_model", "selection_new_worker_reads_current_model",
                              "selection_tiny_raw_bypass"}
        if os.name != "nt":
            expected_selection.add("selection_tiny_empty_path")
        assert len(selection_rows) == len(expected_selection) and {r["case"] for r in selection_rows} == expected_selection
        receipt["selection_rows"] = selection_rows
        assert len(before) == len(after) == 25, (len(before), len(after))
        assert len({r["case"] for r in before}) == len(before), "duplicate baseline case"
        assert [r["case"] for r in before] == [r["case"] for r in after], "case alignment"
        comparable = ("case", "callbacks", "synthetic_samples", "asr_calls", "vc_calls", "releases",
                      "nonzero_samples", "decision", "known_safety_gap", "positive_candidate_suppressed",
                      "sink", "playback_callbacks", "gate_accepts", "gate_rejects", "conversion_samples",
                      "boundary_segment_samples_48k", "boundary_job_samples_16k",
                      "boundary_preroll_samples_48k", "hold_frames")
        # The final tuple entry identifies the conversion containing the faulted
        # physical input, not merely the first (possibly cancelled) job marker.
        approved_faults = {"init_failure": (1, 0, 0), "model_failure": (1, 0, 0),
                           "model_fault_then_recovery": (2, 1, 0),
                           "spec_init_fault_cancel_then_recovery": (2, 1, 1),
                           "spec_fault_cancel_then_recovery": (2, 1, 1)}
        changed_fields = {"releases", "nonzero_samples", "known_safety_gap", "gate_accepts", "gate_rejects"}
        deltas = []
        for b, c in zip(before, after):
            case = b["case"]
            fields = [k for k in comparable if case not in approved_faults or k not in changed_fields]
            assert [b[k] for k in fields] == [c[k] for k in fields], (case, "unexpected compatibility change")
            assert c["sink"] == "device_free_on_playback" and c["playback_callbacks"] > 0, case
            if case in approved_faults:
                expected_before, expected_after, fault_conversion = approved_faults[case]
                assert (b["releases"], c["releases"]) == (expected_before, expected_after), (case, "fault release delta")
                assert b["nonzero_samples"] - c["nonzero_samples"] == b["conversion_samples"][fault_conversion], case
                assert (b["known_safety_gap"], c["known_safety_gap"]) == (1, 0), case
                assert c["gate_accepts"] == b["gate_accepts"] - 1, case
                assert c["gate_rejects"] == b["gate_rejects"] + 1, case
                deltas.append({"case": case, "baseline_releases": b["releases"], "candidate_releases": c["releases"],
                               "discarded_samples_48k": b["nonzero_samples"] - c["nonzero_samples"]})
        assert all(r["outcome"] == "baseline_unclassified" for r in before)
        for case, outcome in (("successful_empty", "normal_empty"), ("failed_result", "result_failed"),
                              ("no_result", "no_result"), ("init_failure", "init_failed"),
                              ("model_failure", "model_load_failed"), ("explicit_raw", "bypassed_raw")):
            assert next(r for r in after if r["case"] == case)["outcome"] == outcome
        assert next(r for r in after if r["case"] == "recognized_text")["releases"] == 1
        assert sum(r["known_safety_gap"] for r in before) == 5
        assert sum(r["known_safety_gap"] for r in after) == 0
        for case, samples in (("short_96ms_no_preroll", 1536), ("queue_249_9375ms", 3999),
                              ("queue_250ms", 4000), ("short_96ms_with_160ms_preroll", 4096),
                              ("short_96ms_with_160ms_hold", 4096)):
            assert next(r for r in after if r["case"] == case)["boundary_job_samples_16k"] == samples, case
        receipt.update({"passed": True, "cases_per_binary": len(after), "rows": after, "baseline_rows": before,
                        "baseline_release_compatibility": False,
                        "unchanged_case_compatibility": True, "approved_fault_release_deltas": deltas,
                        "known_safety_gap_observations": sum(r["known_safety_gap"] for r in after),
                        "positive_candidate_suppressed_observations": sum(r["positive_candidate_suppressed"] for r in after),
                        "input_sha256": {str(p.relative_to(ROOT)): sha(p) for p in (
                            SOURCE, Path(__file__), Path(__file__).with_suffix(".cpp"),
                            ROOT / "native/mobmate_native_core/CMakeLists.txt",
                            ROOT / "native/mobmate_native_core/src/moonshine_bridge.cpp")},
                        "limits": ["no real speech, DSP/model quality or voice inference",
                                   "controlled VAD/RVC and mock ASR except optional real zero-PCM ASR",
                                   "pre-armed mock VAD for zero PCM, not real noise/speech detection rates",
                                   "sample-clock event positions and barriers are not live conversation latency",
                                    "init/model fault suppression is provisional; normal-empty policy unchanged",
                                    "96ms without preroll/hold remains below the unchanged 250ms queue threshold",
                                    "fake devices/models exercise lifecycle, not real device output or real inference"]})
        print("PASS: 25 native control cases per binary; only 5 fault-affected segment observations change release", flush=True)
        print("PASS: private model selection, load options and device-free sink controls", flush=True)
        if os.name != "nt":
            print("PASS: public native lifecycle with callback-only fake devices and a mock model", flush=True)
        if real_asr:
            print("PASS: real tiny arch=2 / zero PCM through Mobfy worker and isolated playback sink (normal_empty)", flush=True)
        print("KNOWN LIMITS: short positive candidate lost=1; synthetic playback callback is not audio UX success", flush=True)
    except Exception as error:
        receipt.update({"passed": False, "error": str(error)})
        raise
    finally:
        receipt["retained_result_directory"] = str(run)
        if not args.keep_build:
            # This is solely the runner-owned private build, validated above.
            # Refuse a newly introduced link instead of following it in cleanup.
            private_path(build)
            receipt["build_bytes_removed"] = tree_bytes_without_links(build)
            shutil.rmtree(build)
        receipt["build_retained"] = build.exists()
        (run / "results.json").write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print("results", run, flush=True)


if __name__ == "__main__":
    main()
