"""Isolated Linux profile/API smoke; optionally keep the bounded host for Chromium tests.

Run with /usr/bin/python3 ... --run-dir temp/<unique-directory> [--browser].
No model/runtime download, production config, live audio, package or release.
"""
import argparse
import http.server
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import threading
import time
import urllib.error
import urllib.request
import urllib.parse


ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument("--run-dir", required=True)
parser.add_argument("--native-dir", type=Path, required=True, help="Existing native directory containing the executable and bridge")
parser.add_argument("--dotnet", type=Path, required=True, help="Path to the .NET executable")
parser.add_argument("--browser", action="store_true")
args = parser.parse_args()
DOTNET = args.dotnet.resolve()
NATIVE = args.native_dir.resolve()
if not DOTNET.is_file() or not all((NATIVE / name).is_file() for name in ("mobmate-native", "libmobmate_native_bridge.so")):
    parser.error("--dotnet and both files in --native-dir must already exist")
RUN = Path(args.run_dir).resolve()
assert RUN.is_relative_to(ROOT / "temp"), "fixtures must remain in ignored temp"
RUN.mkdir(exist_ok=False)
HOST = RUN / "host"
HOST.mkdir()
OUTPUT = ROOT / "src/MobMate.App.Linux/bin/Debug/net8.0"
checks = []
process = None
error = None
proxy = None
denied = []


class BrowserGuard(http.server.BaseHTTPRequestHandler):
    # Fail closed: only these observed read routes and scoped, non-download
    # addon mutations can reach the isolated app. No product API is changed.
    reads = {"/", "/index.html", "/app.js", "/addon-language.js", "/favicon.ico", "/language-browser-test.html", "/language-browser-test.js",
             "/api/ui/i18n", "/api/addon-asr", "/api/realtime/languages", "/api/realtime/status", "/api/health"}
    writes = {"/api/addon-asr/profile", "/api/addon-asr/enabled", "/api/addon-asr/joint", "/api/addon-asr/language",
              "/api/addon-asr/suspend", "/api/addon-asr/probe", "/api/addon-asr/cancel"}

    def reply(self, status, content, mime="application/json"):
        self.send_response(status)
        self.send_header("Content-Type", mime)
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(content)

    def dispatch(self):
        path = urllib.parse.urlsplit(self.path).path
        allowed = self.command == "GET" and path in self.reads or self.command == "POST" and path in self.writes
        if not allowed:
            denied.append({"method": self.command, "path": path})
            self.reply(403, b'{"code":"diagnostic.side_effect_blocked"}')
            return
        length = int(self.headers.get("Content-Length", "0"))
        if not 0 <= length <= 64 * 1024:
            self.reply(413, b'{}')
            return
        body = self.rfile.read(length) if self.command == "POST" else None
        req = urllib.request.Request(base + self.path, body, method=self.command, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=10) as response:
                self.reply(response.status, response.read(), response.headers.get("Content-Type", "application/octet-stream"))
        except urllib.error.HTTPError as response:
            self.reply(response.code, response.read(), response.headers.get("Content-Type", "application/json"))
        except (OSError, TimeoutError):
            self.reply(502, b'{"code":"diagnostic.backend_unavailable"}')

    do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = do_HEAD = dispatch

    def log_message(self, *_):
        pass


def check(name, condition):
    if not condition:
        raise AssertionError(name)
    checks.append(name)
    print("PASS " + name, flush=True)


def request(path, body=None):
    req = urllib.request.Request(base + path, None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=8) as response:
            content = response.read()
            return response.status, json.loads(content) if content.strip() else {}
    except urllib.error.HTTPError as response:
        content = response.read()
        return response.code, json.loads(content) if content.strip() else {}


def mutation(action, body, language):
    status, result = request("/api/addon-asr/" + action, body)
    assert status == 202, (action, status, result)
    deadline = time.monotonic() + 8
    while True:
        status, result = request("/api/addon-asr?language=" + language)
        assert status == 200
        if not result["operationRunning"] and not result["loading"]:
            return result
        if time.monotonic() > deadline:
            raise TimeoutError(action)
        time.sleep(0.02)


try:
    for source in OUTPUT.iterdir():
        if source.is_file() and (source.suffix == ".dll" or source.name in {"MobMateLinux.deps.json", "MobMateLinux.runtimeconfig.json"}):
            shutil.copy2(source, HOST / source.name)
    shutil.copytree(OUTPUT / "web", HOST / "web")
    shutil.copytree(OUTPUT / "libs/preset", HOST / "libs/preset")
    (HOST / "mobmate_native_core").mkdir()
    for name in ("mobmate-native", "libmobmate_native_bridge.so"):
        shutil.copy2(NATIVE / name, HOST / "mobmate_native_core" / name)
    shutil.copy2(ROOT / "tests/MobMate.Core.Tests/addon_language_browser_smoke.js", HOST / "web/language-browser-test.js")
    (HOST / "web/language-browser-test.html").write_text(
        '<!doctype html><html lang="ja"><meta charset="utf-8"><title>ASR language UI fixture</title>'
        '<body><h1>ASR language UI fixture (fake runtime)</h1><div id="fixture"></div><pre id="results"></pre>'
        '<script src="/language-browser-test.js"></script></body></html>', encoding="utf-8")
    home = RUN / "home"
    home.mkdir()
    (RUN / "models").mkdir()
    documents = RUN / "documents"
    env = os.environ.copy()
    for key in list(env):
        if key.startswith("MOBMATE_") or key in {"SteamAppId", "SteamGameId", "SteamClientLaunch", "LD_PRELOAD", "LD_LIBRARY_PATH", "ASPNETCORE_URLS", "DOTNET_URLS"}:
            del env[key]
    env.update({"HOME": str(home), "XDG_CONFIG_HOME": str(home / ".config"), "XDG_CACHE_HOME": str(home / ".cache"),
                "XDG_DATA_HOME": str(home / ".local/share"), "DOTNET_ROOT": str(DOTNET.parent),
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "MOBMATE_DOCUMENTS_ROOT": str(documents),
                "MOBMATE_MODELS_ROOT": str(RUN / "models"), "MOBMATE_NO_BROWSER": "1",
                "MOBMATE_PACTL_EXE": "/usr/bin/false", "PULSE_SERVER": "unix:" + str(RUN / "no-audio-server")})
    with (RUN / "host.log").open("w", encoding="utf-8") as log:
        process = subprocess.Popen([str(DOTNET), str(HOST / "MobMateLinux.dll")],
                                   cwd=HOST, env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        runtime = documents / "MobMateWhispTalk/v2/config/webhost-runtime.json"
        deadline = time.monotonic() + 20
        while not runtime.exists():
            if process.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError("host did not start; see host.log")
            time.sleep(0.05)
        endpoint = json.loads(runtime.read_text())
        assert endpoint["pid"] == process.pid
        base = "http://127.0.0.1:" + str(endpoint["port"])
        settings = documents / "MobMateWhispTalk/v2/asr-addons/language-profiles.json"
        status, first = request("/api/addon-asr?language=en")
        check("Linux Qwen capability never claims runtime readiness", status == 200 and first["supported"] and first["runtimeSupported"] and not first["state"]["ready"] and
              [e["id"] for e in first["engines"] if e["platform"] == "linux-x64"] == ["qwen3_asr_ja"])
        request("/api/addon-asr?language=ja")
        check("GET/search does not create settings", not settings.exists())
        status, _ = request("/api/addon-asr/language", {"language": "ja--jp"})
        check("malformed language is rejected before any mutation", status == 400 and not settings.exists())
        status, _ = request("/api/addon-asr/install", {"engineId": "haas_ja", "modelId": "q8_0"})
        check("Linux never downloads a Windows-only worker", status == 400 and not settings.exists())
        saved = mutation("profile", {"language": "en", "engineId": "qwen3_asr_ja", "modelId": "q8_0"}, "en")
        check("explicit profile save is OFF without runtime or consent", saved["operationError"] is None and not saved["state"]["enabled"] and not saved["state"]["jointRequested"] and not saved["state"]["ready"])
        before = settings.read_bytes()
        failed = mutation("profile", {"language": "ko", "engineId": "qwen3_asr_ja", "modelId": "q8_0"}, "ko")
        check("unsupported language is rejected without modifying stored profile", failed["operationError"] is not None and settings.read_bytes() == before)
        failed = mutation("enabled", {"language": "en", "enabled": True}, "en")
        check("missing runtime cannot be enabled as Ready", failed["operationError"] is not None and not failed["state"]["enabled"])
        applied = mutation("language", {"language": "en"}, "en")
        check("language application keeps desired separate from actual resident", applied["languageState"]["desired"]["engineId"] == "qwen3_asr_ja" and applied["languageState"]["resident"] is None)
        failed = mutation("joint", {"language": "en", "jointRequested": True}, "en")
        check("unsupported runtime cannot silently acquire Joint consent", failed["operationError"] is not None and not failed["state"]["jointRequested"])
        mutation("profile", {"language": "ja", "engineId": "haas_ja", "modelId": "q8_0"}, "ja")
        _, ja = request("/api/addon-asr?language=ja")
        check("JA and EN preferences remain independent", ja["profiles"]["ja"]["engineId"] == "haas_ja" and ja["profiles"]["en"]["engineId"] == "qwen3_asr_ja")
        _, before_language = request("/api/addon-asr")
        before_rejected_start = settings.read_bytes()
        status, rejected = request("/api/realtime/start", {"engine": "moonshine", "language": "ko", "ttsEnabled": True})
        check("retained KO is rejected on an unsupported Linux route before native start", status == 400 and rejected["code"] == "realtime.unsupported_language")
        _, after_language = request("/api/addon-asr")
        check("unsupported explicit start preserves profiles and active language", settings.read_bytes() == before_rejected_start and
              after_language["languageState"]["language"] == before_language["languageState"]["language"] and
              after_language["languageState"]["generation"] == before_language["languageState"]["generation"])
        status, rejected = request("/api/realtime/start", {"engine": "haas_ja", "language": "ja", "ttsEnabled": False})
        check("Windows-only engine requests retain the existing Linux unsupported response", status == 400 and rejected["code"] == "addon.unsupported")
        _, after_unsupported_addon = request("/api/addon-asr")
        check("unsupported addon start also preserves session-side language context and settings",
              settings.read_bytes() == before_rejected_start and
              after_unsupported_addon["languageState"]["language"] == before_language["languageState"]["language"] and
              after_unsupported_addon["languageState"]["generation"] == before_language["languageState"]["generation"])
        status, rejected = request("/api/realtime/start", {"engine": "qwen3_asr_ja", "language": "ko", "useLanguageProfile": True, "ttsEnabled": False})
        _, after_qwen_rejection = request("/api/addon-asr")
        check("explicit unsupported Qwen language cannot fallback or mutate session/profile", status == 400 and rejected["code"] == "realtime.unsupported_language" and
              settings.read_bytes() == before_rejected_start and after_qwen_rejection["languageState"]["generation"] == before_language["languageState"]["generation"])
        _, languages = request("/api/realtime/languages")
        check("shared Linux language catalog keeps its public fields and reports missing Moonshine files",
              list(languages["languagesByEngine"]) == ["whisper", "moonshine", "joint_auto", "safe_auto", "sense_voice", "paraformer_zh"] and
              languages["languagesByEngine"]["moonshine"] == [] and languages["languagesByEngine"]["joint_auto"] == [])
        check("no model assets or legacy active.json created", not list((RUN / "models").iterdir()) and not (settings.parent / "packages").exists() and not (settings.parent / "active.json").exists())
        check("quality and Joint value are not equated with readiness", ja["languageState"]["quality"] == "not_evaluated" and ja["languageState"]["jointValue"] == "not_evaluated")
        status, idle = request("/api/realtime/status")
        check("output-epoch integration returns idle without starting capture", status == 200 and idle["state"] == "idle")
        status, idle = request("/api/realtime/stop?discard=true", {})
        check("discard Stop on an idle host is safe and idempotent", status == 200 and idle["ok"])
        status, idle = request("/api/realtime/destroy", {})
        check("idle destroy releases ownership without native inference", status == 200 and idle["ok"])
        browser_base = base
        if args.browser:
            proxy = http.server.ThreadingHTTPServer(("127.0.0.1", 0), BrowserGuard)
            threading.Thread(target=proxy.serve_forever, daemon=True).start()
            browser_base = "http://127.0.0.1:" + str(proxy.server_port)
            # A deliberate denial probe must never reach the app or create a payload.
            for route in ("/api/voicevox/install", "/api/unknown-artifact-download"):
                try:
                    urllib.request.urlopen(urllib.request.Request(browser_base + route, b"{}", method="POST"), timeout=5)
                    raise AssertionError("guard allowed " + route)
                except urllib.error.HTTPError as response:
                    assert response.code == 403
            check("browser guard blocks known and unknown download POST routes", len(denied) == 2 and not (settings.parent.parent / "config/voicevox").exists())
        ready = {"base_url": base, "pid": process.pid, "checks": checks, "browser_url": browser_base + "/language-browser-test.html"}
        (RUN / "browser-ready.json").write_text(json.dumps(ready, indent=2) + "\n")
        print(json.dumps(ready), flush=True)
        if args.browser:
            deadline = time.monotonic() + 600
            while not (RUN / "browser-done.json").exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise TimeoutError("bounded browser phase expired")
                time.sleep(0.2)
            browser = json.loads((RUN / "browser-done.json").read_text())
            check("Chromium interaction smoke passed", browser.get("passed") is True)
            check("browser produced no unexpected side-effect requests", len(denied) == 2)
        os.killpg(process.pid, signal.SIGTERM)
        check("host exits cleanly", process.wait(timeout=15) == 0 and not runtime.exists())
except Exception as exc:
    error = f"{type(exc).__name__}: {exc}"
    print(error, flush=True)
finally:
    if proxy is not None:
        proxy.shutdown()
        proxy.server_close()
        (RUN / "browser-guard.json").write_text(json.dumps({"expected_denials": 2, "denied": denied,
            "unexpected": denied[2:]}, indent=2) + "\n")
    if process is not None and process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=5)
    (RUN / "http-results.json").write_text(json.dumps({"passed": error is None, "checks": checks, "error": error}, indent=2) + "\n")
if error:
    raise SystemExit(1)
