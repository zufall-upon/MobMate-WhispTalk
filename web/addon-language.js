// Language-scoped add-on controls. Search/pick are local editing operations;
// install, save, probe, enable and Joint consent are separate explicit actions.
window.AddonLanguageUi = (() => {
  let data = null, catalog = [], picked = "", open = false, highlight = -1, choices = [];
  let refreshRevision = 0, changeRevision = 0, serial = Promise.resolve(), resumeMain = false;
  let automaticLanguageOperation = false;
  let explicitPending = false;
  const el = id => document.getElementById(id);
  const language = () => el("rt-language-select")?.value || "ja";
  const explicit = value => typeof value === "string" && value.length <= 20 && /^[a-z]{2,3}(?:-[a-z]{1,8})*$/.test(value) && value !== "auto";
  // Recognition codes, not UI locales or proof of runtime availability. Keep
  // this finite baseline aligned with WebHost's whisperLanguageLabels; observed
  // product catalogs may add further explicit codes during this page session.
  const knownLanguages = new Set(("af am ar as az ba be bg bn bo br bs ca cs cy da de el en es et eu fa fi fo fr gl gu ha haw he hi hr ht hu hy " +
    "id is it ja jw ka kk km kn ko la lb ln lo lt lv mg mi mk ml mn mr ms mt my ne nl nn no oc pa pl ps pt ro ru sa sd si sk sl sn so sq sr su sv sw " +
    "ta te tg th tk tl tr tt uk ur uz vi yi yo yue zh").split(" "));
  const key = model => `${model.engineId}/${model.id}`;
  const message = error => { if (el("addon-asr-status")) el("addon-asr-status").textContent = `${t("addon.asr.error")}: ${error.message}`; };
  const busy = () => explicitPending || data?.loading || data?.operationRunning || data?.state?.downloading;
  const wizardBlocked = () => typeof setupMicBlocksOtherCapture === "function" && setupMicBlocksOtherCapture();
  const wizardRevision = () => typeof setupMicCaptureRevision === "function" ? setupMicCaptureRevision() : 0;
  const checkWizard = revision => {
    if (wizardBlocked() || revision !== wizardRevision()) throw new Error(t("addon.asr.wizardBlocked"));
  };
  // Platform is the existing host-selected catalog contract. A Linux Qwen
  // runtime does not make the other Windows-only catalog entries executable.
  const runtimeFor = model => !!data?.runtimeSupported &&
    (!catalog.some(item => item.platform === "linux-x64") || model?.platform === "linux-x64");
  const profileKey = profile => JSON.stringify(profile ? [profile.engineId, profile.modelId, profile.packageId ?? null,
    profile.enabled === true, profile.jointRequested === true, profile.languageMode || "requested"] : null);

  function setOpen(value, restoreFocus = false) {
    open = value;
    el("addon-asr-wrap").classList.toggle("open", value);
    el("addon-asr-trigger").setAttribute("aria-expanded", String(value));
    el("addon-asr-search").setAttribute("aria-expanded", String(value));
    if (value) { highlight = -1; renderChoices(); el("addon-asr-search").focus(); }
    else { el("addon-asr-search").removeAttribute("aria-activedescendant"); if (restoreFocus) el("addon-asr-trigger").focus(); }
  }
  function selectModel(model) {
    picked = key(model);
    el("addon-asr-model").value = picked;
    addonAsrModelTouched = true;
    el("addon-asr-label").textContent = model.name;
    updateAddonAsrDetails();
    renderControls();
    setOpen(false, true);
  }
  function renderChoices() {
    const query = (el("addon-asr-search").value || "").trim().toLocaleLowerCase();
    choices = catalog.filter(m => `${m.engineId} ${m.name} ${m.languages.join(" ")} ${m.license}`.toLocaleLowerCase().includes(query));
    const list = el("addon-asr-list");
    list.replaceChildren();
    choices.forEach((model, index) => {
      const option = document.createElement("div");
      option.id = `addon-asr-option-${index}`;
      option.className = `pdc-item${index === highlight ? " pdc-selected" : ""}`;
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", String(picked === key(model)));
      option.textContent = `${model.name} · ${model.languages.join(", ")} · ${model.platform}${formatBytes(model.bytes)}`;
      option.addEventListener("mousedown", event => event.preventDefault());
      option.addEventListener("click", () => selectModel(model));
      list.appendChild(option);
    });
    if (!choices.length) { const empty = document.createElement("div"); empty.className = "pdc-item"; empty.textContent = t("addon.asr.noResults"); list.appendChild(empty); }
    if (highlight >= 0 && choices[highlight]) {
      const id = `addon-asr-option-${highlight}`;
      el("addon-asr-search").setAttribute("aria-activedescendant", id);
      el(id)?.scrollIntoView({ block: "nearest" });
    } else el("addon-asr-search").removeAttribute("aria-activedescendant");
  }
  function renderControls() {
    if (!data) return;
    const lang = language(), model = catalog.find(m => key(m) === picked);
    const matching = !explicit(lang) || data.languageState?.language === lang;
    const state = matching ? data.state || {} : {}, description = matching ? data.languageState || {} : {};
    const validLanguage = explicit(lang), modelSupportsLanguage = model?.languages.includes(lang);
    const savedModel = catalog.find(item => key(item) === `${description.desired?.engineId}/${description.desired?.modelId}`);
    const reconnect = typeof RealtimeSessionClient !== "undefined" && RealtimeSessionClient.requiresReconnect?.();
    const wizard = wizardBlocked();
    const locked = busy() || reconnect || wizard;
    el("addon-asr-profile-language").textContent = validLanguage ? t("addon.asr.profileLanguage", { language: lang }) : t("addon.asr.explicitLanguage");
    el("addon-asr-label").textContent = model?.name || t("addon.asr.chooseModel");
    el("addon-asr-setup").disabled = locked || !data.setupAvailable || !model || !runtimeFor(model) || model.installed;
    el("addon-asr-save-profile").disabled = locked || !validLanguage || !modelSupportsLanguage;
    el("addon-asr-probe").disabled = locked || !runtimeFor(savedModel) || !validLanguage || !state.installed || state.ready;
    el("addon-asr-enabled").disabled = locked || !validLanguage || !description.desired || (!state.enabled && !state.ready);
    el("addon-asr-enabled").textContent = t(state.enabled ? "addon.asr.disable" : "addon.asr.enable");
    el("addon-asr-joint").disabled = locked || !validLanguage || !description.desired || (!state.jointRequested && (!state.enabled || !state.ready));
    el("addon-asr-joint").textContent = t(state.jointRequested ? "addon.asr.jointOff" : "addon.asr.jointOn");
    el("addon-asr-joint").setAttribute("aria-pressed", String(!!state.jointRequested));
    el("addon-asr-cancel").disabled = !data.operationRunning;
    const desiredKey = description.desired ? `${description.desired.engineId}/${description.desired.modelId}` : "";
    const desiredName = catalog.find(m => key(m) === desiredKey)?.name || desiredKey;
    el("addon-asr-active").textContent = desiredName ? t("addon.asr.configuredModel", { model: desiredName }) : t("addon.asr.noConfiguredModel");
    el("addon-asr-controls-target").textContent = desiredName ? t("addon.asr.controlsFor", { model: desiredName }) : t("addon.asr.controlsUnavailable");
    const resident = description.resident;
    el("addon-asr-resident").textContent = resident ? t("addon.asr.resident", {
      model: `${resident.engineId}/${resident.modelId}`, language: resident.language,
      ready: String(resident.ready), active: String(resident.active)
    }) : t("addon.asr.noResident");
    el("addon-asr-evaluation").textContent = t("addon.asr.evaluation");
    el("addon-asr-list").setAttribute("aria-label", t("addon.asr.model"));
    el("addon-asr-runtime-warning").textContent = data.runtimeSupported && (!model || runtimeFor(model)) ? "" : t("addon.asr.runtimeUnavailable");
    el("addon-asr-selection").textContent = model ? t(picked === desiredKey ? "addon.asr.selectedCurrent" : "addon.asr.selectedNeedsSetup", { model: model.name }) : "";
    if (model && !modelSupportsLanguage && validLanguage) el("addon-asr-selection").textContent += ` · ${t("addon.asr.languageUnsupported")}`;
    el("addon-asr-hint-controls").hidden = model?.engineId !== "qwen3_asr_ja";
    const status = busy() ? "working" : state.ready && state.enabled ? "enabled" : state.ready ? "ready" : state.installed ? "installed"
      : state.error?.startsWith("settings_invalid:") ? "invalid" : "notInstalled";
    el("addon-asr-status").textContent = t(`addon.asr.${status}`);
    const error = data.operationError || state.error;
    if (error && !["language_not_configured", "addon_runtime_unsupported"].includes(error)) message(new Error(error));
    if (reconnect) message(new Error(t("addon.asr.reconnectRequired")));
    if (wizard) message(new Error(t("addon.asr.wizardBlocked")));
  }
  async function fetchState(lang) {
    const response = await fetch(`/api/addon-asr${explicit(lang) ? `?language=${encodeURIComponent(lang)}` : ""}`);
    if (!response.ok) throw new Error(String(response.status));
    return response.json();
  }
  async function refresh() {
    const revision = ++refreshRevision, lang = language();
    try {
      const next = await fetchState(lang);
      if (revision !== refreshRevision || lang !== language()) return;
      data = next;
      catalog = (data.engines || []).flatMap(engine => engine.models.map(model => ({
        ...model, engineId: engine.id, platform: engine.platform, runtimeName: engine.runtime.id,
        runtimeVersion: engine.runtime.version, runtimeBytes: engine.runtime.bytes,
        runtimeLicense: engine.runtime.license, runtimeSource: engine.runtime.sourcePageUrl
      })));
      addonAsrCatalog = catalog;
      addonAsrEnabled = data.state?.enabled === true;
      addonAsrJointRequested = data.state?.jointRequested === true;
      addonAsrActiveEngine = data.state?.engineId || null;
      if (typeof updateRealtimeLanguageOptions === "function") updateRealtimeLanguageOptions();
      const select = el("addon-asr-model");
      select.replaceChildren();
      const placeholder = document.createElement("option"); placeholder.value = ""; placeholder.textContent = t("addon.asr.chooseModel"); select.appendChild(placeholder);
      for (const model of catalog) { const option = document.createElement("option"); option.value = key(model); option.textContent = model.name; select.appendChild(option); }
      select.value = catalog.some(m => key(m) === picked) ? picked : "";
      updateAddonAsrDetails();
      renderControls();
      if (open) renderChoices();
    } catch (error) { if (revision === refreshRevision && lang === language()) message(error); }
  }
  async function post(path, payload) {
    const response = await fetch(`/api/addon-asr/${path}`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(payload || {}) });
    if (!response.ok) throw new Error(`${response.status}: ${(await response.json().catch(() => ({}))).code || path}`);
    const deadline = Date.now() + 30 * 60 * 1000;
    for (;;) {
      const current = await fetchState(payload?.language || language());
      if ((payload?.language || language()) === language()) { data = current; renderControls(); }
      if (!current.operationRunning && !current.loading) {
        if (current.operationError) throw new Error(current.operationError);
        return current;
      }
      if (Date.now() > deadline) throw new Error("addon_operation_timeout");
      await new Promise(resolve => setTimeout(resolve, 200));
    }
  }
  function cancelFollow() {
    ++changeRevision; resumeMain = false;
    if (automaticLanguageOperation) fetch("/api/addon-asr/cancel", { method: "POST" }).catch(() => {});
  }
  async function pauseMain() {
    if (RealtimeSessionClient.requiresReconnect?.()) throw new Error(t("addon.asr.reconnectRequired"));
    if (!RealtimeSessionClient.isMainActive()) return;
    if (resumeMain || RealtimeSessionClient.isMainActive()) ++realtimePollToken;
    // Explicit stop is the cancellation boundary. Do not publish its old-language
    // result or TTS; restart only after native stop and language application finish.
    await RealtimeSessionClient.stopRealtimeSession("language-switch");
    setRecordingButtons(false); meterStop(); spkStop(); waveClear();
  }
  function queueChange(work, allowResume = true, target = null) {
    const explicitAction = target !== null;
    if (wizardBlocked()) { message(new Error(t("addon.asr.wizardBlocked"))); return Promise.resolve(); }
    if (explicitAction && busy()) return Promise.resolve();
    const wizardAtAcceptance = wizardRevision();
    const revision = explicitAction ? changeRevision : ++changeRevision;
    resumeMain = allowResume && (resumeMain || RealtimeSessionClient.isMainActive());
    if (resumeMain || RealtimeSessionClient.isMainActive()) ++realtimePollToken;
    if (explicitAction) { explicitPending = true; renderControls(); }
    serial = serial.catch(() => {}).then(async () => {
      if (!explicitAction && revision !== changeRevision) return;
      checkWizard(wizardAtAcceptance);
      el("addon-asr-status").textContent = t("addon.asr.switching");
      await pauseMain();
      if (!explicitAction && revision !== changeRevision) return;
      checkWizard(wizardAtAcceptance);
      if (explicitAction && target.profile !== undefined) {
        const fresh = await fetchState(target.language);
        checkWizard(wizardAtAcceptance);
        if (profileKey(fresh.languageState?.desired) !== target.profile)
          throw new Error(`${t("addon.asr.profileChanged")} (${target.language})`);
      }
      await work();
      await refresh();
      checkWizard(wizardAtAcceptance);
      if (revision === changeRevision && resumeMain && (explicitAction || !explicitPending) && !isMobfyMode()) {
        resumeMain = false;
        await startRealtime(true);
      }
    }).finally(() => {
      if (explicitAction) { explicitPending = false; renderControls(); }
    }).catch(error => {
      resumeMain = false;
      if (explicitAction || revision === changeRevision) {
        const warning = t("addon.asr.wizardBlocked");
        message(wizardBlocked() && !error.message.includes(warning) ? new Error(`${error.message} · ${warning}`) : error);
      }
    });
    return serial;
  }
  function queueExplicit(path, payload) {
    // Bind the full saved profile (including package, opt-ins and hint) at the
    // user's click. A later edit must never retarget this operation silently.
    const target = { language: payload.language,
      profile: path === "install" ? undefined : profileKey(data?.profiles?.[payload.language]) };
    return queueChange(() => post(path, payload), true, target);
  }
  function followLanguage(allowResume = true) {
    const lang = language();
    return queueChange(async () => {
      automaticLanguageOperation = true;
      try { await post(explicit(lang) ? "language" : "suspend", { language: lang }); }
      finally { automaticLanguageOperation = false; }
    }, allowResume);
  }
  function init() {
    const trigger = el("addon-asr-trigger"), input = el("addon-asr-search");
    trigger.addEventListener("click", () => setOpen(!open));
    trigger.addEventListener("keydown", event => { if (event.key === "ArrowDown") { event.preventDefault(); setOpen(true); } });
    input.addEventListener("input", () => { highlight = -1; renderChoices(); });
    input.addEventListener("keydown", event => {
      if (event.key === "Escape") { event.preventDefault(); setOpen(false, true); }
      else if (event.key === "Tab") setOpen(false);
      else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        if (choices.length) highlight = highlight < 0 ? (event.key === "ArrowDown" ? 0 : choices.length - 1)
          : (highlight + (event.key === "ArrowDown" ? 1 : -1) + choices.length) % choices.length;
        renderChoices();
      } else if (event.key === "Enter") { event.preventDefault(); if (highlight >= 0 && choices[highlight]) selectModel(choices[highlight]); }
    });
    document.addEventListener("click", event => { if (!el("addon-asr-wrap").contains(event.target)) setOpen(false); });
    el("addon-asr-setup").addEventListener("click", () => {
      const model = catalog.find(m => key(m) === picked);
      if (!model || !confirm(`${t("addon.asr.confirm")}\n${model.name}${formatBytes(model.bytes)} + ${model.runtimeName}${formatBytes(model.runtimeBytes)}\n${model.license}`)) return;
      queueExplicit("install", { engineId: model.engineId, modelId: model.id });
    });
    el("addon-asr-save-profile").addEventListener("click", () => {
      const model = catalog.find(m => key(m) === picked), lang = language();
      if (!model || !model.languages.includes(lang)) return;
      const mode = model.engineId === "qwen3_asr_ja" ? el("addon-asr-language-mode").value || "requested" : "requested";
      queueExplicit("profile", { language: lang, engineId: model.engineId, modelId: model.id, languageMode: mode });
    });
    el("addon-asr-probe").addEventListener("click", () => queueExplicit("probe", { language: language() }));
    el("addon-asr-enabled").addEventListener("click", () => queueExplicit("enabled", { language: language(), enabled: !data.state.enabled }));
    el("addon-asr-joint").addEventListener("click", () => queueExplicit("joint", { language: language(), jointRequested: !data.state.jointRequested }));
    el("addon-asr-cancel").addEventListener("click", () => { cancelFollow(); fetch("/api/addon-asr/cancel", { method: "POST" }).then(refresh); });
    refresh();
  }
  function hintSettings(engine, lang, mixedManifest = false) {
    const profile = data?.profiles?.[lang];
    const used = engine === "qwen3_asr_ja" || (engine === "joint_auto" && profile?.engineId === "qwen3_asr_ja" && profile.enabled && profile.jointRequested);
    return used ? { addonLanguageMode: profile?.languageMode || "requested" } : {};
  }
  function languageOptions(existing) {
    const result = existing.slice();
    for (const option of existing) if (explicit(option.code)) knownLanguages.add(option.code);
    for (const code of new Set(catalog.flatMap(model => model.languages))) {
      if (!explicit(code)) continue;
      knownLanguages.add(code);
      if (!result.some(option => option.code === code))
        result.push({ code, label: t("addon.asr.retainedLanguage", { language: code }) });
    }
    for (const code of [language(), localStorage.getItem("mobmate.realtimeLanguage"), ...Object.keys(data?.profiles || {})]) {
      if (explicit(code) && knownLanguages.has(code) && !result.some(option => option.code === code))
        result.push({ code, label: t("addon.asr.retainedLanguage", { language: code }) });
    }
    return result;
  }
  function translate() {
    if (typeof updateRealtimeLanguageOptions === "function") updateRealtimeLanguageOptions();
    renderControls();
    if (open) renderChoices();
  }
  return { init, refresh, followLanguage, cancelFollow, hintSettings, languageOptions, translate, whenIdle: () => serial };
})();
