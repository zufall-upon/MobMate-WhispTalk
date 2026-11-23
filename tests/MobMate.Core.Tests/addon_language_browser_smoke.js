// Runs in Chromium at the temporary /language-browser-test.html fixture.
// Uses the real combo/follow module and the real RealtimeSessionClient queue.
(async () => {
  const originalFetch = window.fetch.bind(window), checks = [], requests = [];
  const check = (name, condition) => { if (!condition) throw new Error(name); checks.push(name); };
  const deferred = () => { let resolve; const promise = new Promise(done => { resolve = done; }); return { promise, resolve }; };
  const element = id => document.getElementById(id);
  try {
    const [html, moduleSource, appSource, translations] = await Promise.all([
      originalFetch("/index.html").then(r => r.text()), originalFetch("/addon-language.js").then(r => r.text()),
      originalFetch("/app.js").then(r => r.text()), originalFetch(`/api/ui/i18n?lang=${encodeURIComponent(new URLSearchParams(location.search).get("locale") || "ja")}`).then(r => r.json())
    ]);
    new Function(appSource);
    check("actual app.js parses", true);
    if (new URLSearchParams(location.search).has("navigation-only")) {
      // Full production DOM: unfinished management must not be reachable.
      const parsed = new DOMParser().parseFromString(html, "text/html");
      for (const style of parsed.querySelectorAll("style")) document.head.appendChild(style.cloneNode(true));
      for (const node of parsed.body.children) if (node.tagName !== "SCRIPT") document.body.appendChild(node.cloneNode(true));
      for (const node of document.querySelectorAll("[data-i18n]")) node.textContent = translations.strings[node.dataset.i18n] || node.dataset.i18n;
      let reads = 0;
      const chromeStart = appSource.indexOf("// The drawer and setup wizard are exclusive modals;");
      const chromeEnd = appSource.indexOf("async function pollRealtime(", chromeStart);
      window.fetch = async () => { throw new Error("unexpected navigation request"); };
      new Function("updateVoicevoxInstallUi", "refreshAddonAsr", appSource.slice(chromeStart, chromeEnd) + ";initAppChrome();")(
        () => {}, () => { ++reads; });
      let voicevoxActions = [];
      const voicevoxStart = appSource.indexOf("function ensureVoicevoxPathRow()"), voicevoxEnd = appSource.indexOf("async function browseVoicevoxPath()", voicevoxStart);
      check("actual VOICEVOX setting builder is present", voicevoxStart >= 0 && voicevoxEnd > voicevoxStart);
      new Function("t", "applyI18n", "updateVoicevoxInstallUi", "browseVoicevoxPath", "saveVoicevoxPath", "installVoicevoxLinux",
        appSource.slice(voicevoxStart, voicevoxEnd) + ";ensureVoicevoxPathRow();ensureVoicevoxPathRow();")(
        key => translations.strings[key] || key, () => {}, () => {}, () => voicevoxActions.push("browse"), () => voicevoxActions.push("save"), () => voicevoxActions.push("install"));
      const entry = element("addon-asr-open-btn"), language = element("rt-language-select"), engine = element("rt-engine-select");
      const before = [language.value, engine.value];
      check("both unfinished management surfaces are hidden and inert",
        [entry, element("addon-asr")].every(node => node.hidden && node.inert && node.getAttribute("aria-hidden") === "true" && !node.getClientRects().length));
      check("the existing language picker and base ASR stay visible",
        document.querySelectorAll("#rt-language-select").length === 1 && language.getClientRects().length > 0 && engine.getClientRects().length > 0);
      check("base ASR label and explanation distinguish selection from the actual engine",
        document.querySelector('label[for="rt-engine-select"]').textContent === translations.strings["addon.asr.baseEngine"] &&
        engine.getAttribute("aria-describedby") === "addon-asr-follow-hint" &&
        element("addon-asr-follow-hint").textContent === translations.strings["addon.asr.baseEngineHint"]);
      entry.focus(); entry.click();
      check("hidden entry cannot receive focus or open the drawer even through its old click binding",
        document.activeElement !== entry && !element("settings-drawer").classList.contains("open"));
      element("menu-btn").click();
      check("settings menu opens without exposing management and focuses the title",
        element("settings-drawer").classList.contains("open") && document.activeElement.id === "drawer-title" && !element("addon-asr").getClientRects().length);
      check("Whisper model remains a normal setting outside collapsed diagnostics",
        element("cfg-defaultModelPath").getClientRects().length > 0 && !element("config-diagnostics").open &&
        !element("cfg-defaultModelPath").closest("details") && !element("cfg-nativeExePath").checkVisibility());
      check("dynamic VOICEVOX setting is outside diagnostics and is not duplicated",
        element("cfg-voicevoxExePath").getClientRects().length > 0 && !element("cfg-voicevoxExePath").closest("details") && document.querySelectorAll("#cfg-voicevoxExePath").length === 1);
      element("cfg-voicevoxExePath-browse").click(); element("cfg-voicevoxExePath-save").click();
      check("normal VOICEVOX browse/save retain their public handlers", voicevoxActions.join(",") === "browse,save");
      document.activeElement.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
      check("Escape closes the drawer and returns to the visible menu", !element("settings-drawer").classList.contains("open") && document.activeElement.id === "menu-btn");
      element("menu-btn").click();
      check("existing menu entry still focuses the settings title", document.activeElement.id === "drawer-title");
      element("drawer-backdrop").click();
      check("existing backdrop close still returns focus to the menu", document.activeElement.id === "menu-btn");
      check("navigation requests only the existing read-only refresh and keeps selected settings",
        reads === 2 && language.value === before[0] && engine.value === before[1]);
      window.__languageTestResults = { passed: true, checks, scope: "full source DOM with actual modal/chrome; no runtime, profile mutation or audio" };
      document.getElementById("results").textContent = JSON.stringify(window.__languageTestResults, null, 2);
      document.body.dataset.testResult = "passed";
      return;
    }
    const parsed = new DOMParser().parseFromString(html, "text/html");
    for (const style of parsed.querySelectorAll("style")) document.head.appendChild(style.cloneNode(true));
    // Internal component safety fixture only; production non-exposure is checked
    // above against the unchanged hidden/inert source DOM.
    const managementFixture = parsed.getElementById("addon-asr").cloneNode(true);
    managementFixture.hidden = false; managementFixture.inert = false; managementFixture.removeAttribute("aria-hidden");
    document.getElementById("fixture").appendChild(managementFixture);
    const select = document.createElement("select"); select.id = "rt-language-select";
    for (const code of ["ja", "en", "ko"]) { const option = document.createElement("option"); option.value = code; option.textContent = code; select.appendChild(option); }
    document.getElementById("fixture").prepend(select);
    window.t = (key, vars = {}) => (translations.strings[key] || key).replace(/\{(\w+)\}/g, (_, key) => vars[key] ?? key);
    window.formatBytes = n => ` (${n})`;
    window.confirm = () => true;
    window.addonAsrCatalog = []; window.addonAsrModelTouched = false;
    window.addonAsrEnabled = false; window.addonAsrJointRequested = false; window.addonAsrActiveEngine = null;
    window.realtimePollToken = 0;
    window.setRecordingButtons = window.meterStop = window.spkStop = window.waveClear = () => {};
    window.isMobfyMode = () => false;
    const detailsStart = appSource.indexOf("function updateAddonAsrDetails()"), detailsEnd = appSource.indexOf("async function refreshAddonAsr()", detailsStart);
    if (detailsStart < 0 || detailsEnd <= detailsStart) throw new Error("actual add-on details source missing");
    window.updateAddonAsrDetails = new Function(appSource.slice(detailsStart, detailsEnd) + ";return updateAddonAsrDetails;")();
    const profiles = {}, installed = new Set();
    let current = "ja", ready = false, supported = true, failStop = false, failProfile = false;
    let holdLanguage = null, holdRead = null, holdStatus = null, holdProfile = null, statusState = "running", failStatus = false;
    const engines = [
      { id: "qwen3_asr_ja", name: "Qwen", platform: "windows-x64", runtime: { id: "speech", version: "1", bytes: 1, license: "MIT" },
        models: [{ id: "q8", name: "Qwen JA EN", languages: ["ja", "en"], license: "Apache-2.0", bytes: 10 }] },
      { id: "nemotron_3_5", name: "Nemotron", platform: "windows-x64", runtime: { id: "nemo", version: "1", bytes: 1, license: "Apache-2.0" },
        models: [{ id: "q8", name: "Nemotron KO", languages: ["ja", "en", "ko"], license: "OpenMDW", bytes: 10 }] }
    ];
    const stateFor = lang => {
      const desired = profiles[lang], found = desired && installed.has(`${desired.engineId}/${desired.modelId}`);
      const isReady = supported && current === lang && ready && !!found;
      return { supported: true, runtimeSupported: supported, setupAvailable: supported, loading: false, operationRunning: false,
        profiles: structuredClone(profiles),
        engines: engines.map(e => ({ ...e, models: e.models.map(m => ({ ...m, installed: installed.has(`${e.id}/${m.id}`) })) })),
        state: { ...desired, installed: !!found, ready: isReady },
        languageState: { language: lang, desired, resident: isReady ? { ...desired, language: current, ready: true, active: !!desired.enabled } : null } };
    };
    const json = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
    window.fetch = async (url, init = {}) => {
      requests.push({ url, method: init.method || "GET", body: init.body ? JSON.parse(init.body) : null });
      const request = requests.at(-1);
      if (url.startsWith("/api/addon-asr") && request.method === "GET") {
        const lang = new URL(url, location.origin).searchParams.get("language") || current;
        const result = stateFor(lang);
        if (holdRead && lang === holdRead.language) { const hold = holdRead; holdRead = null; hold.entered.resolve(); await hold.release.promise; }
        return json(result);
      }
      if (url.startsWith("/api/realtime/stop")) { if (failStop) { failStop = false; return json({ ok: false }, 500); } return json({ ok: true }); }
      if (url === "/api/realtime/start") return json({ ok: true });
      if (url === "/api/realtime/status") { holdStatus?.entered?.resolve(); if (holdStatus) await holdStatus.promise; return json({ state: statusState, final: "old-language final" }, failStatus ? 500 : 200); }
      if (url === "/api/voice-conversion/status") return json({ running: false });
      if (url.startsWith("/api/addon-asr/")) {
        const body = request.body || {}, action = url.split("/").at(-1);
        if (action === "install") installed.add(`${body.engineId}/${body.modelId}`);
        if (action === "profile") {
          if (holdProfile) { const hold = holdProfile; holdProfile = null; hold.entered.resolve(); await hold.release.promise; }
          if (failProfile) { failProfile = false; return json({ code: "injected.save_failure" }, 500); }
          profiles[body.language] = { engineId: body.engineId, modelId: body.modelId, enabled: false, jointRequested: false, languageMode: body.languageMode };
        }
        if (action === "probe") { current = body.language; ready = true; }
        if (action === "enabled") profiles[body.language].enabled = body.enabled;
        if (action === "joint") profiles[body.language].jointRequested = body.jointRequested;
        if (action === "language") {
          if (holdLanguage) { const hold = holdLanguage; holdLanguage = null; hold.entered.resolve(); await hold.release.promise; }
          current = body.language; ready = true;
        }
        return json({}, 202);
      }
      throw new Error(`unexpected fixture request ${url}`);
    };
    new Function(moduleSource)();
    const cueStart = appSource.indexOf("const AsrAcceptanceCue = (() => {");
    const clientStart = appSource.indexOf("const RealtimeSessionClient = (() => {");
    window.AsrAcceptanceCue = new Function(appSource.slice(cueStart, clientStart) + ";return AsrAcceptanceCue;")();
    const clientEnd = appSource.indexOf("async function fetchAudioInputDevices()", clientStart);
    window.RealtimeSessionClient = new Function("apiMessage", "getRealtimeSettings", appSource.slice(clientStart, clientEnd) + ";return RealtimeSessionClient;")(
      () => "fixture failure", () => ({ engine: "joint_auto", language: select.value }));
    window.startRealtime = async fromFollow => {
      check("automatic resume does not require a manual engine change", fromFollow === true);
      await RealtimeSessionClient.startRealtimeSession("main", { engine: "joint_auto", language: select.value });
    };
    AddonLanguageUi.init();
    await AddonLanguageUi.refresh();
    if (new URLSearchParams(location.search).has("linux-runtime-only")) {
      engines[0].platform = "linux-x64";
      profiles.ja = { engineId: "nemotron_3_5", modelId: "q8", enabled: false, jointRequested: false, languageMode: "requested" };
      installed.add("nemotron_3_5/q8");
      await AddonLanguageUi.refresh();
      const pick = query => {
        element("addon-asr-trigger").click();
        element("addon-asr-search").value = query;
        element("addon-asr-search").dispatchEvent(new Event("input"));
        element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
        element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
      };
      pick("Qwen");
      check("Linux Qwen platform and install are correct without falsely enabling saved Windows probe",
        !element("addon-asr-setup").disabled && element("addon-asr-probe").disabled &&
        element("addon-asr-details").textContent.includes("linux-x64") && !element("addon-asr-details").textContent.includes("Windows x64 Vulkan"));
      installed.delete("nemotron_3_5/q8"); await AddonLanguageUi.refresh(); pick("Nemotron");
      check("a global Linux runtime never enables Windows-only install or mislabels its platform",
        element("addon-asr-setup").disabled && element("addon-asr-runtime-warning").textContent.length > 0 &&
        element("addon-asr-details").textContent.includes("windows-x64") && !element("addon-asr-details").textContent.includes("linux-x64"));
      pick("Qwen"); profiles.ja = { ...profiles.ja, engineId: "qwen3_asr_ja" }; installed.add("qwen3_asr_ja/q8");
      await AddonLanguageUi.refresh();
      check("saved installed Qwen may probe but is not already Ready or Enabled", !element("addon-asr-probe").disabled && !ready && !profiles.ja.enabled && !profiles.ja.jointRequested);
      check("runtime presentation never posts or starts capture", requests.every(r => r.method === "GET"));
      window.__languageTestResults = { passed: true, checks, scope: "mixed Linux catalog; fake API/runtime; no audio/download" };
      document.getElementById("results").textContent = JSON.stringify(window.__languageTestResults, null, 2);
      document.body.dataset.testResult = "passed";
      return;
    }
    if (new URLSearchParams(location.search).has("reconnect-only")) {
      const noOp = () => {};
      let startupUiUpdates = 0, staleFlagStarts = 0;
      const warning = parsed.getElementById("addon-asr-reconnect-warning").cloneNode(true);
      document.getElementById("fixture").appendChild(warning);
      const resetClient = () => {
        window.RealtimeSessionClient = new Function("apiMessage", "getRealtimeSettings", appSource.slice(clientStart, clientEnd) + ";return RealtimeSessionClient;")(
          () => "fixture failure", () => ({ engine: "joint_auto", language: select.value }));
      };
      window.startRealtime = () => RealtimeSessionClient.startRealtimeSession("main", { engine: "joint_auto", language: select.value, useLanguageProfile: true });
      const syncStart = appSource.indexOf("async function syncStartupRecordingState()");
      const syncEnd = appSource.indexOf("function resetRealtimeLiveUi()", syncStart);
      const sync = new Function("switchMode", "setRecordingButtons", "setRealtimeWasRecording", "setRealtimeGuide", "meterStart", "spkStart", "pollRealtime", "updateRealtimeResults", "vcFetchStatus",
        appSource.slice(syncStart, syncEnd) + ";return syncStartupRecordingState;")(
          noOp, () => { ++startupUiUpdates; }, noOp, noOp, noOp, noOp, noOp, noOp, noOp);
      profiles.ja = { engineId: "qwen3_asr_ja", modelId: "q8", enabled: true, jointRequested: true, languageMode: "requested" };
      profiles.en = { ...profiles.ja, jointRequested: false };
      installed.add("qwen3_asr_ja/q8"); ready = true;
      await AddonLanguageUi.refresh();
      statusState = "recording"; holdStatus = deferred(); holdStatus.resolve();
      const restored = await sync(), adopted = RealtimeSessionClient.isMainActive();
      const unconfirmed = RealtimeSessionClient.requiresReconnect();
      // Current hosts return no session/lease identity: safe inability to
      // reconnect is intentional, not a successful transparent adoption.
      check("unidentified recording is displayed without ownership, with a warning and locked mutations",
        restored && !adopted && unconfirmed && !warning.hidden && element("addon-asr-enabled").disabled && element("addon-asr-joint").disabled);
      const beforeBlocked = requests.length;
      select.value = "en"; await AddonLanguageUi.followLanguage();
      const blockedPosts = requests.slice(beforeBlocked).filter(r => r.method === "POST");
      const blockedVisible = element("addon-asr-status").textContent.includes(translations.strings["addon.asr.reconnectRequired"]);
      await RealtimeSessionClient.stopRealtimeSession("main");
      const clearedAfterStop = !RealtimeSessionClient.requiresReconnect() && warning.hidden;
      select.value = "ja"; current = "ja"; await AddonLanguageUi.refresh();
      await RealtimeSessionClient.startRealtimeSession("main", { language: "ja" });
      const beforeFollow = requests.length;
      select.value = "en"; await AddonLanguageUi.followLanguage();
      const posts = requests.slice(beforeFollow).filter(r => r.method === "POST");
      check("unconfirmed follow has no POST; explicit Stop/Start restores stop-discard/apply/resume",
        blockedPosts.length === 0 && blockedVisible && clearedAfterStop && posts[0]?.url === "/api/realtime/stop?discard=true" &&
        posts[1]?.url === "/api/addon-asr/language" && posts.at(-1)?.url === "/api/realtime/start");
      await RealtimeSessionClient.stopRealtimeSession("main");
      resetClient(); statusState = "idle";
      localStorage.setItem("mobmate.realtime.wasRecording", "1");
      const startupStart = appSource.indexOf("  await syncStartupRecordingState();");
      const startupEnd = appSource.indexOf('  document.getElementById("bench-start-btn")', startupStart);
      if (startupStart < 0 || startupEnd <= startupStart) throw new Error("startup source boundary missing");
      const runStartup = new Function("syncStartupRecordingState", "restoreRealtimeRecordingIfNeeded",
        `return (async () => {${appSource.slice(startupStart, startupEnd)}})();`);
      const beforeIdle = requests.length;
      await runStartup(sync, async () => { ++staleFlagStarts; });
      check("idle host plus stale recording flag never starts capture", staleFlagStarts === 0 &&
        requests.slice(beforeIdle).every(r => r.method === "GET") && !RealtimeSessionClient.requiresReconnect());
      const lateResponses = [];
      for (const action of ["start", "stop"]) {
        resetClient(); statusState = "recording";
        holdStatus = { ...deferred(), entered: deferred() };
        const beforeLateUi = startupUiUpdates;
        const lateStartup = sync(); await holdStatus.entered.promise;
        if (action === "start") await RealtimeSessionClient.startRealtimeSession("main", { language: select.value });
        else await RealtimeSessionClient.stopRealtimeSession("main");
        holdStatus.resolve(); await lateStartup;
        lateResponses.push(startupUiUpdates === beforeLateUi && !RealtimeSessionClient.requiresReconnect() &&
          RealtimeSessionClient.isMainActive() === (action === "start"));
      }
      check("late startup GET cannot overwrite an accepted Start or Stop", lateResponses.every(Boolean));
      resetClient(); holdStatus = null; failStatus = true;
      const beforeFailure = requests.length;
      await runStartup(sync, async () => { ++staleFlagStarts; });
      const unknownAfterError = RealtimeSessionClient.requiresReconnect();
      select.value = "ja"; await AddonLanguageUi.followLanguage();
      failStop = true;
      await RealtimeSessionClient.stopRealtimeSession("main").catch(noOp);
      await runStartup(sync, async () => { ++staleFlagStarts; });
      check("failed GET and failed Stop keep reconnection blocked without any automatic Start",
        unknownAfterError && RealtimeSessionClient.requiresReconnect() && !warning.hidden && staleFlagStarts === 0 &&
        requests.slice(beforeFailure).filter(r => r.method === "POST").every(r => r.url === "/api/realtime/stop"));
      window.__reconnectObservations = { restored, adopted, unconfirmed, blockedPosts: blockedPosts.length, clearedAfterStop, staleFlagStarts, lateResponses, unknownAfterError };
      window.__languageTestResults = { passed: true, checks, observations: window.__reconnectObservations,
        scope: "actual startup/client/follow functions with fake API; no audio/runtime" };
      document.getElementById("results").textContent = JSON.stringify(window.__languageTestResults, null, 2);
      document.body.dataset.testResult = "passed";
      return;
    }
    check("no implicit first model selection", element("addon-asr-model").value === "");
    element("addon-asr-trigger").click();
    element("addon-asr-search").value = "ko";
    element("addon-asr-search").dispatchEvent(new Event("input"));
    element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    check("search and bare Enter do not select or mutate", element("addon-asr-model").value === "" && !requests.some(r => r.method === "POST"));
    element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
    check("keyboard highlight has ARIA active descendant", element("addon-asr-search").getAttribute("aria-activedescendant") === "addon-asr-option-0");
    element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    check("explicit pick changes editing target only", element("addon-asr-model").value === "nemotron_3_5/q8" && !requests.some(r => r.method === "POST"));
    check("pick restores focus and closes combo", document.activeElement.id === "addon-asr-trigger" && element("addon-asr-trigger").getAttribute("aria-expanded") === "false");
    element("addon-asr-trigger").click();
    element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowUp", bubbles: true }));
    check("ArrowUp from no highlight selects last visible result", element("addon-asr-search").getAttribute("aria-activedescendant") === "addon-asr-option-0");
    element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    check("Escape restores trigger focus without changing selection", document.activeElement.id === "addon-asr-trigger" && element("addon-asr-model").value === "nemotron_3_5/q8");
    element("addon-asr-trigger").click();
    element("addon-asr-search").dispatchEvent(new KeyboardEvent("keydown", { key: "Tab", bubbles: true }));
    check("Tab closes list without hijacking focus navigation", element("addon-asr-trigger").getAttribute("aria-expanded") === "false");
    check("recognition language choices include saved-model capabilities", AddonLanguageUi.languageOptions([{ code: "ja" }]).some(o => o.code === "ko"));
    check("catalog-only languages retain an unverified availability label",
      AddonLanguageUi.languageOptions([{ code: "ja" }]).find(o => o.code === "ko")?.label === t("addon.asr.retainedLanguage", { language: "ko" }));
    select.value = "ko"; await AddonLanguageUi.refresh();
    element("addon-asr-setup").click(); await AddonLanguageUi.whenIdle();
    check("installation does not save or enable a profile", !profiles.ko && installed.has("nemotron_3_5/q8"));
    element("addon-asr-save-profile").click(); await AddonLanguageUi.whenIdle();
    check("save is explicit and grants no consent", profiles.ko && !profiles.ko.enabled && !profiles.ko.jointRequested);
    element("addon-asr-probe").click(); await AddonLanguageUi.whenIdle();
    check("probe does not enable or join", !profiles.ko.enabled && !profiles.ko.jointRequested);
    element("addon-asr-enabled").click(); await AddonLanguageUi.whenIdle();
    check("enable does not grant Joint consent", profiles.ko.enabled && !profiles.ko.jointRequested);
    element("addon-asr-joint").click(); await AddonLanguageUi.whenIdle();
    check("Joint consent requires its own action", profiles.ko.jointRequested);
    installed.add("qwen3_asr_ja/q8");
    profiles.ja = { engineId: "qwen3_asr_ja", modelId: "q8", enabled: true, jointRequested: true, languageMode: "requested" };
    profiles.en = { ...profiles.ja, jointRequested: false };
    await RealtimeSessionClient.startRealtimeSession("main", { language: "ko" });
    const before = requests.length;
    select.value = "ja"; const obsolete = AddonLanguageUi.followLanguage();
    select.value = "en"; const latest = AddonLanguageUi.followLanguage();
    await Promise.all([obsolete, latest]);
    const changes = requests.slice(before).filter(r => r.method === "POST");
    check("latest language wins and resumes automatically", changes.filter(r => r.url === "/api/realtime/start").length === 1 && changes.at(-1).body.language === "en");
    check("stop/discard precedes applying language and restarting", changes[0].url.includes("/stop?discard=true") && changes[1].url.endsWith("/language") && changes[1].body.language === "en");
    holdLanguage = { entered: deferred(), release: deferred() };
    const held = holdLanguage;
    select.value = "ko"; const switching = AddonLanguageUi.followLanguage();
    await held.entered.promise;
    const startsBeforeStop = requests.filter(r => r.url === "/api/realtime/start").length;
    await RealtimeSessionClient.stopRealtimeSession("main");
    held.release.resolve(); await switching;
    check("user Stop cancels automatic restart", requests.filter(r => r.url === "/api/realtime/start").length === startsBeforeStop);
    await RealtimeSessionClient.startRealtimeSession("main", { language: "ko" });
    failStop = true;
    const beforeFailure = requests.length;
    select.value = "ja"; await AddonLanguageUi.followLanguage();
    check("uncertain stop does not apply or start another route", requests.slice(beforeFailure).filter(r => r.method === "POST").length === 1);
    holdRead = { language: "ja", entered: deferred(), release: deferred() };
    const heldRead = holdRead;
    select.value = "ja"; const slow = AddonLanguageUi.refresh(); await heldRead.entered.promise;
    select.value = "en"; await AddonLanguageUi.refresh(); heldRead.release.resolve(); await slow;
    check("late GET cannot relabel old-language state as current", element("addon-asr-profile-language").textContent.includes("en"));
    supported = false; ready = false; await AddonLanguageUi.refresh();
    check("unsupported Linux runtime is not shown as Ready", element("addon-asr-setup").disabled && element("addon-asr-probe").disabled && element("addon-asr-runtime-warning").textContent.length > 0);
    check("quality and Joint benefit remain separate from Ready", element("addon-asr-evaluation").textContent.includes("未評価"));
    const translated = window.t;
    window.t = key => key; AddonLanguageUi.translate();
    window.t = translated; AddonLanguageUi.translate();
    check("dynamic labels update when startup i18n becomes available", element("addon-asr-profile-language").textContent.includes("認識言語") && !element("addon-asr-runtime-warning").textContent.startsWith("addon.asr."));
    const status = document.createElement("div"); status.id = "rt-status"; status.textContent = "new language"; document.getElementById("fixture").appendChild(status);
    let published = false;
    window.updateRealtimeResults = () => { published = true; };
    const statusStart = appSource.indexOf("async function fetchRealtimeStatus() {");
    const statusEnd = appSource.indexOf("async function startRealtime(", statusStart);
    const fetchStatus = new Function(appSource.slice(statusStart, statusEnd) + ";return fetchRealtimeStatus;")();
    holdStatus = deferred();
    const oldStatus = fetchStatus(); ++realtimePollToken; holdStatus.resolve(); await oldStatus;
    check("standalone old status responses cannot publish after a language switch", !published && status.textContent === "new language");
    // Two explicit clicks within one event turn must not silently replace Save
    // with an action computed from the previous saved profile.
    await RealtimeSessionClient.stopRealtimeSession("main");
    select.value = "ja"; current = "ja"; supported = true; ready = true;
    await AddonLanguageUi.refresh();
    const beforeRapidClick = requests.length;
    element("addon-asr-save-profile").click();
    element("addon-asr-enabled").click();
    await AddonLanguageUi.whenIdle();
    const rapid = requests.slice(beforeRapidClick).filter(r => r.method === "POST" && r.url.startsWith("/api/addon-asr/"));
    check("explicit Save is not silently superseded by a rapid Enable click", rapid[0]?.url === "/api/addon-asr/profile" && rapid[0].body.engineId === "nemotron_3_5");
    check("rapid Enable cannot mutate the previous saved model after Save", rapid.every(r => r.url !== "/api/addon-asr/enabled") && profiles.ja.engineId === "nemotron_3_5" && !profiles.ja.enabled);
    check("explicit action buttons unlock after completion", !element("addon-asr-enabled").disabled);
    const oldProfile = { ...profiles.ja }, beforeStaleTarget = requests.length;
    element("addon-asr-enabled").click();
    check("conflicting controls disable synchronously on acceptance", element("addon-asr-enabled").disabled && element("addon-asr-save-profile").disabled);
    profiles.ja = { ...oldProfile, packageId: "external-package-change" };
    await AddonLanguageUi.whenIdle();
    check("execution rechecks package identity instead of silently enabling another package", !requests.slice(beforeStaleTarget).some(r => r.url === "/api/addon-asr/enabled") && !profiles.ja.enabled);
    check("stale explicit target produces a visible error", element("addon-asr-status").textContent.includes("保存済み構成が変わった"));
    profiles.ja = oldProfile; await AddonLanguageUi.refresh();
    failProfile = true;
    const beforeFailedSave = requests.length;
    element("addon-asr-save-profile").click(); element("addon-asr-enabled").click();
    await AddonLanguageUi.whenIdle();
    check("a failed Save never chains into Enable of the previous profile", !profiles.ja.enabled && !requests.slice(beforeFailedSave).some(r => r.url === "/api/addon-asr/enabled"));
    await AddonLanguageUi.refresh();
    await RealtimeSessionClient.startRealtimeSession("main", { language: "ja" });
    holdProfile = { entered: deferred(), release: deferred() };
    const heldSave = holdProfile, beforeSaveAndLanguage = requests.length;
    element("addon-asr-save-profile").click();
    await heldSave.entered.promise;
    select.value = "en"; const pendingEn = AddonLanguageUi.followLanguage();
    select.value = "ja"; const pendingJa = AddonLanguageUi.followLanguage();
    select.value = "en"; const finalEn = AddonLanguageUi.followLanguage();
    heldSave.release.resolve();
    await Promise.all([pendingEn, pendingJa, finalEn]);
    const saveAndLanguage = requests.slice(beforeSaveAndLanguage).filter(r => r.method === "POST");
    check("language changes never discard an accepted explicit Save", saveAndLanguage.some(r => r.url === "/api/addon-asr/profile" && r.body.language === "ja"));
    check("only the latest pending language applies after Save", saveAndLanguage.filter(r => r.url === "/api/addon-asr/language").length === 1 && current === "en");
    check("automatic recording resumes only after the latest pending language", saveAndLanguage.at(-1).url === "/api/realtime/start" && saveAndLanguage.at(-1).body.language === "en");
    await RealtimeSessionClient.stopRealtimeSession("main");
    select.value = "ja"; current = "ja"; await AddonLanguageUi.refresh();
    await RealtimeSessionClient.startRealtimeSession("main", { language: "ja" });
    holdProfile = { entered: deferred(), release: deferred() };
    const heldStopSave = holdProfile;
    element("addon-asr-save-profile").click(); await heldStopSave.entered.promise;
    const beforeExplicitStop = requests.filter(r => r.url === "/api/realtime/start").length;
    await RealtimeSessionClient.stopRealtimeSession("main");
    heldStopSave.release.resolve(); await AddonLanguageUi.whenIdle();
    check("Stop preserves an accepted Save but forbids its automatic restart", profiles.ja.engineId === "nemotron_3_5" && requests.filter(r => r.url === "/api/realtime/start").length === beforeExplicitStop);
    // Re-run the actual app's restoration/normalization with a fresh addon
    // instance, as if its catalog request had not completed during startup.
    const savedLanguage = localStorage.getItem("mobmate.realtimeLanguage");
    const savedEngine = localStorage.getItem("mobmate.realtimeEngine");
    try {
      localStorage.setItem("mobmate.realtimeLanguage", "ko");
      localStorage.setItem("mobmate.realtimeEngine", "moonshine");
      select.value = "ja"; ready = false;
      new Function(moduleSource)();
      const engineSelect = document.createElement("select"); engineSelect.id = "rt-engine-select";
      for (const code of ["moonshine", "whisper", "haas_ja"]) { const option = document.createElement("option"); option.value = code; engineSelect.appendChild(option); }
      document.getElementById("fixture").prepend(engineSelect);
      const langStart = appSource.indexOf("function getRealtimeLanguageOptions(");
      const langEnd = appSource.indexOf("/* --- UI i18n --- */", langStart);
      const restoreStart = appSource.indexOf("function restoreRealtimeSettings()");
      const restoreEnd = appSource.indexOf("async function fetchRealtimeLanguages()", restoreStart);
      const settingsStart = appSource.indexOf("function getRealtimeSettings()");
      const settingsEnd = appSource.indexOf("function saveRealtimeSettings(", settingsStart);
      const noOp = () => {};
      const appLanguage = new Function("REALTIME_ENGINE", "defaultRealtimeEngine", "realtimeLanguagesByEngine", "normalizeRealtimeEngine",
        "uiLanguageToRealtimeLanguage", "ensureJointAutoEngineOption", "ensureSafeAutoEngineOption", "ensureParaformerZhEngineOption",
        "normalizeSetting", "REALTIME_SCENES", "REALTIME_PREFILTERS", "addonHintSettings",
        appSource.slice(langStart, langEnd) + appSource.slice(restoreStart, restoreEnd) + appSource.slice(settingsStart, settingsEnd) +
        ";return { normalizeRealtimeLanguage, restoreRealtimeSettings, updateRealtimeLanguageOptions, getRealtimeSettings };")(
          { WHISPER: "whisper" }, "moonshine", { moonshine: [{ code: "en" }, { code: "ja" }], whisper: [], haas_ja: [{ code: "ja" }] },
          value => value, () => "ja", noOp, noOp, noOp, (value, allowed, fallback) => allowed.includes(value) ? value : fallback,
          ["auto"], ["auto"], (engine, lang) => AddonLanguageUi.hintSettings(engine, lang));
      check("saved KO survives normalization before addon catalog arrival", appLanguage.normalizeRealtimeLanguage("ko", "moonshine") === "ko");
      window.updateRealtimeLanguageOptions = appLanguage.updateRealtimeLanguageOptions;
      const restoreReads = [];
      window.refreshAddonAsr = () => { const pending = AddonLanguageUi.refresh(); restoreReads.push(pending); return pending; };
      holdRead = { language: "ja", entered: deferred(), release: deferred() };
      const heldStartup = holdRead, beforeRestore = requests.length;
      const earlyCatalog = AddonLanguageUi.refresh(); await heldStartup.entered.promise;
      appLanguage.restoreRealtimeSettings();
      const immediatelyRestored = select.value;
      const retainedKoLabel = [...select.options].find(o => o.value === "ko")?.textContent || "";
      await Promise.all(restoreReads);
      check("startup restores saved KO rather than a temporary engine default", immediatelyRestored === "ko");
      check("a retained language is labeled as an unverified choice, not Ready", retainedKoLabel.includes("保持した選択") && retainedKoLabel.includes("未確認"));
      check("restoration reads the matching saved-language profile", restoreReads.length === 1 && element("addon-asr-profile-language").textContent.includes("ko"));
      select.value = "en"; localStorage.setItem("mobmate.realtimeLanguage", "en");
      await AddonLanguageUi.refresh();
      heldStartup.release.resolve(); await earlyCatalog;
      check("a late startup catalog cannot undo a newer manual language choice", select.value === "en" && element("addon-asr-profile-language").textContent.includes("en"));
      engineSelect.value = "haas_ja"; appLanguage.updateRealtimeLanguageOptions();
      check("changing the fallback engine never silently changes the selected language", select.value === "en" && appLanguage.getRealtimeSettings().language === "en");
      engines[1].models[0].languages.push("vi");
      await AddonLanguageUi.refresh();
      check("late model-catalog capabilities refresh choices without selecting a language", [...select.options].some(o => o.value === "vi") && select.value === "en");
      check("restoration and catalog refresh have no mutation or recording side effects", requests.slice(beforeRestore).every(r => r.method === "GET"));
      localStorage.setItem("mobmate.realtimeLanguage", "bad<script>");
      check("malformed saved language is never turned into a retained choice", !AddonLanguageUi.languageOptions([]).some(o => o.code === "bad<script>"));
      localStorage.setItem("mobmate.realtimeLanguage", "xx");
      check("unknown arbitrary language codes are not promoted by their syntax alone", !AddonLanguageUi.languageOptions([]).some(o => o.code === "xx"));
      engineSelect.remove();
    } finally {
      if (savedLanguage === null) localStorage.removeItem("mobmate.realtimeLanguage"); else localStorage.setItem("mobmate.realtimeLanguage", savedLanguage);
      if (savedEngine === null) localStorage.removeItem("mobmate.realtimeEngine"); else localStorage.setItem("mobmate.realtimeEngine", savedEngine);
    }
    window.__languageTestResults = { passed: true, checks };
  } catch (error) { window.__languageTestResults = { passed: false, checks, error: error.stack || String(error) }; }
  document.getElementById("results").textContent = JSON.stringify(window.__languageTestResults, null, 2);
  document.body.dataset.testResult = window.__languageTestResults.passed ? "passed" : "failed";
})();
