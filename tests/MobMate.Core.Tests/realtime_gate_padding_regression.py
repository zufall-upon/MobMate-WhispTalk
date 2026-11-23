"""Compile the existing Linux public-poll fixture; no capture, models or Joint run."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument("--native-build-dir", required=True)
parser.add_argument("--run-dir", required=True)
parser.add_argument("--sherpa-library", type=Path, help="Host library corresponding to the build cache's sandbox path")
args = parser.parse_args()
build = (ROOT / args.native_build_dir).resolve()
run = (ROOT / args.run_dir).resolve()
assert run.is_relative_to(ROOT / "temp")
assert shutil.disk_usage(ROOT).free > 1024**3 + 64 * 1024**2
if run.exists():
    assert run.is_dir() and not run.is_symlink()
    run = run / ("attempt-" + str(time.time_ns()))
print("回帰保存先:", run, flush=True)
run.mkdir(parents=True, exist_ok=False)
cache = (build / "CMakeCache.txt").read_text()
sherpa = Path(next(x.split("=", 1)[1] for x in cache.splitlines()
                   if x.startswith("SHERPA_ONNX_LIB:FILEPATH=")))
# Some build caches contain a sandbox path rather than a host path.
# An explicit host library must correspond to that same build input.
if not sherpa.exists() and str(sherpa) == "/deps/sherpa/libsherpa-onnx-c-api.so":
    if args.sherpa_library is None:
        parser.error("the cached sandbox library is unavailable; supply its host path with --sherpa-library")
    sherpa = args.sherpa_library.resolve()
assert sherpa.is_file(), f"Sherpa library from native build is missing: {sherpa}"
core = build / "mobmate_native_core/libmobmate_native_core.a"
fixture = ROOT / "tests/MobMate.Core.Tests/addon_linux_connection_regression.cpp"
binary = run / "gate-fixture"
compile_command = ["/usr/bin/c++", "-std=c++20", "-O0", "-g", "-pthread",
    "-I", str(ROOT / "native/mobmate_native_core/include"),
    "-I", str(ROOT / "libs/sherpa-onnx/include"),
    "-I", str(ROOT / "libs/onnxruntime/include"), str(fixture), str(core), str(sherpa),
    "-ldl", "-lm", "-Wl,-rpath," + str(sherpa.parent), "-o", str(binary)]
steps = []
env = {**os.environ, "TMPDIR": str(run), "LC_ALL": "C.UTF-8"}
for name, command in (("compile", compile_command), ("gate-padding", [str(binary), "--gate-padding"]),
                      ("silero-poll-continuity", [str(binary), "--silero-poll-continuity"])):
    start = time.monotonic()
    result = subprocess.run(command, cwd=ROOT, env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, timeout=120)
    elapsed = time.monotonic() - start
    (run / (name + ".log")).write_bytes(result.stdout)
    steps.append({"name": name, "exit": result.returncode, "elapsed_seconds": elapsed})
    print(name, "exit", result.returncode, "seconds", round(elapsed, 3), result.stdout.decode(errors="replace"), flush=True)
    if result.returncode:
        break
receipt = {"steps": steps, "capture_started": False, "asr_inference": False, "joint_run": False,
    "sherpa_path": str(sherpa), "sherpa_sha256": hashlib.sha256(sherpa.read_bytes()).hexdigest(),
    "inputs_sha256": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
        for p in (fixture, ROOT / "native/mobmate_native_core/src/native_api.cpp", core)},
    "binary_sha256": hashlib.sha256(binary.read_bytes()).hexdigest() if binary.exists() else None}
(run / "results.json").write_text(json.dumps(receipt, ensure_ascii=False, indent=2) + "\n")
raise SystemExit(0 if len(steps) == 3 and all(s["exit"] == 0 for s in steps) else 1)
