// Committed regression/real-page fixture. Install in the fully initialized app tab, not as a
// production hook. No native Storage prototype, fixed sleeps or real writes.
(() => {
  if (window.__mobmateWizardGuard) throw new Error("A Wizard guard already exists in this tab");
  const assert = (ok, message) => { if (!ok) throw new Error(message); };
  const stable = value => JSON.stringify(value, (_, v) => v && typeof v === "object" && !Array.isArray(v)
    ? Object.fromEntries(Object.keys(v).sort().map(k => [k, v[k]])) : v);
  const el = id => document.getElementById(id);
  const settle = async () => { for (let i = 0; i < 24; i++) await Promise.resolve(); };
  const activeStep = () => Number(document.querySelector(".setup-step.active")?.dataset.step ?? -1);
  const keys = [...UI_SETTINGS_KEYS];
  const clientSnapshot = () => Object.fromEntries(keys.map(k => [k, uiSettings.getItem(k)]));
  const defined = snapshot => Object.fromEntries(Object.entries(snapshot).filter(([, v]) => v !== null));
  let state = null;
  async function readServer(fetcher) {
    const response = await fetcher.call(window, "/api/ui-settings", { cache: "no-store", signal: AbortSignal.timeout(10000) });
    assert(response.ok, "Cannot read the settings document safely");
    return response.json();
  }
  function captureSchedule(work) {
    const timeout = window.setTimeout, clear = window.clearTimeout;
    // scheduleSave is synchronous inside the actual uiSettings setter. Capture
    // only that call; do not replace timers across an await or mock its save body.
    window.setTimeout = (callback, delay, ...args) => {
      assert(delay === 250 && typeof callback === "function", "Unexpected timer inside uiSettings setter");
      const token = state.nextToken--;
      state.pending.set(token, () => callback(...args));
      return token;
    };
    window.clearTimeout = token => { if (!state.pending.delete(token)) state.nativeClearTimeout(token); };
    try { return work(); }
    finally { window.setTimeout = timeout; window.clearTimeout = clear; }
  }
  async function drain() {
    assert(state, "Guard not active");
    while (state.pending.size) {
      const callbacks = [...state.pending.values()]; state.pending.clear();
      for (const callback of callbacks) await callback();
    }
  }
  const count = path => state.requests.filter(r => r.path === path).length;
  const click = async id => { assert(el(id), `Missing control ${id}`); el(id).click(); await settle(); };
  function checkClient(expected, message) { assert(stable(clientSnapshot()) === stable(expected), message); }
  async function begin() {
    assert(!state, "Guard already active");
    assert(typeof uiSettings?.setItem === "function" && localStorage === uiSettings, "Actual uiSettings adapter is required");
    assert(!setupMicTesting && !setupMicStarting, "Do not interrupt an existing microphone test");
    assert(ttsOutputPending === 0, "Do not interrupt an existing output configuration");
    assert(!el("setup-overlay").classList.contains("open") && !el("settings-drawer").classList.contains("open"), "Close existing overlays first");
    const originalFetch = window.fetch;
    const serverBefore = await readServer(originalFetch);
    assert(ttsOutputPending === 0, "Output configuration started during guard setup");
    assert(serverBefore.browserMigrationCompleted === true, "Do not run this fixture during settings migration");
    const clientBefore = clientSnapshot();
    const serverSettings = Object.fromEntries(Object.entries(serverBefore.settings || {}).filter(([key]) => UI_SETTINGS_KEYS.has(key)));
    assert(stable(defined(clientBefore)) === stable(serverSettings), "Client and server settings differ before the fixture; preserve pending work and stop");
    state = {
      originalFetch, serverBefore, clientBefore, setItem: uiSettings.setItem, removeItem: uiSettings.removeItem,
      nativeClearTimeout: window.clearTimeout.bind(window), nextToken: -1, pending: new Map(), requests: [], mutations: [], saves: [], results: [],
      devices: [{ id: "ux-fixture-mic-before", name: "Fixture microphone A" }, { id: "ux-fixture-mic-after", name: "Fixture microphone B" }],
      rawBefore: Object.fromEntries(keys.map(k => [k, rawLocalStorage.getItem(k)])),
      selects: ["rt-device-select", "tts-output-device-select", "setup-mic-select", "setup-out-select"].map(id => ({ id, html: el(id).innerHTML, value: el(id).value })),
      ttsOutput: { attempted: ttsOutputAttempted, confirmed: ttsOutputConfirmed },
    };
    uiSettings.setItem = (key, value) => { state.mutations.push({ key, remove: false }); return captureSchedule(() => state.setItem.call(uiSettings, key, value)); };
    uiSettings.removeItem = key => { state.mutations.push({ key, remove: true }); return captureSchedule(() => state.removeItem.call(uiSettings, key)); };
    window.fetch = async (input, init = {}) => {
      const url = new URL(typeof input === "string" || input instanceof URL ? input : input.url, location.href);
      const method = (init.method || input?.method || "GET").toUpperCase();
      if (!["GET", "HEAD"].includes(method)) {
        state.requests.push({ path: url.pathname, method });
        if (url.pathname === "/api/ui-settings" && method === "PUT") state.saves.push(JSON.parse(init.body));
        // Return failure so save() cannot clear raw keys or imply a real save.
        return new Response(JSON.stringify({ success: false, ok: false, code: "ux.fixture.blocked" }), { status: 503, headers: { "Content-Type": "application/json" } });
      }
      if (url.origin === location.origin && url.pathname === "/api/audio/input-devices")
        return Response.json({ ok: true, devices: state.devices });
      if (url.origin === location.origin && url.pathname === "/api/tts/devices")
        return Response.json({ ok: true, devices: [{ id: "ux-fixture-out-before", name: "Fixture output A" }, { id: "ux-fixture-out-after", name: "Fixture output B" }] });
      return originalFetch.call(window, input, init);
    };
    return { installed: true, registeredKeysBackedUp: keys.length, mutatingRequestsForwarded: 0 };
  }
  function ensureOption(id, value, name) {
    const select = el(id);
    if (!Array.from(select.options).some(option => option.value === value)) select.add(new Option(name, value));
  }
  async function seed(completed) {
    uiSettings.setItem("mobmate.captureDeviceId", "ux-fixture-mic-before");
    uiSettings.setItem("mobmate.ttsOutputDeviceId", "ux-fixture-out-before");
    if (completed === null) uiSettings.removeItem("mobmate.wizard.completed"); else uiSettings.setItem("mobmate.wizard.completed", completed);
    for (const [id, prefix] of [["rt-device-select", "mic"], ["tts-output-device-select", "out"]]) {
      ensureOption(id, `ux-fixture-${prefix}-before`, "Fixture selection A");
      ensureOption(id, `ux-fixture-${prefix}-after`, "Fixture selection B");
      el(id).value = `ux-fixture-${prefix}-before`;
    }
    await drain();
  }
  async function show(step = 0) {
    assert(state, "Guard not active");
    if (el("setup-overlay").classList.contains("open")) await closeSetupWizard(false);
    await click("menu-btn"); await click("drawer-wizard-btn");
    assert(document.activeElement.id === "setup-title", "Wizard entry focus");
    for (let i = 0; i < step; i++) await click("setup-next-btn");
    assert(activeStep() === step, "Wizard step did not settle");
    if (step === 0) assert(el("setup-mic-select").options.length >= 3, "Fixture microphone enumeration is incomplete");
    if (step === 2) assert(el("setup-out-select").options.length >= 3, "Fixture output enumeration is incomplete");
    return { step, focus: document.activeElement.id, skippedFinalHidden: el("setup-skip-btn").hidden };
  }
  async function run() {
    assert(state, "Guard not active");
    await seed(null);
    const baseline = clientSnapshot();
    for (const action of ["setup-close-btn", "setup-later-btn", "setup-backdrop", "Escape"]) {
      await show(0);
      const mutations = state.mutations.length, puts = count("/api/ui-settings");
      if (action === "Escape") { document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true })); await settle(); }
      else await click(action);
      assert(!el("setup-overlay").classList.contains("open") && document.activeElement.id === "menu-btn", `${action}: close/focus`);
      checkClient(baseline, `${action}: changed existing settings`);
      assert(state.mutations.length === mutations && count("/api/ui-settings") === puts && state.pending.size === 0, `${action}: scheduled a save`);
      state.results.push(`${action}: no new save or completion flag`);
    }
    await show(0);
    await click("setup-skip-btn"); assert(activeStep() === 1 && document.activeElement.id === "setup-step-title-1", "Skip must advance only the current step");
    await click("setup-back-btn"); assert(activeStep() === 0 && document.activeElement.id === "setup-step-title-0", "Back focus");
    checkClient(baseline, "Navigation changed settings");
    await click("setup-next-btn"); await click("setup-skip-btn");
    assert(activeStep() === 2, "Output step");
    const mutations = state.mutations.length, config = count("/api/tts/config"), speak = count("/api/tts/speak");
    el("setup-out-select").value = "ux-fixture-out-after";
    el("setup-out-select").dispatchEvent(new Event("change", { bubbles: true })); await settle();
    assert(state.mutations.length === mutations && uiSettings.getItem("mobmate.ttsOutputDeviceId") === "ux-fixture-out-before", "Output selection alone saved");
    await click("setup-out-play");
    assert(count("/api/tts/config") === config + 1 && count("/api/tts/speak") === speak, "Config failure must not start playback");
    assert(el("setup-out-result").textContent === t("setup.s4.configFailed"), "Output configuration failure text must not imply playback was requested");
    assert(uiSettings.getItem("mobmate.ttsOutputDeviceId") === "ux-fixture-out-after", "Preserve the established explicit client selection");
    await drain();
    assert(state.saves.at(-1).settings["mobmate.ttsOutputDeviceId"] === "ux-fixture-out-after", "Actual deferred save body missing explicit selection");
    state.results.push("Output selection: no save; explicit config failure: partial-change text, no speak; UI-settings PUT captured, never forwarded");
    await click("setup-skip-btn"); assert(activeStep() === 3 && el("setup-skip-btn").hidden, "Final step must not offer skip");
    const beforeFinish = state.mutations.length, beforeRequests = state.requests.length;
    await click("setup-next-btn");
    const finishWrites = state.mutations.slice(beforeFinish);
    assert(finishWrites.length === 1 && finishWrites[0].key === "mobmate.wizard.completed" && !finishWrites[0].remove, "Finish may only set the completion flag");
    assert(uiSettings.getItem("mobmate.wizard.completed") === "1" && state.requests.length === beforeRequests, "Finish started an extra operation");
    await drain(); assert(state.saves.at(-1).settings["mobmate.wizard.completed"] === "1", "Deferred completion save body");
    await show(0); const finished = clientSnapshot(); await click("setup-later-btn"); checkClient(finished, "Revisiting a completed guide changed its flag");
    await show(0);
    el("setup-mic-select").value = "ux-fixture-mic-after"; el("setup-mic-select").dispatchEvent(new Event("change", { bubbles: true })); await settle();
    assert(uiSettings.getItem("mobmate.captureDeviceId") === "ux-fixture-mic-after" && el("rt-device-select").value === "ux-fixture-mic-after", "Explicit microphone selection did not reach the real client/main control");
    await drain(); const explicit = clientSnapshot(); await click("setup-close-btn"); checkClient(explicit, "Close rolled back the explicit selection");
    assert(!state.requests.some(r => ["/api/realtime/start", "/api/tts/speak", "/api/virtual-audio/install", "/api/external/discord-voice-settings", "/api/speaker/reset"].includes(r.path)), "Unexpected destructive/audio/external attempt");
    state.results.push("Step skip/back/final finish and completed revisit preserve their save contracts; explicit microphone change retained; no audio/installer/external/reset attempts");
    uiSettings.setItem("mobmate.captureDeviceId", "ux-fixture-mic-before");
    el("rt-device-select").value = "ux-fixture-mic-before";
    await drain();
    state.devices = [{ id: "ux-fixture-mic-after", name: "Fixture microphone B" }];
    await show(0);
    const missing = el("setup-mic-select"), beforeMissing = state.mutations.length, beforeStarts = count("/api/realtime/start");
    assert(missing.value === "ux-fixture-mic-before" && missing.selectedOptions[0]?.dataset.captureUnavailable === "missing", "Missing saved microphone must remain selected and visibly unavailable");
    await click("setup-mic-test");
    assert(uiSettings.getItem("mobmate.captureDeviceId") === "ux-fixture-mic-before" && state.mutations.length === beforeMissing, "Missing test must not overwrite the client selection");
    assert(count("/api/realtime/start") === beforeStarts && el("setup-mic-result").textContent === t("ui.web.device.inputCannotStart"), "Missing test must not send a Start request");
    await click("setup-close-btn");
    state.results.push("Missing saved microphone remains selected; Wizard test is rejected without a save or Start request");
    return report();
  }
  function report() {
    assert(state, "Guard not active");
    return { results: [...state.results], interceptedMutations: state.requests.map(({ path, method }) => ({ path, method })),
      capturedSettingsPuts: state.saves.length, pendingFixtureSaveCallbacks: state.pending.size, mutatingRequestsForwarded: 0,
      limits: "Real rendered UI/public handlers and actual uiSettings adapter with synthetic device GETs and blocked mutation responses. Escape in run() is a DOM-dispatched key, not native keyboard automation. No backend save, native audio or Review PASS." };
  }
  async function restore() {
    assert(state, "Guard not active");
    const current = state;
    try {
      if (el("setup-overlay").classList.contains("open")) await closeSetupWizard(false);
      if (el("settings-drawer").classList.contains("open")) closeUiModal("settings-drawer");
      for (const key of keys) if (uiSettings.getItem(key) !== current.clientBefore[key]) {
        if (current.clientBefore[key] === null) uiSettings.removeItem(key); else uiSettings.setItem(key, current.clientBefore[key]);
      }
      // Drain the real save closure while PUT is still blocked. The virtual
      // timeout IDs were never registered with the browser, so none can leak.
      await drain(); checkClient(current.clientBefore, "Client Map restoration failed");
      for (const key of keys) {
        if (current.rawBefore[key] === null) rawLocalStorage.removeItem(key); else rawLocalStorage.setItem(key, current.rawBefore[key]);
      }
      for (const saved of current.selects) { el(saved.id).innerHTML = saved.html; el(saved.id).value = saved.value; }
      assert(ttsOutputPending === 0, "Output configuration has not settled");
      // Fixture HTTP mutations never reached the host. Restore its prior
      // confirmation without reviving any older in-flight status observation.
      ttsOutputRevision++;
      ttsOutputAttempted = current.ttsOutput.attempted;
      ttsOutputConfirmed = current.ttsOutput.confirmed;
      assert(keys.every(key => rawLocalStorage.getItem(key) === current.rawBefore[key]), "Raw key presence/value restoration failed");
      const serverAfter = await readServer(current.originalFetch);
      assert(stable(serverAfter) === stable(current.serverBefore), "Settings server document changed; do not claim preservation");
      assert(current.pending.size === 0, "Undrained fixture save callbacks");
      uiSettings.setItem = current.setItem; uiSettings.removeItem = current.removeItem; window.fetch = current.originalFetch;
      state = null;
      return { restored: true, fullClientMap: true, registeredRawKeys: true, mainAndWizardSelects: true, serverDocumentUnchanged: true,
        pendingFixtureSaveCallbacks: 0, fetchRestored: window.fetch === current.originalFetch,
        clientMethodsRestored: uiSettings.setItem === current.setItem && uiSettings.removeItem === current.removeItem, mutatingRequestsForwarded: 0 };
    } catch (error) {
      // Safer to keep the mutation guard than restore fetch with uncertain state.
      return { restored: false, guardRetained: true, error: String(error), pendingFixtureSaveCallbacks: current.pending.size, mutatingRequestsForwarded: 0 };
    }
  }
  window.__mobmateWizardGuard = { begin, run, show, report, restore };
  return { installed: true, started: false, usage: "await __mobmateWizardGuard.begin(); try { await __mobmateWizardGuard.run(); ...screenshots... } finally { await __mobmateWizardGuard.restore(); }" };
})()
