// Run from the repository root: node tests/MobMate.Core.Tests/settings_interaction_regression.js
// Uses real public handlers/client functions. Browser layout, native dialog keys and
// assistive technology still require the separate real-UI checks.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const source = fs.readFileSync("web/app.js", "utf8");
const html = fs.readFileSync("web/index.html", "utf8");
const section = (from, to) => {
  const start = source.indexOf(from), end = source.indexOf(to, start);
  assert.ok(start >= 0 && end > start, from);
  return source.slice(start, end);
};
const flush = async () => { for (let i = 0; i < 40; i++) await Promise.resolve(); };
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };

async function fixture(completed = "1", savedSettings = {}, options = {}) {
  const elements = {}, observers = [], requests = [], writes = [], timers = new Set();
  const initialSettings = new Map([["mobmate.captureDeviceId", "mic-old"], ["mobmate.ttsOutputDeviceId", "out-old"], ["mobmate.ttsMonitorVolume", "30"]]);
  for (const [key, value] of Object.entries(savedSettings)) initialSettings.set(key, value);
  if (completed !== null) initialSettings.set("mobmate.wizard.completed", completed);
  const rawStorage = new Map([["foreign.setting", "keep"]]), scheduledSaves = new Map(), pollTimers = new Map();
  for (const [key, value] of Object.entries(options.rawSettings || {})) rawStorage.set(key, value);
  let nextTimeout = 1;
  let document;
  class Element {
    constructor(tag = "div", id = "") {
      this.tagName = tag.toUpperCase(); this.id = id; this.children = []; this.handlers = {}; this.attrs = {};
      this.value = ""; this.textContent = ""; this.dataset = {}; this.style = {}; this.disabled = false; this.hidden = false;
      this.inert = false; this.open = false; this.isConnected = true; this.tabIndex = /^(BUTTON|SELECT|INPUT|TEXTAREA)$/.test(this.tagName) ? 0 : -1;
      const classes = new Set(); this.classList = { contains: name => classes.has(name), add: (...names) => names.forEach(n => classes.add(n)),
        remove: (...names) => names.forEach(n => classes.delete(n)), toggle: (name, force) => { const on = force ?? !classes.has(name); if (on) classes.add(name); else classes.delete(name); return on; } };
      if (id) elements[id] = this;
    }
    appendChild(child) { this.children.push(child); child.parent = this; return child; }
    prepend(child) { this.children.unshift(child); child.parent = this; }
    replaceChildren(...children) { this.children = []; for (const child of children) this.appendChild(child); }
    scrollIntoView() {}
    get options() { return this.children; }
    get selectedOptions() { return this.children.filter(el => el.value === this.value); }
    get innerHTML() { return this.children.length ? JSON.stringify(this.children.map(child => ({ value: child.value, textContent: child.textContent, dataset: child.dataset }))) : ""; }
    set innerHTML(value) {
      this.children = []; this.value = "";
      if (value) for (const data of JSON.parse(value)) { const option = new Element("option"); Object.assign(option, data); this.appendChild(option); }
    }
    setAttribute(name, value) { this.attrs[name] = value; if (name === "tabindex") this.tabIndex = Number(value); }
    getAttribute(name) { return this.attrs[name]; }
    removeAttribute(name) { delete this.attrs[name]; }
    contains(el) { return el === this || this.children.some(child => child.contains(el)); }
    closest(selector) { assert.equal(selector, "[inert]"); return this.inert ? this : this.parent?.closest(selector); }
    all() { return this.children.flatMap(el => [el, ...el.all()]); }
    querySelectorAll() { return this.all().filter(el => /^(BUTTON|SELECT|INPUT|TEXTAREA|A)$/.test(el.tagName) || el.attrs.tabindex !== undefined); }
    getClientRects() {
      for (let el = this; el; el = el.parent) {
        if (!el.isConnected || el.hidden || el.style.display === "none" ||
          (["settings-drawer", "setup-overlay"].includes(el.id) && !el.classList.contains("open")) ||
          (el.classList.contains("setup-step") && !el.classList.contains("active")) || (el.tagName === "DIALOG" && !el.open)) return [];
      }
      return [{}];
    }
    focus() { document.activeElement = this; }
    addEventListener(name, handler, options) { (this.handlers[name] ||= []).push({ handler, once: options?.once }); }
    async fire(name, extra = {}) {
      const handlers = [...(this.handlers[name] || [])];
      this.handlers[name] = (this.handlers[name] || []).filter(item => !item.once);
      await Promise.all(handlers.map(item => item.handler({ target: this, ...extra })));
      if (name === "change" && this.onchange) await this.onchange({ target: this });
    }
    click() { if (!this.disabled && !this.hidden) void this.fire("click"); }
    dispatchEvent(event) { void this.fire(event.type); return !event.defaultPrevented; }
    add(option) { this.appendChild(option); }
    showModal() { this.open = true; }
    close(value) { if (value !== undefined) this.returnValue = value; this.open = false; return this.fire("close"); }
  }
  const body = new Element("body");
  document = { body, activeElement: body, handlers: {}, getElementById: id => elements[id] || null, createElement: tag => new Element(tag),
    addEventListener(name, handler) { (this.handlers[name] ||= []).push(handler); },
    querySelector: selector => selector === "dialog[open]" ? (elements["speaker-reset-dialog"].open ? elements["speaker-reset-dialog"] : null)
      : selector === ".setup-step.active" ? body.all().find(el => el.classList.contains("setup-step") && el.classList.contains("active")) : null,
    dispatchEvent(event) { for (const handler of this.handlers[event.type] || []) handler(event); return !event.defaultPrevented; },
    querySelectorAll: selector => selector === ".acc-head" ? [] : body.all().filter(el => el.classList.contains(selector.slice(1))) };
  const add = (id, tag = "div", parent = body) => parent.appendChild(new Element(tag, id));
  const main = add("main"); add("menu-btn", "button", main); add("main-control", "button", main);
  add("rt-filler-enabled", "input", main); add("rt-filler-status", "p", main);
  add("setup-mic-warning", "div", main).hidden = true; add("setup-mic-warning-text", "span", elements["setup-mic-warning"]); add("setup-mic-reopen", "button", elements["setup-mic-warning"]);
  const previouslyInert = add("previously-inert"); previouslyInert.inert = true;
  const drawer = add("settings-drawer");
  for (const id of ["drawer-title", "drawer-backdrop", "drawer-wizard-btn", "drawer-discord-btn", "drawer-close-btn", "speaker-reset-btn", "speaker-reset-status"])
    add(id, id.endsWith("btn") ? "button" : "div", drawer);
  elements["drawer-title"].setAttribute("tabindex", "-1");
  elements["speaker-reset-status"].setAttribute("tabindex", "-1");
  const dialog = add("speaker-reset-dialog", "dialog", drawer); add("speaker-reset-cancel", "button", dialog);
  const overlay = add("setup-overlay");
  for (const id of ["setup-title", "setup-backdrop", "setup-back-btn", "setup-next-btn", "setup-skip-btn", "setup-later-btn", "setup-close-btn"])
    add(id, id.endsWith("btn") ? "button" : "div", overlay);
  elements["setup-title"].setAttribute("tabindex", "-1");
  for (let step = 0; step < 4; step++) {
    const el = add(`step-${step}`, "section", overlay); el.classList.add("setup-step"); el.attrs["data-step"] = String(step); el.dataset.step = String(step);
    add(`setup-step-title-${step}`, "h3", el).setAttribute("tabindex", "-1");
    const dot = add(`dot-${step}`, "span", overlay); dot.classList.add("setup-dot"); dot.attrs["data-step"] = String(step);
  }
  for (const [id, tag, step] of [["setup-mic-select", "select", 0], ["setup-mic-test", "button", 0], ["setup-mic-retry-stop", "button", 0], ["setup-mic-result", "div", 0], ["setup-mic-meter", "div", 0],
    ["setup-va-status", "div", 1], ["setup-va-install", "button", 1], ["setup-va-rescan", "button", 1], ["setup-out-select", "select", 2],
    ["setup-out-play", "button", 2], ["setup-out-result", "div", 2], ["setup-out-trouble", "div", 2], ["setup-discord-btn", "button", 3]]) add(id, tag, elements[`step-${step}`]);
  for (const id of ["rt-device-select", "tts-output-device-select"]) {
    const el = add(id, "select", main);
    for (const value of id === "rt-device-select" ? ["mic-old", "mic-new"] : ["out-old", "out-new"]) { const opt = new Element("option"); opt.value = value; el.appendChild(opt); }
    el.value = el.options[0].value;
  }
  add("tts-device-status", "div", main);
  for (const [id, tag] of [["rt-status", "div"], ["rt-device-status", "div"], ["rt-start-btn", "button"], ["rt-stop-btn", "button"]]) add(id, tag, main);
  const state = { reset: { success: true, adaptiveVocabReset: false }, resetHttpOk: true, resetMalformed: false, configOk: true, configTransportError: false, configResponses: [], virtualResponses: [], ttsStatusResponses: [], ttsAppliedDevice: "out-old", ttsAppliedConfig: { engine: "voicevox", voicevoxSpeakerId: "3", outputDeviceId: "out-old" }, speak: { success: true }, speakTransportError: false, holdConfig: null, holdReset: null, holdStart: null, holdDevices: null, transportError: false,
    devices: [{ id: "mic-old" }, { id: "mic-new" }], deviceResponses: [], ttsDeviceResponses: [], startResponses: [], stopResponses: [], holdStop: null, statusResponses: [], restartResponses: [],
    saveOk: options.saveOk ?? true, saves: [], uiSettingsReadFailures: options.uiSettingsReadFailures ?? 0,
    uiSettingsMalformedReads: options.uiSettingsMalformedReads ?? 0,
    serverDocument: { settings: Object.fromEntries(initialSettings), browserMigrationCompleted: options.browserMigrationCompleted ?? true } };
  const context = vm.createContext({ document, fixtureState: state, console: { info() {}, error() {}, warn() {} },
    URL, Response, AbortSignal, Event, KeyboardEvent: class extends Event { constructor(type, options) { super(type, options); this.key = options.key; } },
    Option: class extends Element { constructor(text, value) { super("option"); this.textContent = text; this.value = value; } },
    location: { href: "http://127.0.0.1:5000/", origin: "http://127.0.0.1:5000" },
    MutationObserver: class { constructor(cb) { this.cb = cb; observers.push(this); } observe() { this.active = true; } disconnect() { this.active = false; } },
    t: key => key, setText: (id, text) => { if (elements[id]) elements[id].textContent = text; },
    updateVoicevoxInstallUi() {}, refreshAddonAsr() {}, linuxCoreOnly: () => true,
    isMobfyMode: () => false, getRealtimeSettings: () => ({ engine: "joint_auto", language: "ja" }), apiMessage: () => "fixture failure",
    realtimePollToken: 0, vcRunning: false, vcPendingStarts: 0, stopMobfyDirectForModeSwitch: async () => {}, setRecordingButtons() {}, resetRealtimeLiveUi() {},
    setRealtimeWasRecording() {}, setRealtimeGuide() {}, clearNoiseWarmupUi() {}, startNoiseWarmupUi() {}, meterStart() {}, spkStart() {}, meterStop() {}, spkStop() {}, waveClear() {}, pollRealtime() {},
    window: { localStorage: { getItem: key => rawStorage.get(key) ?? null, setItem: (key, value) => rawStorage.set(key, String(value)), removeItem: key => rawStorage.delete(key) }, open() { throw Error("unexpected external action"); } },
    setTimeout: (callback, delay) => { const id = nextTimeout++; (delay === 100 ? pollTimers : scheduledSaves).set(id, callback); return id; }, clearTimeout: id => { scheduledSaves.delete(id); pollTimers.delete(id); },
    setInterval: () => { const id = timers.size + 1; timers.add(id); return id; }, clearInterval: id => timers.delete(id),
    fetch: async (url, options) => {
      requests.push({ url, options });
      if (url === "/api/ui-settings") {
        if (options?.method === "PUT") {
          const body = JSON.parse(options.body); state.saves.push(body);
          if (state.saveOk) state.serverDocument = body;
          return { ok: state.saveOk, status: state.saveOk ? 200 : 500 };
        }
        if (state.uiSettingsReadFailures > 0) { state.uiSettingsReadFailures--; return { ok: false, status: 503 }; }
        if (state.uiSettingsMalformedReads > 0) { state.uiSettingsMalformedReads--; return { ok: true, json: async () => ({ settings: [] }) }; }
        return { ok: true, json: async () => structuredClone(state.serverDocument) };
      }
      if (url === "/api/speaker/reset") { if (state.holdReset) await state.holdReset.promise; if (state.transportError) throw Error("lost response"); return { ok: state.resetHttpOk, json: async () => { if (state.resetMalformed) throw SyntaxError("bad JSON"); return state.reset; } }; }
      if (url === "/api/system/restart") {
        const response = state.restartResponses.shift();
        if (response?.transportError) throw Error("restart response lost");
        return { ok: response?.httpOk !== false, status: response?.httpOk === false ? 503 : 200,
          json: async () => { if (response?.malformed) throw SyntaxError("bad restart JSON"); return response?.body ?? { ok: true, steps: [] }; } };
      }
      if (url === "/api/audio/input-devices") {
        if (state.holdDevices) await state.holdDevices.promise;
        let response = state.deviceResponses.shift();
        if (response?.promise) response = await response.promise;
        if (response?.transportError) throw Error("device enumeration transport failure");
        return { ok: response?.httpOk ?? true, status: response?.httpOk === false ? 503 : 200,
          json: async () => { if (response?.malformed) throw SyntaxError("bad device JSON"); return response?.body ?? { devices: state.devices }; } };
      }
      if (url === "/api/tts/devices") {
        const response = state.ttsDeviceResponses.shift();
        if (response?.transportError) throw Error("TTS device enumeration failed");
        return { ok: true, json: async () => { if (response?.malformed) throw SyntaxError("bad device JSON"); return response?.body ?? { devices: [{ id: "out-old", name: "Old" }, { id: "out-new", name: "New" }] }; } };
      }
      if (url === "/api/virtual-audio/status") return { ok: true, json: async () => ({ installed: true }) };
      if (url === "/api/virtual-audio/apply") {
        let response = state.virtualResponses.shift();
        if (response?.promise) response = await response.promise;
        if (response?.transportError) throw Error("virtual apply response lost");
        const body = response?.body ?? { ok: true, outputDeviceId: "out-old" };
        if (body.ok === true && typeof body.outputDeviceId === "string") state.ttsAppliedDevice = body.outputDeviceId;
        return { ok: response?.httpOk !== false, status: response?.httpOk === false ? 500 : 200, json: async () => body };
      }
      if (url === "/api/realtime/start") {
        if (state.holdStart) await state.holdStart.promise;
        const response = state.startResponses.shift();
        if (response?.transportError) throw Error("Start response lost");
        return { ok: response?.httpOk ?? true, status: response?.httpOk === false ? 503 : 200, json: async () => response?.body ?? { ok: true } };
      }
      if (url === "/api/realtime/stop" || url === "/api/realtime/stop?discard=true") {
        if (state.holdStop) await state.holdStop.promise;
        const response = state.stopResponses.shift();
        if (response?.transportError) throw Error("stop response lost");
        return { ok: response?.httpOk ?? true, status: response?.httpOk === false ? 503 : 200, json: async () => { if (response?.malformed) throw SyntaxError("bad Stop JSON"); return response?.body ?? { ok: true }; } };
      }
      if (url === "/api/realtime/status") {
        let response = state.statusResponses.shift();
        if (response?.promise) response = await response.promise;
        if (response?.transportError) throw Error("status response lost");
        return { ok: true, json: async () => response ?? { state: "recording", captureRms: 0 } };
      }
      if (url === "/api/tts/config") {
        let response = state.configResponses.shift();
        if (state.holdConfig) await state.holdConfig.promise;
        if (response?.promise) response = await response.promise;
        if (state.configTransportError || response?.transportError) throw Error("configuration response lost");
        const ok = response?.httpOk ?? state.configOk;
        const body = response?.body ?? { ok: true };
        const sent = JSON.parse(options.body);
        if (ok && (body.ok === true || body.code === "tts.config_applied")) {
          Object.assign(state.ttsAppliedConfig, sent);
          if (Object.hasOwn(sent, "outputDeviceId")) state.ttsAppliedDevice = sent.outputDeviceId;
        }
        return { ok, status: ok ? 200 : 500, json: async () => { if (response?.malformed) throw SyntaxError("bad config JSON"); return body; } };
      }
      if (url === "/api/tts/status") {
        let response = state.ttsStatusResponses.shift();
        if (response?.promise) response = await response.promise;
        if (response?.transportError) throw Error("status response lost");
        return { ok: response?.httpOk !== false, status: response?.httpOk === false ? 503 : 200,
          json: async () => { if (response?.malformed) throw SyntaxError("bad status JSON"); return response?.body ?? { outputDeviceId: state.ttsAppliedDevice }; } };
      }
      if (url === "/api/tts/speak") { if (state.speakTransportError) throw Error("playback response lost"); return { ok: true, json: async () => state.speak }; }
      throw Error(`unexpected request: ${url}`);
    } });
  // In a browser these are the same global properties. Preserve that alias for
  // the real-page guard's short synchronous timer-capture window.
  for (const name of ["fetch", "setTimeout", "clearTimeout"]) Object.defineProperty(context.window, name, {
    get: () => context[name], set: value => { context[name] = value; }, configurable: true,
  });
  vm.runInContext(section("const rawLocalStorage =", "let currentMode ="), context);
  await vm.runInContext("uiSettings.initialize()", context);
  const client = vm.runInContext("uiSettings", context);
  const originalSet = client.setItem, originalRemove = client.removeItem;
  client.setItem = (key, value) => { writes.push(key); originalSet(key, value); };
  client.removeItem = key => { writes.push(`remove:${key}`); originalRemove(key); };
  const storage = { get: key => client.getItem(key), has: key => client.getItem(key) !== null };
  const drainSaves = async () => {
    while (scheduledSaves.size) {
      const callbacks = [...scheduledSaves.values()]; scheduledSaves.clear();
      for (const callback of callbacks) await callback();
    }
  };
  for (const code of [section("function selectedDeviceName(", "function vcCleanDeviceName("),
    section("async function startVoiceConversionRequest(", "function vcIsSupportedCatalogModel("),
    section("function saveRealtimeSettings(", "function restoreRealtimeSettings("),
    section("const AsrAcceptanceCue =", "async function fetchRealtimeStatus("),
    section("async function startRealtime(", "async function stopRealtime("),
    section("async function stopRealtime(", "// The drawer and setup wizard"),
    section("function vcGetOptions(", "async function startMobfyVoice("),
    section("async function startMobfyVoice(", "async function stopMobfyVoice("),
    section("function vcCleanDeviceName(", "function showLegacyWindowsCpuOnlyNotice("),
    section("// The drawer and setup wizard", "async function pollRealtime(token)"),
    section("/* --- TTS output confirmation --- */", "async function setMonitorVolume("),
    section("const SETUP_STEP_COUNT = 4;", "// Piper Voice DL")]) vm.runInContext(code, context);
  vm.runInContext("initAppChrome(); initSetupWizard();", context);
  elements["rt-device-select"].addEventListener("change", vm.runInContext("captureInputSelectionChanged", context));
  return { elements, storage, rawStorage, writes, requests, timers, state, document, scheduledSaves, pollTimers, drainSaves,
    run: code => vm.runInContext(code, context), notify: () => observers.filter(o => o.active).forEach(o => o.cb()) };
}

// The real wizard, realtime client and complete add-on module share one global
// context, as they do in the page. Only the HTTP boundary is a private fake.
async function wizardAddonFixture() {
  const f = await fixture(), e = f.elements;
  const ids = {
    "rt-language-select": "select", "addon-asr-wrap": "div", "addon-asr-trigger": "button",
    "addon-asr-search": "input", "addon-asr-list": "div", "addon-asr-model": "select",
    "addon-asr-label": "span", "addon-asr-profile-language": "span", "addon-asr-setup": "button",
    "addon-asr-save-profile": "button", "addon-asr-probe": "button", "addon-asr-enabled": "button",
    "addon-asr-joint": "button", "addon-asr-cancel": "button", "addon-asr-active": "span",
    "addon-asr-controls-target": "span", "addon-asr-resident": "span", "addon-asr-evaluation": "span",
    "addon-asr-runtime-warning": "span", "addon-asr-selection": "span", "addon-asr-hint-controls": "div",
    "addon-asr-status": "span", "addon-asr-language-mode": "select"
  };
  for (const [id, tag] of Object.entries(ids)) {
    const node = f.document.createElement(tag); node.id = id; e[id] = node; e.main.appendChild(node);
  }
  e["rt-language-select"].value = "ja";
  e["addon-asr-language-mode"].value = "requested";
  f.run(`
    globalThis.formatBytes = () => "";
    globalThis.addonAsrCatalog = []; globalThis.addonAsrModelTouched = false;
    globalThis.addonAsrEnabled = false; globalThis.addonAsrJointRequested = false; globalThis.addonAsrActiveEngine = null;
    globalThis.updateAddonAsrDetails = () => {}; globalThis.confirm = () => true;
    globalThis.fixtureAddon = { posts: [], reads: [], holdRead: null, holdPost: null, failProfile: false,
      profiles: { ja: { engineId: "qwen3_asr_ja", modelId: "q8", enabled: false, jointRequested: false, languageMode: "requested" } } };
    const originalFetchForAddon = fetch;
    globalThis.fetch = async (url, options = {}) => {
      if (!url.startsWith("/api/addon-asr")) return originalFetchForAddon(url, options);
      if (options.method === "POST") {
        const body = options.body ? JSON.parse(options.body) : {};
        fixtureAddon.posts.push({ url, body });
        if (fixtureAddon.holdPost && url.endsWith('/' + (fixtureAddon.holdPost.action || 'profile'))) {
          const held = fixtureAddon.holdPost; fixtureAddon.holdPost = null; held.entered.resolve(); await held.release.promise;
        }
        if (url.endsWith('/profile') && fixtureAddon.failProfile) return { ok: false, status: 500, json: async () => ({ code: 'fixture.save_failure' }) };
        if (url.endsWith('/install')) {
          fixtureAddon.operationError = fixtureAddon.failInstall ? 'HttpRequestException' : null;
          if (!fixtureAddon.failInstall) fixtureAddon.installed = true;
        }
        if (url.endsWith('/profile')) fixtureAddon.profiles[body.language] = { ...body, enabled: false, jointRequested: false };
        return { ok: true, json: async () => ({}) };
      }
      const lang = new URL(url, 'http://fixture').searchParams.get('language') || 'ja';
      fixtureAddon.reads.push(lang);
      if (fixtureAddon.holdRead && fixtureAddon.holdRead.language === lang) {
        const held = fixtureAddon.holdRead; fixtureAddon.holdRead = null; held.entered.resolve(); await held.release.promise;
      }
      const desired = fixtureAddon.profiles[lang];
      return { ok: true, json: async () => ({ runtimeSupported: fixtureAddon.supported !== false, setupAvailable: fixtureAddon.supported !== false,
        operationError: fixtureAddon.operationError, profiles: fixtureAddon.profiles,
        engines: [{ id: 'qwen3_asr_ja', platform: 'linux-x64', runtime: { id: 'speech', bytes: 1 },
          models: [{ id: 'q8', name: 'Qwen', languages: ['ja','en'], license: 'Apache', bytes: 1, installed: fixtureAddon.installed !== false }] }],
        state: { installed: !!desired && fixtureAddon.installed !== false, ready: !!desired && fixtureAddon.ready !== false && fixtureAddon.installed !== false && fixtureAddon.supported !== false,
          enabled: !!desired?.enabled, jointRequested: !!desired?.jointRequested },
        languageState: { language: lang, desired } }) };
    };
  `);
  f.run(fs.readFileSync("web/addon-language.js", "utf8") + "\nglobalThis.AddonLanguageUi = window.AddonLanguageUi;");
  f.run("AddonLanguageUi.init()"); await f.run("AddonLanguageUi.refresh()");
  return f;
}

function linuxMonitorShutdownBindingRegression() {
  const code = fs.readFileSync("src/MobMate.App.Linux/Program.cs", "utf8");
  const shutdown = code.slice(code.indexOf("// ---- Shutdown ----"), code.indexOf("void TryOpenWebUiInBrowser("));
  assert.match(shutdown, /linuxMonitorLoopback\.RequestStop\(\)/, "shutdown prevents new monitor configuration");
  assert.match(shutdown, /linuxMonitorStopping\.Cancel\(\)/, "shutdown cancels in-flight monitor configuration");
  assert.match(shutdown, /linuxMonitorLoopback\.StopWithinAsync\(linuxMonitorGate, TimeSpan\.FromSeconds\(2\)\)/, "owned monitor cleanup has a bounded shared-gate wait");
  const cleanup = shutdown.indexOf("linuxMonitorLoopback.StopWithinAsync(");
  for (const operation of ["linuxMonitorLoopback.RequestStop()", "linuxMonitorStopping.Cancel()", "realtimeSessionService.Dispose()", "ttsRouter.Dispose()", "primaryTts.Dispose()"]) {
    assert.ok(shutdown.indexOf(operation) >= 0 && shutdown.indexOf(operation) < cleanup, `${operation} precedes monitor cleanup`);
  }
  assert.match(code, /CreateLinkedTokenSource\(ct, linuxMonitorStopping\.Token\)/, "settings inherit shutdown cancellation");
  const restart = code.slice(code.indexOf('app.MapPost("/api/system/restart"'), code.indexOf("(bool available, string? error, List<object> speakers) ProbeVoicevox()"));
  assert.match(restart, /linuxMonitorGate\.WaitAsync\(monitorRequest\.Token\)/, "restart cannot queue monitor work after shutdown");
  assert.match(restart, /UnloadAsync\(monitorRequest\.Token\)/, "restart monitor cleanup is cancellable");
  const loopback = fs.readFileSync("src/MobMate.App.Linux/LinuxMonitorLoopback.cs", "utf8");
  assert.match(loopback, /ct\.Register\(TerminateChild\)/, "cancellation signals the owned child");
  const cancelledChild = loopback.slice(loopback.indexOf("catch (OperationCanceledException) when (ct.IsCancellationRequested)", loopback.indexOf("RunChildAsync")));
  assert.ok(cancelledChild.indexOf("TerminateChild();") >= 0 && cancelledChild.indexOf("TerminateChild();") < cancelledChild.indexOf("cancelChild.Dispose();"), "the await continuation cannot unregister cancellation before requesting child termination");
}

async function linuxMonitorRestartUiRegression() {
  const f = await fixture(), button = f.document.createElement("button");
  button.id = "system-restart-btn"; f.elements[button.id] = button; f.elements["settings-drawer"].appendChild(button);
  f.run(section("  // Linux: system soft-restart", "  // Phase 7A: Speaker ID reset"));
  button.textContent = "restart";
  await button.fire("click");
  assert.equal(button.textContent, "✓", "confirmed soft-restart retains the established success indication");
  await f.drainSaves();
  for (const response of [
    { body: { ok: false, steps: ["monitor loopback failed: linux_monitor_volume_set_failed"] } },
    { body: { ok: false, error: "pactl unavailable", steps: [] } },
    { body: {} }, { malformed: true }, { transportError: true }
  ]) {
    f.state.restartResponses.push(response);
    await button.fire("click");
    assert.equal(button.textContent, "ui.web.system.restartFailed", "HTTP 200 or an unconfirmed response is not successful audio reinitialization");
    await f.drainSaves();
    assert.equal(button.disabled, false, "the user can retry after a failed or unknown outcome");
    assert.equal(button.textContent, "restart");
  }
  f.state.restartResponses.push({ httpOk: false, body: { ok: true } });
  await button.fire("click");
  assert.equal(button.textContent, "HTTP 503", "HTTP failure retains the existing indication even with a successful body");
  await f.drainSaves();
  assert.equal(f.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "restart feedback does not request capture or playback");
  const key = "ui.web.system.restartFailed=";
  const translations = ["en", "ja", "ko", "zh_cn", "zh_tw"].map(locale =>
    fs.readFileSync(`libs/preset/_ui_${locale}.txt`, "utf8").split(/\r?\n/).find(line => line.startsWith(key))?.slice(key.length));
  assert.ok(translations.every(Boolean), "restart failure is translated in all five locales");
  assert.equal(new Set(translations).size, 5, "restart failure translations are not copied text");
  console.log("Linux monitor UI regression passed: shutdown binding, confirmed restart, monitor failure and unknown response; no audio.");
}

async function onsetFillerRegression() {
  assert.match(html, /id="rt-filler-enabled"[^>]*type="checkbox"/);
  assert.doesNotMatch(html.match(/<input id="rt-filler-enabled"[^>]*>/)[0], /\bchecked\b/);
  const fallbackHint = html.match(/<p id="rt-filler-hint"[^>]*>(.*?)<\/p>/)[1];
  assert.doesNotMatch(fallbackHint, /Piper/, "untranslated fallback must not advertise excluded fillers");
  assert.match(fallbackHint, /自動準備/);
  assert.match(fallbackHint, /初期ON/);
  assert.match(fallbackHint, /Start/);
  assert.doesNotMatch(fallbackHint, /ずんだもん|固定声/);
  const fallbackDetails = html.match(/<p class="hint" data-i18n="ui\.web\.onsetFiller\.details">(.*?)<\/p>/)[1];
  assert.match(fallbackDetails, /MOSS/, "untranslated fallback includes the selected MOSS voice");
  assert.doesNotMatch(html, /rt-filler-consent|onsetFiller\.consent/, "normal capture uses no extra route consent UI");
  const createFillerFixture = async (saved = {}, options = {}) => {
  const f = await fixture("1", saved, options);
  for (const [id, tag] of [["rt-filler-enabled", "input"], ["rt-filler-status", "p"], ["rt-language-select", "select"], ["rt-engine-select", "select"]]) {
    if (!f.elements[id]) {
      const node = f.document.createElement(tag); node.id = id; f.elements[id] = node; f.elements.main.appendChild(node);
    }
  }
  f.run(`
    globalThis.fillerFixture = { enabled: false, prepared: 0, posts: [], prepareConfigs: [], enabledConfigs: [], startApplications: [], controlGeneration: 0, fail: false, playbackReason: 'off' };
    const originalFillerFetch = fetch;
    globalThis.fetch = async (url, options = {}) => {
      if (url === '/api/realtime/filler') {
        if (fillerFixture.fail) return { ok: false, status: 404 };
        if (options.method === 'POST') {
          const body = JSON.parse(options.body); fillerFixture.posts.push(body);
          if (body.enabled === false && fillerFixture.failNextOff) {
            fillerFixture.failNextOff = false;
            return { ok: false, status: 503 };
          }
          if (body.enabled === false && fillerFixture.cancelUnconfirmedNextOff) {
            fillerFixture.cancelUnconfirmedNextOff = false;
            fillerFixture.enabled = false; fillerFixture.prepared = 0; fillerFixture.preparedSource = -1;
            return { ok: true, json: async () => ({ enabled: false, prepared: 0, reason: 'cancel-unconfirmed' }) };
          }
          const generation = typeof body.enabled === 'boolean' ? ++fillerFixture.controlGeneration : fillerFixture.controlGeneration;
          if (fillerFixture.hold) { const hold = fillerFixture.hold; fillerFixture.hold = null; await hold; }
          if (typeof body.enabled === 'boolean' && generation === fillerFixture.controlGeneration) {
            fillerFixture.enabled = body.enabled;
            if (body.enabled) fillerFixture.enabledConfigs.push({ ...fixtureState.ttsAppliedConfig });
            if (!body.enabled) { fillerFixture.prepared = 0; fillerFixture.preparedSource = -1; }
          }
          if (!Object.hasOwn(body, 'enabled') && body.language) {
            fillerFixture.prepareConfigs.push({ ...fixtureState.ttsAppliedConfig });
            if (fillerFixture.completePreparation &&
                !(fillerFixture.prepared > 0 && fillerFixture.preparedLanguage === body.language)) {
              fillerFixture.prepared = 5;
              fillerFixture.preparedLanguage = body.language;
              fillerFixture.preparedSource = fillerFixture.posts.indexOf(body);
            }
          }
          if (body.inputChanged) fillerFixture.playbackReason = 'input-changed';
        }
        return { ok: true, json: async () => ({ enabled: fillerFixture.enabled, prepared: fillerFixture.prepared, reason: 'fixture', playbackReason: fillerFixture.playbackReason }) };
      }
      if (url === '/api/realtime/start') {
        // Both hosts capture the control generation before session creation and
        // bind only after that work completes. OFF retires an in-flight Start.
        const generation = fillerFixture.controlGeneration;
        const response = await originalFillerFetch(url, options), body = await response.json();
        if (response.ok && body?.ok !== false) {
          fillerFixture.enabled = JSON.parse(options.body).fillerEnabled === true && generation === fillerFixture.controlGeneration;
          fillerFixture.startApplications.push({ enabled: fillerFixture.enabled, config: { ...fixtureState.ttsAppliedConfig } });
          fillerFixture.playbackReason = 'waiting-onset';
        }
        return { ...response, json: async () => body };
      }
      if (url === '/api/realtime/stop') { fillerFixture.enabled = false; fillerFixture.playbackReason = 'off'; }
      return originalFillerFetch(url, options);
    };
    globalThis.normalizeRealtimeEngine = value => value;
    globalThis.normalizeRealtimeLanguage = value => value;
    globalThis.normalizeSetting = value => value;
    globalThis.ensureJointAutoEngineOption = () => {}; globalThis.ensureSafeAutoEngineOption = () => {};
    globalThis.ensureParaformerZhEngineOption = () => {}; globalThis.uiLanguageToRealtimeLanguage = () => 'ja';
    globalThis.addonHintSettings = () => ({});
    globalThis.defaultRealtimeEngine = 'joint_auto';
    globalThis.restoreTtsSettingsAfterRead = async () =>
      (await postTtsConfig({ engine: 'voicevox', voicevoxSpeakerId: '3', outputDeviceId: currentTtsOutputDevice() })).confirmed;
    globalThis.REALTIME_SCENES = ['auto']; globalThis.REALTIME_PREFILTERS = ['auto'];
    globalThis.t = key => key.endsWith('onsetFiller.ready') ? '{count}/5' : key.endsWith('onsetFiller.readyOff') ? 'OFF {count}/5' : key;
    globalThis.Audio = class { constructor() { throw Error('filler must not play browser audio'); } };
  `);
  f.run(section("function getRealtimeSettings()", "function saveRealtimeSettings("));
  f.run(section("function restoreRealtimeSettings()", "function updateRealtimeLanguageOptions("));
  f.run("globalThis.fillerUiUnderTest = OnsetFillerUi; globalThis.updateRealtimeLanguageOptions = () => {}; globalThis.TTS_ENGINE = { MOSS: 'moss' };");
  f.run(source.split(/\r?\n/).find(line => line.includes('document.getElementById("rt-device-select").addEventListener("change"')));
  f.run(section('  document.getElementById("rt-engine-select")?.addEventListener("change"', '  document.getElementById("text-filter-ng-mode")?.addEventListener'));
  f.elements["rt-language-select"].value = "ja";
  f.elements["rt-engine-select"].value = "joint_auto";
  await f.run("restoreRealtimeSettings()"); await flush();
  if (f.run("uiSettings.isReady()") && !options.deferInitialTtsConfig) {
    await f.run("postTtsConfig({ engine: 'voicevox', voicevoxSpeakerId: '3', outputDeviceId: currentTtsOutputDevice() })"); await flush();
  }
  return f;
  };
  const f = await createFillerFixture();
  const toggle = f.elements["rt-filler-enabled"];
  assert.equal(f.run("getRealtimeSettings().fillerEnabled"), true, "unconfigured defaults ON");
  assert.equal(f.run("localStorage.getItem('mobmate.fillerEnabled')"), null, "default does not overwrite the saved preference");
  assert.equal(f.run("fillerFixture.enabled"), false, "default ON does not enable a stopped session");
  assert.equal(f.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "restore and prewarm never start capture or playback");
  const defaultStart = await createFillerFixture();
  await defaultStart.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  assert.equal(JSON.parse(defaultStart.requests.find(r => r.url === "/api/realtime/start").options.body).fillerEnabled, true, "normal Start uses the default ON preference");
  await defaultStart.run("RealtimeSessionClient.stopRealtimeSession('main')"); await flush();
  assert.equal(defaultStart.elements["rt-filler-enabled"].checked, true, "Stop retains the default ON preference");
  assert.equal(defaultStart.run("localStorage.getItem('mobmate.fillerEnabled')"), null, "Stop does not save a changed preference");
  for (const [before, after] of [[true, false], [false, true]]) {
    const queued = await createFillerFixture({ "mobmate.fillerEnabled": String(before) });
    queued.state.holdStop = deferred();
    const stopping = queued.run("RealtimeSessionClient.stopRealtimeSession('mode-switch')"); await flush();
    const starting = queued.run("startRealtime()"); await flush();
    assert.equal(queued.requests.filter(r => r.url === "/api/realtime/start").length, 0, "main Start is still in the client queue");
    queued.elements["rt-filler-enabled"].checked = after;
    await queued.elements["rt-filler-enabled"].fire("change"); await flush();
    queued.state.holdStop.resolve(); await Promise.all([stopping, starting]); await flush(); await flush();
    assert.equal(queued.run("fillerFixture.enabled"), after, "the queued Start must respect the latest preference");
    if (!after) assert.equal(JSON.parse(queued.requests.find(r => r.url === "/api/realtime/start").options.body).fillerEnabled, false,
      "the retired ON must not be sent after OFF has already reached the host");
    await queued.drainSaves();
    assert.equal(queued.state.serverDocument.settings["mobmate.fillerEnabled"], String(after));
  }
  const initialPending = await createFillerFixture({}, { deferInitialTtsConfig: true });
  assert.equal(initialPending.elements["rt-filler-enabled"].checked, true, "the initial ON preference remains visible while TTS is unconfirmed");
  await initialPending.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  assert.equal(JSON.parse(initialPending.requests.find(r => r.url === "/api/realtime/start").options.body).fillerEnabled, false,
    "Start before the initial TTS configuration cannot enable the old voice");
  await initialPending.run("postTtsConfig({ engine: 'voicevox', voicevoxSpeakerId: '3', outputDeviceId: 'out-old' })"); await flush(); await flush();
  assert.equal(initialPending.run("fillerFixture.enabled"), true, "the initial ON preference takes effect after the TTS configuration is confirmed");
  const newerOutput = await createFillerFixture({}, { deferInitialTtsConfig: true });
  const heldVoice = deferred(); newerOutput.state.configResponses.push(heldVoice);
  const initialVoice = newerOutput.run("postTtsConfig({ engine: 'voicevox', voicevoxSpeakerId: '3', outputDeviceId: 'out-old' })"); await flush();
  await newerOutput.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  const outputChange = newerOutput.run("setTtsOutputDevice('out-new')"); await flush();
  heldVoice.resolve({ body: { ok: true } }); await Promise.all([initialVoice, outputChange]); await flush(); await flush();
  assert.equal(newerOutput.run("fillerFixture.enabled"), true, "a newer output can combine with the confirmed voice after both requests finish");
  assert.equal(newerOutput.run("fillerFixture.enabledConfigs.at(-1).outputDeviceId"), "out-new");
  const explicitOff = await createFillerFixture({ "mobmate.fillerEnabled": "false" });
  assert.equal(explicitOff.elements["rt-filler-enabled"].checked, false, "explicit saved OFF overrides the default");
  assert.equal(explicitOff.run("fillerFixture.enabled"), false, "restored OFF does not enable a session");
  assert.ok(explicitOff.run("fillerFixture.prepareConfigs.length") > 0, "confirmed voice preparation remains available with a saved OFF preference");
  assert.equal(explicitOff.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "restored OFF does not start capture or playback");
  const explicitOn = await createFillerFixture({ "mobmate.fillerEnabled": "true" });
  assert.equal(explicitOn.elements["rt-filler-enabled"].checked, true, "explicit saved ON is retained");
  assert.equal(explicitOn.run("fillerFixture.enabled"), false, "restored ON does not enable a stopped session");
  assert.equal(explicitOn.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "restored ON does not start capture or playback");
  const unavailable = await createFillerFixture(
    { "mobmate.fillerEnabled": "false", "mobmate.uiLanguage": "ja", "mobmate.mode": "mobfy" }, { uiSettingsReadFailures: 10 });
  assert.equal(unavailable.run("getRealtimeSettings().fillerEnabled"), false, "unknown saved preference does not use the missing-setting default");
  assert.equal(unavailable.elements["rt-filler-status"].textContent, "ui.web.onsetFiller.settingsUnavailable", "unknown is not presented as saved OFF");
  unavailable.run("globalThis.currentMode = 'stt'; globalThis.updateModeSpecificSections = () => {}; globalThis.updateMainActionLabels = () => {};");
  unavailable.run(section("function switchMode(", "function updateModeSpecificSections("));
  unavailable.run("switchMode('stt', { skipExclusion: true })");
  assert.equal(unavailable.run("uiSettings.isKnown('mobmate.mode')"), false, "passive startup mode is not an explicit user change");
  const uiLanguage = unavailable.document.createElement("select"); uiLanguage.id = "ui-language-select";
  unavailable.elements[uiLanguage.id] = uiLanguage; unavailable.elements.main.appendChild(uiLanguage);
  unavailable.run("globalThis.normalizeUiLanguage = value => value; globalThis.setTextFilterStatus = () => {};");
  unavailable.run(section("function applyTextFilterConfig(", "async function fetchTextFilterConfig("));
  unavailable.run("applyTextFilterConfig({ uiLanguage: 'en' }); localStorage.setItem('mobmate.ttsOutputDeviceId', 'out-new')");
  assert.equal(unavailable.run("uiSettings.isKnown('mobmate.uiLanguage')"), false, "passive text-filter configuration cannot replace an unread UI language");
  await unavailable.drainSaves();
  assert.equal(unavailable.state.saves.length, 0, "startup and unrelated edits never PUT a partially loaded document");
  assert.equal(unavailable.state.serverDocument.settings["mobmate.fillerEnabled"], "false", "saved OFF survives a failed read");
  assert.equal(unavailable.state.serverDocument.settings["mobmate.uiLanguage"], "ja", "other saved preferences survive a failed read");
  unavailable.state.uiSettingsReadFailures = 0;
  await unavailable.run("fillerUiUnderTest.refresh()"); await flush(); await unavailable.drainSaves();
  assert.equal(unavailable.elements["rt-filler-enabled"].checked, false, "recovery restores saved OFF");
  assert.equal(unavailable.state.serverDocument.settings["mobmate.fillerEnabled"], "false", "recovered save preserves OFF");
  assert.equal(unavailable.state.serverDocument.settings["mobmate.uiLanguage"], "ja", "recovered save preserves other server values");
  assert.equal(unavailable.state.serverDocument.settings["mobmate.mode"], "mobfy", "recovered save preserves the unread startup mode");
  assert.equal(unavailable.state.serverDocument.settings["mobmate.ttsOutputDeviceId"], "out-new", "edits made while unavailable survive recovery");
  assert.equal(unavailable.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "read failure and recovery do not start capture or playback");
  const unavailableOn = await createFillerFixture({ "mobmate.fillerEnabled": "true" }, { uiSettingsReadFailures: 10 });
  assert.equal(unavailableOn.run("getRealtimeSettings().fillerEnabled"), false, "unknown ON is silent before it can be restored");
  unavailableOn.state.uiSettingsReadFailures = 0;
  await unavailableOn.run("fillerUiUnderTest.refresh()"); await flush(); await unavailableOn.drainSaves();
  assert.equal(unavailableOn.elements["rt-filler-enabled"].checked, true, "recovery restores saved ON");
  assert.equal(unavailableOn.run("fillerFixture.enabled"), false, "restoring saved ON alone does not enable a stopped session");
  assert.equal(unavailableOn.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "recovery of ON does not start capture or playback");
  const otherClient = await createFillerFixture({ "mobmate.fillerEnabled": "false" }, { uiSettingsReadFailures: 10 });
  otherClient.run("fillerFixture.enabled = true");
  const otherClientPosts = otherClient.run("fillerFixture.posts.length");
  otherClient.state.uiSettingsReadFailures = 0;
  await otherClient.run("fillerUiUnderTest.refresh()"); await flush();
  assert.equal(otherClient.run("fillerFixture.enabled"), true, "recovery does not disable another client's active filler");
  assert.equal(otherClient.run(`fillerFixture.posts.slice(${otherClientPosts}).some(body => 'enabled' in body)`), false, "restoration does not control an unowned session");
  const changedWhileUnavailable = await createFillerFixture({ "mobmate.fillerEnabled": "false" }, { uiSettingsReadFailures: 10 });
  changedWhileUnavailable.elements["rt-filler-enabled"].checked = true;
  await changedWhileUnavailable.elements["rt-filler-enabled"].fire("change"); await flush(); await changedWhileUnavailable.drainSaves();
  assert.equal(changedWhileUnavailable.state.saves.length, 0, "explicit changes wait for a complete server read");
  changedWhileUnavailable.state.uiSettingsReadFailures = 0;
  await changedWhileUnavailable.run("fillerUiUnderTest.refresh()"); await flush(); await changedWhileUnavailable.drainSaves();
  assert.equal(changedWhileUnavailable.elements["rt-filler-enabled"].checked, true, "the user's newer ON choice wins over restored OFF");
  assert.equal(changedWhileUnavailable.state.serverDocument.settings["mobmate.fillerEnabled"], "true", "the newer choice is saved after recovery");
  const malformed = await createFillerFixture({ "mobmate.fillerEnabled": "false" }, { uiSettingsMalformedReads: 10 });
  assert.equal(malformed.run("getRealtimeSettings().fillerEnabled"), false, "a malformed settings document is unknown, not an empty preference");
  malformed.run("localStorage.setItem('mobmate.mode', 'stt')"); await malformed.drainSaves();
  assert.equal(malformed.state.saves.length, 0, "a malformed read cannot authorize a partial settings PUT");
  assert.equal(malformed.state.serverDocument.settings["mobmate.fillerEnabled"], "false", "malformed reads preserve saved OFF");
  const createTtsSelectionFixture = async (linux, options = {}, savedOverrides = {}) => {
      const selection = await createFillerFixture({
        "mobmate.ttsEngine": "moss", "mobmate.mossModelVoice": "moss-model|Soyo",
        "mobmate.piperPlusModel": "chosen-model", "mobmate.fillerEnabled": "false", ...savedOverrides
      }, options);
      for (const id of ["tts-engine-select", "tts-voicevox-speaker-select", "tts-piper-model-select", "tts-moss-model-select", "tts-monitor-volume-select"]) {
        const select = selection.document.createElement("select"); select.id = id;
        const append = select.appendChild.bind(select);
        select.appendChild = option => {
          const first = select.options.length === 0;
          const wasSelected = option.selected === true;
          const result = append(option);
          let selected = false;
          Object.defineProperty(option, "selected", {
            get: () => selected,
            set: value => { selected = value; if (value) select.value = option.value; },
            configurable: true
          });
          if (first) select.value = option.value; // Browser selects the first option.
          if (wasSelected) option.selected = true;
          return result;
        };
        selection.elements[id] = select; selection.elements.main.appendChild(select);
      }
      const engineSelect = selection.elements["tts-engine-select"];
      for (const value of ["piper_plus", "moss", "voicevox"]) {
        const option = selection.document.createElement("option"); option.value = value; engineSelect.appendChild(option);
      }
      for (const value of ["0", "30", "60"]) {
        const option = selection.document.createElement("option"); option.value = value;
        selection.elements["tts-monitor-volume-select"].appendChild(option);
      }
      for (const [id, tag] of [["tts-status", "div"], ["wav-output-enabled", "input"]]) {
        const element = selection.document.createElement(tag); element.id = id;
        selection.elements[id] = element; selection.elements.main.appendChild(element);
      }
      selection.run(`
        globalThis.linuxCoreOnly = () => ${linux};
        globalThis.engineFixture = {
          engines: [{ id: 'piper_plus' }, { id: 'moss' }, { id: 'voicevox' }],
          voicevoxSpeakers: [{ id: '3', name: 'Other' }],
          piperPlusModels: [{ id: 'default-model' }, { id: 'chosen-model' }]
        };
        globalThis.TTS_ENGINE = { AUTO: 'auto', PIPER: 'piper', PIPER_PLUS: 'piper_plus', MOSS: 'moss', KOKORO: 'kokoro', VOICEVOX: 'voicevox' };
        globalThis._kokoroDlRenderList = () => {}; globalThis._kokoroPopulateModelSelect = () => {}; globalThis.refreshRealtimeGuide = () => {};
        globalThis._mossCatalog = [{ id: 'moss-model', name: 'MOSS', installed: true, voices: [{ id: 'Ava' }, { id: 'Soyo' }] }];
        const previousEngineFetch = fetch;
        globalThis.fetch = async (url, options) => {
          if (url !== '/api/tts/engines') return previousEngineFetch(url, options);
          const hold = engineFixture.hold; engineFixture.hold = null;
          if (hold) await hold;
          return { ok: true, json: async () => engineFixture };
        };
      `);
      selection.run(section("// The prompt's native language", "async function _mossDlStart("));
      selection.run(section("async function fetchTtsOutputDevices()", "function setVirtualAudioPopoverOpen("));
      selection.run(section("async function setMonitorVolume(", "/* --- TTS Engine Selection --- */"));
      selection.run(section("/* --- TTS Engine Selection --- */", "/* --- TTS Speak body builder"));
      selection.run(section('  document.getElementById("tts-output-device-select")?.addEventListener("change"',
        '  document.getElementById("virtual-audio-help-btn")?.addEventListener("click"'));
      selection.run(section('  document.getElementById("tts-engine-select")?.addEventListener("change"',
        '  document.getElementById("ui-language-select")?.addEventListener("change"'));
      return selection;
  };
  const configBodies = f => f.requests.filter(r => r.url === "/api/tts/config").map(r => JSON.parse(r.options.body));
  const holdMossCatalog = f => f.run(`
    globalThis.delayedMossCatalog = _mossCatalog; _mossCatalog = [];
    globalThis.initPiperVoiceDl = async () => {};
    globalThis.initKokoroUi = async () => {};
    globalThis.initMossUi = async () => {
      await new Promise(resolve => { globalThis.releaseMossCatalog = resolve; });
      _mossCatalog = delayedMossCatalog; _mossPopulateModelSelect();
    };
  `);
  const startTtsStartup = f => f.run("globalThis.mossStartupDone = false; globalThis.mossStartupCompletion = (async () => {" +
    section("  const piperVoiceDlReady = initPiperVoiceDl();", "  await fetchConfig();") +
    section("  await kokoroUiReady;", "  fetchNgWords();") + "mossStartupDone = true; })();");
  for (const linux of [false, true]) {
    for (const malformedRead of [false, true]) {
    for (const explicitlyChanged of [false, true]) {
      const selection = await createTtsSelectionFixture(linux, {
        uiSettingsReadFailures: malformedRead ? 0 : 10, uiSettingsMalformedReads: malformedRead ? 10 : 0,
        rawSettings: { "mobmate.ttsOutputDeviceId": "out-new", "mobmate.ttsMonitorVolume": "0" }
      });
      const engineSelect = selection.elements["tts-engine-select"];
      await selection.run("fetchTtsEngines()"); await flush();
      assert.equal(configBodies(selection).length, 0, "unread settings cannot auto-apply a provisional engine, voice, output or monitor");
      await selection.run("fetchTtsOutputDevices()"); await flush();
      assert.equal(configBodies(selection).length, 0, "unverified browser fallback cannot auto-apply an output or monitor");
      assert.equal(selection.run("uiSettings.isKnown('mobmate.ttsEngine')"), false, "passive engine enumeration is not an explicit choice");
      assert.equal(selection.run("uiSettings.isKnown('mobmate.piperPlusModel')"), false, "passive model enumeration is not an explicit choice");
      if (explicitlyChanged) {
        engineSelect.value = "piper_plus"; await engineSelect.fire("change"); await flush();
        assert.deepEqual(configBodies(selection).at(-1), { engine: "piper_plus" }, "an explicit engine change must not send unread selections");
        selection.elements["tts-piper-model-select"].value = "default-model";
        await selection.elements["tts-piper-model-select"].fire("change"); await flush();
        assert.deepEqual(configBodies(selection).at(-1), { engine: "piper_plus", piperPlusModelId: "default-model" },
          "an explicit model change must not send an unread output or monitor");
      }
      selection.elements["tts-monitor-volume-select"].value = "60";
      await selection.elements["tts-monitor-volume-select"].fire("change"); await flush();
      assert.deepEqual(configBodies(selection).at(-1), { monitorVolumePercent: 60 }, "an explicit monitor change preserves an unread output");
      assert.equal(selection.state.ttsAppliedDevice, "out-old", "a partial monitor request does not change the host output");
      await selection.drainSaves();
      assert.equal(selection.state.saves.length, 0, "enumeration and explicit edits cannot PUT an unread document");
      selection.state.uiSettingsReadFailures = 0;
      selection.state.uiSettingsMalformedReads = 0;
      await selection.run("fillerUiUnderTest.refresh()"); await flush(); await flush(); await selection.drainSaves(); await flush();
      assert.equal(selection.state.serverDocument.settings["mobmate.ttsEngine"], explicitlyChanged ? "piper_plus" : "moss",
        "recovery preserves the saved engine unless the user selected another");
      assert.equal(selection.state.serverDocument.settings["mobmate.piperPlusModel"], explicitlyChanged ? "default-model" : "chosen-model",
        "recovery preserves the saved model unless the user selected another");
      assert.equal(selection.state.serverDocument.settings["mobmate.ttsMonitorVolume"], "60", "unrelated explicit edits survive recovery");
      assert.equal(selection.state.serverDocument.settings["mobmate.fillerEnabled"], "false", "enumeration and recovery retain saved filler OFF");
      const recovered = configBodies(selection).at(-1);
      assert.equal(recovered.engine, explicitlyChanged ? "piper_plus" : "moss", "recovery applies the restored or explicitly selected engine");
      assert.equal(recovered.outputDeviceId, "out-old", "recovery applies the saved output, not the unread browser fallback");
      assert.equal(recovered.monitorVolumePercent, 60, "recovery applies the newer explicit monitor choice");
      if (explicitlyChanged) assert.equal(recovered.piperPlusModelId, "default-model");
      else {
        assert.equal(recovered.mossModelId, "moss-model");
        assert.equal(recovered.mossVoiceId, "Soyo", `recovery restores the saved MOSS voice rather than the provisional first voice (${linux}, ${malformedRead}; ${selection.elements["tts-moss-model-select"].value})`);
      }
      assert.equal(selection.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "enumeration and recovery stay silent");
    }
    }
    const optionalMoss = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "3" });
    holdMossCatalog(optionalMoss); startTtsStartup(optionalMoss);
    await flush(); await flush();
    assert.equal(optionalMoss.run("mossStartupDone"), true, "a pending optional MOSS catalog cannot block unrelated startup");
    assert.equal(configBodies(optionalMoss).at(-1).engine, "voicevox", "VOICEVOX is configured while the MOSS catalog is pending");
    assert.equal(configBodies(optionalMoss).at(-1).voicevoxSpeakerId, "3");
    assert.equal(optionalMoss.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false);

    for (const preferred of [false, true]) {
      const lateMoss = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
        { "mobmate.fillerEnabled": String(preferred) });
      holdMossCatalog(lateMoss); startTtsStartup(lateMoss);
      await flush(); await flush();
      assert.equal(lateMoss.run("mossStartupDone"), true, "selected MOSS also leaves unrelated startup unblocked");
      assert.equal(lateMoss.run("fillerFixture.enabled"), false, "MOSS remains silent until its catalog is ready");
      assert.equal(configBodies(lateMoss).some(body => body.engine === "moss"), false,
        "the unconfirmed saved MOSS voice is not applied");
      lateMoss.run("releaseMossCatalog()"); await lateMoss.run("mossStartupCompletion"); await flush(); await flush();
      assert.equal(configBodies(lateMoss).at(-1).engine, "moss", "startup applies MOSS after its delayed catalog is available");
      assert.equal(configBodies(lateMoss).at(-1).mossVoiceId, "Soyo", "startup retains the saved prompt voice");
      assert.ok(lateMoss.run("fillerFixture.prepareConfigs.length") > 0, "the selected voice is prewarmed after catalog readiness");
      assert.equal(lateMoss.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "catalog readiness alone does not capture or play");
      await lateMoss.run("startRealtime()"); await flush();
      assert.equal(lateMoss.run("fillerFixture.enabled"), preferred, "normal Start respects ON and OFF after delayed MOSS initialization");

      const pendingMossSpeech = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
        { "mobmate.fillerEnabled": String(preferred) });
      holdMossCatalog(pendingMossSpeech); startTtsStartup(pendingMossSpeech);
      await pendingMossSpeech.run("mossStartupCompletion"); await pendingMossSpeech.run("startRealtime()"); await flush();
      pendingMossSpeech.run("engineFixture.hold = new Promise(resolve => { globalThis.releaseMossSpeechEngines = resolve; });");
      pendingMossSpeech.run("releaseMossCatalog()"); await flush(); await flush();
      for (const [id, tag] of [["ts-input", "textarea"], ["ts-send-btn", "button"], ["ts-wrap", "div"], ["ts-status", "div"], ["ts-counter", "span"], ["tts-text-input", "input"]]) {
        const element = pendingMossSpeech.document.createElement(tag); element.id = id;
        pendingMossSpeech.elements[id] = element; pendingMossSpeech.elements.main.appendChild(element);
      }
      pendingMossSpeech.run(section("/* --- TTS Speak body builder", "/* --- TTS Speak --- */"));
      pendingMossSpeech.run(section("/* --- TTS Speak --- */", "async function ttsStop()"));
      pendingMossSpeech.elements["ts-input"].value = "Wait for the selected MOSS voice";
      pendingMossSpeech.elements["tts-text-input"].value = "Wait for the selected MOSS voice";
      assert.equal(pendingMossSpeech.run("isSelectedMossUnavailable()"), false, "catalog readiness confirms availability, not the host's selected voice");
      assert.equal(pendingMossSpeech.run("OnsetFillerUi.isVoiceConfirmed()"), false);
      assert.equal(pendingMossSpeech.state.ttsAppliedConfig.engine, "voicevox", "the old host engine has not been replaced yet");
      await pendingMossSpeech.run("textSpeechSend()");
      pendingMossSpeech.state.speak = { success: false }; // Do not enter the ordinary button's fake completion timer if it incorrectly sends.
      await pendingMossSpeech.run("ttsSpeak()");
      assert.equal(pendingMossSpeech.requests.some(r => r.url === "/api/tts/speak"), false,
        "both manual speech controls must wait for the available MOSS voice to reach the host");
      assert.equal(pendingMossSpeech.elements["ts-input"].value, "Wait for the selected MOSS voice", "unconfirmed voice refusal retains the draft");
      assert.match(pendingMossSpeech.elements["ts-status"].className, /err/);
      assert.match(pendingMossSpeech.elements["tts-status"].className, /err/);
      await pendingMossSpeech.run("setMonitorVolume(30)"); await flush();
      await pendingMossSpeech.run("textSpeechSend()"); await pendingMossSpeech.run("ttsSpeak()");
      assert.equal(pendingMossSpeech.requests.some(r => r.url === "/api/tts/speak"), false,
        "output-only confirmation cannot authorize the old host voice after catalog readiness");
      assert.equal(pendingMossSpeech.run("fillerFixture.enabled"), false);
      pendingMossSpeech.run("releaseMossSpeechEngines()"); await flush(); await flush(); await flush();
      assert.equal(pendingMossSpeech.state.ttsAppliedConfig.engine, "moss");
      assert.equal(pendingMossSpeech.state.ttsAppliedConfig.mossVoiceId, "Soyo");
      assert.equal(pendingMossSpeech.run("fillerFixture.enabled"), preferred, "voice confirmation respects saved filler ON and OFF");
      assert.equal(pendingMossSpeech.requests.some(r => r.url === "/api/tts/speak"), false, "voice confirmation never resends a blocked draft");
      assert.equal(pendingMossSpeech.requests.filter(r => r.url === "/api/realtime/start").length, 1, "voice confirmation does not restart capture");
      pendingMossSpeech.state.speak = { success: true };
      await pendingMossSpeech.run("textSpeechSend()");
      assert.equal(pendingMossSpeech.requests.filter(r => r.url === "/api/tts/speak").length, 1, "an explicit retry uses the confirmed MOSS voice");
      assert.equal(pendingMossSpeech.elements["ts-input"].value, "");
      pendingMossSpeech.state.speak = { success: false };
      await pendingMossSpeech.run("ttsSpeak()");
      assert.equal(pendingMossSpeech.requests.filter(r => r.url === "/api/tts/speak").length, 2, "the ordinary button also recovers after confirmation");
      await pendingMossSpeech.drainSaves();
      assert.equal(pendingMossSpeech.state.serverDocument.settings["mobmate.fillerEnabled"], String(preferred));
    }
    const switchedDuringMoss = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.fillerEnabled": "true", "mobmate.voicevoxSpeaker": "3" });
    holdMossCatalog(switchedDuringMoss); startTtsStartup(switchedDuringMoss);
    await switchedDuringMoss.run("mossStartupCompletion"); await flush();
    await switchedDuringMoss.run("startRealtime()"); await flush();
    assert.equal(switchedDuringMoss.run("fillerFixture.enabled"), false, "pending MOSS starts without filler");
    switchedDuringMoss.elements["tts-engine-select"].value = "voicevox";
    await switchedDuringMoss.elements["tts-engine-select"].fire("change"); await flush(); await flush();
    assert.equal(configBodies(switchedDuringMoss).at(-1).engine, "voicevox");
    const configsBeforeMossArrival = configBodies(switchedDuringMoss).length;
    switchedDuringMoss.run("releaseMossCatalog()"); await flush(); await flush();
    assert.equal(configBodies(switchedDuringMoss).length, configsBeforeMossArrival,
      "a late MOSS catalog must not reconfigure a newer VOICEVOX selection");
    assert.equal(switchedDuringMoss.run("fillerFixture.enabledConfigs.at(-1).engine"), "voicevox");

    const selectedDuringMoss = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "3", "mobmate.fillerEnabled": "true" });
    holdMossCatalog(selectedDuringMoss); startTtsStartup(selectedDuringMoss);
    await selectedDuringMoss.run("mossStartupCompletion"); await selectedDuringMoss.run("startRealtime()"); await flush();
    selectedDuringMoss.elements["tts-engine-select"].value = "moss";
    await selectedDuringMoss.elements["tts-engine-select"].fire("change"); await flush();
    assert.equal(selectedDuringMoss.run("fillerFixture.enabled"), false, "a newly selected MOSS voice stays silent before catalog readiness");
    selectedDuringMoss.run("releaseMossCatalog()"); await flush(); await flush();
    assert.equal(configBodies(selectedDuringMoss).at(-1).engine, "moss");
    assert.equal(configBodies(selectedDuringMoss).at(-1).mossVoiceId, "Soyo", "late readiness applies the current selected voice");
    assert.equal(selectedDuringMoss.run("fillerFixture.enabledConfigs.at(-1).engine"), "moss");

    const mossDuringEngineFetch = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true });
    holdMossCatalog(mossDuringEngineFetch);
    mossDuringEngineFetch.run("engineFixture.hold = new Promise(resolve => { globalThis.releaseEngineCatalog = resolve; });");
    startTtsStartup(mossDuringEngineFetch);
    await flush(); mossDuringEngineFetch.run("releaseMossCatalog()"); await flush();
    mossDuringEngineFetch.run("releaseEngineCatalog()"); await mossDuringEngineFetch.run("mossStartupCompletion"); await flush(); await flush();
    assert.equal(configBodies(mossDuringEngineFetch).filter(body => body.engine === "moss").length, 1,
      "catalog arrival during engine enumeration does not reapply an already confirmed voice");

    const unreadDuringMoss = await createTtsSelectionFixture(linux,
      { deferInitialTtsConfig: true, uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "false" });
    holdMossCatalog(unreadDuringMoss); startTtsStartup(unreadDuringMoss);
    await unreadDuringMoss.run("mossStartupCompletion"); unreadDuringMoss.run("releaseMossCatalog()"); await flush(); await flush();
    assert.equal(configBodies(unreadDuringMoss).length, 0, "late catalog readiness cannot apply unread settings");
    unreadDuringMoss.state.uiSettingsReadFailures = 0;
    await unreadDuringMoss.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    assert.equal(configBodies(unreadDuringMoss).at(-1).mossVoiceId, "Soyo", "settings recovery applies the saved MOSS voice");
    assert.equal(unreadDuringMoss.state.serverDocument.settings["mobmate.fillerEnabled"], "false");

    const normal = await createTtsSelectionFixture(linux);
    normal.elements["tts-monitor-volume-select"].value = "30";
    await normal.run("fetchTtsOutputDevices()"); await normal.run("fetchTtsEngines()"); await flush();
    const body = configBodies(normal).at(-1);
    assert.equal(body.engine, "moss");
    assert.equal(body.mossModelId, "moss-model");
    assert.equal(body.mossVoiceId, "Soyo");
    assert.equal(body.outputDeviceId, "out-old");
    assert.equal(body.monitorVolumePercent, 30);
    assert.equal(normal.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "normal restoration does not capture or play");
    const earlySpeech = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26" });
    earlySpeech.elements["tts-engine-select"].value = "auto"; // HTML value before saved engine restoration.
    for (const [id, tag] of [["ts-input", "textarea"], ["ts-send-btn", "button"], ["ts-wrap", "div"], ["ts-status", "div"], ["ts-counter", "span"]]) {
      const element = earlySpeech.document.createElement(tag); element.id = id;
      earlySpeech.elements[id] = element; earlySpeech.elements.main.appendChild(element);
    }
    earlySpeech.run(section("/* --- TTS Speak body builder", "/* --- TTS Speak --- */"));
    earlySpeech.elements["ts-input"].value = "Wait for my saved voice";
    await earlySpeech.run("textSpeechSend()");
    assert.equal(earlySpeech.requests.some(r => r.url === "/api/tts/speak"), false,
      "the initial HTML Auto value must not route manual speech before the saved VOICEVOX voice is restored");
    assert.equal(earlySpeech.elements["ts-input"].value, "Wait for my saved voice");

    const missingSpeaker = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26", "mobmate.fillerEnabled": "true" });
    await missingSpeaker.run("fetchTtsEngines()"); await flush(); await flush();
    assert.equal(missingSpeaker.storage.get("mobmate.voicevoxSpeaker"), "26", "an unavailable saved style must stay saved");
    assert.equal(missingSpeaker.elements["tts-voicevox-speaker-select"].value, "26");
    assert.equal(missingSpeaker.elements["tts-voicevox-speaker-select"].selectedOptions[0].disabled, true);
    assert.equal(configBodies(missingSpeaker).some(b => b.engine === "voicevox" || b.voicevoxSpeakerId === "3"), false,
      "restoration must not apply the first different VOICEVOX style");
    assert.equal(missingSpeaker.run("fillerFixture.prepareConfigs.length"), 0, "the old host voice must not be prewarmed");
    await missingSpeaker.run("startRealtime()"); await flush();
    assert.equal(missingSpeaker.run("fillerFixture.enabled"), false, "an unavailable saved style must stay silent after Start");
    for (const [id, tag] of [["ts-input", "textarea"], ["ts-send-btn", "button"], ["ts-wrap", "div"], ["ts-status", "div"], ["ts-counter", "span"]]) {
      const element = missingSpeaker.document.createElement(tag); element.id = id;
      missingSpeaker.elements[id] = element; missingSpeaker.elements.main.appendChild(element);
    }
    missingSpeaker.run(section("/* --- TTS Speak body builder", "/* --- TTS Speak --- */"));
    missingSpeaker.elements["ts-input"].value = "Retain the selected voice";
    await missingSpeaker.run("textSpeechSend()");
    assert.equal(missingSpeaker.requests.some(r => r.url === "/api/tts/speak"), false,
      "manual Text Speech must not use the old host voice while the saved style is unavailable");
    assert.equal(missingSpeaker.elements["ts-input"].value, "Retain the selected voice");
    const regularInput = missingSpeaker.document.createElement("input"); regularInput.id = "tts-text-input";
    missingSpeaker.elements[regularInput.id] = regularInput; missingSpeaker.elements.main.appendChild(regularInput);
    missingSpeaker.run(section("/* --- TTS Speak --- */", "async function ttsStop()"));
    regularInput.value = "Keep the selected voice";
    await missingSpeaker.run("ttsSpeak()");
    assert.equal(missingSpeaker.requests.some(r => r.url === "/api/tts/speak"), false,
      "the ordinary TTS button must also refuse the unavailable saved style");
    assert.match(missingSpeaker.elements["tts-status"].className, /err/);
    missingSpeaker.run("engineFixture.voicevoxSpeakers.push({ id: '26', name: 'Restored' })");
    await missingSpeaker.run("fetchTtsEngines()"); await flush(); await flush();
    assert.equal(missingSpeaker.elements["tts-voicevox-speaker-select"].value, "26");
    assert.equal(configBodies(missingSpeaker).at(-1).voicevoxSpeakerId, "26", "re-enumeration restores the same saved style");
    assert.equal(missingSpeaker.run("fillerFixture.enabledConfigs.at(-1).voicevoxSpeakerId"), "26");
    assert.ok(missingSpeaker.run("fillerFixture.prepareConfigs.length") > 0, "recovered voice is prepared after its configuration is confirmed");
    assert.ok(missingSpeaker.run("fillerFixture.prepareConfigs.every(config => config.voicevoxSpeakerId === '26')"),
      "preparation after recovery must use the restored style");
    await missingSpeaker.run("textSpeechSend()");
    assert.equal(missingSpeaker.requests.filter(r => r.url === "/api/tts/speak").length, 1,
      "manual speech recovers after the selected style is restored");

    const missingAfterReadFailure = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26", "mobmate.fillerEnabled": "true" });
    await missingAfterReadFailure.run("fetchTtsEngines()"); await missingAfterReadFailure.run("startRealtime()");
    missingAfterReadFailure.state.uiSettingsReadFailures = 0;
    await missingAfterReadFailure.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    assert.equal(missingAfterReadFailure.storage.get("mobmate.voicevoxSpeaker"), "26");
    assert.equal(missingAfterReadFailure.elements["tts-voicevox-speaker-select"].selectedOptions[0].disabled, true);
    assert.equal(configBodies(missingAfterReadFailure).some(b => b.voicevoxSpeakerId === "3"), false,
      "recovery from an unread document cannot apply the earlier provisional style");
    assert.equal(missingAfterReadFailure.run("fillerFixture.prepareConfigs.length"), 0);
    assert.equal(missingAfterReadFailure.run("fillerFixture.enabled"), false);

    const speakerDisappeared = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26", "mobmate.fillerEnabled": "true" });
    speakerDisappeared.run("engineFixture.voicevoxSpeakers.push({ id: '26', name: 'Saved' })");
    await speakerDisappeared.run("fetchTtsEngines()"); await speakerDisappeared.run("startRealtime()"); await flush();
    assert.equal(speakerDisappeared.run("fillerFixture.enabled"), true);
    const priorPreparations = speakerDisappeared.run("fillerFixture.prepareConfigs.length");
    speakerDisappeared.run("engineFixture.voicevoxSpeakers = [{ id: '3', name: 'Other' }]");
    await speakerDisappeared.run("fetchTtsEngines()"); await flush(); await flush();
    assert.equal(speakerDisappeared.run("fillerFixture.enabled"), false, "a disappeared style retires the active filler");
    assert.equal(speakerDisappeared.run("fillerFixture.prepareConfigs.length"), priorPreparations,
      "a disappeared style cannot prepare the old or first other voice");
    assert.equal(speakerDisappeared.storage.get("mobmate.voicevoxSpeaker"), "26");

    const stalePreparation = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26", "mobmate.fillerEnabled": "true" });
    stalePreparation.run("engineFixture.voicevoxSpeakers.push({ id: '26', name: 'Saved' })");
    await stalePreparation.run("fetchTtsEngines()"); await stalePreparation.run("startRealtime()"); await flush();
    stalePreparation.run("fillerFixture.completePreparation = true; fillerFixture.hold = new Promise(resolve => { globalThis.releaseStaleStyle = resolve; })");
    const oldPreparation = stalePreparation.run("fillerUiUnderTest.prepare()"); await flush();
    const priorOffs = stalePreparation.run("fillerFixture.posts.filter(body => body.enabled === false).length");
    stalePreparation.run("engineFixture.voicevoxSpeakers = [{ id: '3', name: 'Other' }]");
    await stalePreparation.run("fetchTtsEngines()"); await flush();
    assert.equal(stalePreparation.run("fillerFixture.enabled"), false, "style loss immediately retires active permission");
    stalePreparation.run("releaseStaleStyle()"); await oldPreparation; await flush(); await flush();
    assert.equal(stalePreparation.run("fillerFixture.prepared"), 0, "late old preparation is retired after style loss");
    assert.ok(stalePreparation.run("fillerFixture.posts.filter(body => body.enabled === false).length") >= priorOffs + 2,
      "a final OFF follows the older preparation when no newer choice has superseded it");

    for (const [language, choice, changeOutput = true] of [
      ["ja", "on"], ["en", "on"], ["en", "off"], ["en", "off-then-on"],
      ["ja", "fail-then-retry"], ["ja", "cancel-unconfirmed-then-retry"], ["en", "on", false]
    ]) {
      const nextSession = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
        { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26", "mobmate.fillerEnabled": "true" });
      nextSession.run("engineFixture.voicevoxSpeakers.push({ id: '26', name: 'Saved' })");
      await nextSession.run("fetchTtsEngines()"); await nextSession.run("startRealtime()"); await flush();
      await nextSession.run("fillerUiUnderTest.prepare()"); await flush();
      nextSession.run("fillerFixture.completePreparation = true; fillerFixture.hold = new Promise(resolve => { globalThis.releasePriorSession = resolve; })");
      const oldPrepare = nextSession.run("fillerUiUnderTest.prepare()"); await flush();
      const oldPrepareSource = nextSession.run("fillerFixture.posts.length - 1");
      const heldConfig = deferred();
      let changingOutput;
      if (changeOutput) {
        nextSession.state.configResponses.push(heldConfig);
        changingOutput = nextSession.run("setTtsOutputDevice('out-new')"); await flush();
      }
      await nextSession.run("RealtimeSessionClient.stopRealtimeSession('main')");
      if (changeOutput) { heldConfig.resolve({ body: { ok: true } }); await changingOutput; await flush(); }
      if (language !== "ja") {
        nextSession.elements["rt-language-select"].value = language;
        await nextSession.elements["rt-language-select"].fire("change"); await flush();
      }
      await nextSession.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
      assert.equal(nextSession.run("fillerFixture.enabled"), false, "the next session stays silent until old preparation is retired");
      assert.equal(JSON.parse(nextSession.requests.filter(r => r.url === "/api/realtime/start").at(-1).options.body).fillerEnabled, false,
        "Start is not blocked, but does not enable an old preparation");
      if (choice === "off" || choice === "off-then-on") {
        nextSession.elements["rt-filler-enabled"].checked = false;
        await nextSession.elements["rt-filler-enabled"].fire("change"); await flush();
      }
      if (choice === "off-then-on") {
        nextSession.elements["rt-filler-enabled"].checked = true;
        await nextSession.elements["rt-filler-enabled"].fire("change"); await flush();
        assert.equal(nextSession.run("fillerFixture.enabled"), false, "a newer ON also waits for old preparation to retire");
      }
      if (choice === "fail-then-retry") nextSession.run("fillerFixture.failNextOff = true");
      if (choice === "cancel-unconfirmed-then-retry") nextSession.run("fillerFixture.cancelUnconfirmedNextOff = true");
      nextSession.run("releasePriorSession()"); await oldPrepare; await flush(); await flush();
      if (choice === "fail-then-retry" || choice === "cancel-unconfirmed-then-retry") {
        assert.equal(nextSession.run("fillerFixture.enabled"), false, "unconfirmed retirement must not resume the filler");
        if (choice === "fail-then-retry")
          assert.equal(nextSession.run("fillerFixture.preparedSource"), oldPrepareSource, "the failed OFF has not retired the old preparation");
        assert.match(nextSession.elements["rt-filler-status"].textContent, /onsetFiller.unavailable/);
        nextSession.elements["rt-filler-enabled"].checked = false;
        await nextSession.elements["rt-filler-enabled"].fire("change"); await flush(); await flush();
        nextSession.elements["rt-filler-enabled"].checked = true;
        await nextSession.elements["rt-filler-enabled"].fire("change"); await flush(); await flush();
      }
      assert.notEqual(nextSession.run("fillerFixture.preparedSource"), oldPrepareSource,
        "the delayed old preparation is retired before restoring ON");
      if (nextSession.run("fillerFixture.prepared") > 0) assert.equal(nextSession.run("fillerFixture.preparedLanguage"), language,
        "any new preparation uses the current language");
      assert.equal(nextSession.run("fillerFixture.enabled"), choice !== "off",
        `old retirement cannot override the latest preference in the next session: ${linux}/${language}/${choice}`);
      if (choice !== "off") assert.equal(nextSession.run("fillerFixture.posts.filter(body => body.enabled === true).at(-1).language"), language,
        "the restored ON uses the current language");
      assert.equal(nextSession.requests.filter(r => r.url === "/api/realtime/start").length, 2);
      assert.equal(nextSession.requests.some(r => r.url === "/api/tts/speak"), false);
    }

    const explicitSpeaker = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26", "mobmate.fillerEnabled": "true" });
    await explicitSpeaker.run("fetchTtsEngines()"); await explicitSpeaker.run("startRealtime()");
    explicitSpeaker.elements["tts-voicevox-speaker-select"].value = "3";
    await explicitSpeaker.elements["tts-voicevox-speaker-select"].fire("change"); await flush(); await flush();
    assert.equal(explicitSpeaker.storage.get("mobmate.voicevoxSpeaker"), "3", "an explicit new style replaces the unavailable saved style");
    assert.equal(configBodies(explicitSpeaker).at(-1).voicevoxSpeakerId, "3");
    assert.equal(explicitSpeaker.run("fillerFixture.enabledConfigs.at(-1).voicevoxSpeakerId"), "3");
    assert.ok(explicitSpeaker.run("fillerFixture.prepareConfigs.some(config => config.voicevoxSpeakerId === '3')"));

    const initialSpeaker = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true }, { "mobmate.ttsEngine": "voicevox" });
    await initialSpeaker.run("fetchTtsEngines()"); await flush();
    assert.equal(configBodies(initialSpeaker).at(-1).voicevoxSpeakerId, "3", "an unset style retains the existing first-choice default");
    assert.ok(initialSpeaker.run("fillerFixture.prepareConfigs.some(config => config.voicevoxSpeakerId === '3')"),
      "normal startup still prepares the valid default voice");
    assert.equal(initialSpeaker.storage.get("mobmate.voicevoxSpeaker"), null, "the implicit default does not overwrite settings");
    const presentSpeaker = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "26" });
    presentSpeaker.run("engineFixture.voicevoxSpeakers.push({ id: '26', name: 'Saved' })");
    await presentSpeaker.run("fetchTtsEngines()"); await flush();
    assert.equal(configBodies(presentSpeaker).at(-1).voicevoxSpeakerId, "26", "an available saved style still wins over the first entry");
    for (const kind of ["voice", "output"]) {
      for (const queued of [false, true]) {
        const changedStart = await createTtsSelectionFixture(linux, {}, { "mobmate.fillerEnabled": "true" });
        await changedStart.run("fetchTtsEngines()"); await flush();
        const heldStart = deferred();
        if (queued) changedStart.state.holdStop = heldStart;
        else changedStart.state.holdStart = heldStart;
        const stopping = queued ? changedStart.run("RealtimeSessionClient.stopRealtimeSession('mode-switch')") : Promise.resolve();
        await flush();
        const starting = changedStart.run("startRealtime()"); await flush();
        const heldConfig = deferred(); changedStart.state.configResponses.push(heldConfig);
        const changing = kind === "voice"
          ? (changedStart.elements["tts-moss-model-select"].value = "moss-model|Ava", changedStart.elements["tts-moss-model-select"].fire("change"))
          : (changedStart.elements["tts-output-device-select"].value = "out-new", changedStart.elements["tts-output-device-select"].fire("change"));
        await flush();
        heldStart.resolve(); await Promise.all([stopping, starting]); await flush();
        assert.equal(changedStart.run("fillerFixture.startApplications.at(-1).enabled"), false,
          `${kind} change invalidates ${queued ? "queued" : "in-flight"} Start before its new configuration is confirmed`);
        assert.equal(changedStart.run("fillerFixture.enabled"), false, "pending configuration keeps the old voice/output silent");
        heldConfig.resolve({ body: { ok: false } }); await changing; await flush(); await flush();
        assert.equal(changedStart.run("fillerFixture.enabled"), false, "configuration rejection cannot restore the old ON");
        assert.equal(changedStart.elements["rt-filler-enabled"].checked, true, "temporary refusal does not erase the ON preference");
        const retry = kind === "voice" ? changedStart.elements["tts-moss-model-select"].fire("change")
          : changedStart.elements["tts-output-device-select"].fire("change");
        await retry; await flush(); await flush();
        assert.equal(changedStart.run("fillerFixture.enabled"), true, "the latest ON resumes after a confirmed retry without restarting capture");
        assert.equal(changedStart.run(`fillerFixture.enabledConfigs.at(-1).${kind === "voice" ? "mossVoiceId" : "outputDeviceId"}`),
          kind === "voice" ? "Ava" : "out-new");
        assert.equal(changedStart.requests.filter(r => r.url === "/api/realtime/start").length, 1);
        assert.equal(changedStart.requests.some(r => r.url === "/api/tts/speak"), false, "configuration control does not itself request audio");
      }
      for (const preferred of [false, true]) {
        const confirmedFirst = await createTtsSelectionFixture(linux, {}, { "mobmate.fillerEnabled": String(preferred) });
        await confirmedFirst.run("fetchTtsEngines()"); await flush();
        confirmedFirst.state.holdStart = deferred();
        const starting = confirmedFirst.run("startRealtime()"); await flush();
        if (kind === "voice") {
          confirmedFirst.elements["tts-moss-model-select"].value = "moss-model|Ava";
          await confirmedFirst.elements["tts-moss-model-select"].fire("change");
        } else {
          confirmedFirst.elements["tts-output-device-select"].value = "out-new";
          await confirmedFirst.elements["tts-output-device-select"].fire("change");
        }
        await flush(); await flush();
        assert.equal(confirmedFirst.run("fillerFixture.posts.some(body => body.enabled === true)"), false,
          "a confirmed configuration still waits for the in-flight Start");
        confirmedFirst.state.holdStart.resolve(); await starting; await flush(); await flush();
        assert.equal(confirmedFirst.run("fillerFixture.enabled"), preferred,
          "Start completion reapplies the latest confirmed preference, including saved OFF");
        if (preferred) assert.equal(confirmedFirst.run(`fillerFixture.enabledConfigs.at(-1).${kind === "voice" ? "mossVoiceId" : "outputDeviceId"}`),
          kind === "voice" ? "Ava" : "out-new");
        await confirmedFirst.drainSaves();
        assert.equal(confirmedFirst.state.serverDocument.settings["mobmate.fillerEnabled"], String(preferred),
          "temporary active OFF is not a saved preference change");
      }
    }
    for (const preferred of [false, true]) {
      for (const restoreSavedVoice of [false, true]) {
        const unavailableSelection = restoreSavedVoice ? "moss-model|Unavailable" : "missing-model|Soyo";
        const missingMoss = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true }, {
          "mobmate.ttsEngine": "voicevox", "mobmate.voicevoxSpeaker": "3",
          "mobmate.mossModelVoice": unavailableSelection, "mobmate.fillerEnabled": String(preferred)
        });
        await missingMoss.run("fetchTtsEngines()"); await missingMoss.run("startRealtime()"); await flush();
        assert.equal(missingMoss.run("fillerFixture.enabled"), preferred, "the prior VOICEVOX selection follows the saved preference");
        const preparations = missingMoss.run("fillerFixture.prepareConfigs.length");
        missingMoss.elements["tts-engine-select"].value = "moss";
        await missingMoss.elements["tts-engine-select"].fire("change"); await flush(); await flush();
        assert.equal(missingMoss.elements["tts-moss-model-select"].selectedOptions[0].disabled, true);
        assert.equal(missingMoss.run("fillerFixture.enabled"), false, "an unavailable MOSS selection retires the previous engine's filler");
        assert.equal(missingMoss.run("fillerFixture.prepareConfigs.length"), preparations, "an unavailable MOSS selection cannot prepare the old host voice");
        missingMoss.elements["tts-monitor-volume-select"].value = "30";
        await missingMoss.elements["tts-monitor-volume-select"].fire("change"); await flush(); await flush();
        assert.equal(missingMoss.run("fillerFixture.enabled"), false, "output-only confirmation cannot re-enable the old voice");
        await missingMoss.run("fillerUiUnderTest.prepare()");
        assert.equal(missingMoss.run("fillerFixture.prepareConfigs.length"), preparations);
        for (const [id, tag] of [["ts-input", "textarea"], ["ts-send-btn", "button"], ["ts-wrap", "div"], ["ts-status", "div"], ["ts-counter", "span"], ["tts-text-input", "input"]]) {
          const element = missingMoss.document.createElement(tag); element.id = id;
          missingMoss.elements[id] = element; missingMoss.elements.main.appendChild(element);
        }
        missingMoss.run(section("/* --- TTS Speak body builder", "/* --- TTS Speak --- */"));
        missingMoss.run(section("/* --- TTS Speak --- */", "async function ttsStop()"));
        missingMoss.elements["ts-input"].value = "Keep the selected MOSS voice";
        missingMoss.elements["tts-text-input"].value = "Keep the selected MOSS voice";
        await missingMoss.run("textSpeechSend()"); await missingMoss.run("ttsSpeak()");
        assert.equal(missingMoss.requests.some(r => r.url === "/api/tts/speak"), false, "both manual speech controls reject an unavailable MOSS voice");
        assert.equal(missingMoss.elements["ts-input"].value, "Keep the selected MOSS voice", "rejected Text Speech retains its text");
        assert.match(missingMoss.elements["tts-status"].className, /err/);
        await missingMoss.drainSaves();
        assert.equal(missingMoss.state.serverDocument.settings["mobmate.mossModelVoice"], unavailableSelection);
        assert.equal(missingMoss.state.serverDocument.settings["mobmate.fillerEnabled"], String(preferred));
        if (restoreSavedVoice) {
          missingMoss.run("_mossCatalog[0].voices.push({ id: 'Unavailable' })");
          await missingMoss.run("fetchTtsEngines()");
        } else {
          missingMoss.elements["tts-moss-model-select"].value = "moss-model|Ava";
          await missingMoss.elements["tts-moss-model-select"].fire("change");
        }
        await flush(); await flush();
        assert.equal(configBodies(missingMoss).at(-1).mossVoiceId, restoreSavedVoice ? "Unavailable" : "Ava");
        assert.equal(missingMoss.run("fillerFixture.enabled"), preferred, "voice recovery restores only the current preference");
        if (preferred) assert.equal(missingMoss.run("fillerFixture.enabledConfigs.at(-1).engine"), "moss");
        await missingMoss.run("textSpeechSend()");
        assert.equal(missingMoss.requests.filter(r => r.url === "/api/tts/speak").length, 1, "manual speech resumes with the confirmed MOSS selection");
        assert.equal(missingMoss.requests.filter(r => r.url === "/api/realtime/start").length, 1, "voice recovery does not restart capture");
      }
    }
    const missingVoice = await createTtsSelectionFixture(linux, {}, { "mobmate.mossModelVoice": "moss-model|Unavailable" });
    await missingVoice.run("fetchTtsEngines()"); await flush();
    const missingBody = configBodies(missingVoice).at(-1);
    assert.equal(Object.hasOwn(missingBody, "engine"), false, "an unavailable saved voice cannot switch to a different MOSS voice");
    assert.equal(Object.hasOwn(missingBody, "mossVoiceId"), false, "the first enumerated voice is not an unavailable saved voice");
    assert.equal(missingVoice.elements["tts-moss-model-select"].value, "moss-model|Unavailable");
    assert.equal(missingVoice.elements["tts-moss-model-select"].selectedOptions[0].disabled, true);
    missingVoice.elements["tts-moss-model-select"].value = "moss-model|Ava";
    await missingVoice.elements["tts-moss-model-select"].fire("change"); await flush();
    assert.equal(configBodies(missingVoice).at(-1).engine, "moss", "an explicitly selected available voice recovers the engine");
    assert.equal(configBodies(missingVoice).at(-1).mossVoiceId, "Ava");
    const unconfirmedVoice = await createTtsSelectionFixture(linux, { deferInitialTtsConfig: true },
      { "mobmate.fillerEnabled": "true", "mobmate.mossModelVoice": "moss-model|Unavailable" });
    await unconfirmedVoice.run("fetchTtsEngines()"); await flush();
    await unconfirmedVoice.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    assert.equal(JSON.parse(unconfirmedVoice.requests.find(r => r.url === "/api/realtime/start").options.body).fillerEnabled, false,
      "an unavailable saved voice cannot enable a previously configured voice");
    unconfirmedVoice.elements["tts-moss-model-select"].value = "moss-model|Ava";
    await unconfirmedVoice.elements["tts-moss-model-select"].fire("change"); await flush(); await flush();
    assert.equal(unconfirmedVoice.run("fillerFixture.enabledConfigs.at(-1).mossVoiceId"), "Ava",
      "an explicitly selected and confirmed voice restores the ON preference");
    const explicitVoice = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 });
    await explicitVoice.run("fetchTtsEngines()");
    explicitVoice.elements["tts-engine-select"].value = "moss";
    await explicitVoice.elements["tts-engine-select"].fire("change"); await flush();
    explicitVoice.elements["tts-moss-model-select"].value = "moss-model|Ava";
    await explicitVoice.elements["tts-moss-model-select"].fire("change"); await flush();
    assert.deepEqual(configBodies(explicitVoice).at(-1), { engine: "moss", mossModelId: "moss-model", mossVoiceId: "Ava" },
      "an explicit voice change does not apply an unread output or monitor");
    assert.equal(explicitVoice.run("OnsetFillerUi.isVoiceConfirmed()"), true, "a successful explicit voice-only configuration confirms that voice");
    const explicitMossSpeech = await explicitVoice.run("requestTtsSpeak({ text: 'Explicitly chosen voice', engine: 'moss', mossModelId: 'moss-model', mossVoiceId: 'Ava' })");
    assert.equal(explicitMossSpeech?.ok, true, "manual speech with a confirmed explicit voice and observed output does not require unread saved settings");
    await explicitVoice.run("startRealtime()"); await flush();
    assert.equal(explicitVoice.run("fillerFixture.enabled"), false, "voice confirmation cannot authorize filler while saved settings remain unknown");
    assert.equal(explicitVoice.run("fillerFixture.prepareConfigs.length"), 0);
    explicitVoice.state.uiSettingsReadFailures = 0;
    await explicitVoice.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    assert.equal(configBodies(explicitVoice).at(-1).mossVoiceId, "Ava", "recovery retains the newer explicit voice");
    const explicitOutput = await createTtsSelectionFixture(linux, {
      uiSettingsReadFailures: 10, rawSettings: { "mobmate.ttsMonitorVolume": "0" }
    });
    explicitOutput.elements["tts-output-device-select"].value = "out-new";
    await explicitOutput.elements["tts-output-device-select"].fire("change"); await flush();
    const outputBody = configBodies(explicitOutput).at(-1);
    assert.equal(outputBody.outputDeviceId, "out-new");
    assert.equal(Object.hasOwn(outputBody, "monitorVolumePercent"), false, "explicit output does not apply an unread monitor");
    assert.equal(Object.hasOwn(outputBody, "engine"), false, "explicit output does not apply an unread engine");
    explicitOutput.state.uiSettingsReadFailures = 0;
    await explicitOutput.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    assert.equal(configBodies(explicitOutput).at(-1).outputDeviceId, "out-new", "recovery retains the newer explicit output");
    assert.equal(configBodies(explicitOutput).at(-1).monitorVolumePercent, 30, "recovery restores the saved monitor, not the raw fallback");
    const failedDeviceRecovery = await createTtsSelectionFixture(linux, {
      uiSettingsReadFailures: 10, rawSettings: { "mobmate.ttsOutputDeviceId": "out-new" }
    });
    await failedDeviceRecovery.run("fetchTtsOutputDevices()");
    assert.equal(failedDeviceRecovery.elements["tts-output-device-select"].value, "out-new");
    failedDeviceRecovery.state.uiSettingsReadFailures = 0;
    failedDeviceRecovery.state.ttsDeviceResponses.push({ transportError: true });
    await failedDeviceRecovery.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    assert.equal(configBodies(failedDeviceRecovery).at(-1).outputDeviceId, "out-old",
      "a failed device enumeration during recovery cannot apply the earlier unread output");
    const raced = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 });
    raced.run("engineFixture.hold = new Promise(resolve => { globalThis.releaseEngineRead = resolve; })");
    const unreadEngineRead = raced.run("fetchTtsEngines()"); await flush();
    raced.state.uiSettingsReadFailures = 0;
    await raced.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    const recoveredCount = configBodies(raced).length;
    assert.ok(recoveredCount > 0, "recovery configures restored selections");
    raced.run("releaseEngineRead()"); await unreadEngineRead; await flush();
    assert.equal(configBodies(raced).length, recoveredCount, "an enumeration begun before recovery must not apply again after its late response");

    const active = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "true" });
    await active.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    active.run("engineFixture.hold = new Promise(resolve => { globalThis.releaseRestoredEngines = resolve; })");
    active.state.uiSettingsReadFailures = 0;
    await active.run("fillerUiUnderTest.refresh()"); await flush();
    assert.equal(active.run("fillerFixture.enabled"), false, "restored ON cannot use the old voice while engine enumeration is pending");
    assert.equal(active.run("fillerFixture.enabledConfigs.length"), 0);
    active.run("releaseRestoredEngines()"); await flush(); await flush(); await flush();
    assert.equal(active.run("fillerFixture.enabled"), true, `restored ON is sent after confirmed TTS restoration: ${JSON.stringify({ linux, bodies: configBodies(active), status: active.elements["rt-filler-status"].textContent, posts: active.run("fillerFixture.posts"), confirmed: active.run("ttsOutputConfirmed"), pending: active.run("ttsOutputPending") })}`);
    const restoredConfig = active.run("fillerFixture.enabledConfigs.at(-1)");
    assert.equal(restoredConfig.engine, "moss");
    assert.equal(restoredConfig.mossVoiceId, "Soyo");
    assert.equal(restoredConfig.outputDeviceId, "out-old", "the enabled filler observes the restored output");

    const heldPrepare = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "true" });
    await heldPrepare.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    heldPrepare.run("fillerFixture.hold = new Promise(resolve => { globalThis.releaseOldPrepare = resolve; })");
    const oldPrepare = heldPrepare.run("fillerUiUnderTest.prepare()"); await flush();
    heldPrepare.state.uiSettingsReadFailures = 0;
    await heldPrepare.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    assert.equal(heldPrepare.run("fillerFixture.enabled"), false);
    heldPrepare.run("releaseOldPrepare()"); await oldPrepare; await flush(); await flush(); await flush();
    assert.equal(heldPrepare.run("fillerFixture.enabled"), true, "a later prepare cannot cancel the latest restored ON");
    assert.equal(heldPrepare.run("fillerFixture.enabledConfigs.at(-1).mossVoiceId"), "Soyo");

    const failedRestore = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "true" });
    await failedRestore.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    failedRestore.state.configResponses.push({ body: { ok: true } }, { body: { ok: false } });
    failedRestore.state.uiSettingsReadFailures = 0;
    await failedRestore.run("fillerUiUnderTest.refresh()"); await flush(); await flush(); await flush();
    assert.equal(failedRestore.run("fillerFixture.enabled"), false, "an unconfirmed restored voice must remain silent");
    failedRestore.elements["tts-moss-model-select"].value = "moss-model|Ava";
    await failedRestore.elements["tts-moss-model-select"].fire("change"); await flush(); await flush();
    assert.equal(failedRestore.run("fillerFixture.enabled"), true, "a later explicit, confirmed voice choice restores ON");
    assert.equal(failedRestore.run("fillerFixture.enabledConfigs.at(-1).mossVoiceId"), "Ava");

    const newerVoice = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "true" });
    await newerVoice.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    const oldStatus = deferred(); newerVoice.state.ttsStatusResponses.push(oldStatus);
    newerVoice.state.uiSettingsReadFailures = 0;
    await newerVoice.run("fillerUiUnderTest.refresh()"); await flush(); await flush();
    assert.equal(newerVoice.run("fillerFixture.enabled"), false);
    newerVoice.elements["tts-moss-model-select"].value = "moss-model|Ava";
    await newerVoice.elements["tts-moss-model-select"].fire("change"); await flush(); await flush();
    oldStatus.resolve({ body: { outputDeviceId: "out-old" } }); await flush(); await flush(); await flush();
    assert.equal(newerVoice.run("fillerFixture.enabled"), true, "a newer confirmed voice wins over a late recovery response");
    assert.equal(newerVoice.run("fillerFixture.enabledConfigs.at(-1).mossVoiceId"), "Ava");

    const stoppedRecovery = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "true" });
    await stoppedRecovery.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    stoppedRecovery.run("engineFixture.hold = new Promise(resolve => { globalThis.releaseStoppedRecovery = resolve; })");
    stoppedRecovery.state.uiSettingsReadFailures = 0;
    await stoppedRecovery.run("fillerUiUnderTest.refresh()"); await flush();
    await stoppedRecovery.run("RealtimeSessionClient.stopRealtimeSession('main')");
    stoppedRecovery.run("releaseStoppedRecovery()"); await flush(); await flush(); await flush();
    assert.equal(stoppedRecovery.run("fillerFixture.enabledConfigs.length"), 0, "a completed recovery must not enable after Stop");

    const startedDuringRecovery = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "true" });
    startedDuringRecovery.run("engineFixture.hold = new Promise(resolve => { globalThis.releaseStartRecovery = resolve; })");
    startedDuringRecovery.state.uiSettingsReadFailures = 0;
    await startedDuringRecovery.run("fillerUiUnderTest.refresh()"); await flush();
    await startedDuringRecovery.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    assert.equal(JSON.parse(startedDuringRecovery.requests.find(r => r.url === "/api/realtime/start").options.body).fillerEnabled, false,
      "Start during recovery cannot enable the old voice");
    startedDuringRecovery.run("releaseStartRecovery()"); await flush(); await flush(); await flush();
    assert.equal(startedDuringRecovery.run("fillerFixture.enabled"), true, "the saved ON is applied after recovery in the newly started session");

    const offDuringRecovery = await createTtsSelectionFixture(linux, { uiSettingsReadFailures: 10 }, { "mobmate.fillerEnabled": "true" });
    await offDuringRecovery.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
    offDuringRecovery.run("engineFixture.hold = new Promise(resolve => { globalThis.releaseOffRecovery = resolve; })");
    offDuringRecovery.state.uiSettingsReadFailures = 0;
    await offDuringRecovery.run("fillerUiUnderTest.refresh()"); await flush();
    offDuringRecovery.elements["rt-filler-enabled"].checked = false;
    await offDuringRecovery.elements["rt-filler-enabled"].fire("change"); await flush();
    offDuringRecovery.run("releaseOffRecovery()"); await flush(); await flush(); await flush();
    assert.equal(offDuringRecovery.run("fillerFixture.enabled"), false, "an explicit OFF during recovery wins over the saved ON");
    assert.equal(offDuringRecovery.run("fillerFixture.enabledConfigs.length"), 0);
  }
  const migrationFailure = await createFillerFixture(
    { "mobmate.fillerEnabled": "false" }, { browserMigrationCompleted: false, saveOk: false });
  assert.equal(migrationFailure.elements["rt-filler-enabled"].checked, false, "failed migration save retains the successfully read OFF");
  assert.equal(migrationFailure.run("uiSettings.isReady()"), true, "a failed migration PUT does not discard a valid server read");
  migrationFailure.state.saveOk = true;
  migrationFailure.run("localStorage.setItem('mobmate.mode', 'stt')"); await migrationFailure.drainSaves();
  assert.equal(migrationFailure.state.serverDocument.settings["mobmate.fillerEnabled"], "false", "later save keeps OFF after migration failure");
  const activeRecovery = await createFillerFixture({ "mobmate.fillerEnabled": "true" }, { uiSettingsReadFailures: 10 });
  await activeRecovery.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  assert.equal(activeRecovery.run("fillerFixture.enabled"), false, "explicit Start stays silent while saved preference is unknown");
  activeRecovery.state.uiSettingsReadFailures = 0;
  await activeRecovery.run("fillerUiUnderTest.refresh()"); await flush(); await activeRecovery.drainSaves();
  assert.equal(activeRecovery.elements["rt-filler-enabled"].checked, true, "active recovery restores the saved ON preference");
  assert.equal(activeRecovery.run("fillerFixture.enabled"), true, "saved ON is applied to future onset events in the active session");
  assert.equal(activeRecovery.requests.filter(r => r.url === "/api/realtime/start").length, 1, "recovery never starts another capture");
  assert.equal(activeRecovery.requests.some(r => r.url === "/api/tts/speak"), false, "recovery does not itself play audio");
  const pendingChoice = await createFillerFixture({ "mobmate.fillerEnabled": "false" });
  await pendingChoice.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  pendingChoice.run("fillerFixture.hold = new Promise(resolve => { globalThis.releaseChoicePrepare = resolve; })");
  const priorPrepare = pendingChoice.run("fillerUiUnderTest.prepare()"); await flush();
  pendingChoice.elements["rt-filler-enabled"].checked = true;
  await pendingChoice.elements["rt-filler-enabled"].fire("change"); await flush();
  const laterPrepare = pendingChoice.run("fillerUiUnderTest.prepare()"); await flush();
  pendingChoice.run("releaseChoicePrepare()"); await Promise.all([priorPrepare, laterPrepare]); await flush();
  assert.equal(pendingChoice.run("fillerFixture.enabled"), true, "prepare after a queued ON must not discard that latest preference");
  const queuedOn = await createFillerFixture({ "mobmate.fillerEnabled": "false" });
  await queuedOn.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  queuedOn.run("fillerFixture.hold = new Promise(resolve => { globalThis.releaseQueuedPrepare = resolve; })");
  const queuedPrepare = queuedOn.run("fillerUiUnderTest.prepare()"); await flush();
  queuedOn.elements["rt-filler-enabled"].checked = true; await queuedOn.elements["rt-filler-enabled"].fire("change");
  const newVoice = deferred(); queuedOn.state.configResponses.push(newVoice);
  const changingVoice = queuedOn.run("postTtsConfig({ engine: 'moss', mossModelId: 'moss-model', mossVoiceId: 'Soyo', outputDeviceId: 'out-old' })"); await flush();
  queuedOn.run("releaseQueuedPrepare()"); await queuedPrepare; await flush();
  assert.equal(queuedOn.run("fillerFixture.enabled"), false, "a queued ON rechecks TTS when the selected voice is changing");
  newVoice.resolve({ body: { ok: true } }); await changingVoice; await flush(); await flush();
  assert.equal(queuedOn.run("fillerFixture.enabledConfigs.at(-1).mossVoiceId"), "Soyo", "the queued ON resumes only with the confirmed newer voice");
  await f.run("postTtsConfig({ outputDeviceId: 'out-old' })"); await flush();
  assert.deepEqual(JSON.parse(JSON.stringify(f.run("fillerFixture.posts.at(-1)"))), { language: "ja" }, "successful normal TTS configuration prewarms selected language without enabling playback");
  f.elements["rt-language-select"].value = "zh"; await f.elements["rt-language-select"].fire("change"); await flush();
  assert.deepEqual(JSON.parse(JSON.stringify(f.run("fillerFixture.posts.at(-1)"))), { language: "zh" }, "actual realtime language handler prewarms the new language");
  f.elements["rt-language-select"].value = "ja";
  toggle.checked = true; await toggle.fire("change"); await flush();
  assert.deepEqual(JSON.parse(JSON.stringify(f.run("fillerFixture.posts.at(-1)"))), { language: "ja" }, "pre-start toggle only prewarms, never enables playback");
  assert.equal(f.run("fillerFixture.enabled"), false);
  f.run("fillerFixture.prepared = 5"); await f.run("fillerUiUnderTest.refresh()");
  assert.equal(f.elements["rt-filler-status"].textContent, "ON · 5/5 · ui.web.onsetFiller.startRequired", "saved ON is distinct from stopped session playback");
  await f.drainSaves();
  assert.equal(f.state.serverDocument.settings["mobmate.fillerEnabled"], "true", "ON is saved through normal UI settings");
  const restoredOn = await createFillerFixture(f.state.serverDocument.settings);
  assert.equal(restoredOn.elements["rt-filler-enabled"].checked, true, "saved ON restores in a fresh client");
  assert.equal(restoredOn.run("fillerFixture.enabled"), false, "restored ON does not enable a session");
  assert.equal(restoredOn.requests.some(r => /\/api\/(realtime\/start|tts\/speak)/.test(r.url)), false, "restored ON and preparation do not start capture or play");
  restoredOn.state.holdStart = deferred();
  const pendingStart = restoredOn.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  await flush();
  restoredOn.elements["rt-filler-enabled"].checked = false;
  await restoredOn.elements["rt-filler-enabled"].fire("change"); await flush();
  assert.equal(restoredOn.run("fillerFixture.posts.at(-1).enabled"), false, "OFF is sent while Start is still pending");
  restoredOn.state.holdStart.resolve(); await pendingStart; await flush();
  assert.equal(restoredOn.run("fillerFixture.enabled"), false, "pending Start does not restore the retired choice in the fixture");
  const onDuringStart = await createFillerFixture({ "mobmate.fillerEnabled": "false" });
  onDuringStart.state.holdStart = deferred();
  const waitingStart = onDuringStart.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })"); await flush();
  onDuringStart.elements["rt-filler-enabled"].checked = true;
  await onDuringStart.elements["rt-filler-enabled"].fire("change"); await flush();
  assert.equal(onDuringStart.run("fillerFixture.posts.some(body => body.enabled === true)"), false, "ON waits until Start is confirmed");
  onDuringStart.state.holdStart.resolve(); await waitingStart; await flush(); await flush();
  assert.equal(onDuringStart.run("fillerFixture.enabled"), true, "the newer ON is applied after Start completes");
  await f.run("RealtimeSessionClient.startRealtimeSession('main', { captureDeviceId: 'mic-old', ...getRealtimeSettings() })");
  assert.equal(JSON.parse(f.requests.find(r => r.url === "/api/realtime/start").options.body).fillerEnabled, true, "public Start carries the saved ON preference");
  f.run("fillerFixture.prepared = 5; Date.now = () => 9999999999999"); await f.run("fillerUiUnderTest.refresh()");
  assert.match(f.elements["rt-filler-status"].textContent, /5\/5.*onsetFiller.waitingOnset$/, "normal capture waits for onset without an extra consent action");
  f.run("fillerFixture.playbackReason = 'native-unavailable'; Date.now = () => 10000000019999");
  await f.run("fillerUiUnderTest.refresh()");
  assert.match(f.elements["rt-filler-status"].textContent, /onsetFiller.nativeUnavailable$/, "missing playback capability remains actionable even with five clips");
  f.run("fillerFixture.hold = new Promise(resolve => { globalThis.releasePreparation = resolve; })");
  const pendingPreparation = f.run("fillerUiUnderTest.prepare()"); await flush();
  await f.elements["rt-device-select"].fire("change"); await flush();
  assert.equal(f.run("fillerFixture.posts.at(-1).inputChanged"), true);
  f.run("releasePreparation()"); await pendingPreparation; await flush();
  assert.match(f.elements["rt-filler-status"].textContent, /onsetFiller.inputChanged$/);
  assert.equal(f.run("fillerFixture.posts.some(body => 'consentToken' in body)"), false, "normal capture sends no special consent message");
  f.run("fillerFixture.hold = new Promise(resolve => { globalThis.releaseFiller = resolve; })");
  toggle.checked = true; await toggle.fire("change"); await flush();
  toggle.checked = false; await toggle.fire("change"); await flush();
  assert.equal(f.run("fillerFixture.posts.at(-1).enabled"), false, "OFF is sent before an earlier ON completes or settings save runs");
  f.run("releaseFiller()"); await flush(); await flush();
  assert.equal(f.run("fillerFixture.enabled"), false, "serialized active OFF wins over late ON response");
  assert.match(f.elements["rt-filler-status"].textContent, /^OFF/);
  assert.equal(f.requests.filter(r => /\/api\/realtime\/(start|stop)$/.test(r.url)).length, 1, "active control never restarts recognition");
  await f.drainSaves();
  assert.equal(f.state.serverDocument.settings["mobmate.fillerEnabled"], "false", "OFF is saved through normal UI settings");
  const restoredOff = await createFillerFixture(f.state.serverDocument.settings);
  assert.equal(restoredOff.elements["rt-filler-enabled"].checked, false, "saved OFF restores in a fresh client");
  toggle.checked = true; await toggle.fire("change"); await flush(); await f.drainSaves();
  await f.run("RealtimeSessionClient.stopRealtimeSession('main')"); await flush();
  f.elements["rt-language-select"].value = "zh"; await f.elements["rt-language-select"].fire("change"); await flush();
  await f.run("postTtsConfig({ outputDeviceId: 'out-new' })"); await flush(); await f.drainSaves();
  assert.equal(toggle.checked, true, "Stop and language/output changes retain the preference");
  assert.equal(f.state.serverDocument.settings["mobmate.fillerEnabled"], "true", "Stop and changes do not save OFF");
  f.run("fillerFixture.fail = true; Date.now = () => 10000000029999"); await f.run("fillerUiUnderTest.refresh()");
  assert.equal(toggle.checked, true); assert.equal(toggle.disabled, false, "temporary status failure does not erase the preference or prevent OFF");
  assert.match(f.elements["rt-filler-status"].textContent, /^ON.*onsetFiller.unavailable$/);
  const fields = ["title", "hint", "detailsTitle", "details", "off", "ready", "readyOff", "unprepared", "changing", "unavailable", "settingsUnavailable", "startRequired", "inputRequired", "inputChanged", "outputRequired", "approvalPending", "translationOn", "waitingOnset", "waitingStart", "started", "reservationRejected", "bodyPriority", "playbackFailed", "nativeUnavailable", "cancelUnconfirmed"];
  const locales = ["en", "ja", "ko", "zh_cn", "zh_tw"].map(locale => fs.readFileSync(`libs/preset/_ui_${locale}.txt`, "utf8"));
  for (const field of fields) {
    const key = "ui.web.onsetFiller." + field + "=";
    const values = locales.map(text => text.split(/\r?\n/).find(line => line.startsWith(key))?.slice(key.length));
    assert.ok(values.every(Boolean), key); assert.equal(new Set(values).size, 5, key + " actual locale translations");
  }
  for (const text of locales) {
    const hint = text.split(/\r?\n/).find(line => line.startsWith("ui.web.onsetFiller.hint="));
    const details = text.split(/\r?\n/).find(line => line.startsWith("ui.web.onsetFiller.details="));
    for (const required of ["Kokoro", "VOICEVOX", "MOSS"]) assert.ok(details.includes(required), required + " in secondary details");
    for (const obsolete of ["3 af_heart", "45 zf_xiaobei", "0.25.2", "Zundamon"]) assert.ok(!hint.includes(obsolete), obsolete + " is not a fixed-voice requirement");
    assert.doesNotMatch(hint, /Piper/);
    assert.doesNotMatch(text, /ui\.web\.onsetFiller\.monitorOn=/, "monitor OFF is not a product requirement");
  }
  for (const host of ["src/MobMate.App.WebHost/Program.cs", "src/MobMate.App.Linux/Program.cs"]) {
    const code = fs.readFileSync(host, "utf8");
    assert.match(code, /expectedGeneration: onsetStartGeneration/, host + " binds Start to its original generation");
    assert.match(code, /generation == Volatile\.Read\(ref onsetControlGeneration\) && OnsetPermitted\(\)/, host + " rechecks OFF generation at playback");
    assert.match(code, /Interlocked\.Increment\(ref onsetControlGeneration\)[\s\S]{0,120}ttsRouter\.StopOnset\(\)/, host + " retires OFF before waiting for the session gate");
    assert.match(code, /mobfyOnset\.BeforeStart\(optionsJson/, host + " binds the actual VC selection");
    assert.match(code, /mobfyOnset\.Attach\(\)[\s\S]{0,250}VoiceConversionStart/, host + " attaches without starting a preparation player");
    assert.match(code, /mobfyOnset\.ObserveStatus\(nativeResult\.RawJson\)/, host + " uses the existing status poll");
    assert.match(code, /if \(hasEnabled && !enabled\) mobfyOnset\.Stop\(\)/, host + " explicit OFF acknowledges an empty retired bank");
  }
  const createMobfyFillerFixture = async preferred => {
    const mobfy = await createFillerFixture({ "mobmate.mode": "mobfy", "mobmate.fillerEnabled": String(preferred) });
    mobfy.run(`
      globalThis.isMobfyMode = () => true;
      globalThis.vcFillerFixture = { starts: [], stops: 0, preparedModels: [] };
      const beforeVcFillerFetch = fetch;
      globalThis.fetch = async (url, options = {}) => {
        if (url === '/api/ui-settings' && options.method === 'PUT' && fixtureState.holdSettingsSave) {
          const held = fixtureState.holdSettingsSave; fixtureState.holdSettingsSave = null;
          fixtureState.settingsSaveEntered = true; await held.promise;
        }
        if (url === '/api/realtime/filler' && options.method === 'POST')
          vcFillerFixture.preparedModels.push(fixtureState.serverDocument.settings['mobmate.vc.modelId']);
        if (url === '/api/voice-conversion/start') {
          vcFillerFixture.starts.push(JSON.parse(options.body));
          if (fixtureState.holdVcStart) await fixtureState.holdVcStart.promise;
          return { ok: true, json: async () => ({ ok: true }) };
        }
        if (url === '/api/voice-conversion/status')
          return { ok: true, json: async () => ({ running: true, conversionRuntimeAvailable: true, onnx: { loaded: true } }) };
        if (url === '/api/voice-conversion/stop') {
          vcFillerFixture.stops++; return { ok: true, json: async () => ({ ok: true }) };
        }
        return beforeVcFillerFetch(url, options);
      };
    `);
    mobfy.run(section("async function startVoiceConversionRequest(", "function vcIsSupportedCatalogModel("));
    mobfy.run(source.slice(source.indexOf("function vcFetchStatus()")));
    mobfy.run(`globalThis.vcPollingTimer = null; globalThis.MOBFY_STATUS_POLL_MS = 100;
      globalThis.updateMainActionLabels = () => {}; globalThis.meterSetRms = () => {};
      globalThis.waveUpdate = () => {}; globalThis.waveDraw = () => {};
      globalThis.spkSetLevel = () => {}; globalThis.vcHasSelectedModel = () => true;`);
    return mobfy;
  };
  const selectedMobfy = await createMobfyFillerFixture(true);
  selectedMobfy.state.holdSettingsSave = deferred();
  const heldSettings = selectedMobfy.state.holdSettingsSave;
  selectedMobfy.run("localStorage.setItem('mobmate.vc.modelId', 'old-model')");
  const oldSave = selectedMobfy.run("uiSettings.flush()"); await flush();
  assert.equal(selectedMobfy.state.settingsSaveEntered, true);
  selectedMobfy.run("localStorage.setItem('mobmate.vc.modelId', 'new-model')");
  const selectedPreparation = selectedMobfy.run("fillerUiUnderTest.prepare()"); await flush();
  assert.equal(selectedMobfy.run("vcFillerFixture.preparedModels.length"), 0, "preparation waits for earlier settings PUTs");
  heldSettings.resolve(); await Promise.all([oldSave, selectedPreparation]);
  assert.equal(selectedMobfy.state.serverDocument.settings["mobmate.vc.modelId"], "new-model");
  assert.equal(selectedMobfy.run("vcFillerFixture.preparedModels.at(-1)"), "new-model");
  assert.deepEqual(JSON.parse(JSON.stringify(selectedMobfy.run("fillerFixture.posts.at(-1)"))),
    { language: "ja" }, "Mobfy preparation is distinct from explicit OFF retirement");
  assert.equal(selectedMobfy.run("vcFillerFixture.starts.length"), 0, "selection/preparation never starts VC capture");
  for (const preferred of [false, true]) {
    const mobfy = await createMobfyFillerFixture(preferred);
    await mobfy.run("startVoiceConversionRequest({ modelId: 'selected-rvc', f0ShiftSemitones: 1 })"); await flush();
    assert.equal(mobfy.run("fillerFixture.posts.at(-1).enabled"), preferred, "Mobfy Start respects the saved preference");
    assert.deepEqual(JSON.parse(JSON.stringify(mobfy.run("vcFillerFixture.starts[0]"))),
      { modelId: "selected-rvc", f0ShiftSemitones: 1 }, "existing VC HTTP options are unchanged");
    if (preferred) {
      mobfy.run("fillerFixture.prepared = 5; fillerFixture.playbackReason = 'waiting-onset'; Date.now = () => 1000000000000; vcFetchStatus()");
      await flush(); await flush();
      assert.match(mobfy.elements["rt-filler-status"].textContent, /5\/5.*onsetFiller.waitingOnset$/,
        "the existing VC poll refreshes filler readiness without an ASR session");
    }
    mobfy.elements["rt-filler-enabled"].checked = false;
    await mobfy.elements["rt-filler-enabled"].fire("change"); await flush();
    assert.equal(mobfy.run("fillerFixture.enabled"), false, "owned Mobfy can be disabled without an ASR session");
    mobfy.elements["rt-filler-enabled"].checked = true;
    await mobfy.elements["rt-filler-enabled"].fire("change"); await flush(); await flush();
    assert.equal(mobfy.run("fillerFixture.enabled"), true, "owned Mobfy can resume without restarting capture");
    assert.equal(mobfy.run("vcFillerFixture.starts.length"), 1);
    assert.equal(mobfy.run("vcFillerFixture.stops"), 0);
    mobfy.run("fillerUiUnderTest.sessionStopping(); fillerUiUnderTest.mobfyStopped()"); await flush();
    assert.equal(mobfy.run("fillerFixture.enabled"), false);
    assert.equal(mobfy.elements["rt-filler-enabled"].checked, true, "VC Stop does not erase the preference");
  }
  const pendingMobfy = await createMobfyFillerFixture(true);
  pendingMobfy.state.holdVcStart = deferred();
  const vcStarting = pendingMobfy.run("startVoiceConversionRequest({ modelId: 'selected-rvc' })"); await flush();
  pendingMobfy.elements["rt-filler-enabled"].checked = false;
  await pendingMobfy.elements["rt-filler-enabled"].fire("change"); await flush();
  pendingMobfy.state.holdVcStart.resolve(); await vcStarting; await flush(); await flush();
  assert.equal(pendingMobfy.run("fillerFixture.enabled"), false, "OFF also wins across a pending VC Start");
  console.log("Onset filler UI regression passed: saved preference, silent restore, immediate OFF, Stop and changes, five translations; no audio.");
}

async function acceptanceCueRegression() {
  const digest = path => require("node:crypto").createHash("sha256").update(fs.readFileSync(path)).digest("hex");
  assert.equal(digest("web/audio/asr-accepted-a.wav"), "0030d63c4ad8928ae530c54b02b56cabeb1e2eea154f580bcaa9e1f4469632bc", "production cue must include the fixed WAV, not a missing/placeholder URL");
  assert.equal(digest("tools/generate-asr-acceptance-cues.py"), "343568ce47365b3914a36ca7df3696e9023745ae81d9c49321834be4a966942c", "preserve the actual asset generator provenance");
  const create = async () => {
    const f = await fixture();
    for (const [name, tag] of Object.entries({ enabled: "input", status: "p", preview: "button", choose: "button", output: "select", confirmed: "input", volume: "select" })) {
      const node = f.document.createElement(tag); node.id = "asr-cue-" + name;
      f.elements[node.id] = node; f.elements.main.appendChild(node);
    }
    f.run(`
      globalThis.cueTest = { now: 100000, played: [], audio: [], lock: true, autoEnd: true, events: {}, devices: [
        { kind: 'audiooutput', deviceId: 'default', label: 'Default' },
        { kind: 'audiooutput', deviceId: 'virtual', label: 'Virtual cable' },
        { kind: 'audiooutput', deviceId: 'headphones', label: 'Personal headphones' }] };
      Date.now = () => cueTest.now;
      globalThis.Audio = class {
        constructor(src) { this.src = src; this.sinkId = ''; this.handlers = {}; cueTest.audio.push(this); }
        addEventListener(name, handler) { this.handlers[name] = handler; }
        async setSinkId(id) { if (cueTest.holdSink) await cueTest.holdSink; if (cueTest.failSink) throw Error('denied'); this.sinkId = id; }
        async play() {
          if (cueTest.failPlay) throw Error('autoplay denied');
          if (this.sinkId !== 'headphones') throw Error('unsafe sink');
          cueTest.played.push({ sink: this.sinkId, src: this.src, volume: this.volume });
          if (cueTest.autoEnd) Promise.resolve().then(() => this.handlers.ended?.());
        }
        pause() { this.paused = true; }
      };
      globalThis.navigator = { mediaDevices: {
        selectAudioOutput: async () => { if (cueTest.failChoose) throw Error('denied'); return cueTest.devices.find(d => d.deviceId === 'headphones'); },
        enumerateDevices: async () => cueTest.devices,
        getUserMedia: () => { throw Error('must not request a microphone'); },
        addEventListener: (name, handler) => { cueTest.events[name] = handler; }
      }, locks: { request: async (name, options, work) => work(cueTest.lock ? {} : null) } };
      globalThis.BroadcastChannel = class { constructor() { cueTest.channel = this; } postMessage() {} close() {} };
      window.addEventListener = (name, handler) => { cueTest.events[name] = handler; };
      globalThis.updateRealtimeResults = () => {};
      AsrAcceptanceCue.init();
    `);
    const e = f.elements;
    f.prepare = async () => {
      await e["asr-cue-choose"].fire("click");
      e["asr-cue-confirmed"].checked = true; await e["asr-cue-confirmed"].fire("change");
      await e["asr-cue-preview"].fire("click"); await flush();
      e["asr-cue-enabled"].checked = true; await e["asr-cue-enabled"].fire("change");
    };
    f.offer = (ts, extra = {}, token = "realtimePollToken") => f.run(`AsrAcceptanceCue.accept(${JSON.stringify({ final: "同じ言葉", finalDetectedAtMs: ts, ...extra })}, ${token})`);
    return f;
  };
  const f = await create(), e = f.elements, api = f.run("cueTest");
  assert.equal(e["asr-cue-enabled"].checked, false, "default OFF");
  assert.match(e["asr-cue-status"].textContent, /asrCue.off$/, "OFF must not demand output preparation");
  assert.equal(api.played.length, 0, "init must not play or acquire a microphone");
  await f.prepare(); assert.match(e["asr-cue-status"].textContent, /asrCue.ready/);
  assert.equal(e["asr-cue-output"].value, "headphones", "native picker success must update the displayed selection before confirmation/preview");
  assert.deepEqual(Array.from(e["asr-cue-output"].options, o => o.value), ["", "headphones"], "default and known virtual routes must not be offered");
  await f.offer(100010); assert.equal(api.played.length, 1, "startup observation has no local recording owner");
  await f.run("startRealtime()"); await flush(); api.now = 101000;
  await f.offer(100010); await f.offer(100010, { translatedFinal: "translation only" });
  assert.equal(api.played.length, 2, "one cue despite a repeated poll/late translation");
  await f.offer(100020); assert.equal(api.played.length, 2, "timestamp alone cannot distinguish a new utterance from a retained native final");
  for (const [ts, extra] of [[100030, { textFilter: { dropped: true } }], [100040, { loopbackSuspected: true }],
    [100050, { discarded: true }], [100060, { error: "obsolete" }], [100070, { processedFinal: " " }]]) {
    const final = "除外された結果" + ts;
    await f.offer(ts, { final, ...extra }); await f.offer(ts, { final });
    await f.offer(ts + 1, { final });
  }
  await f.offer(0); await f.offer(99000); await f.offer(102000); await f.offer(100080, {}, "-1");
  assert.equal(api.played.length, 2, "filtered/discarded/error/empty/old/future/unknown boundaries cannot be replayed, including timestamp-only updates");
  api.autoEnd = false;
  const first = f.offer(100080, { final: "次の結果" }); await flush(); await f.offer(100090, { final: "再生中の結果" });
  assert.equal(api.played.length, 3, "overlapping cues are dropped, not queued");
  api.audio.at(-1).handlers.ended(); await first;
  api.autoEnd = true; await f.offer(100090, { final: "再生中の結果" }); assert.equal(api.played.length, 3, "busy boundary is consumed");
  f.state.stopResponses.push({ body: { state: "completed", final: "Stopで確定した別の結果", finalDetectedAtMs: 100100 } });
  await f.run("stopRealtime()"); await flush(); assert.equal(api.played.length, 4, "normal Stop may publish one new accepted final");
  await f.offer(100110); assert.equal(api.played.length, 4, "no later notification after Stop");
  api.now = 102000; await f.run("startRealtime()"); await flush(); api.now = 103000;
  await f.offer(102010);
  f.state.stopResponses.push({ body: { state: "completed", final: "同じ言葉", finalDetectedAtMs: 102020 } });
  await f.run("stopRealtime()"); await flush(); assert.equal(api.played.length, 5, "Stop must not duplicate a retained final even if its timestamp changes");
  api.now = 104000; await f.run("startRealtime()"); await flush(); api.now = 105000;
  await f.run("RealtimeSessionClient.stopRealtimeSession('language-switch')");
  await f.offer(104010); assert.equal(api.played.length, 5, "discarding language switch retires cue ownership");
  assert.equal(f.requests.some(r => r.url.startsWith("/api/tts/")), false, "cue must never mutate or call the TTS route");
  await f.drainSaves(); // Start retains the established realtime-settings save.
  assert.equal(Object.keys(f.state.serverDocument.settings).some(k => k.startsWith("mobmate.asrCue.")), false,
    "browser output permission/preferences are not a server format change");

  const lost = await create(), l = lost.run("cueTest"); await lost.prepare();
  await lost.run("startRealtime()"); await flush(); l.now = 101000;
  l.devices = []; await lost.offer(100010);
  assert.equal(l.played.length, 1, "missing selected device never falls back to default");
  assert.match(lost.elements["asr-cue-status"].textContent, /asrCue.unavailable/);
  l.devices = [{ kind: "audiooutput", deviceId: "headphones", label: "Personal headphones" }];
  await lost.offer(100020, { final: "再接続後の結果" }); assert.equal(l.played.length, 1, "reconnection alone must not re-arm sound");
  await lost.prepare(); l.failSink = true; await lost.offer(100030, { final: "出力拒否の結果" });
  assert.equal(l.played.length, 2, "sink rejection never calls play/default");
  l.failSink = false; await lost.prepare(); l.failPlay = true; await lost.offer(100040, { final: "再生拒否の結果" });
  assert.match(lost.elements["asr-cue-status"].textContent, /asrCue.unavailable/);
  l.failPlay = false; await lost.offer(100050, { final: "失敗後の新結果" }); assert.equal(l.played.length, 3, "play failure disables future notifications until explicit preview");

  const stale = await create(), s = stale.run("cueTest"); await stale.prepare();
  await stale.run("startRealtime()"); await flush(); s.now = 101000;
  stale.run("cueTest.holdSink = new Promise(resolve => cueTest.releaseSink = resolve)");
  const pending = stale.offer(100010); await flush();
  await stale.run("stopRealtime()"); s.releaseSink(); await pending; await flush();
  assert.equal(s.played.length, 1, "a sink operation completing after Stop must not play");
  s.holdSink = null; s.now = 102000; await stale.run("startRealtime()"); await flush(); s.now = 103000;
  s.channel.onmessage({ data: "changed" }); await stale.offer(102010);
  assert.equal(s.played.length, 1, "another tab's operation revokes local sound ownership");
  s.lock = false; await stale.run("startRealtime()"); await flush(); s.now = 104000; await stale.offer(103010);
  assert.equal(s.played.length, 1, "lock contention must stay silent");
  s.events.storage({ key: "mobmate.asrCue.enabled" });
  assert.equal(stale.elements["asr-cue-confirmed"].checked, false, "another tab's preference change invalidates the prior output confirmation");
  assert.equal(stale.elements["asr-cue-preview"].disabled, true);

  const noPrompt = await create(); noPrompt.run("delete navigator.mediaDevices.selectAudioOutput");
  await noPrompt.prepare(); assert.match(noPrompt.elements["asr-cue-status"].textContent, /asrCue.setup/);
  noPrompt.elements["asr-cue-output"].value = "headphones"; await noPrompt.elements["asr-cue-output"].fire("change");
  noPrompt.elements["asr-cue-confirmed"].checked = true; await noPrompt.elements["asr-cue-confirmed"].fire("change");
  await noPrompt.elements["asr-cue-preview"].fire("click"); await flush();
  assert.match(noPrompt.elements["asr-cue-status"].textContent, /asrCue.ready/, "existing enumerateDevices permission works without a new microphone request");
  const denied = await create(); denied.run("cueTest.failChoose = true");
  await denied.elements["asr-cue-choose"].fire("click");
  assert.equal(denied.run("cueTest.played.length"), 0);
  assert.match(denied.elements["asr-cue-status"].textContent, /asrCue.offUnavailable/);
  denied.run("delete Audio.prototype.setSinkId");
  await denied.elements["asr-cue-choose"].fire("click");
  assert.equal(denied.elements["asr-cue-preview"].disabled, true, "unsupported output API must never fall back to default playback");

  const sticky = await create(), se = sticky.elements, st = sticky.run("cueTest");
  await sticky.prepare(); await sticky.run("startRealtime()"); await flush(); st.now = 101000;
  se["asr-cue-volume"].value = "30"; await se["asr-cue-volume"].fire("change");
  assert.equal(st.played.length, 1, "volume change must not auto-play or demand another preview");
  assert.match(se["asr-cue-status"].textContent, /asrCue.ready/);
  await sticky.offer(100010); assert.equal(st.played.length, 2); assert.equal(st.played.at(-1).volume, .3);
  se["asr-cue-enabled"].checked = false; await se["asr-cue-enabled"].fire("change");
  assert.match(se["asr-cue-status"].textContent, /asrCue.offReady/);
  await sticky.offer(100020, { final: "OFF中の結果" }); assert.equal(st.played.length, 2, "OFF keeps preparation but never notifies");
  se["asr-cue-enabled"].checked = true; await se["asr-cue-enabled"].fire("change");
  assert.match(se["asr-cue-status"].textContent, /asrCue.ready/);
  await sticky.offer(100020, { final: "OFF中の結果" }); assert.equal(st.played.length, 2, "OFF receipt cannot replay on enable");
  await sticky.offer(100025, { final: "OFF中の結果" }); assert.equal(st.played.length, 2, "new timestamp cannot replay an OFF receipt on enable");
  await sticky.offer(100030, { final: "再開後の新結果" }); assert.equal(st.played.length, 3, "OFF to ON reuses previewed output on this page");
  await sticky.run("stopRealtime()"); await flush();
  assert.match(se["asr-cue-status"].textContent, /asrCue.ready/, "normal Stop retains output preparation");
  st.now = 102000; await sticky.run("startRealtime()"); await flush(); st.now = 103000;
  await sticky.offer(102010); assert.equal(st.played.length, 4, "next Start can notify without a repeated preview");
  st.events.devicechange(); await flush();
  await sticky.offer(102020, { final: "機器変更後の結果" }); assert.equal(st.played.length, 4, "device changes still require an explicit new preview");
  await sticky.prepare();
  se["asr-cue-confirmed"].checked = false; await se["asr-cue-confirmed"].fire("change");
  await sticky.offer(102030, { final: "確認解除後の結果" }); assert.equal(st.played.length, 5, "removing confirmation invalidates preparation");
  console.log("ASR receipt cue regression passed: explicit sink/preview, local session ownership, final boundaries, Stop/discard, races and fail-closed output; fake browser audio, no physical routing proof.");
}

async function wizardAddonGuardRegression() {
  const f = await wizardAddonFixture(), e = f.elements, api = f.run("fixtureAddon");
  f.run("openSetupWizard()"); await flush(); await e["setup-mic-test"].fire("click");
  f.state.stopResponses.push({ transportError: true });
  await e["setup-close-btn"].fire("click");
  assert.equal(f.run("setupMicUnconfirmed && setupMicBlocksOtherCapture() && !RealtimeSessionClient.requiresReconnect() && !RealtimeSessionClient.isMainActive()"), true);
  const before = api.posts.length;
  e["rt-language-select"].value = "en"; await f.run("AddonLanguageUi.followLanguage()");
  assert.equal(api.posts.slice(before).filter(p => p.url === "/api/addon-asr/language").length, 0, "unconfirmed wizard Stop must reject actual module follow");
  assert.equal(e["addon-asr-save-profile"].disabled, true);
  assert.match(e["addon-asr-status"].textContent, /addon.asr.wizardBlocked/);
  // Local search and candidate pick remain editing-only, not runtime mutations.
  await e["addon-asr-trigger"].fire("click"); e["addon-asr-search"].value = "Qwen";
  await e["addon-asr-search"].fire("input");
  await e["addon-asr-search"].fire("keydown", { key: "ArrowDown", preventDefault() {} });
  await e["addon-asr-search"].fire("keydown", { key: "Enter", preventDefault() {} });
  assert.equal(e["addon-asr-model"].value, "qwen3_asr_ja/q8");
  await e["addon-asr-save-profile"].fire("click"); await f.run("AddonLanguageUi.whenIdle()");
  assert.equal(api.posts.length, before, "blocked explicit action must not POST or replay");
  await e["setup-mic-reopen"].fire("click"); await flush();
  await e["setup-mic-retry-stop"].fire("click"); await f.run("AddonLanguageUi.whenIdle()");
  assert.equal(api.posts.length, before, "successful wizard retry must not replay a rejected add-on action");
  await f.run("AddonLanguageUi.followLanguage()");
  assert.equal(api.posts.at(-1).url, "/api/addon-asr/language");
  assert.equal(api.posts.at(-1).body.language, "en");

  // The target/profile read is asynchronous: entering the real wizard while it
  // is held must stop the not-yet-sent mutation, not retarget or replay it.
  e["rt-language-select"].value = "ja"; await f.run("AddonLanguageUi.refresh()");
  api.holdRead = { language: "ja", entered: deferred(), release: deferred() };
  const heldRead = api.holdRead, beforeQueued = api.posts.length;
  await e["addon-asr-save-profile"].fire("click"); await heldRead.entered.promise;
  await e["setup-mic-test"].fire("click");
  heldRead.release.resolve(); await f.run("AddonLanguageUi.whenIdle()");
  assert.equal(api.posts.slice(beforeQueued).filter(p => p.url === "/api/addon-asr/profile").length, 0, "wizard started during fresh profile read");
  assert.equal(e["addon-asr-save-profile"].disabled, true);
  await e["setup-close-btn"].fire("click");
  assert.equal(api.posts.slice(beforeQueued).filter(p => p.url === "/api/addon-asr/profile").length, 0, "recovery must not replay queued profile");

  const stopping = await wizardAddonFixture(), s = stopping.elements, stopApi = stopping.run("fixtureAddon");
  stopping.run("openSetupWizard()"); await flush(); await s["setup-mic-test"].fire("click");
  stopping.state.holdStop = deferred(); stopping.state.stopResponses.push({ transportError: true });
  const close = s["setup-close-btn"].fire("click"); await flush();
  assert.equal(stopping.run("!!setupMicStopping"), true);
  assert.equal(s["addon-asr-save-profile"].disabled, true);
  assert.match(s["addon-asr-status"].textContent, /addon.asr.wizardBlocked/);
  const postsWhileStopping = stopApi.posts.length;
  s["rt-language-select"].value = "en"; await stopping.run("AddonLanguageUi.followLanguage()");
  assert.equal(stopApi.posts.length, postsWhileStopping, "pending Stop must not send follow");
  stopping.state.holdStop.resolve(); await close;
  assert.equal(stopping.run("setupMicUnconfirmed"), true);

  // Even if the wizard starts and successfully stops while the accepted target
  // read is in flight, its old action cannot replay after the guard clears.
  const crossed = await wizardAddonFixture(), c = crossed.elements, crossApi = crossed.run("fixtureAddon");
  await c["addon-asr-trigger"].fire("click");
  await c["addon-asr-search"].fire("keydown", { key: "ArrowDown", preventDefault() {} });
  await c["addon-asr-search"].fire("keydown", { key: "Enter", preventDefault() {} });
  crossApi.holdRead = { language: "ja", entered: deferred(), release: deferred() };
  const crossRead = crossApi.holdRead, beforeCross = crossApi.posts.length;
  await c["addon-asr-save-profile"].fire("click"); await crossRead.entered.promise;
  crossed.run("openSetupWizard()"); await flush(); await c["setup-mic-test"].fire("click");
  await c["setup-close-btn"].fire("click");
  assert.equal(crossed.run("setupMicBlocksOtherCapture()"), false);
  crossRead.release.resolve(); await crossed.run("AddonLanguageUi.whenIdle()");
  assert.equal(crossApi.posts.slice(beforeCross).filter(p => p.url === "/api/addon-asr/profile").length, 0, "a completed wizard cycle must not release old queued work");

  // A profile already sent is not undone. Its payload stays bound to the
  // language and hint captured at the user's click, not the later selection.
  const sent = await wizardAddonFixture(), p = sent.elements, sentApi = sent.run("fixtureAddon");
  await p["addon-asr-trigger"].fire("click");
  await p["addon-asr-search"].fire("keydown", { key: "ArrowDown", preventDefault() {} });
  await p["addon-asr-search"].fire("keydown", { key: "Enter", preventDefault() {} });
  p["addon-asr-language-mode"].value = "auto";
  sentApi.holdPost = { entered: deferred(), release: deferred() };
  const heldPost = sentApi.holdPost;
  await p["addon-asr-save-profile"].fire("click"); await heldPost.entered.promise;
  sent.run("openSetupWizard()"); await flush(); await p["setup-mic-test"].fire("click");
  p["rt-language-select"].value = "en"; p["addon-asr-language-mode"].value = "requested";
  await sent.run("AddonLanguageUi.followLanguage()");
  heldPost.release.resolve(); await sent.run("AddonLanguageUi.whenIdle()");
  const saved = sentApi.posts.filter(post => post.url === "/api/addon-asr/profile");
  assert.equal(saved.length, 1); assert.equal(saved[0].body.language, "ja"); assert.equal(saved[0].body.languageMode, "auto");
  assert.equal(sentApi.profiles.ja.languageMode, "auto"); assert.equal(sentApi.profiles.en, undefined);
  assert.equal(sentApi.posts.filter(post => post.url === "/api/addon-asr/language").length, 0);
  await p["setup-close-btn"].fire("click");
  assert.equal(sentApi.profiles.ja.languageMode, "auto", "wizard stop must not roll back an already sent save");

  const queued = await wizardAddonFixture(), q = queued.elements, queueApi = queued.run("fixtureAddon");
  queueApi.holdPost = { action: "language", entered: deferred(), release: deferred() };
  const heldFollow = queueApi.holdPost;
  q["rt-language-select"].value = "en"; const firstFollow = queued.run("AddonLanguageUi.followLanguage()");
  await heldFollow.entered.promise;
  q["rt-language-select"].value = "ja"; const queuedFollow = queued.run("AddonLanguageUi.followLanguage()");
  queued.run("openSetupWizard()"); await flush(); await q["setup-mic-test"].fire("click");
  heldFollow.release.resolve(); await Promise.all([firstFollow, queuedFollow]);
  assert.equal(queueApi.posts.filter(post => post.url === "/api/addon-asr/language").map(post => post.body.language).join(","), "en", "a queued follow must be rejected when the real wizard starts");
  await q["setup-close-btn"].fire("click"); await queued.run("AddonLanguageUi.whenIdle()");
  assert.equal(queueApi.posts.filter(post => post.url === "/api/addon-asr/language").length, 1, "the rejected queued follow is not replayed after Stop");

  const failure = await wizardAddonFixture(), x = failure.elements, failureApi = failure.run("fixtureAddon");
  await x["addon-asr-trigger"].fire("click");
  await x["addon-asr-search"].fire("keydown", { key: "ArrowDown", preventDefault() {} });
  await x["addon-asr-search"].fire("keydown", { key: "Enter", preventDefault() {} });
  failureApi.failProfile = true; failureApi.holdPost = { entered: deferred(), release: deferred() };
  const failingPost = failureApi.holdPost;
  await x["addon-asr-save-profile"].fire("click"); await failingPost.entered.promise;
  failure.run("openSetupWizard()"); await flush(); await x["setup-mic-test"].fire("click");
  failingPost.release.resolve(); await failure.run("AddonLanguageUi.whenIdle()");
  assert.match(x["addon-asr-status"].textContent, /fixture.save_failure/, "an already sent save failure remains visible");
  assert.match(x["addon-asr-status"].textContent, /addon.asr.wizardBlocked/, "the wizard warning is not overwritten by the late failure");
  await x["setup-close-btn"].fire("click");
}

async function missingInputSelectionRegression() {
  const starts = f => f.requests.filter(r => r.url === "/api/realtime/start");
  const input = f => f.elements["rt-device-select"];
  const chosen = select => select.selectedOptions[0];
  const callStart = f => f.run("startRealtime()");

  const f = await fixture();
  await f.run("fetchAudioInputDevices()");
  assert.equal(input(f).value, "mic-old");
  await callStart(f);
  assert.equal(JSON.parse(starts(f).at(-1).options.body).captureDeviceId, "mic-old", "available explicit input reaches the real Start client");
  await f.drainSaves();
  assert.equal(f.state.serverDocument.settings["mobmate.captureDeviceId"], "mic-old", "the actual debounced PUT saved the explicit input in the fixture document");
  f.state.devices = [{ id: "mic-new", name: "New microphone" }];
  await f.run("fetchAudioInputDevices()");
  assert.equal(input(f).value, "mic-old");
  assert.equal(chosen(input(f)).dataset.captureUnavailable, "missing");
  assert.equal(f.storage.get("mobmate.captureDeviceId"), "mic-old");
  assert.equal(f.run("selectedDeviceIdOrName('rt-device-select')"), "mic-old");
  assert.equal(f.run("vcGetOptions().inputDeviceId"), "mic-old", "the missing label must never become another device identifier");
  const before = starts(f).length, writes = f.writes.length;
  await callStart(f);
  assert.equal(starts(f).length, before); assert.equal(f.writes.length, writes);
  assert.equal(f.elements["rt-status"].textContent, "ui.web.device.inputCannotStart");
  assert.equal(f.storage.get("mobmate.captureDeviceId"), "mic-old");
  await f.drainSaves(); assert.equal(f.state.serverDocument.settings["mobmate.captureDeviceId"], "mic-old");
  await f.run("startMobfyVoice()");
  assert.equal(f.requests.some(r => r.url === "/api/voice-conversion/start"), false, "Mobfy shares the main input and must not start an unavailable selection");

  f.run("openSetupWizard()"); await flush();
  const wizard = f.elements["setup-mic-select"];
  assert.equal(wizard.value, "mic-old"); assert.equal(chosen(wizard).dataset.captureUnavailable, "missing");
  const beforeWizardWrites = f.writes.length;
  await f.elements["setup-mic-test"].fire("click");
  assert.equal(starts(f).length, before); assert.equal(f.writes.length, beforeWizardWrites);
  assert.equal(f.elements["setup-mic-result"].textContent, "ui.web.device.inputCannotStart");
  await f.elements["setup-close-btn"].fire("click");

  // Main selection is deliberately not persisted until Start. A returning must
  // not replace a newer, still-unsaved B or explicit default selection.
  input(f).value = "mic-new"; await input(f).fire("change");
  f.state.devices = [{ id: "mic-old" }, { id: "mic-new" }];
  await f.run("fetchAudioInputDevices()");
  assert.equal(input(f).value, "mic-new"); assert.equal(f.storage.get("mobmate.captureDeviceId"), "mic-old");
  await callStart(f); assert.equal(JSON.parse(starts(f).at(-1).options.body).captureDeviceId, "mic-new");
  input(f).value = ""; await input(f).fire("change"); await callStart(f);
  assert.equal(JSON.parse(starts(f).at(-1).options.body).captureDeviceId, "");
  assert.equal(f.storage.get("mobmate.captureDeviceId"), "");
  await f.drainSaves(); assert.equal(f.state.serverDocument.settings["mobmate.captureDeviceId"], "");
  await f.run("fetchAudioInputDevices()"); assert.equal(input(f).value, "");

  const empty = await fixture(); empty.run("RealtimeSessionClient.setSelectedCaptureDeviceId('')");
  await empty.run("fetchAudioInputDevices()"); await callStart(empty);
  assert.equal(JSON.parse(starts(empty).at(-1).options.body).captureDeviceId, "", "an originally empty preference remains a valid default");
  const unnamed = await fixture(); unnamed.state.devices = [{ id: "mic-old", name: "" }];
  await unnamed.run("fetchAudioInputDevices()");
  assert.equal(chosen(input(unnamed)).textContent, "mic-old", "an empty name still displays the existing device ID");

  for (const response of [{ httpOk: false, body: { error: "offline" } }, { body: { ok: false, error: "native" } },
    { transportError: true }, { body: { ok: true } }, { body: { ok: true, devices: [null] } }, { malformed: true }]) {
    const failed = await fixture(); await failed.run("fetchAudioInputDevices()");
    failed.state.deviceResponses.push(response); await failed.run("fetchAudioInputDevices()");
    assert.equal(input(failed).value, "mic-old"); assert.equal(chosen(input(failed)).dataset.captureUnavailable, "unverified");
    assert.equal(failed.storage.get("mobmate.captureDeviceId"), "mic-old");
    await callStart(failed); assert.equal(starts(failed).length, 0);
    failed.state.deviceResponses.push(response); failed.run("openSetupWizard()"); await flush();
    assert.equal(failed.elements["setup-mic-select"].value, "mic-old");
    assert.equal(chosen(failed.elements["setup-mic-select"]).dataset.captureUnavailable, "unverified");
    assert.equal(failed.elements["setup-mic-result"].textContent, "ui.web.device.inputEnumerationFailed");
    await failed.elements["setup-mic-test"].fire("click"); assert.equal(starts(failed).length, 0);
    assert.equal(failed.storage.get("mobmate.captureDeviceId"), "mic-old");
  }
  const startupFailure = await fixture();
  input(startupFailure).innerHTML = ""; startupFailure.state.deviceResponses.push({ transportError: true });
  await startupFailure.run("fetchAudioInputDevices()");
  assert.equal(input(startupFailure).value, "mic-old");
  assert.equal(chosen(input(startupFailure)).dataset.captureUnavailable, "unverified");
  await callStart(startupFailure); assert.equal(starts(startupFailure).length, 0);
  assert.equal(startupFailure.storage.get("mobmate.captureDeviceId"), "mic-old");

  const wizardMissing = await fixture(); await wizardMissing.run("fetchAudioInputDevices()");
  wizardMissing.state.devices = [{ id: "mic-new" }]; wizardMissing.run("openSetupWizard()"); await flush();
  assert.equal(chosen(wizardMissing.elements["setup-mic-select"]).dataset.captureUnavailable, "missing");
  assert.equal(chosen(input(wizardMissing)).dataset.captureUnavailable, "missing", "Wizard enumeration must invalidate the matching main selection too");
  await wizardMissing.elements["setup-close-btn"].fire("click"); await callStart(wizardMissing);
  assert.equal(starts(wizardMissing).length, 0); assert.equal(wizardMissing.storage.get("mobmate.captureDeviceId"), "mic-old");
  const wizardFailed = await fixture(); await wizardFailed.run("fetchAudioInputDevices()");
  wizardFailed.state.deviceResponses.push({ transportError: true }); wizardFailed.run("openSetupWizard()"); await flush();
  assert.equal(chosen(input(wizardFailed)).dataset.captureUnavailable, "unverified");
  await wizardFailed.elements["setup-close-btn"].fire("click"); await callStart(wizardFailed);
  assert.equal(starts(wizardFailed).length, 0);

  for (const delayed of [{ body: { ok: true, devices: [{ id: "mic-old" }] } }, { transportError: true }]) {
    const race = await fixture(); await race.run("fetchAudioInputDevices()");
    const held = deferred(); race.state.deviceResponses.push(held);
    const older = race.run("fetchAudioInputDevices()"); await flush();
    assert.equal(chosen(input(race)).dataset.captureUnavailable, "unverified");
    input(race).value = "mic-new"; await input(race).fire("change");
    race.state.deviceResponses.push({ body: { ok: true, devices: [{ id: "mic-old" }, { id: "mic-new" }] } });
    await race.run("fetchAudioInputDevices()");
    assert.equal(input(race).value, "mic-new"); assert.equal(race.storage.get("mobmate.captureDeviceId"), "mic-old");
    held.resolve(delayed); await older;
    assert.equal(input(race).value, "mic-new"); assert.equal(chosen(input(race)).dataset.captureUnavailable, undefined);
    assert.equal(race.elements["rt-device-status"].className, "status ok");
    await callStart(race); assert.equal(JSON.parse(starts(race).at(-1).options.body).captureDeviceId, "mic-new");
  }
  const selectionRace = await fixture(); await selectionRace.run("fetchAudioInputDevices()");
  const prior = deferred(); selectionRace.state.deviceResponses.push(prior);
  const pending = selectionRace.run("fetchAudioInputDevices()"); await flush();
  input(selectionRace).value = "mic-new"; await input(selectionRace).fire("change");
  prior.resolve({ body: { ok: true, devices: [{ id: "mic-old" }] } }); await pending;
  assert.equal(input(selectionRace).value, "mic-new");
  assert.equal(chosen(input(selectionRace)).dataset.captureUnavailable, "unverified");
  assert.equal(selectionRace.storage.get("mobmate.captureDeviceId"), "mic-old");
  await callStart(selectionRace); assert.equal(starts(selectionRace).length, 0);

  const wizardNew = await fixture();
  wizardNew.state.devices = [{ id: "mic-old" }]; await wizardNew.run("fetchAudioInputDevices()");
  wizardNew.state.devices = [{ id: "mic-old" }, { id: "mic-new", name: "New microphone" }];
  wizardNew.run("openSetupWizard()"); await flush();
  wizardNew.elements["setup-mic-select"].value = "mic-new"; await wizardNew.elements["setup-mic-select"].fire("change");
  assert.equal(input(wizardNew).value, "mic-new"); assert.equal(chosen(input(wizardNew)).dataset.captureUnavailable, undefined);
  assert.equal(wizardNew.storage.get("mobmate.captureDeviceId"), "mic-new");
  await wizardNew.elements["setup-close-btn"].fire("click"); await callStart(wizardNew);
  assert.equal(JSON.parse(starts(wizardNew).at(-1).options.body).captureDeviceId, "mic-new");
  const wizardOlder = await fixture(); await wizardOlder.run("fetchAudioInputDevices()");
  const oldWizardList = deferred(); wizardOlder.state.deviceResponses.push(oldWizardList); wizardOlder.run("openSetupWizard()"); await flush();
  await wizardOlder.run("fetchAudioInputDevices()");
  oldWizardList.resolve({ body: { ok: true, devices: [{ id: "mic-new" }] } }); await flush();
  assert.equal(chosen(input(wizardOlder)).dataset.captureUnavailable, undefined, "older Wizard response must not invalidate a newer main update");
  assert.equal(chosen(wizardOlder.elements["setup-mic-select"]).dataset.captureUnavailable, "unverified");
  const wizardRace = await fixture(); await wizardRace.run("fetchAudioInputDevices()");
  const held = deferred(); wizardRace.state.deviceResponses.push(held); wizardRace.run("openSetupWizard()"); await flush();
  assert.equal(wizardRace.elements["setup-mic-select"].value, "mic-old");
  wizardRace.elements["setup-mic-select"].value = ""; await wizardRace.elements["setup-mic-select"].fire("change");
  held.resolve({ body: { ok: true, devices: [{ id: "mic-old" }] } }); await flush();
  assert.equal(wizardRace.elements["setup-mic-select"].value, "");
  assert.equal(input(wizardRace).value, ""); assert.equal(wizardRace.storage.get("mobmate.captureDeviceId"), "");
  await wizardRace.elements["setup-mic-test"].fire("click");
  assert.equal(JSON.parse(starts(wizardRace).at(-1).options.body).captureDeviceId, "");
}

async function wizardStopRecoveryRegression() {
  const count = (f, path) => f.requests.filter(r => r.url === path).length;
  const start = "/api/realtime/start", stop = "/api/realtime/stop";
  for (const failure of [{ transportError: true }, { httpOk: false }, { body: { ok: false } }]) {
    const f = await fixture(), e = f.elements;
    f.run("openSetupWizard()"); await flush();
    await e["setup-mic-test"].fire("click");
    f.state.stopResponses.push(failure);
    await e["setup-close-btn"].fire("click");
    assert.equal(e["setup-overlay"].classList.contains("open"), false);
    assert.equal(f.run("setupMicUnconfirmed"), true);
    assert.equal(e["setup-mic-warning"].hidden, false);
    assert.equal(count(f, start), 1); assert.equal(count(f, stop), 1);
    await f.run("startRealtime()"); await f.run("startRealtime(true)"); await f.run("startMobfyVoice()"); await f.run("stopRealtime()");
    f.run(section("async function stopRealtimeDirectForModeSwitch()", "async function stopMobfyDirectForModeSwitch()"));
    f.run(section("async function vcToggle()", "async function vcLoadModels()"));
    const token = f.run("realtimePollToken");
    await f.run("stopRealtimeDirectForModeSwitch()"); await f.run("vcToggle()");
    assert.equal(f.run("realtimePollToken"), token, "mode switching must not send an unrelated Stop or reset main polling");
    assert.equal(count(f, start), 1); assert.equal(count(f, stop), 1);
    assert.equal(count(f, "/api/voice-conversion/start"), 0);
    await e["setup-mic-reopen"].fire("click"); await flush();
    assert.equal(e["setup-mic-result"].textContent, "setup.s2.stopUnconfirmed");
    assert.equal(e["setup-mic-retry-stop"].hidden, false);
    await e["setup-mic-test"].fire("click");
    await e["setup-next-btn"].fire("click"); await e["setup-skip-btn"].fire("click");
    assert.equal(f.run("setupCurrentStep"), 0); assert.equal(count(f, start), 1); assert.equal(count(f, stop), 1);
    await e["setup-mic-retry-stop"].fire("click");
    assert.equal(f.run("setupMicUnconfirmed"), false); assert.equal(e["setup-mic-warning"].hidden, true);
    assert.equal(count(f, stop), 2); assert.equal(count(f, start), 1, "successful retry must not auto-start");
    await e["setup-mic-test"].fire("click"); assert.equal(count(f, start), 2);
    await e["setup-close-btn"].fire("click");
  }

  // Navigation and selection are implicit stop callers, not retry gestures.
  const f = await fixture(), e = f.elements;
  f.run("openSetupWizard()"); await flush(); await e["setup-mic-test"].fire("click");
  f.state.stopResponses.push({ transportError: true });
  await e["setup-next-btn"].fire("click");
  assert.equal(f.run("setupCurrentStep"), 0); assert.equal(e["setup-mic-result"].textContent, "setup.s2.stopUnconfirmed");
  e["setup-mic-select"].value = "mic-new"; await e["setup-mic-select"].fire("change");
  await f.drainSaves();
  assert.equal(f.storage.get("mobmate.captureDeviceId"), "mic-new");
  assert.equal(f.state.serverDocument.settings["mobmate.captureDeviceId"], "mic-new");
  await e["setup-skip-btn"].fire("click"); await e["setup-back-btn"].fire("click");
  f.run("renderSetupStep()"); await flush();
  assert.equal(count(f, stop), 1); assert.equal(e["setup-mic-result"].textContent, "setup.s2.stopUnconfirmed");
  await e["setup-mic-retry-stop"].fire("click");
  await e["setup-next-btn"].fire("click"); assert.equal(f.run("setupCurrentStep"), 1);
  await e["setup-back-btn"].fire("click"); assert.equal(f.run("setupCurrentStep"), 0);

  // A late poll cannot overwrite a failed stop, and simultaneous close/navigation
  // must share a pending stop rather than issue an automatic retry.
  const race = await fixture(), r = race.elements;
  race.run("openSetupWizard()"); await flush(); await r["setup-mic-test"].fire("click");
  const heldPoll = deferred(); race.state.statusResponses.push(heldPoll);
  const poll = race.run("pollSetupMic()"); await flush();
  race.state.holdStop = deferred(); race.state.stopResponses.push({ transportError: true });
  const navigation = r["setup-next-btn"].fire("click"); await flush();
  assert.equal(r["setup-mic-result"].textContent, "setup.s2.stopping");
  await race.run("startRealtime()");
  const close = r["setup-close-btn"].fire("click"); await flush();
  assert.equal(count(race, stop), 1);
  race.state.holdStop.resolve(); await navigation; await close;
  heldPoll.resolve({ state: "recording", captureRms: 5000, final: "late success" }); await poll;
  assert.equal(r["setup-mic-result"].textContent, "setup.s2.stopUnconfirmed");
  assert.equal(r["setup-mic-warning"].hidden, false); assert.equal(race.timers.size, 0);
  assert.equal(count(race, start), 1); assert.equal(count(race, stop), 1);

  const lateStart = await fixture(), l = lateStart.elements;
  lateStart.run("openSetupWizard()"); await flush(); lateStart.state.holdStart = deferred();
  const starting = l["setup-mic-test"].fire("click"); await flush();
  lateStart.state.stopResponses.push({ transportError: true });
  const closing = l["setup-close-btn"].fire("click"); await flush();
  assert.equal(l["setup-mic-result"].textContent, "setup.s2.stopping");
  lateStart.state.holdStart.resolve(); await starting; await closing;
  assert.equal(count(lateStart, start), 1); assert.equal(count(lateStart, stop), 1);
  assert.equal(lateStart.timers.size, 0); assert.equal(l["setup-mic-warning"].hidden, false);
  assert.equal(l["setup-mic-result"].textContent, "setup.s2.stopUnconfirmed", "late Start success must not overwrite failed Stop");

  const held = await fixture(), h = held.elements;
  held.run("openSetupWizard()"); await flush(); await h["setup-mic-test"].fire("click");
  held.state.holdStop = deferred(); held.state.stopResponses.push({ transportError: true });
  const pendingClose = h["setup-close-btn"].fire("click"); await flush();
  assert.equal(h["setup-overlay"].classList.contains("open"), false, "Close must not trap the user behind a pending Stop");
  assert.equal(h["setup-mic-warning"].hidden, false); assert.equal(h["setup-mic-warning-text"].textContent, "setup.s2.stopping");
  await held.run("startRealtime()"); assert.equal(count(held, start), 1);
  await h["setup-mic-reopen"].fire("click"); await flush();
  assert.equal(h["setup-overlay"].classList.contains("open"), true);
  assert.equal(h["setup-mic-result"].textContent, "setup.s2.stopping");
  assert.equal(h["setup-mic-test"].disabled, true); assert.equal(count(held, stop), 1);
  held.state.holdStop.resolve(); await pendingClose;
  assert.equal(h["setup-mic-result"].textContent, "setup.s2.stopUnconfirmed");
  assert.equal(h["setup-mic-warning-text"].textContent, "setup.s2.stopWarning");
  assert.equal(h["setup-mic-retry-stop"].hidden, false); assert.equal(count(held, stop), 1);

  const reopened = await fixture(), q = reopened.elements;
  reopened.run("openSetupWizard()"); await flush(); await q["setup-mic-test"].fire("click");
  reopened.state.holdStop = deferred();
  const pendingNext = q["setup-next-btn"].fire("click"); await flush();
  const interruptedClose = q["setup-close-btn"].fire("click"); await flush();
  await q["setup-mic-reopen"].fire("click"); await flush();
  reopened.state.holdStop.resolve(); await pendingNext; await interruptedClose;
  assert.equal(reopened.run("setupCurrentStep"), 0, "an old pending Next must not move a reopened wizard");
  assert.equal(q["setup-mic-warning"].hidden, true); assert.equal(count(reopened, stop), 1);

  const changing = await fixture(), c = changing.elements;
  changing.run("openSetupWizard()"); await flush(); await c["setup-mic-test"].fire("click");
  changing.state.stopResponses.push({ body: { ok: false } });
  c["setup-mic-select"].value = "mic-new"; await c["setup-mic-select"].fire("change");
  assert.equal(changing.storage.get("mobmate.captureDeviceId"), "mic-new");
  assert.equal(c["setup-mic-result"].textContent, "setup.s2.stopUnconfirmed");
  await c["setup-mic-test"].fire("click"); assert.equal(count(changing, start), 1);
  await c["setup-mic-retry-stop"].fire("click"); assert.equal(count(changing, stop), 2);
}

async function wizardStartUncertaintyRegression() {
  const count = (f, path) => f.requests.filter(request => request.url === path).length;
  for (const response of [{ transportError: true }, { httpOk: false }, { body: { ok: false } }]) {
    const f = await fixture(), e = f.elements;
    f.run("openSetupWizard()"); await flush();
    f.state.startResponses.push(response);
    await e["setup-mic-test"].fire("click");
    if (!response.transportError) {
      assert.equal(f.run("setupMicUnconfirmed"), false, "an explicit Start rejection does not claim ownership of an existing session");
      assert.equal(e["setup-mic-result"].textContent, "setup.s2.failed");
      await e["setup-close-btn"].fire("click");
      assert.equal(count(f, "/api/realtime/stop"), 0, "do not stop a different session after a rejected Start");
      continue;
    }
    assert.equal(f.run("setupMicUnconfirmed"), true, "a lost Start response is not proof that capture never began");
    assert.equal(e["setup-mic-test"].disabled, true);
    await e["setup-close-btn"].fire("click");
    assert.equal(e["setup-mic-warning"].hidden, false);
    assert.equal(f.run("setupMicBlocksOtherCapture()"), true);
    assert.equal(count(f, "/api/realtime/stop"), 0, "Close must not implicitly retry an already unconfirmed operation");
    await e["setup-mic-reopen"].fire("click"); await flush();
    await e["setup-mic-test"].fire("click"); await f.run("startRealtime()");
    assert.equal(count(f, "/api/realtime/start"), 1, "uncertain Start must retain the capture guard after reopening");
    await e["setup-mic-retry-stop"].fire("click");
    assert.equal(count(f, "/api/realtime/stop"), 1);
    assert.equal(f.run("setupMicUnconfirmed"), false);
    assert.equal(count(f, "/api/realtime/start"), 1, "confirmed Stop must not auto-start");
    await e["setup-mic-test"].fire("click");
    assert.equal(count(f, "/api/realtime/start"), 2);
    await e["setup-close-btn"].fire("click");
  }
  const pending = await fixture(), p = pending.elements;
  pending.run("openSetupWizard()"); await flush();
  pending.state.holdStart = deferred(); pending.state.startResponses.push({ transportError: true });
  pending.state.stopResponses.push({ transportError: true });
  const starting = p["setup-mic-test"].fire("click"); await flush();
  const closing = p["setup-close-btn"].fire("click"); await flush();
  assert.equal(p["setup-overlay"].classList.contains("open"), false);
  pending.state.holdStart.resolve(); await starting; await closing;
  assert.equal(count(pending, "/api/realtime/stop"), 1, "Close accepted behind Start must still attempt Stop after its response is lost");
  assert.equal(pending.run("setupMicUnconfirmed"), true);
  assert.equal(p["setup-mic-warning"].hidden, false);
  await p["setup-mic-reopen"].fire("click"); await flush();
  await p["setup-mic-retry-stop"].fire("click");
  assert.equal(count(pending, "/api/realtime/stop"), 2);
  assert.equal(pending.run("setupMicBlocksOtherCapture()"), false);
}

async function wizardRetiredMainPollRegression() {
  const f = await fixture(), e = f.elements;
  await f.run("startRealtime()"); await f.drainSaves();
  const response = deferred(); f.state.statusResponses.push(response);
  f.run(section("async function pollRealtime(token)", "/* --- Realtime Route Bench --- */"));
  f.run("globalThis.wizardRetiredPollOutputs = []; updateRealtimeResults = data => wizardRetiredPollOutputs.push(data); meterStop = () => {}; spkStop = () => {}; waveClear = () => {};");
  const poll = f.run("pollRealtime(realtimePollToken)");
  const [timer, callback] = [...f.pollTimers.entries()][0];
  f.pollTimers.delete(timer); callback(); await flush();
  f.run("openSetupWizard()"); await flush();
  await e["setup-mic-test"].fire("click"); await e["setup-close-btn"].fire("click");
  assert.equal(f.run("setupMicBlocksOtherCapture()"), false);
  const stoppedStatus = e["rt-status"].textContent;
  response.resolve({ state: "completed", final: "retired main result", vadState: "speech", captureRms: 900 });
  await poll;
  assert.equal(f.run("wizardRetiredPollOutputs.length"), 0, "old main output must stay retired after the wizard capture guard clears");
  assert.equal(e["rt-status"].textContent, stoppedStatus);
  assert.equal(f.requests.filter(request => request.url === "/api/realtime/stop").length, 1, "retired main cleanup must not issue another Stop");
  await f.run("startRealtime()");
  assert.equal(f.requests.filter(request => request.url === "/api/realtime/start").length, 3, "a subsequent explicit main Start still works");
}

// Use the page's recording and result helpers as well as the actual client and
// poll. Only device/API responses, clocks and drawing are faked here.
function installMainRecordingUi(f) {
  for (const id of ["rt-partial", "rt-live-vad", "rt-live-rms", "rt-live-engine", "rt-final-history", "rt-final-tabs"]) {
    const node = f.document.createElement("div"); node.id = id; f.elements[id] = node; f.elements.main.appendChild(node);
  }
  f.run(`
    globalThis.noiseWarmupTimer = null; globalThis.noiseWarmupGeneration = 0;
    globalThis.lastRealtimeFinal = ""; globalThis.lastRealtimePartial = "";
    globalThis.lastTranslatedFinalShown = null; globalThis.lastHistoryOriginalText = null;
    globalThis.lastRealtimeFinalDetectedAtMs = 0; globalThis.lastRealtimeFinalUiAgeMs = 0; globalThis.lastRealtimeHistoryFinalDetectedAtMs = 0; globalThis.realtimeFinalTabs = [];
    globalThis.uiStops = { meter: 0, speaker: 0, wave: 0 };
    globalThis.updateMainActionLabels = () => {}; globalThis.setRealtimeGuide = key => { globalThis.guide = key; };
    globalThis.meterStop = () => uiStops.meter++; globalThis.spkStop = () => uiStops.speaker++; globalThis.waveClear = () => uiStops.wave++;
    globalThis.meterSetRms = () => {}; globalThis.waveUpdate = () => {}; globalThis.waveDraw = () => {};
    globalThis.engineDisplayName = name => name; globalThis.ensureSenseVoiceEngineOption = () => {};
  `);
  for (const code of [section("function setRecordingButtons(", "function formatVcclientInstallStatus("),
    section("function renderRealtimeTabs(", "/* ── Translation"),
    section("function formatRealtimeLatencyMs(", "const AsrAcceptanceCue ="),
    section("async function pollRealtime(token)", "/* --- Realtime Route Bench --- */")]) f.run(code);
}

// The actual shared warmup helpers, main handlers, result helpers and session
// client run together; only the clock, drawing and HTTP boundary are faked.
function installWarmupStopUi(f) {
  installMainRecordingUi(f);
  f.run(`
    globalThis.intervalCallbacks = new Map(); globalThis.nextInterval = 0;
    globalThis.setInterval = callback => { const id = ++nextInterval; intervalCallbacks.set(id, callback); return id; };
    globalThis.clearInterval = id => intervalCallbacks.delete(id);
    globalThis.pollRealtime = () => {}; globalThis.checkReviewPrompt = () => {};
  `);
  for (const code of [section("function updateMainActionLabels(", "async function stopRealtimeDirectForModeSwitch()"),
    section("async function startMainMode()", "/* --- Config --- */")]) f.run(code);
  f.elements["rt-start-btn"].addEventListener("click", f.run("startMainMode"));
  f.elements["rt-stop-btn"].addEventListener("click", f.run("stopMainMode"));
}

async function warmupStopRegression() {
  const count = (f, path) => f.requests.filter(request => request.url === path).length;
  const stop = "/api/realtime/stop", start = "/api/realtime/start";
  for (const failure of [null, { httpOk: false }, { body: { ok: false } }, { transportError: true }, { body: {} }, { body: { state: "not-a-native-state" } }, { malformed: true }]) {
    const f = await fixture(), e = f.elements; installWarmupStopUi(f);
    await e["rt-start-btn"].fire("click");
    const oldTimer = f.run("noiseWarmupTimer"), oldCallback = f.run("intervalCallbacks.get(noiseWarmupTimer)");
    assert.ok(oldTimer && oldCallback); assert.equal(e["rt-stop-btn"].disabled, false);
    assert.match(e["rt-stop-btn"].textContent, /vc.btn.stop.*ui.web.button.noiseWarmup/);
    assert.equal(f.run("guide"), "ui.web.guide.noiseWarmup");
    oldCallback(); assert.match(e["rt-stop-btn"].textContent, /ui.web.button.noiseWarmup/);
    f.run("updateRealtimeResults({state:'recording', final:'confirmed history', selectedEngine:'qwen', captureRms:123, vadState:'speech'})");
    f.state.holdStop = deferred(); if (failure) f.state.stopResponses.push(failure);
    const pending = e["rt-stop-btn"].fire("click"); await flush();
    assert.equal(count(f, stop), 1); assert.equal(e["rt-stop-btn"].disabled, true);
    assert.equal(e["rt-start-btn"].style.display, "none");
    assert.equal(e["rt-status"].textContent, "ui.web.status.stopping");
    assert.equal(f.run("guide"), "ui.web.status.stopping");
    assert.equal(f.run("noiseWarmupTimer"), null); assert.equal(f.run(`intervalCallbacks.has(${oldTimer})`), false);
    for (let i = 0; i < 9; i++) oldCallback();
    assert.equal(e["rt-stop-btn"].disabled, true, "a retired timer cannot release a pending Stop");
    assert.equal(f.run("guide"), "ui.web.status.stopping");
    f.state.holdStop.resolve(); await pending;
    assert.equal(e["rt-live-engine"].textContent, "qwen");
    assert.equal(e["rt-final-history"].children[0].children[0].textContent, "✓ confirmed history");
    if (failure) {
      assert.equal(e["rt-start-btn"].style.display, "none");
      assert.equal(e["rt-stop-btn"].style.display, "block"); assert.equal(e["rt-stop-btn"].disabled, false);
      assert.equal(f.run("guide"), "ui.web.guide.stopUnconfirmed");
      for (let i = 0; i < 9; i++) oldCallback();
      assert.equal(f.run("guide"), "ui.web.guide.stopUnconfirmed");
      assert.equal(e["rt-stop-btn"].disabled, false);
      await e["rt-stop-btn"].fire("click");
      assert.equal(count(f, stop), 2, "only an explicit retry sends another Stop");
    }
    assert.equal(e["rt-start-btn"].style.display, "block");
    assert.equal(e["rt-stop-btn"].style.display, "none"); assert.equal(f.run("guide"), "ui.web.guide.stopped");
    for (let i = 0; i < 9; i++) oldCallback();
    assert.equal(f.run("guide"), "ui.web.guide.stopped"); assert.equal(count(f, start), 1);
    await e["rt-start-btn"].fire("click");
    const newTimer = f.run("noiseWarmupTimer"); assert.notEqual(newTimer, oldTimer);
    const nextLabel = e["rt-stop-btn"].textContent;
    for (let i = 0; i < 9; i++) oldCallback();
    assert.equal(e["rt-stop-btn"].textContent, nextLabel); assert.equal(e["rt-stop-btn"].disabled, false);
    assert.equal(f.run("noiseWarmupTimer"), newTimer); assert.equal(f.run("guide"), "ui.web.guide.noiseWarmup");
    assert.equal(count(f, start), 2, "only the next explicit Start restarts capture");
  }

  // Windows returns a final poll state rather than Linux's {ok:true}; its
  // final-recognition error is not a failed capture Stop.
  const windows = await fixture(); installWarmupStopUi(windows);
  await windows.elements["rt-start-btn"].fire("click");
  windows.state.stopResponses.push({ body: { state: "completed", error: "final recognition failed" } });
  await windows.elements["rt-stop-btn"].fire("click");
  assert.equal(windows.elements["rt-start-btn"].style.display, "block");
  assert.equal(windows.elements["rt-status"].className, "status err");

  // A Stop accepted while Start is pending continues to win through the real
  // serialized client and poll token: no late warmup or automatic restart.
  const starting = await fixture(), s = starting.elements; installWarmupStopUi(starting);
  starting.state.holdStart = deferred(); starting.state.holdStop = deferred();
  const startPending = s["rt-start-btn"].fire("click"); await flush();
  const stopPending = s["rt-stop-btn"].fire("click"); await flush();
  starting.state.holdStart.resolve(); await startPending; await flush();
  assert.equal(starting.run("noiseWarmupTimer"), null); assert.equal(s["rt-stop-btn"].disabled, true);
  starting.state.holdStop.resolve(); await stopPending;
  assert.equal(count(starting, start), 1); assert.equal(count(starting, stop), 1);
  assert.equal(s["rt-start-btn"].style.display, "block");
}

async function warmupMobfyDefaultRegression() {
  for (const caller of ["startMobfyVoice()", "vcToggle()"] ) {
    const f = await fixture(), e = f.elements; installWarmupStopUi(f);
    f.run(`globalThis.currentMode = "mobfy"; globalThis.vcRunning = false; globalThis.vcPollingTimer = null;
      globalThis.vcSelectedCatalogModel = () => null; globalThis.vcHasSelectedModel = () => false;
      globalThis.setMobfyWasRunning = () => {}; globalThis.vcFetchStatus = () => {};
      globalThis.mobfyActions = []; const priorFetch = fetch;
      globalThis.fetch = async (url, options) => {
        if (!url.startsWith('/api/voice-conversion/')) return priorFetch(url, options);
        mobfyActions.push(url); return { ok:true, json:async () => ({}) };
      };`);
    for (const code of [section("function isMobfyMode()", "function switchMode("),
      section("async function stopRealtimeDirectForModeSwitch()", "async function stopMobfyDirectForModeSwitch()"),
      section("async function stopMobfyVoice()", "async function vcLoadModels()")]) f.run(code);
    if (caller === "vcToggle()") f.run("setRecordingButtons(true)");
    await f.run(caller);
    const callback = f.run("intervalCallbacks.get(noiseWarmupTimer)");
    assert.ok(callback); assert.equal(e["rt-stop-btn"].disabled, true, `${caller} retains the shared default`);
    assert.equal(e["rt-stop-btn"].textContent, "ui.web.button.noiseWarmup");
    for (let i = 0; i < 8; i++) callback();
    assert.equal(e["rt-stop-btn"].disabled, false); assert.equal(f.run("guide"), "vc.guide.runningPassthrough");
    await f.run("stopMobfyVoice()");
    assert.equal(f.run("mobfyActions.filter(path => path.endsWith('/stop')).length"), 1);
    assert.equal(e["rt-start-btn"].style.display, "block");
  }
}

async function stepPollTimer(f) {
  const timer = [...f.pollTimers.entries()][0];
  assert.ok(timer, "main poll must schedule its next status request");
  f.pollTimers.delete(timer[0]); timer[1](); await flush();
}

async function wizardRejectedStartMainPollRegression() {
  for (const [response, oldResponse] of [[{ httpOk: false }, { state: "recording", partial: "held old status", captureRms: 100 }],
    [{ body: { ok: false } }, { transportError: true }]]) {
    const f = await wizardAddonFixture(), e = f.elements;
    await f.run("startRealtime()"); await f.drainSaves(); installMainRecordingUi(f);
    f.run("setRecordingButtons(true)");
    const oldStatus = deferred(); f.state.statusResponses.push(oldStatus);
    const poll = f.run("pollRealtime(realtimePollToken)"); await stepPollTimer(f);
    f.run("openSetupWizard()"); await flush();
    f.state.holdStart = deferred(); f.state.startResponses.push(response);
    const starting = e["setup-mic-test"].fire("click"); await flush();
    oldStatus.resolve(oldResponse); await flush();
    assert.notEqual(e["rt-partial"].textContent, "held old status", "pending wizard must not publish old status");
    const statusCount = f.requests.filter(r => r.url === "/api/realtime/status").length;
    await stepPollTimer(f);
    assert.equal(f.requests.filter(r => r.url === "/api/realtime/status").length, statusCount + 1, "pending Start keeps main polling without publishing it");
    assert.notEqual(e["rt-partial"].textContent, "held old status");
    f.state.holdStart.resolve(); await starting;
    f.state.statusResponses.push({ state: "recording", partial: "continued main result", captureRms: 200 });
    await stepPollTimer(f);
    assert.equal(e["rt-partial"].textContent, "continued main result", "new main status must appear after explicit rejection");
    assert.equal(f.run("RealtimeSessionClient.isMainActive()"), true, "explicit rejection must retain the prior main owner");
    f.run("realtimePollToken++"); await stepPollTimer(f); await poll;
    const before = f.requests.filter(r => r.url.startsWith("/api/realtime/stop")).length;
    f.run(`globalThis.followTimeline = []; const previousFetchForTimeline = fetch;
      globalThis.fetch = async (url, options) => { followTimeline.push(url); return previousFetchForTimeline(url, options); };`);
    e["rt-language-select"].value = "en"; await f.run("AddonLanguageUi.followLanguage()");
    const timeline = Array.from(f.run("followTimeline"));
    assert.ok(timeline.indexOf("/api/realtime/stop?discard=true") >= 0 && timeline.indexOf("/api/realtime/stop?discard=true") < timeline.indexOf("/api/addon-asr/language"), "real follow must stop main before changing language");
    assert.equal(f.requests.filter(r => r.url.startsWith("/api/realtime/stop")).length, before + 1);
  }

  // A newer Stop accepted behind a pending rejected Start must not regain the
  // main resume right while Stop itself is still in flight.
  const stopped = await fixture(), s = stopped.elements;
  await stopped.run("RealtimeSessionClient.startRealtimeSession('main', {})");
  stopped.run("openSetupWizard()"); await flush();
  stopped.state.holdStart = deferred(); stopped.state.startResponses.push({ httpOk: false });
  stopped.state.holdStop = deferred();
  const start = s["setup-mic-test"].fire("click"); await flush();
  const stop = stopped.run("RealtimeSessionClient.stopRealtimeSession('main')");
  stopped.state.holdStart.resolve(); await start; await flush();
  assert.equal(stopped.run("RealtimeSessionClient.isMainActive()"), false, "newer accepted Stop wins over rejection restoration");
  stopped.state.holdStop.resolve(); await stop;
  assert.equal(stopped.run("RealtimeSessionClient.isMainActive()"), false);

  // A successful or uncertain takeover must still retire a response held during
  // Start; it must not revive output or issue main-poll-exit Stop afterwards.
  for (const response of [{ body: { ok: true } }, { transportError: true }]) {
    const f = await fixture(), e = f.elements;
    await f.run("startRealtime()"); await f.drainSaves(); installMainRecordingUi(f);
    const held = deferred(); f.state.statusResponses.push(held);
    const poll = f.run("pollRealtime(realtimePollToken)"); await stepPollTimer(f);
    f.run("openSetupWizard()"); await flush();
    f.state.holdStart = deferred(); f.state.startResponses.push(response);
    const starting = e["setup-mic-test"].fire("click"); await flush();
    held.resolve({ state: "completed", final: "retired result", captureRms: 900 }); await flush();
    f.state.holdStart.resolve(); await starting; await stepPollTimer(f); await poll;
    assert.equal(e["rt-final-history"].children.length, 0);
    assert.equal(f.requests.filter(r => r.url.startsWith("/api/realtime/stop")).length, 0);
    if (response.transportError) await e["setup-mic-retry-stop"].fire("click");
    else await e["setup-close-btn"].fire("click");
    assert.equal(f.requests.filter(r => r.url.startsWith("/api/realtime/stop")).length, 1);
  }
}

async function wizardConfirmedStopMainUiRegression() {
  const f = await fixture(), e = f.elements;
  await f.run("startRealtime()"); await f.drainSaves(); installMainRecordingUi(f);
  f.run("setRecordingButtons(true); updateRealtimeResults({state:'recording', final:'confirmed history', selectedEngine:'qwen', captureRms:123, vadState:'speech'}); noiseWarmupTimer = setInterval(() => {}, 1000);");
  e["rt-status"].textContent = "recording";
  f.run("openSetupWizard()"); await flush(); await e["setup-mic-test"].fire("click");
  f.state.holdStop = deferred();
  const closing = e["setup-close-btn"].fire("click"); await flush();
  assert.equal(e["rt-start-btn"].style.display, "none", "pending Stop is not a successful reset");
  f.state.holdStop.resolve(); await closing;
  assert.equal(e["rt-start-btn"].style.display, "block", "confirmed wizard Stop restores main Start");
  assert.equal(e["rt-start-btn"].disabled, false); assert.equal(e["rt-stop-btn"].style.display, "none");
  assert.equal(e["rt-status"].textContent, "ui.web.status.stopped");
  assert.equal(e["rt-live-vad"].textContent, "-"); assert.equal(e["rt-live-rms"].textContent, "0.0000");
  assert.equal(e["rt-live-engine"].textContent, "qwen");
  assert.equal(e["rt-final-history"].children[0].children[0].textContent, "✓ confirmed history");
  assert.equal(f.run("noiseWarmupTimer"), null);
  assert.deepEqual(JSON.parse(JSON.stringify(f.run("uiStops"))), { meter: 1, speaker: 1, wave: 1 });
  assert.equal(f.requests.filter(r => r.url.startsWith("/api/realtime/stop")).length, 1);
  assert.equal(f.requests.filter(r => r.url === "/api/realtime/start").length, 2);

  const failed = await fixture(), x = failed.elements;
  await failed.run("startRealtime()"); await failed.drainSaves(); installMainRecordingUi(failed);
  failed.run("setRecordingButtons(true); noiseWarmupTimer = setInterval(() => {}, 1000)");
  x["rt-status"].textContent = "recording";
  failed.run("openSetupWizard()"); await flush(); await x["setup-mic-test"].fire("click");
  failed.state.stopResponses.push({ transportError: true });
  await x["setup-close-btn"].fire("click");
  assert.equal(failed.run("setupMicUnconfirmed"), true);
  assert.equal(x["rt-start-btn"].style.display, "none"); assert.equal(x["rt-status"].textContent, "recording");
  assert.notEqual(failed.run("noiseWarmupTimer"), null);
  assert.deepEqual(JSON.parse(JSON.stringify(failed.run("uiStops"))), { meter: 0, speaker: 0, wave: 0 });
  await x["setup-mic-retry-stop"].fire("click");

  const finalError = await fixture(), y = finalError.elements;
  await finalError.run("startRealtime()"); await finalError.drainSaves(); installMainRecordingUi(finalError);
  finalError.run("setRecordingButtons(true)");
  finalError.run("openSetupWizard()"); await flush(); await y["setup-mic-test"].fire("click");
  finalError.state.stopResponses.push({ body: { ok: true, error: "final recognition failed" } });
  await y["setup-close-btn"].fire("click");
  assert.equal(y["rt-start-btn"].style.display, "block", "a resolved Stop ended capture despite a final recognition error");
  assert.equal(y["rt-status"].className, "status err"); assert.equal(y["rt-status"].textContent, "ui.web.status.error");
}

async function wizardConfirmedStopMobfyUiRegression() {
  const f = await fixture(), e = f.elements;
  installMainRecordingUi(f);
  f.run(`
    globalThis.currentMode = "mobfy";
    globalThis.vcRunning = false; globalThis.vcPollingTimer = null;
    globalThis.MOBFY_STATUS_POLL_MS = 100;
    globalThis.REALTIME_WAS_RECORDING_KEY = "mobmate.realtime.wasRecording";
    globalThis.spkSetLevel = () => {}; globalThis.vcHasSelectedModel = () => false;
    globalThis.mobfyFixture = { running: true, requests: [] };
    const previousMobfyFetch = fetch;
    globalThis.fetch = async (url, options) => {
      if (!url.startsWith("/api/voice-conversion/")) return previousMobfyFetch(url, options);
      mobfyFixture.requests.push({ url, method: options?.method || "GET" });
      if (url === "/api/voice-conversion/stop") mobfyFixture.running = false;
      else if (url !== "/api/voice-conversion/status") throw Error("unexpected Mobfy request: " + url);
      return { ok: true, json: async () => ({ running: mobfyFixture.running, inputRmsLevel: 0.2 }) };
    };
  `);
  for (const code of [section("function isMobfyMode()", "function switchMode("),
    section("function setRealtimeWasRecording(", "function shouldRestoreMobfyVoice("),
    section("function clearNoiseWarmupUi(", "function startNoiseWarmupUi("),
    section("async function stopMobfyVoice(", "async function vcToggle("),
    source.slice(source.indexOf("function vcFetchStatus()"))]) f.run(code);
  // Observe an already-running VC session through its actual status handler.
  // The private API and drawing are fake; no VC runtime or audio is started.
  f.run("vcFetchStatus()"); await flush();
  assert.equal(f.run("vcRunning"), true);
  e["rt-status"].textContent = "existing Mobfy recording";
  f.run("noiseWarmupTimer = setInterval(() => {}, 1000); setRealtimeWasRecording(true)");
  const warmup = f.run("noiseWarmupTimer");
  f.run("openSetupWizard()"); await flush(); await e["setup-mic-test"].fire("click");
  await e["setup-close-btn"].fire("click");
  assert.equal(f.run("setupMicBlocksOtherCapture()"), false);
  assert.equal(e["rt-start-btn"].style.display, "none", "ASR Stop must not expose Start for a still-running Mobfy session");
  assert.equal(e["rt-stop-btn"].style.display, "block"); assert.equal(e["rt-stop-btn"].disabled, false);
  assert.equal(e["rt-status"].textContent, "existing Mobfy recording");
  assert.equal(f.run("noiseWarmupTimer"), warmup);
  assert.deepEqual(JSON.parse(JSON.stringify(f.run("uiStops"))), { meter: 0, speaker: 0, wave: 0 });
  assert.equal(f.storage.get("mobmate.vc.wasRunning"), "1");
  assert.equal(f.storage.get("mobmate.realtime.wasRecording"), "0", "the ASR session flag is independent of the visible VC session");
  assert.equal(f.requests.filter(r => r.url === "/api/realtime/stop").length, 1);
  assert.equal(f.run("mobfyFixture.requests.filter(r => r.url.endsWith('/stop')).length"), 0);
  f.run("vcFetchStatus()"); await flush();
  assert.equal(e["rt-status"].textContent, "existing Mobfy recording", "later VC status must not retain an ASR stopped label");
  await f.run("stopMobfyVoice()"); await flush();
  assert.equal(f.run("mobfyFixture.requests.filter(r => r.url.endsWith('/stop')).length"), 1, "the next explicit Mobfy Stop still works");
  assert.equal(f.run("vcRunning"), false); assert.equal(f.storage.get("mobmate.vc.wasRunning"), "0");
  assert.equal(e["rt-start-btn"].style.display, "block"); assert.equal(e["rt-stop-btn"].style.display, "none");
  assert.equal(e["rt-status"].textContent, "vc.status.off");
}


async function wizardStopSwitchToIdleMobfyRegression() {
  const f = await fixture(), e = f.elements;
  installMainRecordingUi(f);
  f.run(`globalThis.currentMode = "stt"; globalThis.vcRunning = false; globalThis.vcPollingTimer = null;
    globalThis.vcCheckLicenseGate = () => {};`);
  for (const code of [section("function isMobfyMode()", "function updateMainActionLabels("),
    section("function clearNoiseWarmupUi(", "function startNoiseWarmupUi("),
    section("async function stopRealtimeDirectForModeSwitch()", "async function stopMobfyDirectForModeSwitch()"),
    section("function enforceModeExclusion(", "async function startMainMode()")]) f.run(code);
  await f.run("startRealtime()"); await f.drainSaves();
  e["rt-status"].textContent = "recording";
  f.run("openSetupWizard()"); await flush(); await e["setup-mic-test"].fire("click");
  f.state.holdStop = deferred();
  const closing = e["setup-close-btn"].fire("click"); await flush();
  f.run("switchMode('mobfy')");
  f.state.holdStop.resolve(); await closing;
  assert.equal(f.run("setupMicBlocksOtherCapture() || vcRunning || !!vcPollingTimer"), false);
  assert.equal(e["rt-start-btn"].style.display, "block", "switching to idle Mobfy during wizard Stop must restore its explicit Start");
  assert.equal(e["rt-start-btn"].disabled, false); assert.equal(e["rt-stop-btn"].style.display, "none");
  assert.equal(e["rt-status"].textContent, "vc.status.off");
  assert.equal(f.requests.filter(r => r.url === "/api/realtime/stop").length, 1);
  assert.equal(f.requests.filter(r => r.url.startsWith("/api/voice-conversion/")).length, 0, "no automatic VC Start or unrelated Stop");
  f.run(`globalThis.vcSelectedCatalogModel = () => null; globalThis.vcHasSelectedModel = () => false;
    globalThis.setMobfyWasRunning = () => {}; globalThis.vcFetchStatus = () => {};
    globalThis.explicitVcStarts = 0; const beforeExplicitVcFetch = fetch;
    globalThis.fetch = async (url, options) => {
      if (url !== "/api/voice-conversion/start") return beforeExplicitVcFetch(url, options);
      explicitVcStarts++; return { ok: true };
    };`);
  e["rt-start-btn"].addEventListener("click", f.run("startMobfyVoice"));
  await e["rt-start-btn"].fire("click");
  assert.equal(f.run("explicitVcStarts"), 1, "the next explicit Mobfy Start works without an extra user Stop");
  assert.equal(f.run("vcRunning && vcPendingStarts === 0"), true);
  assert.equal(e["rt-start-btn"].style.display, "none"); assert.equal(e["rt-stop-btn"].style.display, "block");
}

async function wizardStopPendingMobfyStartRegression() {
  // Explicit starts and the three existing active-VC setting callers share the
  // same HTTP representation and pending-intent boundary.
  for (const [startCall, active] of [["startMobfyVoice()", false], ["vcToggle()", false],
    ["setTtsOutputDevice('out-new')", true], ["setMonitorVolume(25)", true], ["setWavOutputEnabled(true)", true]]) {
    const f = await fixture(), e = f.elements; installMainRecordingUi(f);
    f.run(`globalThis.currentMode = "mobfy"; globalThis.vcRunning = false;
      globalThis.vcSelectedCatalogModel = () => null; globalThis.vcHasSelectedModel = () => false;
      globalThis.setMobfyWasRunning = () => {}; globalThis.vcFetchStatus = () => {};
      globalThis.setTtsEngineConfig = async () => {};
      globalThis.pendingVcFixture = { requests: 0, release: null };
      pendingVcFixture.promise = new Promise(resolve => { pendingVcFixture.release = resolve; });
      const beforePendingVcFetch = fetch;
      globalThis.fetch = async (url, options) => {
        if (url !== "/api/voice-conversion/start") return beforePendingVcFetch(url, options);
        pendingVcFixture.requests++; await pendingVcFixture.promise; return { ok: true };
      };`);
    for (const code of [section("function isMobfyMode()", "function switchMode("),
      section("function clearNoiseWarmupUi(", "function startNoiseWarmupUi("),
      section("async function stopRealtimeDirectForModeSwitch()", "async function stopMobfyDirectForModeSwitch()"),
      section("async function setMonitorVolume(", "async function fetchTtsStatus()"),
      section("async function vcToggle()", "async function vcLoadModels()")]) f.run(code);
    f.run(`vcRunning = ${active}`);
    const starting = f.run(startCall); await flush();
    assert.equal(f.run("pendingVcFixture.requests"), 1);
    assert.equal(f.run("vcPendingStarts"), 1);
    // During replacement the existing status poll may observe no running VC.
    f.run("vcRunning = false");
    const display = e["rt-start-btn"].style.display, status = e["rt-status"].textContent;
    const stops = JSON.stringify(f.run("uiStops"));
    f.run("openSetupWizard()"); await flush(); await e["setup-mic-test"].fire("click");
    await e["setup-close-btn"].fire("click");
    assert.equal(e["rt-start-btn"].style.display, display, "wizard Stop must preserve the pending VC caller's controls");
    assert.equal(e["rt-status"].textContent, status);
    assert.equal(JSON.stringify(f.run("uiStops")), stops);
    f.run("pendingVcFixture.release()"); await starting;
    assert.equal(f.run("vcRunning && vcPendingStarts === 0"), true, "accepted VC Start is still honored");
    assert.equal(f.run("pendingVcFixture.requests"), 1, "no replay or automatic Start");
  }
}

async function addonInstallRecoveryRegression() {
  const f = await wizardAddonFixture(), e = f.elements, api = f.run("fixtureAddon");
  api.installed = false; api.ready = false; await f.run("AddonLanguageUi.refresh()");
  await e["addon-asr-trigger"].fire("click");
  await e["addon-asr-search"].fire("keydown", { key: "ArrowDown", preventDefault() {} });
  await e["addon-asr-search"].fire("keydown", { key: "Enter", preventDefault() {} });
  assert.equal(e["addon-asr-status"].textContent, "addon.asr.notInstalled");
  assert.equal(e["addon-asr-setup"].disabled, false); assert.equal(e["addon-asr-probe"].disabled, true);
  assert.equal(e["addon-asr-enabled"].disabled, true); assert.equal(e["addon-asr-joint"].disabled, true);
  api.failInstall = true; await e["addon-asr-setup"].fire("click"); await f.run("AddonLanguageUi.whenIdle()");
  assert.match(e["addon-asr-status"].textContent, /HttpRequestException/);
  assert.equal(e["addon-asr-setup"].disabled, false, "a failed download leaves the explicit install action available");
  assert.equal(api.posts.length, 1); assert.equal(api.profiles.ja.enabled, false); assert.equal(api.profiles.ja.jointRequested, false);
  api.failInstall = false; await e["addon-asr-setup"].fire("click"); await f.run("AddonLanguageUi.whenIdle()");
  assert.equal(e["addon-asr-status"].textContent, "addon.asr.installed", "installed is not Ready");
  assert.equal(e["addon-asr-probe"].disabled, false); assert.equal(e["addon-asr-enabled"].disabled, true);
  assert.deepEqual(Array.from(api.posts, p => p.url), ["/api/addon-asr/install", "/api/addon-asr/install"], "only two explicit fake install clicks, no probe/enable/consent or automatic retry");
  api.supported = false; await f.run("AddonLanguageUi.refresh()");
  assert.equal(e["addon-asr-setup"].disabled, true); assert.equal(e["addon-asr-probe"].disabled, true);
  assert.equal(e["addon-asr-runtime-warning"].textContent, "addon.asr.runtimeUnavailable");
  console.log("Add-on recovery regression passed: actual module, missing/failed install/explicit retry/runtime unavailable; fake API, no download or worker.");
}

async function textSpeechRegression() {
  // Inspect the real HTML separately from the lightweight handler fixture below.
  const sttStart = html.indexOf('<div id="mode-stt-content">');
  const sttEnd = html.indexOf('</div><!-- /mode-stt-content -->', sttStart);
  assert.ok(sttStart >= 0 && sttEnd > sttStart, "STT mode container must exist");
  const stack = [], locations = new Map();
  const voidTags = new Set(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr"]);
  for (const match of html.slice(sttStart, sttEnd).matchAll(/<\/?([a-z][\w-]*)\b[^>]*>/gi)) {
    const tag = match[1].toLowerCase(), closing = match[0].startsWith("</");
    if (closing) {
      assert.equal(stack.pop()?.tag, tag, `mismatched STT HTML closing tag: ${tag}`);
      continue;
    }
    const id = match[0].match(/\bid="([^"]+)"/)?.[1];
    if (id) {
      assert.ok(!locations.has(id), `duplicate STT element: ${id}`);
      locations.set(id, stack.map(node => node.id));
    }
    if (!voidTags.has(tag) && !match[0].endsWith("/>")) stack.push({ tag, id });
  }
  assert.deepEqual(locations.get("text-speech-panel"), ["mode-stt-content"], "manual speech must stay in STT mode outside the hidden live panel");
  assert.deepEqual(locations.get("rt-live-panel"), ["mode-stt-content"], "live recognition remains separate and conditional");
  for (const id of ["ts-input", "ts-send-btn", "ts-targets", "ts-status"])
    assert.ok(locations.get(id)?.includes("text-speech-panel"), `${id} must remain within the existing speech panel`);
  const create = async (options = {}) => {
    const f = await fixture("1", {}, options);
    for (const [id, tag] of [["ts-input", "textarea"], ["ts-send-btn", "button"], ["ts-wrap", "div"], ["ts-status", "div"], ["ts-counter", "span"], ["tts-text-input", "input"], ["tts-status", "div"],
      ["tts-engine-select", "select"], ["pending-mode-select", "select"], ["pending-wait-seconds", "select"], ["pending-status", "div"],
      ["pending-mode-instant-btn", "button"], ["pending-mode-wait-btn", "button"]]) {
      const el = f.document.createElement(tag); el.id = id; f.elements[id] = el; f.elements.main.appendChild(el);
    }
    f.elements["tts-engine-select"].value = "kokoro";
    f.elements["pending-mode-select"].value = "instant"; // Initial HTML value, not an observed server mode.
    f.elements["pending-wait-seconds"].value = "3";
    f.run(section("const TTS_ENGINE =", "let initialTtsSetupPromptShown"));
    f.run(section("let currentI18n =", "let currentRealtimeGuideKey ="));
    f.run(`currentI18n = { "ui.web.textSpeech.status.error": "Error: {message}", "ui.web.status.error": "Error: {message}" }`);
    f.run(section("/* --- TTS Speak body builder", "/* --- TTS Speak --- */"));
    f.run(section("/* --- TTS Speak --- */", "async function ttsStop()"));
    f.run(section("async function setMonitorVolume(", "/* --- TTS Engine Selection --- */"));
    f.run(section("async function setTtsEngineConfig()", "/* --- TTS Speak body builder"));
    f.run(section("function setPendingStatus(", "async function fetchRadioConfig("));
    const route = f.run(`globalThis.textFixture = { replies: [], requests: [], pendingServerMode: "wait" };
      const previousTextFetch = fetch;
      fetch = async (url, options) => {
        if (!["/api/tts/speak", "/api/vrchat-osc/test", "/api/pending-speech/status", "/api/pending-speech/config"].includes(url)) return previousTextFetch(url, options);
        textFixture.requests.push({ url, options });
        // Both hosts retain the saved mode when a config POST omits it.
        if (url === "/api/pending-speech/config" && options?.method === "POST") {
          const body = JSON.parse(options.body);
          if (Object.hasOwn(body, "mode")) textFixture.pendingServerMode = body.mode;
        }
        let response = textFixture.replies.shift();
        if (response?.promise) response = await response.promise;
        if (response?.transportError) throw Error("response lost");
        return { ok: response?.httpOk !== false, status: response?.httpOk === false ? 503 : 200,
          json: async () => { if (response?.malformed) throw SyntaxError("bad JSON"); return response?.body ?? { success: true }; } };
      }; textFixture`);
    f.run("initTextSpeech()");
    const type = async text => { f.elements["ts-input"].value = text; await f.elements["ts-input"].fire("input"); };
    const enter = async () => { await f.elements["ts-input"].fire("keydown", { key: "Enter", preventDefault() {} }); await flush(); };
    return { ...f, route, type, enter };
  };
  // A failed wizard apply retains the selected output, not proof that the host
  // uses it.  Exercise the same public handlers as the original reproduction.
  const output = await create();
  output.run("openSetupWizard()");
  await output.elements["setup-next-btn"].fire("click"); await output.elements["setup-next-btn"].fire("click");
  output.elements["setup-out-select"].value = "out-new";
  output.state.configOk = false; await output.elements["setup-out-play"].fire("click");
  assert.equal(output.elements["setup-out-result"].textContent, "setup.s4.configFailed");
  assert.equal(output.storage.get("mobmate.ttsOutputDeviceId"), "out-new");
  assert.equal(output.state.ttsAppliedDevice, "out-old");
  await output.elements["setup-close-btn"].fire("click");
  await output.type("Please open the map."); await output.elements["ts-send-btn"].fire("click");
  assert.equal(output.route.requests.filter(r => r.url === "/api/tts/speak").length, 0, "failed output apply must block later Text Speech, not send to the old output");
  assert.equal(output.elements["ts-input"].value, "Please open the map.");
  assert.match(output.elements["ts-status"].className, /err/);
  assert.ok(output.elements["ts-status"].textContent.includes("ui.web.tts.outputUnconfirmed"), "the output refusal must give a retry reason");
  output.elements["tts-text-input"].value = "Other manual TTS";
  await output.run("ttsSpeak()");
  assert.equal(output.route.requests.filter(r => r.url === "/api/tts/speak").length, 0, "the regular TTS button must respect the same output failure");
  assert.equal(output.elements["tts-status"].textContent, "ui.web.tts.outputUnconfirmed");
  output.state.ttsStatusResponses.push({ body: { outputDeviceId: "out-new", available: true } });
  await output.run("fetchTtsStatus()");
  await output.elements["ts-send-btn"].fire("click");
  assert.equal(output.route.requests.filter(r => r.url === "/api/tts/speak").length, 0, "a later poll must not turn a failed apply into an automatic retry");
  await output.drainSaves();
  assert.equal(output.state.serverDocument.settings["mobmate.ttsOutputDeviceId"], "out-new");

  output.run("tsState.target = 'chatbox'"); output.route.replies.push({ body: { ok: true } });
  await output.elements["ts-send-btn"].fire("click");
  assert.equal(output.route.requests.at(-1).url, "/api/vrchat-osc/test", "Chatbox alone does not depend on TTS output");
  output.run("tsState.target = 'both'"); await output.type("Both after failed apply");
  output.route.replies.push({ body: { ok: true } }); await output.elements["ts-send-btn"].fire("click");
  assert.equal(output.route.requests.at(-1).url, "/api/vrchat-osc/test", "an explicit both target still sends Chatbox when TTS is blocked");
  assert.equal(output.route.requests.filter(r => r.url === "/api/tts/speak").length, 0);
  assert.equal(output.elements["ts-input"].value, "Both after failed apply");

  // A later successful Windows-format explicit apply recovers, but never
  // automatically resends either blocked draft.
  output.state.configOk = true; output.state.configResponses.push({ body: { code: "tts.config_applied", message: "config applied" } });
  assert.equal(await output.run("setTtsOutputDevice('out-new')"), true);
  assert.equal(output.elements["tts-device-status"].textContent, "", "a confirmed explicit recovery must clear the old device failure");
  assert.doesNotMatch(output.elements["tts-device-status"].className, /err/);
  assert.equal(output.state.ttsAppliedDevice, "out-new");
  assert.equal(output.route.requests.filter(r => r.url === "/api/tts/speak").length, 0);
  output.run("tsState.target = 'tts'"); output.route.replies.push({ body: { success: true } });
  await output.elements["ts-send-btn"].fire("click");
  assert.equal(output.route.requests.filter(r => r.url === "/api/tts/speak").length, 1);
  assert.equal(JSON.parse(output.route.requests.at(-1).options.body).text, "Both after failed apply");

  const partialMonitor = await create({ uiSettingsReadFailures: 10 });
  await partialMonitor.type("First confirmed output"); partialMonitor.route.replies.push({ body: { success: true } });
  await partialMonitor.elements["ts-send-btn"].fire("click");
  await partialMonitor.run("setMonitorVolume(20)");
  const partialBody = JSON.parse(partialMonitor.requests.filter(r => r.url === "/api/tts/config").at(-1).options.body);
  assert.deepEqual(partialBody, { monitorVolumePercent: 20 }, "unread output is omitted from the monitor-only change");
  await partialMonitor.type("Output remains confirmed"); partialMonitor.route.replies.push({ body: { success: true } });
  await partialMonitor.elements["ts-send-btn"].fire("click");
  assert.equal(partialMonitor.route.requests.filter(r => r.url === "/api/tts/speak").length, 2,
    "a confirmed monitor-only change cannot erase an unchanged confirmed output");
  const partialAfterFailure = await create({ uiSettingsReadFailures: 10 });
  partialAfterFailure.elements["tts-output-device-select"].value = "out-new";
  partialAfterFailure.state.configOk = false; await partialAfterFailure.run("postTtsConfig({ outputDeviceId: 'out-new' })");
  partialAfterFailure.state.configOk = true; await partialAfterFailure.run("setMonitorVolume(20)");
  await partialAfterFailure.type("Unconfirmed output"); await partialAfterFailure.elements["ts-send-btn"].fire("click");
  assert.equal(partialAfterFailure.route.requests.filter(r => r.url === "/api/tts/speak").length, 0,
    "a monitor-only success cannot confirm an earlier failed output change");

  const newlyListed = await create();
  newlyListed.elements["tts-output-device-select"].children = newlyListed.elements["tts-output-device-select"].children.filter(option => option.value !== "out-new");
  newlyListed.run("openSetupWizard()");
  await newlyListed.elements["setup-next-btn"].fire("click"); await newlyListed.elements["setup-next-btn"].fire("click"); await flush();
  newlyListed.elements["setup-out-select"].value = "out-new";
  newlyListed.route.replies.push({ body: { success: true } }); await newlyListed.elements["setup-out-play"].fire("click");
  assert.equal(newlyListed.elements["tts-output-device-select"].value, "out-new", "a newly enumerated wizard device must become the visible main selection");
  assert.equal(newlyListed.state.ttsAppliedDevice, "out-new");
  assert.equal(newlyListed.route.requests.filter(r => r.url === "/api/tts/speak").length, 1, "a confirmed newly listed device remains usable");

  for (const unconfirmed of [{ body: {} }, { body: { ok: false } }, { malformed: true }, { transportError: true }]) {
    const f = await create(); f.state.configResponses.push(unconfirmed);
    assert.equal(await f.run("setTtsOutputDevice('out-new')"), false, "unknown, rejected or lost configuration responses are not confirmation");
    await f.type("Keep this draft"); await f.elements["ts-send-btn"].fire("click");
    assert.equal(f.route.requests.filter(r => r.url === "/api/tts/speak").length, 0);
    assert.equal(f.elements["ts-input"].value, "Keep this draft");
  }

  for (const unknownStatus of [{ httpOk: false }, { body: {} }, { body: { outputDeviceId: "out-other" } }, { body: { ok: false, outputDeviceId: "out-old" } }, { malformed: true }, { transportError: true }]) {
    const f = await create(); f.state.ttsStatusResponses.push(unknownStatus);
    await f.type("Wait for a known output"); await f.elements["ts-send-btn"].fire("click");
    assert.equal(f.route.requests.filter(r => r.url === "/api/tts/speak").length, 0, "an initial unconfirmed status is not a selected output");
    assert.equal(f.elements["ts-input"].value, "Wait for a known output");
  }

  const normalized = await create();
  normalized.elements["tts-output-device-select"].value = "MobMateVirtualMic";
  normalized.state.ttsStatusResponses.push({ body: { outputDeviceId: "MobMateTTSVirtualMic" } });
  await normalized.type("Normalized Linux sink"); normalized.route.replies.push({ body: { success: true } });
  await normalized.elements["ts-send-btn"].fire("click");
  assert.equal(normalized.route.requests.filter(r => r.url === "/api/tts/speak").length, 1, "Linux host sink normalization is a valid same-ID representation");

  const captured = await create(), capturedStatus = deferred();
  captured.state.ttsStatusResponses.push(capturedStatus); await captured.type("Keep the chosen voice");
  const capturedSend = captured.elements["ts-send-btn"].fire("click"); await flush();
  captured.elements["tts-engine-select"].value = "voicevox";
  captured.run("tsState.target = 'both'");
  captured.route.replies.push({ body: { success: true } });
  capturedStatus.resolve({ body: { outputDeviceId: "out-old" } }); await capturedSend;
  assert.equal(JSON.parse(captured.route.requests.at(-1).options.body).engine, "kokoro", "the sent body uses the engine captured by the original click");
  assert.equal(captured.route.requests.some(r => r.url === "/api/vrchat-osc/test"), false, "the click's original target is also captured");

  const otherConfig = await create();
  otherConfig.state.configOk = false; await otherConfig.run("setTtsOutputDevice('out-new')");
  otherConfig.state.configOk = true; await otherConfig.run("setMonitorVolume(20)");
  assert.equal(otherConfig.state.ttsAppliedDevice, "out-new", "monitor config also applies the selected output");
  await otherConfig.type("Monitor recovered output"); otherConfig.route.replies.push({ body: { success: true } });
  await otherConfig.elements["ts-send-btn"].fire("click");
  assert.equal(otherConfig.route.requests.filter(r => r.url === "/api/tts/speak").length, 1);
  otherConfig.state.configOk = false; await otherConfig.run("setTtsEngineConfig()");
  await otherConfig.type("Engine config failed"); await otherConfig.elements["ts-send-btn"].fire("click");
  assert.equal(otherConfig.route.requests.filter(r => r.url === "/api/tts/speak").length, 1, "failed engine config invalidates output confirmation too");
  otherConfig.state.configOk = true; await otherConfig.run("setTtsEngineConfig()");
  assert.equal(otherConfig.route.requests.filter(r => r.url === "/api/tts/speak").length, 1, "config recovery must not auto-speak");
  otherConfig.route.replies.push({ body: { success: true } }); await otherConfig.elements["ts-send-btn"].fire("click");
  assert.equal(otherConfig.route.requests.filter(r => r.url === "/api/tts/speak").length, 2);

  const pendingOutput = await create(), firstOutput = deferred();
  pendingOutput.state.configResponses.push(firstOutput);
  const oldApply = pendingOutput.run("setTtsOutputDevice('out-new')"); await flush();
  const newApply = pendingOutput.run("setTtsOutputDevice('out-old')"); await flush();
  assert.equal(pendingOutput.requests.filter(r => r.url === "/api/tts/config").length, 1, "output-changing config requests must not overlap");
  await pendingOutput.type("Do not send while applying"); await pendingOutput.elements["ts-send-btn"].fire("click");
  assert.equal(pendingOutput.route.requests.filter(r => r.url === "/api/tts/speak").length, 0);
  firstOutput.resolve({ body: { ok: true } }); await Promise.all([oldApply, newApply]);
  assert.deepEqual(Array.from(pendingOutput.requests.filter(r => r.url === "/api/tts/config"), r => JSON.parse(r.options.body).outputDeviceId), ["out-new", "out-old"]);
  assert.equal(pendingOutput.state.ttsAppliedDevice, "out-old");
  assert.equal(pendingOutput.elements["ts-input"].value, "Do not send while applying");
  assert.equal(pendingOutput.route.requests.filter(r => r.url === "/api/tts/speak").length, 0, "completion never triggers an automatic send");

  const virtual = await create(), heldVirtual = deferred();
  virtual.run("let lastVirtualAudioStatus = { preferredOutputDeviceId: 'out-old' }; globalThis.fetchTtsOutputDevices = async () => {};");
  virtual.run(section("async function applyVirtualAudioOutput()", "async function openWindowsSoundSettings()"));
  virtual.state.virtualResponses.push(heldVirtual);
  const virtualApply = virtual.run("applyVirtualAudioOutput()"); await flush();
  const laterChoice = virtual.run("setTtsOutputDevice('out-new')"); await flush();
  assert.equal(virtual.requests.filter(r => r.url === "/api/tts/config").length, 0, "virtual apply must not overlap the later output config");
  heldVirtual.resolve({ body: { ok: true, outputDeviceId: "out-old" } });
  await Promise.all([virtualApply, laterChoice]);
  assert.equal(virtual.state.ttsAppliedDevice, "out-new");
  assert.equal(virtual.storage.get("mobmate.ttsOutputDeviceId"), "out-new", "a stale virtual apply must not overwrite the newer selection");

  const linuxVirtual = await create();
  linuxVirtual.run("let lastVirtualAudioStatus = {}; globalThis.fetchTtsOutputDevices = async () => {};");
  linuxVirtual.run(section("async function applyVirtualAudioOutput()", "async function openWindowsSoundSettings()"));
  linuxVirtual.state.virtualResponses.push({ body: { ok: true } });
  await linuxVirtual.run("applyVirtualAudioOutput()");
  await linuxVirtual.type("Linux virtual setup is a no-op"); linuxVirtual.route.replies.push({ body: { success: true } });
  await linuxVirtual.elements["ts-send-btn"].fire("click");
  assert.equal(linuxVirtual.route.requests.filter(r => r.url === "/api/tts/speak").length, 1, "a successful Linux no-op may verify the output normally");
  linuxVirtual.state.configOk = false; await linuxVirtual.run("setTtsOutputDevice('out-new')");
  linuxVirtual.state.virtualResponses.push({ body: { ok: true } }); await linuxVirtual.run("applyVirtualAudioOutput()");
  await linuxVirtual.type("Still unconfirmed"); await linuxVirtual.elements["ts-send-btn"].fire("click");
  assert.equal(linuxVirtual.route.requests.filter(r => r.url === "/api/tts/speak").length, 1, "a Linux no-op must not erase an earlier configuration failure");

  const staleStatus = await create(), oldStatus = deferred();
  staleStatus.state.ttsStatusResponses.push(oldStatus); await staleStatus.type("Captured before selection");
  const waitingStatus = staleStatus.elements["ts-send-btn"].fire("click"); await flush();
  await staleStatus.run("setTtsOutputDevice('out-new')");
  oldStatus.resolve({ body: { outputDeviceId: "out-old" } }); await waitingStatus;
  assert.equal(staleStatus.route.requests.filter(r => r.url === "/api/tts/speak").length, 0, "old status must not authorize a send after a later output selection");
  assert.equal(staleStatus.elements["ts-input"].value, "Captured before selection");
  for (const response of [{ body: { success: false, error: "Kokoro model is not ready or selected." } },
    { httpOk: false, body: { ok: true, error: "Output unavailable" } }, { body: {} }, { malformed: true }, { transportError: true }]) {
    const f = await create(); await f.type("Please open the map."); f.route.replies.push(response);
    await f.elements["ts-send-btn"].fire("click");
    assert.equal(f.elements["ts-input"].value, "Please open the map.", "failed or unconfirmed speech must keep the draft");
    assert.equal(f.elements["ts-send-btn"].disabled, false, "only an explicit retry is enabled");
    assert.match(f.elements["ts-status"].className, /err/);
    if (response.body?.error) assert.ok(f.elements["ts-status"].textContent.includes(response.body.error), "show the actionable model/output rejection");
    assert.equal(f.route.requests.length, 1, "never retry an unconfirmed speech request automatically");
  }
  const held = await create(); await held.type("First sentence");
  const response = deferred(); held.route.replies.push(response); await held.enter();
  await held.type("Next sentence");
  assert.equal(held.elements["ts-send-btn"].disabled, true, "editing while sending must not release the send lock");
  await held.enter(); assert.equal(held.route.requests.length, 1, "repeated Enter must not interrupt/repeat the in-flight utterance");
  response.resolve({ body: { success: true } }); await flush();
  assert.equal(held.elements["ts-input"].value, "Next sentence", "a completed request must not erase the next draft");
  assert.equal(held.elements["ts-send-btn"].disabled, false);
  assert.equal(JSON.parse(held.route.requests[0].options.body).text, "First sentence");
  held.route.replies.push({ body: { ok: true } }); await held.elements["ts-send-btn"].fire("click");
  assert.equal(held.elements["ts-input"].value, ""); assert.equal(held.elements["ts-send-btn"].disabled, true);
  assert.equal(held.run("tsState.history.join('|')"), "First sentence|Next sentence");

  const both = await create(); both.run("tsState.target = 'both'"); await both.type("Both destinations");
  both.route.replies.push({ body: { success: false, error: "Model missing" } }, { body: { ok: true } });
  await both.elements["ts-send-btn"].fire("click");
  assert.deepEqual(Array.from(both.route.requests, r => r.url), ["/api/tts/speak", "/api/vrchat-osc/test"], "an explicit both target still attempts Chatbox after a TTS rejection");
  assert.match(both.elements["ts-status"].textContent, /Model missing/); assert.equal(both.elements["ts-input"].value, "Both destinations");
  const chatFailure = await create(); chatFailure.run("tsState.target = 'both'"); await chatFailure.type("Partial acceptance");
  chatFailure.route.replies.push({ body: { success: true } }, { httpOk: false, body: { ok: true, error: "Chatbox refused" } });
  await chatFailure.elements["ts-send-btn"].fire("click");
  assert.match(chatFailure.elements["ts-status"].textContent, /Chatbox.*Chatbox refused/);
  assert.equal(chatFailure.elements["ts-input"].value, "Partial acceptance"); assert.equal(chatFailure.route.requests.length, 2);
  const unknown = await create(); unknown.run("tsState.target = 'unknown'"); await unknown.type("Keep until destination selected");
  await unknown.elements["ts-send-btn"].fire("click");
  assert.equal(unknown.route.requests.length, 0); assert.equal(unknown.elements["ts-input"].value, "Keep until destination selected");
  assert.match(unknown.elements["ts-status"].className, /err/);

  const wait = await create();
  wait.route.replies.push({ body: { mode: "wait", pendingCount: 1, items: [{ text: "Recognized speech" }] } });
  await wait.run("fetchPendingSpeechStatus()");
  // Manual Text Speech is already an explicit send, not an ASR enqueue. Do not
  // silently change that established opt-in or the server's timed wait behavior.
  await wait.type("Explicit manual speech"); wait.route.replies.push({ body: { success: true } });
  await wait.elements["ts-send-btn"].fire("click");
  assert.equal(wait.route.requests.at(-1).url, "/api/tts/speak");
  assert.equal(wait.run("pendingStatusCache.mode"), "wait");
  for (const failure of [{ httpOk: false }, { transportError: true }, { malformed: true }]) {
    wait.route.replies.push(failure); await wait.run("fetchPendingSpeechStatus()");
    assert.equal(wait.elements["pending-mode-select"].value, "wait", "observation failure must not silently select instant mode");
    assert.match(wait.elements["pending-status"].className, /err/);
  }
  wait.elements["pending-wait-seconds"].value = "5";
  wait.route.replies.push({ body: { ok: true } }, { body: { mode: "wait", pendingCount: 1 } });
  await wait.run("savePendingSpeechConfig()");
  const saved = wait.route.requests.find(r => r.url === "/api/pending-speech/config");
  assert.deepEqual(JSON.parse(saved.options.body), { mode: "wait", waitSeconds: 5 }, "changing the wait duration after failed polling preserves the selected mode");

  for (const failedConfig of [{ transportError: true }, { httpOk: false }, { malformed: true }, { body: {} }]) {
    const initial = await create();
    initial.route.replies.push(failedConfig, { transportError: true });
    await initial.run("fetchPendingSpeechConfig()");
    await initial.run("fetchPendingSpeechStatus()");
    initial.elements["pending-wait-seconds"].value = "5";
    initial.route.replies.push({ body: { ok: true } }, { transportError: true });
    await initial.run("savePendingSpeechConfig()");
    const durationPost = initial.route.requests.find(r => r.options?.method === "POST");
    assert.deepEqual(JSON.parse(durationPost.options.body), { waitSeconds: 5 }, "a duration change must not send the unobserved HTML mode default");
    assert.equal(initial.route.pendingServerMode, "wait", "failed initial observations must not replace the client's saved wait mode");
    initial.route.replies.push({ body: { ok: true } }, { transportError: true });
    await initial.run("setPendingMode('instant')");
    const explicitPost = initial.route.requests.filter(r => r.options?.method === "POST").at(-1);
    assert.deepEqual(JSON.parse(explicitPost.options.body), { mode: "instant", waitSeconds: 5 }, "an explicit instant choice remains authorized even when observations fail");
    assert.equal(initial.route.pendingServerMode, "instant");
  }

  const configured = await create();
  configured.route.replies.push({ body: { mode: "wait", waitSeconds: 3 } }, { transportError: true });
  await configured.run("fetchPendingSpeechConfig()");
  assert.equal(configured.elements["pending-mode-select"].value, "wait", "a valid config observation survives a failed status request");
  configured.elements["pending-wait-seconds"].value = "5";
  configured.route.replies.push({ body: { ok: true } }, { transportError: true });
  await configured.run("savePendingSpeechConfig()");
  assert.deepEqual(JSON.parse(configured.route.requests.find(r => r.options?.method === "POST").options.body), { mode: "wait", waitSeconds: 5 });
  assert.equal(configured.route.pendingServerMode, "wait");

  for (const read of ["fetchPendingSpeechStatus()", "fetchPendingSpeechConfig()"]) {
    const raced = await create();
    raced.route.pendingServerMode = "instant";
    raced.route.replies.push({ body: { mode: "instant", pendingCount: 0 } });
    await raced.run("fetchPendingSpeechStatus()");
    const stale = deferred(); raced.route.replies.push(stale);
    const oldRead = raced.run(read);
    raced.route.replies.push({ body: { ok: true } }, { transportError: true });
    await raced.run("setPendingMode('wait')");
    assert.equal(raced.route.pendingServerMode, "wait");
    stale.resolve({ body: { mode: "instant", waitSeconds: 3, pendingCount: 0 } }); await oldRead;
    assert.equal(raced.elements["pending-mode-select"].value, "wait", `${read}: a pre-save observation must not replace the newer explicit wait choice`);
    assert.match(raced.elements["pending-status"].className, /err/, "a stale success must not hide the current observation failure");
    raced.elements["pending-wait-seconds"].value = "5";
    raced.route.replies.push({ body: { ok: true } }, { transportError: true });
    await raced.run("savePendingSpeechConfig()");
    assert.equal(raced.route.pendingServerMode, "wait", "a duration-only change must not undo the explicit wait choice");
    raced.route.replies.push({ body: { ok: true } }, { body: { mode: "instant", pendingCount: 0 } });
    await raced.run("setPendingMode('instant')");
    assert.equal(raced.route.pendingServerMode, "instant", "a later explicit instant choice remains authorized");
    assert.equal(raced.elements["pending-mode-select"].value, "instant");
  }

  const saving = await create(), heldSave = deferred();
  saving.route.replies.push(heldSave);
  const saveWait = saving.run("setPendingMode('wait')"); await flush();
  // Polling during a POST can still observe the pre-save server state. It is not
  // permission to change the selected mode, whether it succeeds before or after.
  saving.route.replies.push({ body: { mode: "instant", pendingCount: 0 } });
  await saving.run("fetchPendingSpeechStatus()");
  assert.equal(saving.elements["pending-mode-select"].value, "wait", "polling during a save must not undo its mode intent");
  saving.route.replies.length = 0;
  saving.route.replies.push({ transportError: true });
  heldSave.resolve({ body: { ok: true } }); await saveWait;

  const ordered = await create(), firstSave = deferred();
  ordered.route.replies.push(firstSave);
  const firstMode = ordered.run("setPendingMode('instant')"); await flush();
  const lastMode = ordered.run("setPendingMode('wait')"); await flush();
  assert.equal(ordered.route.requests.filter(r => r.options?.method === "POST").length, 1, "mode writes must not overlap and reach the host out of order");
  ordered.route.replies.push({ body: { ok: true } }, { transportError: true });
  firstSave.resolve({ body: { ok: true } }); await Promise.all([firstMode, lastMode]);
  assert.deepEqual(Array.from(ordered.route.requests.filter(r => r.options?.method === "POST"), r => JSON.parse(r.options.body).mode), ["instant", "wait"]);
  assert.equal(ordered.route.pendingServerMode, "wait");
  assert.equal(ordered.elements["pending-mode-select"].value, "wait");

  const refreshing = await create(), oldRefresh = deferred();
  refreshing.route.replies.push({ body: { ok: true } }, oldRefresh);
  const firstRefresh = refreshing.run("setPendingMode('wait')"); await flush();
  assert.equal(refreshing.route.requests.at(-1).url, "/api/pending-speech/status");
  refreshing.route.replies.push({ body: { ok: true } }, { body: { mode: "instant", pendingCount: 0 } });
  const nextSave = refreshing.run("setPendingMode('instant')"); await flush();
  assert.equal(refreshing.route.requests.filter(r => r.options?.method === "POST").length, 2, "a pending observation must not block the next explicit mode save");
  oldRefresh.resolve({ body: { mode: "wait", pendingCount: 0 } });
  await Promise.all([firstRefresh, nextSave]);
  assert.equal(refreshing.elements["pending-mode-select"].value, "instant");
  assert.equal(refreshing.route.pendingServerMode, "instant");

  wait.run("tsState.target = 'chatbox'"); await wait.type("Chatbox only"); wait.route.replies.push({ body: { ok: true } });
  await wait.elements["ts-send-btn"].fire("click");
  assert.equal(wait.route.requests.at(-1).url, "/api/vrchat-osc/test");
  assert.equal(wait.route.requests.filter(r => r.url === "/api/tts/speak").length, 1, "Chatbox target must not implicitly emit TTS");
  console.log("Text Speech/Pending regression passed: explicit targets, refusal/unknown response, draft and in-flight intent, wait-mode preservation; fake HTTP, no audio.");
}

async function mossVoiceSelectionRegression() {
  const f = await fixture("1", { "mobmate.mossModelVoice": "moss-model|Ava" });
  for (const id of ["tts-moss-model-select", "rt-language-select", "translation-target-select"]) {
    const el = f.document.createElement("select"); el.id = id; f.elements[id] = el; f.elements.main.appendChild(el);
  }
  f.run(`globalThis._mossCatalog = [{ id: "moss-model", name: "MOSS", installed: true, voices: [
    { id: "Ava", language: "en" }, { id: "Soyo", language: "ja" }, { id: "Junhao", language: "zh" }
  ] }]; globalThis.setTtsEngineConfig = () => { throw Error("population must not change the configured voice"); };`);
  f.run(section("// The prompt's native language", "async function _mossDlStart("));
  const select = f.elements["tts-moss-model-select"];
  for (const language of ["ja", "fr", "zh", "en"]) {
    f.elements["rt-language-select"].value = language;
    select.value = "moss-model|Ava";
    f.run("_mossPopulateModelSelect()");
    assert.deepEqual(select.options.map(o => o.value), ["moss-model|Ava", "moss-model|Soyo", "moss-model|Junhao"]);
    assert.equal(select.options.find(o => o.value === "moss-model|Ava").selected, true, `selected voice must survive ${language}`);
  }
  console.log("MOSS voice selection regression passed: language changes retain the selected prompt without configuration or audio.");
}

(async () => {
  if (process.argv[2] === "linux-monitor") { linuxMonitorShutdownBindingRegression(); await linuxMonitorRestartUiRegression(); return; }
  if (process.argv[2] === "linux-restart") return linuxMonitorRestartUiRegression();
  if (process.argv[2] === "asr-cue") { await acceptanceCueRegression(); await onsetFillerRegression(); return; }
  if (process.argv[2] === "onset-filler") return onsetFillerRegression();
  if (process.argv[2] === "moss-selection") return mossVoiceSelectionRegression();
  if (process.argv[2] === "text-speech") return textSpeechRegression();
  if (process.argv[2] === "addon-install-recovery") return addonInstallRecoveryRegression();
  if (process.argv[2] === "asr-warmup-stop") { await warmupStopRegression(); await warmupMobfyDefaultRegression(); return; }
  if (process.argv[2] === "wizard-idle-mobfy-switch") return wizardStopSwitchToIdleMobfyRegression();
  if (process.argv[2] === "wizard-pending-mobfy-start") return wizardStopPendingMobfyStartRegression();
  if (process.argv[2] === "wizard-confirmed-stop-mobfy-ui") return wizardConfirmedStopMobfyUiRegression();
  if (process.argv[2] === "wizard-rejected-main-poll") return wizardRejectedStartMainPollRegression();
  if (process.argv[2] === "wizard-confirmed-stop-main-ui") return wizardConfirmedStopMainUiRegression();
  linuxMonitorShutdownBindingRegression();
  await linuxMonitorRestartUiRegression();
  await textSpeechRegression();
  await acceptanceCueRegression();
  await onsetFillerRegression();
  await addonInstallRecoveryRegression();
  await warmupStopRegression();
  await warmupMobfyDefaultRegression();
  await wizardRejectedStartMainPollRegression();
  await wizardConfirmedStopMainUiRegression();
  await wizardConfirmedStopMobfyUiRegression();
  await wizardStopSwitchToIdleMobfyRegression();
  await wizardStopPendingMobfyStartRegression();
  await wizardAddonGuardRegression();
  await missingInputSelectionRegression();
  await wizardStopRecoveryRegression();
  await wizardStartUncertaintyRegression();
  await wizardRetiredMainPollRegression();
  const f = await fixture(), e = f.elements;
  await e["menu-btn"].fire("click");
  assert.equal(f.document.activeElement.id, "drawer-title");
  assert.equal(e.main.inert, true);
  assert.equal(e["settings-drawer"].getAttribute("aria-hidden"), "false");
  const tab = shiftKey => { let prevented = false; f.run("handleUiModalKey")({ key: "Tab", shiftKey, preventDefault() { prevented = true; } }); return prevented; };
  assert.equal(tab(false), true); assert.equal(f.document.activeElement.id, "drawer-wizard-btn");
  assert.equal(tab(true), true); assert.equal(f.document.activeElement.id, "speaker-reset-btn");
  assert.equal(tab(false), true); assert.equal(f.document.activeElement.id, "drawer-wizard-btn");
  e["drawer-wizard-btn"].disabled = true; f.notify(); assert.equal(f.document.activeElement.id, "drawer-title");
  e["drawer-wizard-btn"].disabled = false;
  const dynamic = f.document.createElement("button"); dynamic.id = "dynamic-voicevox"; e["settings-drawer"].appendChild(dynamic);
  dynamic.focus(); dynamic.hidden = true; f.notify(); assert.equal(f.document.activeElement.id, "drawer-title");
  await e["drawer-close-btn"].fire("click");
  assert.equal(e.main.inert, false); assert.equal(e["previously-inert"].inert, true); assert.equal(f.document.activeElement.id, "menu-btn");

  await e["menu-btn"].fire("click");
  const cancelled = e["speaker-reset-btn"].fire("click"); await flush();
  assert.equal(f.document.activeElement.id, "speaker-reset-cancel");
  f.run("handleUiModalKey")({ key: "Escape", preventDefault() { throw Error("native dialog owns Escape"); } });
  await e["speaker-reset-dialog"].close("cancel"); await cancelled;
  assert.equal(f.requests.filter(r => r.url === "/api/speaker/reset").length, 0);
  assert.equal(f.document.activeElement.id, "speaker-reset-btn");
  for (const [body, key] of [[{ success: true, adaptiveVocabReset: false }, "ui.web.speaker.resetDone"], [{ success: false }, "ui.web.speaker.resetFailed"], [null, "ui.web.speaker.resetFailed"]]) {
    f.state.reset = body; const pending = e["speaker-reset-btn"].fire("click"); await flush(); await e["speaker-reset-dialog"].close("reset"); await pending;
    assert.equal(e["speaker-reset-status"].textContent, key);
  }
  f.state.reset = { success: true }; f.state.resetHttpOk = false;
  for (const malformed of [false, true]) {
    f.state.resetMalformed = malformed;
    const failed = e["speaker-reset-btn"].fire("click"); await flush(); await e["speaker-reset-dialog"].close("reset"); await failed;
    assert.equal(e["speaker-reset-status"].textContent, "ui.web.speaker.resetFailed");
  }
  f.state.resetMalformed = false; f.state.resetHttpOk = true;
  f.state.holdReset = deferred();
  const pending = e["speaker-reset-btn"].fire("click"); await flush(); await e["speaker-reset-dialog"].close("reset"); await flush();
  const count = f.requests.filter(r => r.url === "/api/speaker/reset").length;
  await e["speaker-reset-btn"].fire("click"); assert.equal(f.requests.filter(r => r.url === "/api/speaker/reset").length, count);
  f.state.transportError = true; f.state.holdReset.resolve(); await pending;
  assert.equal(e["speaker-reset-status"].textContent, "ui.web.speaker.resetFailed"); assert.equal(e["speaker-reset-btn"].disabled, false);

  f.writes.length = 0;
  await e["drawer-wizard-btn"].fire("click"); await flush();
  assert.equal(e["settings-drawer"].getAttribute("aria-hidden"), "true"); assert.equal(f.document.activeElement.id, "setup-title");
  e["setup-mic-select"].value = "mic-new"; await e["setup-mic-select"].fire("change");
  assert.equal(f.storage.get("mobmate.captureDeviceId"), "mic-new"); assert.equal(e["rt-device-select"].value, "mic-new");
  const writes = f.writes.length;
  assert.equal(f.scheduledSaves.size, 1, "explicit microphone change schedules a debounced client save");
  await e["setup-later-btn"].fire("click"); assert.equal(f.writes.length, writes); assert.equal(f.storage.get("mobmate.wizard.completed"), "1");
  await f.drainSaves();
  assert.equal(f.state.serverDocument.settings["mobmate.captureDeviceId"], "mic-new", "closing retains the save requested by an earlier explicit change");
  assert.equal(f.rawStorage.get("foreign.setting"), "keep");
  assert.equal(f.rawStorage.has("mobmate.captureDeviceId"), false, "real uiSettings does not store registered settings in raw Storage");
  assert.equal(f.document.activeElement.id, "menu-btn");

  const fresh = await fixture(null), s = fresh.elements;
  await flush(); assert.equal(s["setup-overlay"].classList.contains("open"), true);
  for (const id of ["setup-close-btn", "setup-backdrop", "setup-later-btn"]) {
    await s[id].fire("click"); assert.equal(fresh.storage.has("mobmate.wizard.completed"), false); assert.equal(fresh.writes.length, 0);
    assert.equal(fresh.scheduledSaves.size, 0, "dismissal must not schedule a server settings PUT");
    fresh.run("openSetupWizard()"); await flush();
  }
  await s["setup-skip-btn"].fire("click"); assert.equal(fresh.run("setupCurrentStep"), 1);
  assert.equal(fresh.document.activeElement.id, "setup-step-title-1");
  await s["setup-back-btn"].fire("click"); assert.equal(fresh.run("setupCurrentStep"), 0);
  assert.equal(fresh.document.activeElement.id, "setup-step-title-0");
  await s["setup-next-btn"].fire("click"); await s["setup-skip-btn"].fire("click"); await flush();
  s["setup-out-select"].value = "out-new";
  assert.equal(fresh.storage.get("mobmate.ttsOutputDeviceId"), "out-old");
  fresh.state.configOk = false; await s["setup-out-play"].fire("click");
  assert.equal(s["setup-out-result"].textContent, "setup.s4.configFailed"); assert.equal(fresh.requests.some(r => r.url === "/api/tts/speak"), false);
  assert.equal(fresh.storage.get("mobmate.ttsOutputDeviceId"), "out-new", "attempted explicit apply retains established local selection; no rollback claim");
  await fresh.drainSaves();
  assert.equal(fresh.state.serverDocument.settings["mobmate.ttsOutputDeviceId"], "out-new", "the established client save is separate from the failed TTS configuration request");
  fresh.state.configOk = true; fresh.state.configTransportError = true; await s["setup-out-play"].fire("click");
  assert.equal(s["setup-out-result"].textContent, "setup.s4.configFailed"); assert.equal(fresh.requests.some(r => r.url === "/api/tts/speak"), false, "unconfirmed config must not request playback");
  fresh.state.configTransportError = false;
  fresh.state.configOk = true; fresh.state.speak = { success: false }; await s["setup-out-play"].fire("click"); assert.equal(s["setup-out-result"].textContent, "setup.s4.failed");
  const beforeLostReply = fresh.requests.filter(r => r.url === "/api/tts/speak").length;
  fresh.state.speakTransportError = true; await s["setup-out-play"].fire("click");
  assert.equal(s["setup-out-result"].textContent, "setup.s4.failed");
  assert.equal(fresh.requests.filter(r => r.url === "/api/tts/speak").length, beforeLostReply + 1, "do not describe a requested but unconfirmed playback as not requested");
  fresh.state.speakTransportError = false;
  fresh.state.speak = { success: true }; await s["setup-out-play"].fire("click"); assert.equal(s["setup-out-result"].textContent, "setup.s4.requested");
  const config = fresh.requests.filter(r => r.url === "/api/tts/config").at(-1);
  assert.deepEqual(JSON.parse(config.options.body), { outputDeviceId: "out-new", outputDeviceName: "New", monitorVolumePercent: 30 });
  await s["setup-skip-btn"].fire("click"); assert.equal(fresh.run("setupCurrentStep"), 3); assert.equal(s["setup-skip-btn"].hidden, true);
  const beforeFinish = fresh.writes.length, beforeRequests = fresh.requests.length;
  await s["setup-next-btn"].fire("click");
  assert.deepEqual(fresh.writes.slice(beforeFinish), ["mobmate.wizard.completed"]); assert.equal(fresh.storage.get("mobmate.wizard.completed"), "1");
  assert.equal(fresh.requests.length, beforeRequests, "finish does not run tests/installers/external actions");
  await fresh.drainSaves();
  assert.equal(fresh.state.serverDocument.settings["mobmate.wizard.completed"], "1");
  fresh.run("openSetupWizard()"); await flush(); fresh.run("handleUiModalKey")({ key: "Escape", preventDefault() {}, stopPropagation() {} }); await flush();
  assert.equal(fresh.storage.get("mobmate.wizard.completed"), "1"); assert.equal(fresh.document.activeElement.id, "menu-btn");

  const racing = await fixture(); racing.state.holdDevices = deferred(); racing.run("openSetupWizard()"); await racing.elements["setup-close-btn"].fire("click");
  racing.elements["setup-mic-select"].value = "do-not-overwrite"; racing.state.holdDevices.resolve(); await flush();
  assert.equal(racing.elements["setup-mic-select"].value, "do-not-overwrite", "late enumeration must not edit a closed wizard");
  racing.state.holdDevices = null; racing.run("openSetupWizard()"); await flush(); racing.state.holdStart = deferred();
  const starting = racing.elements["setup-mic-test"].fire("click"); await flush();
  const closing = racing.elements["setup-close-btn"].fire("click"); racing.state.holdStart.resolve(); await starting; await closing;
  assert.equal(racing.requests.filter(r => r.url === "/api/realtime/start").length, 1); assert.equal(racing.requests.filter(r => r.url === "/api/realtime/stop").length, 1);
  assert.equal(racing.timers.size, 0); assert.equal(racing.elements["setup-overlay"].classList.contains("open"), false);

  const lateOutput = await fixture(), o = lateOutput.elements;
  lateOutput.run("openSetupWizard()"); await flush(); await o["setup-next-btn"].fire("click"); await o["setup-next-btn"].fire("click"); await flush();
  lateOutput.state.holdConfig = deferred();
  const playing = o["setup-out-play"].fire("click"); await flush(); await o["setup-close-btn"].fire("click");
  lateOutput.run("openSetupWizard()"); await flush(); await o["setup-next-btn"].fire("click"); await o["setup-next-btn"].fire("click"); await flush();
  lateOutput.state.holdConfig.resolve(); await playing;
  assert.equal(lateOutput.requests.some(r => r.url === "/api/tts/speak"), false, "closing must not schedule late playback, even after reopening");
  assert.equal(o["setup-out-result"].textContent, "", "a stale result must not overwrite a new visit");

  const failedSave = await fixture(); failedSave.state.saveOk = false;
  failedSave.run("openSetupWizard()"); await flush();
  failedSave.elements["setup-mic-select"].value = "mic-new"; await failedSave.elements["setup-mic-select"].fire("change");
  await failedSave.drainSaves(); await failedSave.elements["setup-close-btn"].fire("click");
  assert.equal(failedSave.storage.get("mobmate.captureDeviceId"), "mic-new");
  assert.equal(failedSave.state.serverDocument.settings["mobmate.captureDeviceId"], "mic-old", "a resolved client setter does not prove that its deferred server save succeeded");
  assert.equal(failedSave.scheduledSaves.size, 0); assert.equal(failedSave.state.saves.length, 1, "no automatic retry or dismissal save");

  const guarded = await fixture();
  guarded.run(fs.readFileSync("tests/MobMate.Core.Tests/fixtures/wizard_persistence_guard.js", "utf8"));
  const guard = guarded.run("window.__mobmateWizardGuard");
  await guarded.run("setTtsOutputDevice('out-old')");
  await guarded.drainSaves();
  const outputBeforeGuard = guarded.run("({ attempted: ttsOutputAttempted, confirmed: ttsOutputConfirmed, revision: ttsOutputRevision })");
  const serverBeforeGuard = JSON.stringify(guarded.state.serverDocument);
  guarded.requests.length = 0;
  await guard.begin();
  const guardedReport = await guard.run();
  assert.equal(guardedReport.mutatingRequestsForwarded, 0); assert.ok(guardedReport.capturedSettingsPuts > 0);
  const restored = await guard.restore();
  assert.equal(restored.restored, true, restored.error); assert.equal(restored.serverDocumentUnchanged, true);
  assert.equal(restored.pendingFixtureSaveCallbacks, 0); assert.equal(restored.fetchRestored, true); assert.equal(restored.clientMethodsRestored, true);
  assert.equal(guarded.run("ttsOutputAttempted"), outputBeforeGuard.attempted);
  assert.equal(guarded.run("ttsOutputConfirmed"), outputBeforeGuard.confirmed);
  assert.ok(guarded.run("ttsOutputRevision") > outputBeforeGuard.revision, "fixture restore must invalidate earlier observations rather than rewind their generation");
  assert.equal(JSON.stringify(guarded.state.serverDocument), serverBeforeGuard);
  assert.equal(guarded.requests.some(request => request.options?.method === "PUT"), false, "guard must never forward a fixture settings PUT");
  assert.equal(guarded.scheduledSaves.size, 0, "no native delayed fixture save may remain after restoring fetch");

  assert.match(html, /id="speaker-reset-dialog"[^>]+aria-describedby=/);
  assert.match(html, /id="speaker-reset-cancel"[^>]+autofocus/);
  assert.match(html, /aria-labelledby="drawer-title"/); assert.match(html, /aria-labelledby="setup-title"/);
  const keys = ["ui.web.speaker.reset", "ui.web.speaker.resetConfirm", "ui.web.speaker.resetDone", "ui.web.speaker.resetFailed", "ui.web.speaker.resetHint", "ui.web.speaker.resetAction",
    "setup.btn.skip", "setup.btn.later", "setup.saveHint", "setup.s2.desc", "setup.s4.desc", "setup.s4.requested", "setup.s4.failed", "setup.s4.configFailed", "setup.s5.done",
    "ui.web.device.inputMissing", "ui.web.device.inputUnverified", "ui.web.device.inputCannotStart", "ui.web.device.inputEnumerationFailed",
    "setup.s2.stopping", "setup.s2.stopUnconfirmed", "setup.s2.stopWarning", "setup.s2.retryStop", "setup.s2.reopen", "addon.asr.wizardBlocked",
    "ui.web.textSpeech.status.spoke", "ui.web.textSpeech.status.done", "ui.web.tts.outputUnconfirmed",
    ...["title", "details", "hint", "output", "choose", "chooseButton", "safety", "confirmed", "volume", "preview", "off", "offReady", "offUnavailable", "setup", "ready", "unavailable", "unsupported", "otherTab"].map(k => "ui.web.asrCue." + k)];
  const locales = ["en", "ja", "ko", "zh_cn", "zh_tw"].map(locale => {
    const rows = fs.readFileSync(`libs/preset/_ui_${locale}.txt`, "utf8").split(/\r?\n/);
    return Object.fromEntries(rows.filter(row => row.includes("=")).map(row => { const i = row.indexOf("="); return [row.slice(0, i), row.slice(i + 1)]; }));
  });
  for (const key of keys) {
    for (const locale of locales) assert.ok(locale[key], key);
    assert.equal(new Set(locales.map(locale => locale[key])).size, 5, `${key}: real translations, not copied text`);
  }
  assert.match(locales[0]["setup.s2.desc"], /applied immediately[\s\S]*save is requested[\s\S]*not confirmed/, "microphone wording must not promise a persisted save after a failed deferred PUT");
  console.log("Settings interaction regression passed: real uiSettings Map/debounced PUT and save failure, focus, reset cancel/failure, Wizard persistence/explicit tests, five locales.");
})().catch(error => { console.error(error); process.exitCode = 1; });
