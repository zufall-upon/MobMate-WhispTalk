// Run with: node tests/MobMate.Core.Tests/addon_asr_ui_regression.js
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const source = fs.readFileSync("web/app.js", "utf8");
function checkCatalogOnlyLanguageLabel() {
  const module = fs.readFileSync("web/addon-language.js", "utf8").replace(
    "let data = null, catalog = [],",
    'let data = null, catalog = [{ languages: ["ja", "ko"] }],');
  const fixture = vm.createContext({ window: {}, document: { getElementById: () => ({ value: "ja" }) },
    localStorage: { getItem: () => null }, t: (key, vars) => `${key}:${vars.language}` });
  vm.runInContext(module, fixture);
  const options = fixture.window.AddonLanguageUi.languageOptions([{ code: "ja", label: "Japanese" }]);
  assert.equal(options.find(option => option.code === "ja").label, "Japanese");
  assert.equal(options.find(option => option.code === "ko").label, "addon.asr.retainedLanguage:ko");
}
const setupGuardBegin = source.indexOf("let setupMicTesting = false;"), setupGuardEnd = source.indexOf("function openSetupWizard()", setupGuardBegin);
assert.ok(setupGuardBegin >= 0 && setupGuardEnd > setupGuardBegin);
const cueSource = source.slice(source.indexOf("const AsrAcceptanceCue ="), source.indexOf("const RealtimeSessionClient ="));
const setupGuard = cueSource + source.slice(setupGuardBegin, setupGuardEnd);
const start = source.indexOf("let addonAsrTimer = null;");
const end = source.indexOf("function ensureSafeAutoEngineOption()", start);
assert.ok(start >= 0 && end > start, "real addon UI functions must be present");

class Element {
  constructor() { this.children = []; this.value = ""; this.textContent = ""; this.hidden = false; this.handlers = {}; }
  get options() { return this.children; }
  set innerHTML(value) { assert.equal(value, ""); this.replaceChildren(); }
  replaceChildren() { this.children = []; this.value = ""; }
  appendChild(child) { child.parent = this; this.children.push(child); if (!this.value) this.value = child.value; }
  remove() { if (this.parent) { this.parent.children = this.parent.children.filter(child => child !== this); if (this.parent.value === this.value) this.parent.value = this.parent.children[0]?.value || ""; } }
  addEventListener(event, handler) { this.handlers[event] = handler; }
  setAttribute(name, value) { this[name] = value; }
}
const ids = ["addon-asr", "addon-asr-model", "addon-asr-details", "addon-asr-model-source", "addon-asr-runtime-source",
  "addon-asr-active", "addon-asr-selection", "addon-asr-controls-target", "addon-asr-hint-controls", "addon-asr-language-mode",
  "addon-asr-status", "addon-asr-setup", "addon-asr-probe", "addon-asr-enabled", "addon-asr-joint", "addon-asr-cancel", "rt-engine-select", "rt-language-select", "bench-engine-select"];
const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
for (const id of ["rt-engine-select", "bench-engine-select"]) {
  const original = new Element(); original.value = "joint_auto"; elements[id].appendChild(original);
}
const hasHaas = id => elements[id].children.some(option => option.value === "haas_ja");
const document = { getElementById: id => elements[id], createElement: () => new Element() };
const model = (id, bytes) => ({ id, name: id, license: "Apache-2.0", sourcePageUrl: "https://example.invalid/model", bytes });
let data = { supported: true, setupAvailable: true, loading: false, operationRunning: false, state: { installed: false, ready: false },
  engines: [{ id: "haas_ja", runtime: { id: "speech.cpp", version: "0.10.1", license: "MIT", bytes: 10,
    sourcePageUrl: "https://example.invalid/runtime" }, models: [model("q8_0", 100), model("q4_k", 50)] }] };
const requests = [];
let missing = false;
let enableConflict = false;
const context = vm.createContext({ document, console, t: (key, vars = {}) => vars.model ? `${key}:${vars.model}` : key,
  formatBytes: bytes => ` (${bytes})`, confirm: () => true,
  localStorage: { getItem: () => null }, defaultRealtimeEngine: "joint_auto",
  realtimeLanguagesByEngine: {}, ensureSenseVoiceEngineOption: () => {}, ensureParaformerZhEngineOption: () => {},
  setInterval: () => 1, clearInterval: () => {}, fetch: async (url, options) => {
    requests.push({ url, options });
    if (url === "/api/addon-asr") return missing ? { ok: false, status: 404 } : { ok: true, json: async () => data };
    if (url === "/api/realtime/languages") return { ok: true, json: async () => ({ languagesByEngine: {
      whisper: [{ code: "ja", label: "Japanese" }], joint_auto: [{ code: "ja", label: "Japanese" }],
      ...Object.fromEntries(["haas_ja", "parakeet_ja", "qwen3_asr_ja", "nemotron_3_5"].map(engine => [engine,
        data.state.engineId === engine && data.routeAvailable && data.state.ready && data.state.enabled ? [{ code: "ja", label: "Japanese" }] : []]))
    } }) };
    if (url === "/api/addon-asr/setup") data = { ...data, operationRunning: true };
    if (url === "/api/addon-asr/probe") data = { ...data, operationRunning: true };
    if (url === "/api/addon-asr/cancel") data = { ...data, operationRunning: false, operationError: "cancelled" };
    if (url === "/api/addon-asr/enabled") {
      if (enableConflict) return { ok: false, status: 409 };
      const enabled = JSON.parse(options.body).enabled;
      data = { ...data, routeAvailable: enabled, jointAvailable: enabled, state: { ...data.state, enabled, jointRequested: enabled && data.state.jointRequested } };
    }
    if (url === "/api/addon-asr/joint") data = { ...data, state: { ...data.state, jointRequested: JSON.parse(options.body).jointRequested } };
    return { ok: true, status: 200 };
  } });
vm.runInContext(source.slice(start, end), context);
vm.runInContext(source.slice(source.indexOf("const REALTIME_ENGINE ="), source.indexOf("const TTS_ENGINE =")), context);
vm.runInContext(source.slice(source.indexOf("function normalizeRealtimeEngine("), source.indexOf("/* --- Review prompt")), context);
vm.runInContext(source.slice(source.indexOf("function getRealtimeLanguageOptions("), source.indexOf("/* --- UI i18n --- */")), context);
vm.runInContext(source.slice(source.indexOf("function getRealtimeSettings("), source.indexOf("function saveRealtimeSettings(")), context);
vm.runInContext(source.slice(source.indexOf("function updateRealtimeLanguageOptions("), source.indexOf("function setText(")), context);

async function checkRealtimePollExitCleanup() {
  const pollStart = source.indexOf("async function pollRealtime(token)");
  const pollEnd = source.indexOf("/* --- Realtime Route Bench --- */", pollStart);
  assert.ok(pollStart >= 0 && pollEnd > pollStart);
  for (const terminal of [{ state: "failed", error: "inference failed" }, { state: "completed" }, { state: "recording", error: "capture failed" },
    { state: "failed", error: "capture_device_lost", final: "previously displayed" }]) {
    let calls = 0;
    let reserved = true;
    let stopped = 0;
    let updated = 0, remembered = true, stopSource = "";
    const status = { textContent: "", className: "" };
    const live = { "rt-live-vad": "speech (1)", "rt-live-rms": "1592.7020", "rt-partial": "pending", "rt-final-history": "previously displayed" };
    const pollContext = vm.createContext({
      console, realtimePollToken: 1, document: { getElementById: () => status },
      setTimeout: callback => callback(),
      t: key => key, lastRealtimePartial: "pending", setText: (id, value) => { live[id] = value; }, renderRealtimeTabs: () => {}, setRealtimeGuide: () => {},
      updateRealtimeResults: () => { updated++; }, setRealtimeWasRecording: value => { remembered = value; }, setRecordingButtons: recording => {
        if (!recording) assert.equal(reserved, false, "do not show stopped UI before native cleanup");
      }, meterStop: () => {}, spkStop: () => {}, waveClear: () => {},
      RealtimeSessionClient: {
        getStatus: async () => {
          assert.equal(reserved, true, "recording must remain reserved until stop finishes");
          return calls++ === 0 ? { state: "recording" } : terminal;
        },
        stopRealtimeSession: async source => { stopSource = source; stopped++; reserved = false; }
      }
    });
    vm.runInContext(setupGuard + source.slice(pollStart, pollEnd), pollContext);
    await vm.runInContext("pollRealtime(1)", pollContext);
    assert.equal(stopped, 1, `${JSON.stringify(terminal)}: poll exit must stop/dispose the session`);
    assert.equal(reserved, false, "addon operations must be available again after terminal cleanup");
    if (terminal.error === "capture_device_lost") {
      assert.equal(stopSource, "capture-loss");
      assert.equal(updated, 1, "loss must not append the old final or interrupted results");
      assert.equal(remembered, false, "loss must not restore recording on restart");
      assert.equal(status.textContent, "ui.web.status.captureLost");
      assert.equal(pollContext.lastRealtimePartial, "", "only the unconfirmed partial is cleared");
      assert.equal(live["rt-live-vad"], "-", "stopped capture must not display an active VAD");
      assert.equal(live["rt-live-rms"], "0.0000", "stopped capture must not retain a live input level");
      assert.equal(live["rt-partial"], "", "loss clears the unconfirmed partial");
      assert.equal(live["rt-final-history"], "previously displayed", "loss preserves confirmed history");
    }
  }
  let obsoleteStops = 0;
  const obsoleteContext = vm.createContext({
    console, realtimePollToken: 2, document: { getElementById: () => ({}) },
    RealtimeSessionClient: { stopRealtimeSession: async () => { obsoleteStops++; } }
  });
  vm.runInContext(setupGuard + source.slice(pollStart, pollEnd), obsoleteContext);
  await vm.runInContext("pollRealtime(1)", obsoleteContext);
  assert.equal(obsoleteStops, 0, "an obsolete poll must never stop a newer session");
  let obsoleteUiResets = 0;
  let stoppingContext;
  stoppingContext = vm.createContext({
    console, realtimePollToken: 1, document: { getElementById: () => ({}) }, setTimeout: callback => callback(),
    updateRealtimeResults: () => {}, setRecordingButtons: () => { obsoleteUiResets++; },
    meterStop: () => {}, spkStop: () => {}, waveClear: () => {},
    RealtimeSessionClient: {
      getStatus: async () => ({ state: "failed" }),
      stopRealtimeSession: async () => { stoppingContext.realtimePollToken = 2; }
    }
  });
  vm.runInContext(setupGuard + source.slice(pollStart, pollEnd), stoppingContext);
  await vm.runInContext("pollRealtime(1)", stoppingContext);
  assert.equal(obsoleteUiResets, 0, "cleanup finishing after a new poll starts must not reset its UI");
  let failedCleanupUiResets = 0;
  const failedStatus = { textContent: "", className: "" };
  const failedContext = vm.createContext({
    console, realtimePollToken: 1, document: { getElementById: () => failedStatus }, setTimeout: callback => callback(),
    t: (key, vars = {}) => `${key}:${vars.message || ""}`, setRecordingButtons: () => { failedCleanupUiResets++; },
    RealtimeSessionClient: { getStatus: async () => ({ state: "failed", error: "capture_device_lost" }),
      stopRealtimeSession: async () => { throw new Error("stop failed"); } }
  });
  vm.runInContext(setupGuard + source.slice(pollStart, pollEnd), failedContext);
  await vm.runInContext("pollRealtime(1)", failedContext);
  assert.equal(failedCleanupUiResets, 0, "failed cleanup must not show stopped controls");
  assert.match(failedStatus.textContent, /stop failed/, "cleanup failure must be visible");

  let deliverStatus, signalStatus, guardedStops = 0, guardedUpdates = 0;
  const statusEntered = new Promise(resolve => { signalStatus = resolve; });
  const heldStatus = new Promise(resolve => { deliverStatus = resolve; });
  const guardedStatus = { textContent: "unchanged", className: "status" };
  const guardedContext = vm.createContext({ console, realtimePollToken: 1, document: { getElementById: () => guardedStatus },
    setTimeout: callback => callback(), updateRealtimeResults: () => { guardedUpdates++; },
    RealtimeSessionClient: { getStatus: async () => { signalStatus(); return heldStatus; }, stopRealtimeSession: async () => { guardedStops++; } } });
  vm.runInContext(setupGuard + source.slice(pollStart, pollEnd), guardedContext);
  const oldPoll = vm.runInContext("pollRealtime(1)", guardedContext); await statusEntered;
  vm.runInContext("setupMicUnconfirmed = true", guardedContext);
  deliverStatus({ state: "failed", error: "capture_device_lost", final: "stale" }); await oldPoll;
  assert.equal(guardedStops, 0, "an old main poll must not stop an unconfirmed wizard session");
  assert.equal(guardedUpdates, 0); assert.equal(guardedStatus.textContent, "unchanged");
}

async function checkCaptureLossClientDiscard() {
  const begin = source.indexOf("const RealtimeSessionClient ="), end = source.indexOf("async function fetchAudioInputDevices()", begin);
  assert.ok(begin >= 0 && end > begin);
  const requests = []; let cancelled = 0;
  const clientContext = vm.createContext({ console, realtimePollToken: 0, document: { getElementById: () => null },
    localStorage: { getItem: () => "", setItem: () => {} }, getRealtimeSettings: () => ({}),
    AddonLanguageUi: { cancelFollow: () => { cancelled++; }, translate: () => {} },
    fetch: async url => { requests.push(url); return { ok: true, json: async () => ({ ok: true }) }; } });
  vm.runInContext(cueSource + source.slice(begin, end) + ";globalThis.client = RealtimeSessionClient;", clientContext);
  await clientContext.client.startRealtimeSession("main", {});
  await clientContext.client.stopRealtimeSession("capture-loss");
  assert.equal(requests.at(-1), "/api/realtime/stop?discard=true");
  assert.equal(cancelled, 1, "loss must cancel language-follow resume");
  assert.equal(clientContext.client.isMainActive(), false);
}

async function checkRealtimeStateUi() {
  const locales = Object.fromEntries(["en", "ja", "ko", "zh_cn", "zh_tw"].map(language => [language,
    Object.fromEntries(fs.readFileSync(`libs/preset/_ui_${language}.txt`, "utf8").split(/\r?\n/)
      .filter(line => line && !line.startsWith("#") && line.includes("="))
      .map(line => [line.slice(0, line.indexOf("=")), line.slice(line.indexOf("=") + 1)]))]));
  for (const key of ["ui.web.guide.stopUnconfirmed", "ui.web.status.waitingForStart", "ui.web.status.stopped", "ui.web.status.stoppedLatency",
    "ui.web.runtime.whisperDiagnostic", "ui.web.live.engine"]) {
    assert.ok(Object.values(locales).every(strings => strings[key]), `${key}: all five locales`);
    assert.equal(new Set(Object.values(locales).map(strings => strings[key])).size, 5, `${key}: actual translations`);
  }
  const translate = (language, key, vars = {}) => (locales[language][key] || key)
    .replace(/\{(\w+)\}/g, (match, name) => vars[name] ?? match);
  const html = fs.readFileSync("web/index.html", "utf8");
  assert.match(html, /id="rt-status" data-i18n="ui.web.status.waitingForStart"/);
  assert.match(html, /id="runtime-pill">Whisper runtime: -<\/span>/, "initial markup must not claim an unobserved Vulkan runtime");

  let language = "en", config = { whisperRuntimeMode: "linux_unavailable" };
  const fields = {};
  const configContext = vm.createContext({ console, window: {}, isLinuxRuntime: false,
    document: { getElementById: id => fields[id] ??= { textContent: "", tagName: "DIV" }, querySelectorAll: () => [] },
    t: (key, vars) => translate(language, key, vars),
    setText: (id, value) => { (fields[id] ??= {}).textContent = value; },
    ensureVoicevoxPathRow() {}, applyDefaultRealtimeEngine() {}, applyRuntimeFeatureVisibility() {},
    updateVoicevoxInstallUi() {}, updateRadioSpeakButtonLabel() {}, showLegacyWindowsCpuOnlyNotice() {}, applyRealtimeStartRecovery() {},
    detectLinuxRuntimeFromHealth: async () => true,
    fetch: async url => ({ ok: true, json: async () => url === "/api/config" ? config : { runtimeMode: "linux", version: "fixture" } })
  });
  const configBegin = source.indexOf("/* --- Config --- */"), configEnd = source.indexOf("/* --- Demo session limit", configBegin);
  const i18nBegin = source.indexOf("function applyI18n()"), i18nEnd = source.indexOf("function ensureAutoSceneOption()", i18nBegin);
  assert.ok(configBegin >= 0 && configEnd > configBegin && i18nBegin >= 0 && i18nEnd > i18nBegin);
  vm.runInContext(source.slice(configBegin, configEnd) + source.slice(i18nBegin, i18nEnd), configContext);
  vm.runInContext("applyI18n()", configContext);
  assert.equal(fields["runtime-pill"].textContent, "Whisper runtime: -");
  await vm.runInContext("fetchReleaseStatus()", configContext);
  assert.equal(fields["runtime-pill"].textContent, "Whisper runtime: -", "platform is not a Whisper runtime observation");
  await vm.runInContext("fetchConfig()", configContext);
  assert.equal(fields["runtime-pill"].textContent, "Whisper runtime: linux_unavailable");
  await vm.runInContext("fetchReleaseStatus()", configContext);
  assert.equal(fields["runtime-pill"].textContent, "Whisper runtime: linux_unavailable", "release/config ordering must not relabel the diagnostic as overall availability");
  config = { whisperRuntimeMode: "cpu" };
  await vm.runInContext("fetchConfig()", configContext);
  language = "ja";
  vm.runInContext("applyI18n()", configContext);
  assert.equal(fields["runtime-pill"].textContent, "Whisper実行環境: cpu", "language changes retain the observed diagnostic value");
  config = {};
  await vm.runInContext("fetchConfig()", configContext);
  assert.equal(fields["runtime-pill"].textContent, "Whisper実行環境: -", "missing fields must not imply a runtime");

  const clientBegin = source.indexOf("const RealtimeSessionClient ="), clientEnd = source.indexOf("// The drawer and setup wizard", clientBegin);
  const latencyBegin = source.indexOf("function formatRealtimeLatencyMs("), latencyEnd = source.indexOf("function getRealtimeFinalAgeMs(", latencyBegin);
  assert.ok(clientBegin >= 0 && clientEnd > clientBegin && latencyBegin >= 0 && latencyEnd > latencyBegin);
  const setterBegin = source.indexOf("function setText("), setterEnd = source.indexOf("function setRecordingButtons(", setterBegin);
  const historyBegin = source.indexOf("function renderRealtimeTabs("), historyEnd = source.indexOf("/* ── Translation", historyBegin);
  const pollBegin = source.indexOf("async function pollRealtime(token)"), pollEnd = source.indexOf("/* --- Realtime Route Bench --- */", pollBegin);
  assert.ok(setterBegin >= 0 && setterEnd > setterBegin && historyBegin >= 0 && historyEnd > historyBegin && pollBegin >= 0 && pollEnd > pollBegin);
  class LiveElement {
    constructor() { this.textContent = ""; this.className = ""; this.style = {}; this.children = []; this.disabled = false; }
    addEventListener() {}
    setAttribute() {}
    set innerHTML(value) { assert.equal(value, ""); this.children = []; }
    appendChild(child) { this.children.push(child); }
    prepend(child) { this.children.unshift(child); }
    removeChild(child) { this.children.splice(this.children.indexOf(child), 1); }
    get lastChild() { return this.children.at(-1); }
  }
  const makeStopUi = fetch => {
    const elements = {}, buttons = [], guides = [], meters = [];
    const stopContext = vm.createContext({ console, realtimePollToken: 1,
      noiseWarmupGeneration: 0, noiseWarmupTimer: null, clearInterval() {},
      lastRealtimeFinal: "", lastRealtimePartial: "", lastTranslatedFinalShown: null, lastHistoryOriginalText: null,
      realtimeFinalTabs: [], lastRealtimeFinalDetectedAtMs: 0, lastRealtimeFinalUiAgeMs: null, lastRealtimeHistoryFinalDetectedAtMs: 0,
      document: { getElementById: id => elements[id] ??= new LiveElement(), createElement: () => new LiveElement(), addEventListener() {} },
      localStorage: { getItem: () => "", setItem() {} },
      t: (key, vars) => translate("en", key, vars), apiMessage: (body, status) => body?.error || `HTTP ${status}`,
      setRealtimeGuide: key => guides.push(key), setRecordingButtons: value => buttons.push(value),
      setRealtimeWasRecording() {}, meterSetRms: value => meters.push(value), meterStop: () => meters.push("stop"),
      spkStop() {}, waveClear() {}, waveUpdate() {}, waveDraw() {}, checkReviewPrompt() {},
      engineDisplayName: engine => engine, ensureSenseVoiceEngineOption() {},
      AddonLanguageUi: { cancelFollow() {}, translate() {} }, fetch, setTimeout: callback => callback()
    });
    // Exercise production setters, live rendering and confirmed history, not no-op stand-ins.
    const warmupBegin = source.indexOf("function updateMainActionLabels()"), warmupEnd = source.indexOf("function startNoiseWarmupUi(", warmupBegin);
    assert.ok(warmupBegin >= 0 && warmupEnd > warmupBegin);
    vm.runInContext(setupGuard + source.slice(warmupBegin, warmupEnd) + source.slice(setterBegin, setterEnd) + source.slice(historyBegin, historyEnd)
      + source.slice(latencyBegin, source.indexOf("const AsrAcceptanceCue =")) + source.slice(clientBegin, clientEnd) + source.slice(pollBegin, pollEnd), stopContext);
    stopContext.updateRealtimeResults({ state: "recording", final: "Open the map now.", selectedEngine: "qwen3_asr_ja",
      vadState: "speech", vadSpeechCount: 1, captureRms: 1632.8025, captureSamples: 8000, latencyMs: 937,
      finalDetectedAtMs: Date.now() - 50, options: { engine: "moonshine", language: "en" } });
    stopContext.document.getElementById("rt-status").textContent = "recording";
    elements["rt-status"].className = "status ok";
    stopContext.document.getElementById("rt-stop-btn").disabled = false;
    buttons.length = guides.length = meters.length = 0;
    return { stopContext, elements, buttons, guides, meters };
  };
  const historySnapshot = elements => ({
    history: elements["rt-final-history"].children.map(item => item.children.map(child => child.textContent)),
    tabs: elements["rt-final-tabs"].children.map(item => item.textContent), engine: elements["rt-live-engine"].textContent
  });
  const stopCases = [
    { body: { ok: true } }, { body: { ok: true, state: "idle" } }, { body: {}, failed: true }, { body: null, failed: true }, { invalidJson: true, failed: true },
    { body: { state: "stopped", latencyMs: 125 }, latency: "125ms" },
    { body: { state: "stopped", latencyMs: 0 }, latency: "0ms" },
    { body: { latencyMs: null }, failed: true }, { body: { latencyMs: "" }, failed: true }, { body: { latencyMs: Infinity }, failed: true },
    { body: { ok: true, final: "Open the map now.", selectedEngine: "qwen3_asr_ja", vadState: "speech", vadSpeechCount: 2, captureRms: 812 } },
    { body: { state: "failed", error: "final recognition failed" }, pollError: true },
    { body: { ok: false, error: "stop rejected" }, failed: true },
    { body: { error: "server failed" }, httpOk: false, failed: true }, { networkError: true, failed: true }
  ];
  for (const scenario of stopCases) {
    const requests = [];
    let retry = false;
    const { stopContext, elements, buttons, guides, meters } = makeStopUi(async url => {
        requests.push(url);
        if (retry) return { ok: true, json: async () => ({ ok: true }) };
        if (scenario.networkError) throw new Error("network unavailable");
        return { ok: scenario.httpOk !== false, status: scenario.httpOk === false ? 500 : 200,
          json: async () => { if (scenario.invalidJson) throw new SyntaxError("empty response"); return scenario.body; } };
    });
    const confirmed = historySnapshot(elements);
    await vm.runInContext("stopRealtime()", stopContext);
    assert.deepEqual(requests, ["/api/realtime/stop"]);
    const status = elements["rt-status"];
    assert.doesNotMatch(status.textContent, /undefined|NaN|null/);
    if (scenario.failed) {
      assert.equal(status.className, "status err");
      assert.ok(!status.textContent.includes(locales.en["ui.web.status.stopped"]));
      assert.deepEqual(buttons, [], "unconfirmed/failed Stop must not publish stopped controls");
      assert.deepEqual(guides, ["ui.web.status.stopping", "ui.web.guide.stopUnconfirmed"], "failed Stop must not leave a keep-talking or stopped guide");
      assert.equal(elements["rt-stop-btn"].disabled, false, "failed Stop must remain retryable");
      assert.equal(elements["rt-live-vad"].textContent, "speech (1)", "failed/unconfirmed Stop must not publish an inactive VAD");
      assert.equal(elements["rt-live-rms"].textContent, "1632.8025");
      assert.deepEqual(meters, ["stop"], "existing failure cleanup must not fabricate an RMS observation");
    } else {
      assert.deepEqual(buttons, [false], "confirmed Stop follows the actual HTTP/client contract with ok=true or a recognized state");
      assert.deepEqual(guides, ["ui.web.status.stopping", "ui.web.guide.stopped"]);
      assert.equal(elements["rt-live-vad"].textContent, "-", "resolved Stop clears live activity/count, including stale values in its response");
      assert.equal(elements["rt-live-rms"].textContent, "0.0000");
      assert.equal(meters.at(-1), "stop", "live reset accompanies the stopped meter");
      if (scenario.pollError) {
        assert.equal(status.className, "status err");
        assert.match(status.textContent, /final recognition failed/);
      } else {
        assert.equal(status.className, "status ok");
        assert.equal(status.textContent, translate("en", scenario.latency ? "ui.web.status.stoppedLatency" : "ui.web.status.stopped", { latency: scenario.latency }));
      }
    }
    assert.deepEqual(historySnapshot(elements), confirmed, "Stop preserves confirmed text, result engine and final metadata");
    if (scenario.failed) {
      retry = true;
      await vm.runInContext("stopRealtime()", stopContext);
      assert.deepEqual(requests, ["/api/realtime/stop", "/api/realtime/stop"], "client idle after failure must not reject an explicit re-Stop");
      assert.deepEqual(guides, ["ui.web.status.stopping", "ui.web.guide.stopUnconfirmed", "ui.web.status.stopping", "ui.web.guide.stopped"]);
      assert.deepEqual(buttons, [false]);
      assert.equal(elements["rt-status"].textContent, locales.en["ui.web.status.stopped"]);
      assert.equal(elements["rt-status"].className, "status ok");
      assert.equal(elements["rt-live-vad"].textContent, "-");
      assert.equal(elements["rt-live-rms"].textContent, "0.0000");
      assert.deepEqual(historySnapshot(elements), confirmed, "explicit re-Stop preserves confirmed text, engine and final metadata");
    }
  }

  let confirmStop, deliverPoll;
  const pendingStop = new Promise(resolve => { confirmStop = resolve; });
  const pendingPoll = new Promise(resolve => { deliverPoll = resolve; });
  const pendingRequests = [];
  const { stopContext, elements, buttons, meters } = makeStopUi(async url => {
    pendingRequests.push(url);
    return { ok: true, json: () => url === "/api/realtime/status" ? pendingPoll : pendingStop };
  });
  const confirmed = historySnapshot(elements);
  const poll = vm.runInContext("pollRealtime(1)", stopContext);
  while (!pendingRequests.includes("/api/realtime/status")) await Promise.resolve();
  const stopping = vm.runInContext("stopRealtime()", stopContext);
  while (!pendingRequests.includes("/api/realtime/stop")) await Promise.resolve();
  assert.equal(elements["rt-live-vad"].textContent, "speech (1)", "Stop request alone must not reset activity");
  assert.equal(elements["rt-live-rms"].textContent, "1632.8025");
  assert.deepEqual(buttons, []);
  assert.deepEqual(meters, []);
  confirmStop({ ok: true });
  await stopping;
  const stoppedStatus = elements["rt-status"].textContent;
  deliverPoll({ state: "recording", vadState: "speech", vadSpeechCount: 99, captureRms: 999,
    final: "obsolete poll text", selectedEngine: "whisper" });
  await poll;
  assert.equal(elements["rt-live-vad"].textContent, "-", "pre-Stop poll cannot restore an active VAD");
  assert.equal(elements["rt-live-rms"].textContent, "0.0000");
  assert.equal(elements["rt-status"].textContent, stoppedStatus);
  assert.deepEqual(historySnapshot(elements), confirmed, "obsolete poll must not replace confirmed text, engine or metadata");
  assert.deepEqual(buttons, [false], "obsolete poll must not issue a second Stop/control update");
  assert.deepEqual(pendingRequests, ["/api/realtime/status", "/api/realtime/stop"]);
  const engineBegin = source.indexOf("function getRealtimeDisplayEngine("), engineEnd = source.indexOf("function updateTranslationSourceDisplay(", engineBegin);
  assert.ok(engineBegin >= 0 && engineEnd > engineBegin);
  const engineContext = vm.createContext({});
  vm.runInContext(source.slice(engineBegin, engineEnd), engineContext);
  for (const value of [{}, { selectedEngine: "none", options: { engine: "qwen3_asr_ja" } }, { engine: "whisper" },
    { options: { engine: "joint_auto" } }]) {
    assert.equal(engineContext.getRealtimeDisplayEngine(value), "", "requested/resident engines are not result selection evidence");
  }
  assert.equal(engineContext.getRealtimeDisplayEngine({ selectedEngine: "qwen3_asr_ja", options: { engine: "moonshine" } }), "qwen3_asr_ja");
  assert.equal(engineContext.getRealtimeDisplayEngine({ selected_engine: "whisper" }), "whisper", "existing alias stays supported");
  console.log("PASS Stop success/failure/pending, explicit re-Stop, confirmed history, obsolete poll, five locales and Whisper/result engine distinction");
}

function checkRealtimeFinalSegments() {
  class HistoryElement extends Element {
    constructor() { super(); this.style = {}; }
    prepend(child) { this.children.unshift(child); }
    removeChild(child) { this.children.splice(this.children.indexOf(child), 1); }
    get lastChild() { return this.children.at(-1); }
    querySelector() { return this.children[0]?.children[0] || null; }
  }
  const fields = {};
  const historyContext = vm.createContext({
    document: { getElementById: id => fields[id] ??= new HistoryElement(), createElement: () => new HistoryElement() },
    t: key => key, setText: (id, text) => { (fields[id] ??= new HistoryElement()).textContent = text; },
    engineDisplayName: value => value, ensureSenseVoiceEngineOption() {}, meterSetRms() {}, waveUpdate() {}, waveDraw() {},
    setRealtimeGuide() {}, meterStop() {}, waveClear() {}, spkStop() {}
  });
  const stateBegin = source.indexOf('let lastRealtimeFinal = "";');
  const stateEnd = source.indexOf("let defaultRealtimeEngine =", stateBegin);
  const resetBegin = source.indexOf("function resetRealtimeLiveUi()");
  const historyEnd = source.indexOf("/* ── Translation", resetBegin);
  const liveBegin = source.indexOf("function formatRealtimeLatencyMs(");
  const liveEnd = source.indexOf("const AsrAcceptanceCue =", liveBegin);
  assert.ok(stateBegin >= 0 && stateEnd > stateBegin && resetBegin >= 0 && historyEnd > resetBegin && liveEnd > liveBegin);
  vm.runInContext(source.slice(stateBegin, stateEnd) + source.slice(resetBegin, historyEnd) + source.slice(liveBegin, liveEnd), historyContext);
  const emit = (final, timestamp, extra = {}) => historyContext.updateRealtimeResults({
    state: "recording", final, finalDetectedAtMs: timestamp, selectedEngine: "qwen3_asr_ja", latencyMs: 17, ...extra
  });
  const latest = () => fields["rt-final-history"].children[0]?.children[0].textContent;
  emit("Open the map", 1000);
  assert.equal(fields["rt-active-engine"]?.textContent, "qwen3_asr_ja",
    "a Linux status without options still shows the engine of the actual final");
  historyContext.updateRealtimeResults({ state: "processing", options: { engine: "moonshine" } });
  assert.equal(fields["rt-active-engine"].textContent, "qwen3_asr_ja",
    "a later options-only poll must not replace the last actual result engine");
  historyContext.updateRealtimeResults({ state: "recording", final: "result", selectedEngine: "sense_voice", options: { engine: "joint_auto" } });
  assert.equal(fields["rt-active-engine"].textContent, "sense_voice",
    "the actual result engine wins over the captured Auto session option");
  historyContext.resetRealtimeLiveUi();
  historyContext.updateRealtimeResults({ state: "processing", options: { engine: "moonshine" } });
  assert.equal(fields["rt-active-engine"].textContent, "-", "an option without a result is not an observed engine");
  emit("Open the map", 1000);
  emit("", 2000);
  emit("Open the map", 2000);
  emit("Open the map now.", 2000);
  assert.equal(latest(), "✓ Open the map now.", "a different native final must not lose the preceding utterance's prefix");
  emit("Open the map now. Please.", 3000, { processedFinal: "Open the map now. Please!", translatedFinal: "地図を開いてください" });
  assert.equal(latest(), "✓ 地図を開いてください (Open the map now. Please!)", "filtered text and translation keep the entire new utterance");
  assert.match(fields["rt-final-history"].children[0].children[1].textContent, /asr=17ms/);
  assert.equal(fields["rt-live-engine"].textContent, "qwen3_asr_ja");
  const count = fields["rt-final-history"].children.length;
  emit("Open the map now. Please.", 4000);
  assert.equal(fields["rt-final-history"].children.length, count, "timestamp changes alone must not relax content duplicate suppression");
  emit("Blocked", 5000, { textFilter: { dropped: true } });
  emit("Loopback", 6000, { loopbackSuspected: true });
  assert.equal(fields["rt-final-history"].children.length, count, "filter and echo exclusion remain intact");
  historyContext.resetRealtimeLiveUi();
  emit("First line\nSecond line", 6000);
  assert.equal(latest(), "✓ First line\nSecond line", "a known native segment retains all its lines after Start reset");
  historyContext.resetRealtimeLiveUi();
  emit("Legacy", 0); emit("Legacy suffix", 0);
  assert.equal(latest(), "✓ suffix", "responses without segment metadata retain the legacy cumulative contract");
  historyContext.resetRealtimeLiveUi();
  emit("Same boundary", 8000); emit("Same boundary suffix", 8000);
  assert.equal(latest(), "✓ suffix", "updates within one boundary retain the cumulative contract");
  historyContext.updateRealtimeResults({ state: "idle" });
  assert.equal(fields["rt-active-engine"].textContent, "-", "an idle poll clears the active engine");
  historyContext.resetRealtimeLiveUi();
  assert.equal(fields["rt-active-engine"].textContent, "-", "a fresh session does not inherit the old engine");
  console.log("PASS real final/history consumer: distinct native segments, filtered/translated text, labels, duplicates, drop/echo, reset and legacy cumulative responses; no ASR/TTS");
}

async function checkAddonPlatformDetails() {
  const locales = ["en", "ja", "ko", "zh_cn", "zh_tw"];
  for (const language of locales) {
    const lines = fs.readFileSync(`libs/preset/_ui_${language}.txt`, "utf8").split(/\r?\n/);
    const line = lines.find(value => value.startsWith("addon.asr.languages="));
    assert.ok(line && line.includes("{languages}") && line.includes("{platform}"), `${language}: both placeholders are required`);
    const label = line.slice(line.indexOf("=") + 1);
    context.t = (key, vars = {}) => key === "addon.asr.languages"
      ? label.replace(/\{(\w+)\}/g, (match, name) => vars[name] ?? match)
      : vars.model ? `${key}:${vars.model}` : key;
    for (const platform of ["linux-x64", "windows-x64", undefined]) {
      data.engines[0].platform = platform;
      await vm.runInContext("refreshAddonAsr()", context);
      const detail = elements["addon-asr-details"].textContent;
      assert.ok(detail.includes(platform || "-"), `${language}: platform ${platform} missing: ${detail}`);
      assert.ok(!detail.includes("Windows x64 Vulkan"), `${language}: platform must not be hard-coded`);
      assert.ok(!detail.includes("{platform}"), `${language}: platform placeholder must be resolved`);
      if (platform === "linux-x64") assert.ok(!detail.includes("windows-x64"));
      if (platform === "windows-x64") assert.ok(!detail.includes("linux-x64"));
    }
  }
  console.log("PASS addon details: Linux/Windows/unknown platform, fallback caller and five locales");
}

(async () => {
  if (process.env.MOBMATE_FINAL_SEGMENT_ONLY === "1") {
    checkRealtimeFinalSegments();
    return;
  }
  if (process.env.MOBMATE_ADDON_PLATFORM_ONLY === "1") {
    await checkAddonPlatformDetails();
    return;
  }
  if (process.env.MOBMATE_STATE_UI_ONLY === "1") {
    await checkRealtimeStateUi();
    return;
  }
  if (process.env.MOBMATE_POLL_ONLY === "1") {
    await checkRealtimePollExitCleanup();
    await checkCaptureLossClientDiscard();
    console.log("PASS realtime poll exit and capture loss");
    return;
  }
  require("./fixtures/addon_start_state_contract.js")();
  await checkRealtimePollExitCleanup();
  await checkRealtimeStateUi();
  checkRealtimeFinalSegments();
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.notInstalled");
  assert.equal(elements["addon-asr-active"].textContent, "addon.asr.noConfiguredModel");
  assert.equal(elements["addon-asr-selection"].textContent, "addon.asr.selectedNeedsSetup:q8_0");
  assert.equal(elements["addon-asr-model"].children.length, 2);
  assert.match(elements["addon-asr-details"].textContent, /Apache-2.0.*speech\.cpp 0\.10\.1 \(MIT\)/);
  data.setupAvailable = false;
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(elements["addon-asr-setup"].disabled, true);
  data.setupAvailable = true;
  vm.runInContext("initAddonAsrUi()", context);
  await vm.runInContext("refreshAddonAsr()", context);
  elements["addon-asr-model"].value = "haas_ja/q4_k";
  elements["addon-asr-model"].handlers.change();
  await elements["addon-asr-setup"].handlers.click();
  const setup = requests.find(request => request.url === "/api/addon-asr/setup");
  assert.deepEqual(JSON.parse(setup.options.body), { engineId: "haas_ja", modelId: "q4_k" });
  assert.equal(elements["addon-asr-setup"].disabled, true);
  assert.equal(elements["addon-asr-cancel"].disabled, false);
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.working");
  assert.equal(hasHaas("rt-engine-select"), false, "downloading files do not make an engine ready");
  await elements["addon-asr-cancel"].handlers.click();
  assert.match(elements["addon-asr-status"].textContent, /cancelled/);
  data = { ...data, operationError: null, state: { installed: true, ready: false, engineId: "haas_ja", modelId: "q4_k" } };
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.installed");
  assert.equal(elements["addon-asr-probe"].disabled, false);
  assert.equal(elements["addon-asr-setup"].disabled, true);
  data.state.ready = true;
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.ready");
  assert.equal(elements["addon-asr-probe"].disabled, true);
  assert.equal(elements["addon-asr-enabled"].disabled, false);
  assert.equal(elements["addon-asr-joint"].disabled, true);
  assert.equal(hasHaas("rt-engine-select"), false);
  enableConflict = true;
  await elements["addon-asr-enabled"].handlers.click();
  assert.match(elements["addon-asr-status"].textContent, /409/);
  assert.equal(hasHaas("rt-engine-select"), false);
  enableConflict = false;
  await elements["addon-asr-enabled"].handlers.click();
  assert.deepEqual(JSON.parse(requests.find(request => request.url === "/api/addon-asr/enabled").options.body), { enabled: true });
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.enabled");
  assert.equal(hasHaas("rt-engine-select"), true);
  assert.equal(hasHaas("bench-engine-select"), true);
  assert.equal(elements["rt-engine-select"].value, "joint_auto", "enabling Haas must not switch the selected engine");
  assert.equal(elements["addon-asr-joint"].disabled, false);
  await elements["addon-asr-joint"].handlers.click();
  assert.deepEqual(JSON.parse(requests.find(request => request.url === "/api/addon-asr/joint").options.body), { jointRequested: true });
  assert.equal(elements["addon-asr-joint"]["aria-pressed"], "true");
  assert.equal(elements["addon-asr-joint"].textContent, "addon.asr.jointOff");
  const activeState = structuredClone(data.state);
  elements["addon-asr-model"].value = "haas_ja/q8_0";
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(elements["addon-asr-active"].textContent, "addon.asr.activeModel:q4_k");
  assert.equal(elements["addon-asr-selection"].textContent, "addon.asr.selectedNeedsSetup:q8_0");
  assert.equal(elements["addon-asr-controls-target"].textContent, "addon.asr.controlsFor:q4_k");
  assert.equal(elements["addon-asr-setup"].disabled, false);
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.enabled", "status describes the explicitly named active model");
  assert.equal(elements["rt-engine-select"].value, "joint_auto");
  assert.deepEqual(data.state, activeState, "changing the picker must not mutate the active configuration");
  await elements["addon-asr-setup"].handlers.click();
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.working");
  assert.equal(elements["addon-asr-enabled"].disabled, true);
  assert.equal(elements["addon-asr-joint"].disabled, true);
  assert.equal(elements["addon-asr-active"].textContent, "addon.asr.activeModel:q4_k");
  assert.deepEqual(JSON.parse(requests.filter(request => request.url === "/api/addon-asr/setup").at(-1).options.body),
    { engineId: "haas_ja", modelId: "q8_0" });
  await elements["addon-asr-cancel"].handlers.click();
  assert.match(elements["addon-asr-status"].textContent, /cancelled/);
  assert.equal(elements["addon-asr-selection"].textContent, "addon.asr.selectedNeedsSetup:q8_0");
  assert.equal(elements["addon-asr-controls-target"].textContent, "addon.asr.controlsFor:q4_k");
  data.operationError = "HttpRequestException";
  await vm.runInContext("refreshAddonAsr()", context);
  assert.match(elements["addon-asr-status"].textContent, /HttpRequestException/);
  assert.equal(elements["addon-asr-active"].textContent, "addon.asr.activeModel:q4_k");
  assert.equal(hasHaas("rt-engine-select"), true, "a failed model switch must not hide the still-ready active model");
  assert.deepEqual(data.state, activeState);
  data.operationError = null;
  elements["addon-asr-model"].value = "haas_ja/q4_k";
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(elements["addon-asr-selection"].textContent, "addon.asr.selectedCurrent:q4_k");
  data.state.ready = false;
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(hasHaas("rt-engine-select"), false);
  assert.equal(elements["addon-asr-joint"].disabled, false, "an existing request can be cancelled before readiness");
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.installed");
  data.state.ready = true;
  await vm.runInContext("refreshAddonAsr()", context);
  await elements["addon-asr-enabled"].handlers.click();
  assert.equal(hasHaas("rt-engine-select"), false);
  assert.equal(elements["addon-asr-status"].textContent, "addon.asr.ready");
  // The manager reloads persisted enabled settings without a ready worker. Probe
  // completion must refresh the public language cache as well as the engine list.
  data = { ...data, routeAvailable: false, jointAvailable: false,
    state: { installed: true, ready: false, enabled: true, engineId: "haas_ja", modelId: "q4_k" } };
  await vm.runInContext("fetchRealtimeLanguages()", context);
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(vm.runInContext('getRealtimeLanguageOptions("haas_ja").length', context), 0);
  assert.equal(hasHaas("rt-engine-select"), false);
  await elements["addon-asr-probe"].handlers.click();
  assert.equal(hasHaas("rt-engine-select"), false, "an in-progress probe is not ready");
  data = { ...data, operationRunning: false, routeAvailable: true, jointAvailable: true,
    state: { ...data.state, ready: true } };
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(hasHaas("rt-engine-select"), true);
  assert.equal(elements["rt-engine-select"].value, "joint_auto", "probe must not select Haas automatically");
  elements["rt-engine-select"].value = "haas_ja";
  vm.runInContext("updateRealtimeLanguageOptions()", context);
  assert.deepEqual(elements["rt-language-select"].options.map(option => option.value), ["ja"]);
  assert.equal(vm.runInContext("getRealtimeSettings().language", context), "ja", "start request must use the supported language");
  await elements["addon-asr-enabled"].handlers.click();
  assert.equal(hasHaas("rt-engine-select"), false);
  assert.equal(vm.runInContext('getRealtimeLanguageOptions("haas_ja").length', context), 0, "disabled Haas must not publish languages");
  const previousData = structuredClone(data);
  for (const enabled of [true, false]) {
    data = { ...previousData, operationRunning: false, routeAvailable: false, jointAvailable: false,
      state: { ...previousData.state, ready: false, enabled, jointRequested: true } };
    await vm.runInContext("refreshAddonAsr()", context);
    assert.equal(elements["addon-asr-joint"].disabled, false, "revoking a stored request must not require readiness, activation or joint language eligibility");
    const selected = elements["rt-engine-select"].value;
    const start = requests.length;
    await elements["addon-asr-joint"].handlers.click();
    const mutations = requests.slice(start).filter(request => request.options?.method === "POST");
    assert.deepEqual(mutations.map(request => request.url), ["/api/addon-asr/joint"]);
    assert.deepEqual(JSON.parse(mutations[0].options.body), { jointRequested: false });
    assert.equal(data.state.enabled, enabled); assert.equal(data.state.jointRequested, false);
    assert.equal(elements["rt-engine-select"].value, selected, "cancellation must not change the selected main engine");
    assert.equal(elements["addon-asr-joint"].disabled, true, "starting a new request remains gated");
    data.state.jointRequested = true; data.operationRunning = true;
    await vm.runInContext("refreshAddonAsr()", context);
    assert.equal(elements["addon-asr-joint"].disabled, true, "an in-flight operation still blocks cancellation");
  }
  data = previousData;
  const previousEngine = elements["rt-engine-select"].value;
  for (const engine of ["parakeet_ja", "qwen3_asr_ja", "nemotron_3_5"]) {
    data = { ...data, routeAvailable: true, jointAvailable: true, operationRunning: false,
      state: { installed: true, ready: true, enabled: true, jointRequested: false, engineId: engine, modelId: "q8_0" } };
    await vm.runInContext("refreshAddonAsr()", context);
    for (const id of ["rt-engine-select", "bench-engine-select"]) {
      assert.equal(hasHaas(id), false, "new active engine must not enable Haas");
      const addons = elements[id].options.filter(option => ["haas_ja", "parakeet_ja", "qwen3_asr_ja", "nemotron_3_5"].includes(option.value));
      assert.deepEqual(addons.map(option => option.value), [engine]);
    }
    assert.equal(elements["rt-engine-select"].value, previousEngine, "setup and enable must not auto-select new engines");
    assert.equal(elements["addon-asr-hint-controls"].hidden, engine !== "qwen3_asr_ja");
    if (engine === "qwen3_asr_ja") {
      elements["addon-asr-language-mode"].value = "";
      assert.equal(vm.runInContext('addonHintSettings("qwen3_asr_ja").addonLanguageMode', context), "", "no default hint choice");
      elements["addon-asr-language-mode"].value = "auto";
      assert.equal(vm.runInContext('addonHintSettings("qwen3_asr_ja").addonLanguageMode', context), "auto");
      assert.equal(vm.runInContext('addonHintSettings("haas_ja").addonLanguageMode', context), undefined);
      assert.equal(vm.runInContext('addonHintSettings("joint_auto").addonLanguageMode', context), undefined, "explicit Joint opt-in required");
      data.state.jointRequested = true;
      await vm.runInContext("refreshAddonAsr()", context);
      assert.equal(vm.runInContext('addonHintSettings("joint_auto", "en").addonLanguageMode', context), undefined, "live non-JA fallback must not acquire a Qwen hint");
      assert.equal(vm.runInContext('addonHintSettings("joint_auto", "ja").addonLanguageMode', context), "auto");
      assert.equal(vm.runInContext('addonBenchSettings("joint_auto", "en").addonLanguageMode', context), "auto", "mixed manifest rows, not the initial language, determine participation");
      assert.equal(vm.runInContext('addonBenchSettings("joint_auto", "en").mode', context), "production_replay");
      assert.equal(vm.runInContext('addonBenchSettings("haas_ja", "ja").mode', context), undefined, "legacy Haas bench request stays unchanged");
    }
  }
  data.state = { present: true, installed: false, ready: false, error: "InvalidDataException" };
  await vm.runInContext("refreshAddonAsr()", context);
  assert.match(elements["addon-asr-status"].textContent, /^addon\.asr\.invalid/);
  missing = true;
  await vm.runInContext("refreshAddonAsr()", context);
  assert.equal(elements["addon-asr"].hidden, true);
  // The selector's explicit intent uses the existing Start flag, never mutates
  // hidden profiles, and survives a fresh settings read (including language follow).
  const clientSettings = new Map();
  context.localStorage = { getItem: key => clientSettings.get(key) ?? null,
    setItem: (key, value) => clientSettings.set(key, String(value)) };
  let prepareCount = 0;
  context.OnsetFillerUi = { prepare: () => { prepareCount++; } };
  assert.equal(vm.runInContext("getRealtimeSettings().useLanguageProfile", context), true, "old saved-profile behavior remains the default");
  const selectionStart = source.indexOf('  document.getElementById("rt-engine-select")?.addEventListener("change"');
  const selectionEnd = source.indexOf('  document.getElementById("rt-language-select")?.addEventListener("change"', selectionStart);
  assert.ok(selectionStart >= 0 && selectionEnd > selectionStart);
  const savedProfile = structuredClone(data.state), mutationCount = requests.length;
  vm.runInContext(source.slice(selectionStart, selectionEnd), context);
  elements["rt-engine-select"].value = "moonshine";
  elements["rt-engine-select"].handlers.change({ target: elements["rt-engine-select"] });
  const explicitSettings = vm.runInContext("getRealtimeSettings()", context);
  assert.equal(explicitSettings.engine, "moonshine");
  assert.equal(explicitSettings.useLanguageProfile, false, "hidden profile must not overwrite explicit base-engine selection");
  assert.equal(clientSettings.get("mobmate.useLanguageProfile"), "false");
  assert.equal(requests.length, mutationCount, "choosing a base engine must not change profiles or start capture");
  assert.deepEqual(data.state, savedProfile);
  elements["rt-engine-select"].value = "joint_auto";
  elements["rt-engine-select"].handlers.change({ target: elements["rt-engine-select"] });
  const nextSettings = vm.runInContext("getRealtimeSettings()", context);
  assert.equal(nextSettings.engine, "joint_auto", "the next Start reads the current base selection");
  assert.equal(nextSettings.useLanguageProfile, false, "a later Start retains explicit intent");
  assert.equal(explicitSettings.engine, "moonshine", "a previously captured Start is not rewritten by a later selection");
  assert.equal(prepareCount, 2, "each base-engine change retains the existing preparation call");
  assert.deepEqual(data.state, savedProfile, "subsequent base selections do not mutate the saved profile");
  checkCatalogOnlyLanguageLabel();
  assert.match(source.slice(0, source.indexOf("const uiSettings =")), /"mobmate.useLanguageProfile"/, "intent uses the existing persisted client settings store");
  const keys = [...source.matchAll(/addon\.asr\.[A-Za-z]+/g)].map(match => match[0]);
  const markup = fs.readFileSync("web/index.html", "utf8");
  keys.push(...[...markup.matchAll(/addon\.asr\.[A-Za-z]+/g)].map(match => match[0]),
    "ui.web.config.audioSettings", "ui.web.config.diagnostics", "ui.web.config.recognitionRuntime", "ui.web.config.distribution", "ui.web.realtime.activeEngine",
    ...["unsupported", "working", "ready", "enabled", "installed", "invalid", "notInstalled"].map(key => `addon.asr.${key}`));
  for (const lang of ["en", "ja", "ko", "zh_cn", "zh_tw"]) {
    const lines = fs.readFileSync(`libs/preset/_ui_${lang}.txt`, "utf8").split(/\r?\n/);
    for (const key of new Set(keys)) assert.equal(lines.filter(line => line.startsWith(`${key}=`) && line.length > key.length + 1).length, 1, `${lang}: ${key}`);
    const route = lines.find(line => line.startsWith("addon.asr.routePending="));
    assert.match(route, /JointAUTO/);
    assert.doesNotMatch(route, /not yet supported|未対応|지원하지|暂不支持|尚不支援/);
  }
  console.log("addon_asr_ui_regression: PASS");
})().catch(error => { console.error(error); process.exitCode = 1; });
