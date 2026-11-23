/* MobMate Web UI - minimal dependency-free JS */

const rawLocalStorage = window.localStorage;
const UI_SETTINGS_KEYS = new Set([
  "mobmate.mode",
  "mobmate.captureDeviceId",
  "mobmate.realtimeEngine",
  "mobmate.useLanguageProfile",
  "mobmate.realtimeLanguage",
  "mobmate.realtimeScene",
  "mobmate.realtimePrefilter",
  "mobmate.realtimeInitialPrompt",
  "mobmate.ttsEnabled",
  "mobmate.fillerEnabled",
  "mobmate.ttsOutputDeviceId",
  "mobmate.ttsMonitorVolume",
  "mobmate.wavOutputEnabled",
  "mobmate.ttsEngine",
  "mobmate.voicevoxSpeaker",
  "mobmate.piperPlusModel",
  "mobmate.kokoroModelSpeaker",
  "mobmate.mossModelVoice",
  "mobmate.textSpeech.target",
  "mobmate.uiLanguage",
  "mobmate.wizard.completed",
  "mobmate.initialTtsSetupPrompt.en",
  "mobmate.initialTtsSetupPrompt.ja",
  "mobmate.initialTtsSetupPrompt.ko",
  "mobmate.initialTtsSetupPrompt.zh_cn",
  "mobmate.initialTtsSetupPrompt.zh_tw",
  "mobmate.vc.vcclientBaseUrl",
  "mobmate.vc.rawMode",
  "mobmate.vc.f0Shift",
  "mobmate.vc.speakerId",
  "mobmate.vc.dspEnabled",
  "mobmate.vc.neutralizerPreset",
  "mobmate.vc.neutralizerStrength",
  "mobmate.vc.noiseCleanup",
  "mobmate.vc.worldWet",
  "mobmate.vc.modelId",
  "mobmate.vc.inputDevice",
  "mobmate.vc.outputDevice",
]);

const uiSettings = (() => {
  let settings = new Map();
  let initialized = false;
  let settingsReady = false;
  let loadPromise = null;
  let changedBeforeInitialize = false;
  const pendingSettings = new Map();
  let saveTimer = null;
  let saveSerial = Promise.resolve();

  const load = () => {
    if (loadPromise) return loadPromise;
    loadPromise = (async () => {
      const response = await fetch("/api/ui-settings", { cache: "no-store" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const serverDocument = await response.json();
      if (!serverDocument || typeof serverDocument !== "object" || Array.isArray(serverDocument))
        throw new Error("invalid UI settings");
      if (!serverDocument.settings || typeof serverDocument.settings !== "object" || Array.isArray(serverDocument.settings))
        throw new Error("invalid UI settings values");
      const entries = Object.entries(serverDocument.settings).filter(([key]) => UI_SETTINGS_KEYS.has(key));
      if (entries.some(([, value]) => typeof value !== "string")) throw new Error("invalid UI setting value");
      const restored = new Map(entries);
      if (serverDocument.browserMigrationCompleted !== true) {
        for (const key of UI_SETTINGS_KEYS) {
          const value = rawLocalStorage.getItem(key);
          if (value !== null) restored.set(key, value);
        }
      }
      for (const [key, value] of pendingSettings) {
        if (value === null) restored.delete(key);
        else restored.set(key, value);
      }
      pendingSettings.clear();
      settings = restored;
      settingsReady = true;
      if (initialized) document.dispatchEvent(new Event("mobmate-ui-settings-ready"));
      return serverDocument;
    })().finally(() => { loadPromise = null; });
    return loadPromise;
  };

  const save = () => {
    const pending = saveSerial.catch(() => {}).then(async () => {
      // A fallback Map is not a complete server document. Read the latter before
      // sending any local edits, or an unrelated save could erase saved settings.
      if (!settingsReady) await load();
      const response = await fetch("/api/ui-settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ settings: Object.fromEntries(settings), browserMigrationCompleted: true }),
      });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      UI_SETTINGS_KEYS.forEach(key => rawLocalStorage.removeItem(key));
    });
    saveSerial = pending;
    return pending;
  };

  const scheduleSave = () => {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => save().catch(() => {}), 250);
  };

  return {
    async initialize() {
      try {
        const serverDocument = await load();
        if (serverDocument.browserMigrationCompleted !== true) {
          await save();
        } else {
          UI_SETTINGS_KEYS.forEach(key => rawLocalStorage.removeItem(key));
        }
      } catch {
        // Keep an authoritative read even if its migration save failed.
        if (!settingsReady) {
          settings = new Map();
          for (const key of UI_SETTINGS_KEYS) {
            const value = rawLocalStorage.getItem(key);
            if (value !== null) settings.set(key, value);
          }
          for (const [key, value] of pendingSettings) {
            if (value === null) settings.delete(key);
            else settings.set(key, value);
          }
        }
      } finally {
        initialized = true;
        if (changedBeforeInitialize) { changedBeforeInitialize = false; scheduleSave(); }
      }
    },
    isKnown(key) { return settingsReady || pendingSettings.has(key); },
    isReady() { return settingsReady; },
    async flush() {
      clearTimeout(saveTimer);
      await save();
    },
    async retryLoad() {
      const hadPending = pendingSettings.size > 0;
      await load();
      if (hadPending) scheduleSave();
    },
    getItem(key) {
      if (!UI_SETTINGS_KEYS.has(key)) return rawLocalStorage.getItem(key);
      if (!initialized && !pendingSettings.has(key)) return rawLocalStorage.getItem(key);
      return settings.has(key) ? settings.get(key) : null;
    },
    setItem(key, value) {
      if (!UI_SETTINGS_KEYS.has(key)) return rawLocalStorage.setItem(key, value);
      const normalized = String(value);
      if (!settingsReady) pendingSettings.set(key, normalized);
      else rawLocalStorage.removeItem(key);
      settings.set(key, normalized);
      if (!initialized) changedBeforeInitialize = true;
      if (initialized) scheduleSave();
    },
    removeItem(key) {
      if (!UI_SETTINGS_KEYS.has(key)) return rawLocalStorage.removeItem(key);
      if (!settingsReady) pendingSettings.set(key, null);
      settings.delete(key);
      if (!initialized) changedBeforeInitialize = true;
      if (initialized) scheduleSave();
    },
  };
})();

const localStorage = uiSettings;
let currentMode = localStorage.getItem("mobmate.mode") || "stt";
const MOBFY_STATUS_POLL_MS = 100;

function isMobfyMode() { return currentMode === "mobfy"; }

function switchMode(mode, options = {}) {
  currentMode = mode === "mobfy" ? "mobfy" : "stt";
  // Passive startup/session observation must not overwrite an unread choice.
  if (uiSettings.isReady() || !options.skipExclusion) localStorage.setItem("mobmate.mode", currentMode);
  document.getElementById("mode-stt-tab")?.classList.toggle("active", currentMode === "stt");
  document.getElementById("mode-mobfy-tab")?.classList.toggle("active", currentMode === "mobfy");
  const modePill = document.getElementById("rt-mode-pill");
  if (modePill) {
    modePill.textContent = currentMode === "mobfy" ? t("mode.mobfy") : t("mode.stt");
    modePill.className = `mode-pill ${currentMode === "mobfy" ? "mobfy" : "stt"}`;
  }
  const stt = document.getElementById("mode-stt-content");
  const mobfy = document.getElementById("mode-mobfy-content");
  if (stt) stt.style.display = currentMode === "stt" ? "" : "none";
  if (mobfy) mobfy.style.display = currentMode === "mobfy" ? "block" : "none";
  updateModeSpecificSections();
  const outLabel = document.getElementById("rt-output-meter-label");
  if (outLabel) outLabel.textContent = currentMode === "mobfy" ? "RVC" : "TTS";
  if (!options.skipExclusion) enforceModeExclusion(currentMode);
  if (currentMode === "mobfy") vcCheckLicenseGate();
  updateMainActionLabels();
  if (typeof OnsetFillerUi !== "undefined") void OnsetFillerUi.prepare();
}

function updateModeSpecificSections() {
  const hideSttOnly = currentMode === "mobfy";
  ["translation", "vrchat"].forEach(name => {
    const section = document.querySelector(`[data-accordion="${name}"]`);
    if (section) section.style.display = hideSttOnly ? "none" : "";
  });
}

function updateMainActionLabels() {
  const startBtn = document.getElementById("rt-start-btn");
  const stopBtn = document.getElementById("rt-stop-btn");
  if (startBtn) startBtn.textContent = `▶ ${t("vc.btn.start")}`;
  if (stopBtn && noiseWarmupTimer === null) stopBtn.textContent = `■ ${t("vc.btn.stop")}`;
}

function clearNoiseWarmupUi() {
  noiseWarmupGeneration++;
  if (noiseWarmupTimer !== null) {
    clearInterval(noiseWarmupTimer);
    noiseWarmupTimer = null;
  }
  const stopBtn = document.getElementById("rt-stop-btn");
  if (stopBtn) {
    stopBtn.disabled = false;
    stopBtn.style.background = "";
    stopBtn.style.opacity = "";
    stopBtn.style.cursor = "";
  }
  updateMainActionLabels();
}

function startNoiseWarmupUi(sec, guideKey, { allowStop = false } = {}) {
  clearNoiseWarmupUi();
  const generation = noiseWarmupGeneration;
  const label = remaining => `${allowStop ? `■ ${t("vc.btn.stop")} · ` : ""}${t("ui.web.button.noiseWarmup", { sec: remaining })}`;
  const stopBtn = document.getElementById("rt-stop-btn");
  if (stopBtn) {
    stopBtn.disabled = !allowStop;
    if (!allowStop) {
      stopBtn.style.background = "#555";
      stopBtn.style.opacity = "0.5";
      stopBtn.style.cursor = "not-allowed";
    }
    stopBtn.textContent = label(sec);
  }
  setRealtimeGuide(guideKey, { sec });
  let warmupRemaining = sec;
  noiseWarmupTimer = setInterval(() => {
    if (generation !== noiseWarmupGeneration) return;
    warmupRemaining--;
    const btn = document.getElementById("rt-stop-btn");
    if (warmupRemaining <= 0) {
      clearNoiseWarmupUi();
      setRealtimeGuide(isMobfyMode() ? "vc.guide.runningPassthrough" : "ui.web.guide.recording");
    } else if (btn) {
      btn.textContent = label(warmupRemaining);
    }
  }, 1000);
}

async function stopRealtimeDirectForModeSwitch() {
  if (setupMicBlocksOtherCapture()) return;
  realtimePollToken++;
  clearNoiseWarmupUi();
  setRealtimeWasRecording(false);
  try { await RealtimeSessionClient.stopRealtimeSession("mode-switch"); } catch { /* ignore */ }
  meterStop();
  spkStop();
  waveClear();
  setRecordingButtons(false);
}

async function stopMobfyDirectForModeSwitch() {
  if (typeof OnsetFillerUi !== "undefined") OnsetFillerUi.sessionStopping();
  clearNoiseWarmupUi();
  setMobfyWasRunning(false);
  try { await fetch("/api/voice-conversion/stop", { method: "POST" }); } catch { /* ignore */ }
  vcRunning = false;
  if (typeof OnsetFillerUi !== "undefined") OnsetFillerUi.mobfyStopped();
  if (vcPollingTimer) { clearInterval(vcPollingTimer); vcPollingTimer = null; }
  meterStop();
  spkStop();
  waveClear();
  setRecordingButtons(false);
}

function enforceModeExclusion(activeMode) {
  if (activeMode === "mobfy") stopRealtimeDirectForModeSwitch();
  else stopMobfyDirectForModeSwitch();
}

async function startMainMode() {
  if (isMobfyMode()) return startMobfyVoice();
  return startRealtime();
}

async function stopMainMode() {
  if (isMobfyMode()) return stopMobfyVoice();
  return stopRealtime();
}

/* --- Config --- */
let whisperRuntimeDiagnosticMode = "-";
function renderWhisperRuntimeDiagnostic() {
  setText("runtime-pill", t("ui.web.runtime.whisperDiagnostic", { mode: whisperRuntimeDiagnosticMode }));
}

async function fetchConfig() {
  try {
    ensureVoicevoxPathRow();
    const res = await fetch("/api/config");
    const data = await res.json();
    const fields = ["nativeExePath", "defaultModelPath", "whisperRuntimeMode", "whisperDllDir", "whisperRuntimeReason", "defaultMoonshineDllDir", "defaultMoonshineModelRoot", "voicevoxExePath", "defaultRealtimeEngine"];
    for (const f of fields) {
      const el = document.getElementById(`cfg-${f}`);
      if (!el) continue;
      if (el.tagName === "SELECT") {
        if (data[f] && Array.from(el.options).some(o => o.value === data[f])) el.value = data[f];
      } else if (el.tagName === "INPUT") {
        el.value = data[f] || "";
      } else {
        el.textContent = data[f] || "-";
      }
    }
    applyDefaultRealtimeEngine(data.defaultRealtimeEngine);
    whisperRuntimeDiagnosticMode = typeof data.whisperRuntimeMode === "string" && data.whisperRuntimeMode ? data.whisperRuntimeMode : "-";
    renderWhisperRuntimeDiagnostic();
  } catch (e) {
    const el = document.getElementById("cfg-nativeExePath");
    if (el) el.textContent = t("ui.web.status.error", { message: e.message });
  }
}

async function fetchReleaseStatus() {
  try {
    const res = await fetch("/api/release/status");
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const data = await res.json();
    window.__mobmateReleaseStatus = data;
    isLinuxRuntime = data.runtimeMode === "linux" || data.platform === "linux";
    if (!isLinuxRuntime) isLinuxRuntime = await detectLinuxRuntimeFromHealth();
    applyRuntimeFeatureVisibility();
    updateVoicevoxInstallUi();
    setText("cfg-releaseVersion", data.version || "-");
    setText("cfg-releaseRuntimeMode", data.runtimeMode || "-");
    setText("cfg-releaseRuntimeReasonCode", data.runtimeReasonCode || "-");
    setText("cfg-releaseSteamStatus", data.steamStatus || "-");
    setText("cfg-releaseDlcStatus", data.dlcStatus || "-");
    setText("cfg-releaseCloudConfigDir", data.cloudConfigDir || "-");
    showLegacyWindowsCpuOnlyNotice(data);
    applyRealtimeStartRecovery(data);
  } catch (e) {
    const unavailable = t("ui.web.status.unavailable", { message: e.message });
    setText("cfg-releaseVersion", unavailable);
    setText("cfg-releaseRuntimeMode", unavailable);
    setText("cfg-releaseRuntimeReasonCode", unavailable);
    setText("cfg-releaseSteamStatus", unavailable);
    setText("cfg-releaseDlcStatus", unavailable);
    setText("cfg-releaseCloudConfigDir", unavailable);
  }
}

/* --- Demo session limit (P1) --- */
let demoStatusTimer = null;
let demoExpiredApplied = false;

function showDemoBanner(textKey, vars, showStoreLink) {
  const banner = document.getElementById("demo-banner");
  const text = document.getElementById("demo-banner-text");
  const link = document.getElementById("demo-banner-store-link");
  if (!banner || !text) return;
  banner.style.display = "";
  text.textContent = t(textKey, vars || {});
  if (link) {
    link.style.display = showStoreLink ? "" : "none";
    link.textContent = t("ui.web.demo.banner.storeLink");
  }
}

function applyDemoExpiredUi() {
  if (demoExpiredApplied) return;
  demoExpiredApplied = true;
  const startBtn = document.getElementById("rt-start-btn");
  if (startBtn) startBtn.disabled = true;
  const hearingBtn = document.getElementById("hearing-toggle");
  if (hearingBtn) hearingBtn.disabled = true;
  showDemoBanner("ui.web.demo.banner.expired", {}, true);
  if (demoStatusTimer) { clearInterval(demoStatusTimer); demoStatusTimer = null; }
}

function applyDemoStatus(data) {
  if (!data || !data.isDemo) return;
  if (data.expired || data.remainingSeconds <= 0) {
    applyDemoExpiredUi();
    return;
  }
  if (data.remainingSeconds <= 120) {
    const minutes = Math.max(1, Math.ceil(data.remainingSeconds / 60));
    showDemoBanner("ui.web.demo.banner.remaining", { minutes }, false);
  }
}

async function checkDemoStatus() {
  try {
    const res = await fetch("/api/demo/status");
    if (!res.ok) return;
    applyDemoStatus(await res.json());
  } catch { /* server unreachable: ignore */ }
}

async function initDemoSessionUi() {
  try {
    const res = await fetch("/api/demo/status");
    if (!res.ok) return;
    const data = await res.json();
    if (!data.isDemo) return; // 製品版: ポーリングしない
    applyDemoStatus(data);
    if (!demoExpiredApplied && !demoStatusTimer) {
      demoStatusTimer = setInterval(checkDemoStatus, 60000); // デモ時のみ60秒毎
    }
  } catch { /* ignore */ }
}

function ensureVoicevoxPathRow() {
  const container = document.getElementById("config-voicevox");
  if (!container || document.getElementById("cfg-voicevoxExePath")) return;
  container.innerHTML = `<label for="cfg-voicevoxExePath" data-i18n="ui.web.config.voicevoxExePath">${t("ui.web.config.voicevoxExePath")}</label><input id="cfg-voicevoxExePath" type="text" data-i18n-placeholder="ui.web.config.voicevoxPathPlaceholder" placeholder="${t("ui.web.config.voicevoxPathPlaceholder")}"><div class="config-actions"><button id="cfg-voicevoxExePath-browse" type="button" data-i18n="ui.web.config.voicevoxPathBrowse">${t("ui.web.config.voicevoxPathBrowse")}</button><button id="cfg-voicevoxExePath-save" type="button" data-i18n="ui.web.config.voicevoxPathSave">${t("ui.web.config.voicevoxPathSave")}</button><button id="cfg-voicevox-install" type="button" style="display:none;" data-i18n="ui.web.voicevoxInstall.button">${t("ui.web.voicevoxInstall.button")}</button></div><div id="cfg-voicevoxExePath-status" class="hint"></div><div id="cfg-voicevox-install-status" class="hint"></div>`;
  document.getElementById("cfg-voicevoxExePath-browse")?.addEventListener("click", browseVoicevoxPath);
  document.getElementById("cfg-voicevoxExePath-save")?.addEventListener("click", saveVoicevoxPath);
  document.getElementById("cfg-voicevox-install")?.addEventListener("click", installVoicevoxLinux);
  updateVoicevoxInstallUi();
  applyI18n();
}

async function browseVoicevoxPath() {
  const input = document.getElementById("cfg-voicevoxExePath");
  const status = document.getElementById("cfg-voicevoxExePath-status");
  if (status) status.textContent = t("ui.web.voicevoxPath.browsing");
  try {
    const title = encodeURIComponent(t("ui.web.voicevoxPath.dialogTitle"));
    const res = await fetch(`/api/config/voicevox-path/browse?lang=${encodeURIComponent(currentI18nLanguage)}&title=${title}`, { method: "POST" });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      if (status) status.textContent = apiMessage(data, res.status);
      return;
    }
    if (data.cancelled) {
      if (status) status.textContent = t("ui.web.voicevoxPath.cancelled");
      return;
    }
    if (input) input.value = data.path || "";
    if (status) status.textContent = t("ui.web.voicevoxPath.selected");
  } catch (e) {
    if (status) status.textContent = e.message;
  }
}

async function saveVoicevoxPath() {
  const input = document.getElementById("cfg-voicevoxExePath");
  const status = document.getElementById("cfg-voicevoxExePath-status");
  if (!input) return;
  if (status) status.textContent = t("ui.web.voicevoxPath.saving");
  try {
    const res = await fetch("/api/config/voicevox-path", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path: input.value.trim() })
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) {
      if (status) status.textContent = apiMessage(data, res.status);
      return;
    }
    if (status) status.textContent = t("ui.web.voicevoxPath.saved");
    await fetchConfig();
  } catch (e) {
    if (status) status.textContent = e.message;
  }
}

async function fetchWhisperModels() {
  const select = document.getElementById("cfg-defaultModelPath");
  if (!select) return;
  try {
    const res = await fetch("/api/whisper/models");
    if (!res.ok) return;
    const data = await res.json();
    select.innerHTML = "";
    const models = data.models || [];
    if (models.length === 0) {
      const opt = document.createElement("option");
      opt.value = data.selected || data.autoSelected || "";
      opt.textContent = opt.value || t("ui.web.whisper.noLocalModels");
      select.appendChild(opt);
      return;
    }
    for (const model of models) {
      const opt = document.createElement("option");
      opt.value = model.path;
      opt.textContent = `${model.name}${formatBytes(model.size)}`;
      opt.title = model.path;
      select.appendChild(opt);
    }
    const selected = data.selected || data.autoSelected;
    if (selected && Array.from(select.options).some(o => o.value === selected)) select.value = selected;
  } catch (e) {
    select.innerHTML = "";
    const opt = document.createElement("option");
    opt.value = "";
    opt.textContent = t("ui.web.status.error", { message: e.message });
    select.appendChild(opt);
  }
}

function formatBytes(value) {
  const bytes = Number(value);
  if (!Number.isFinite(bytes) || bytes <= 0) return "";
  return ` (${Math.round(bytes / 1024 / 1024)} MB)`;
}

let whisperModelRequest = null;

async function setWhisperModel(path) {
  if (!path || whisperModelRequest) return;
  const select = document.getElementById("cfg-defaultModelPath");
  const download = document.getElementById("whisper-turbo-download");
  const cancel = document.getElementById("whisper-turbo-cancel");
  const status = document.getElementById("whisper-model-status");
  const controller = new AbortController();
  whisperModelRequest = controller;
  if (select) select.disabled = true;
  if (download) download.disabled = true;
  if (cancel) cancel.hidden = false;
  if (status) status.textContent = t("ui.web.whisper.preparing");
  try {
    const res = await fetch("/api/whisper/model", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path }),
      signal: controller.signal
    });
    const data = await res.json();
    if (!res.ok) throw new Error(apiMessage(data, res.statusText));
    if (status) status.textContent = t("ui.web.whisper.selected");
  } catch (e) {
    if (status) status.textContent = e.name === "AbortError"
      ? t("ui.web.api.whisper.download_cancelled")
      : t("ui.web.whisper.failed", { message: e.message });
  } finally {
    // Restore the server's actual selection even after a failed/cancelled request.
    await fetchWhisperModels();
    try { await fetchConfig(); } catch (_) { /* The next refresh can recover offline config. */ }
    whisperModelRequest = null;
    if (select) select.disabled = false;
    if (download) download.disabled = false;
    if (cancel) cancel.hidden = true;
  }
}

function downloadWhisperTurbo() {
  return setWhisperModel("models/whisper/ggml-large-v3-turbo-q8_0.bin");
}

function cancelWhisperModelRequest() {
  whisperModelRequest?.abort();
}

/* --- Realtime ASR --- */
const REALTIME_ENGINE = Object.freeze({ WHISPER: "whisper", MOONSHINE: "moonshine", JOINT_AUTO: "joint_auto", SAFE_AUTO: "safe_auto", SENSE_VOICE: "sense_voice", PARAFORMER_ZH: "paraformer_zh", HAAS: "haas_ja", PARAKEET: "parakeet_ja", QWEN: "qwen3_asr_ja", NEMOTRON: "nemotron_3_5" });
const REALTIME_ENGINES = Object.freeze(Object.values(REALTIME_ENGINE));
const REALTIME_SCENES = Object.freeze(["auto", "short_command", "talk_clean", "talk_noisy", "talk_low_voice", "desktop_hearing"]);
const REALTIME_PREFILTERS = Object.freeze(["auto", "normal", "strong", "off"]);
const UI_LANGUAGES = Object.freeze(["en", "ja", "ko", "zh_cn", "zh_tw"]);
const TTS_ENGINE = Object.freeze({ AUTO: "auto", VOICEVOX: "voicevox", PIPER: "piper", PIPER_PLUS: "piper_plus", KOKORO: "kokoro", MOSS: "moss" });
const VOICEVOX_OFFICIAL_SITE_URL = "https://voicevox.hiroshiba.jp/";
const REALTIME_WAS_RECORDING_KEY = "mobmate.realtime.wasRecording";

let isLinuxRuntime = false;
let realtimePollToken = 0;
let noiseWarmupTimer = null;
let noiseWarmupGeneration = 0;
let initialTtsSetupPromptShown = false;

function applyRuntimeFeatureVisibility() {
  document.getElementById("cfg-voicevoxExePath-browse")?.style.setProperty("display", isLinuxRuntime ? "none" : "");
  document.getElementById("cfg-voicevoxExePath-save")?.style.setProperty("display", isLinuxRuntime ? "none" : "");
  const voicevoxPathStatus = document.getElementById("cfg-voicevoxExePath-status");
  if (isLinuxRuntime && voicevoxPathStatus) voicevoxPathStatus.textContent = "";
  // Linux-only: system restart button
  const restartRow = document.getElementById("system-restart-row");
  if (restartRow) restartRow.style.display = isLinuxRuntime ? "" : "none";
}

let voicevoxInstallPollTimer = null;
let vcclientInstallPollTimer = null;

function formatVoicevoxDownloadStatus(data) {
  const progress = Number(data.progress || 0);
  const pct = data.totalBytes ? (progress * 100).toFixed(1) : "0.0";
  const receivedMb = data.bytesReceived ? (Number(data.bytesReceived) / 1024 / 1024).toFixed(1) : "0.0";
  const totalMb = data.totalBytes ? (Number(data.totalBytes) / 1024 / 1024).toFixed(1) : "?";
  return `${t("ui.web.voicevoxInstall.downloading")} ${pct}% (${receivedMb}/${totalMb} MB)`;
}

async function updateVoicevoxInstallUi() {
  const btn = document.getElementById("cfg-voicevox-install");
  const status = document.getElementById("cfg-voicevox-install-status");
  if (!btn || !status) return;
  btn.style.display = "inline-block";
  try {
    const res = await fetch("/api/voicevox/install-status");
    const data = await res.json().catch(() => ({}));
    if (res.status === 404) {
      btn.style.display = "none";
      status.textContent = "";
      return;
    }
    if (!res.ok) throw new Error(apiMessage(data, res.status));
    applyRuntimeFeatureVisibility();
    const input = document.getElementById("cfg-voicevoxExePath");
    if (input && data.runPath) input.value = data.runPath;
    btn.disabled = data.running === true;
    if (data.running) {
      status.textContent = formatVoicevoxDownloadStatus(data);
      if (!voicevoxInstallPollTimer) voicevoxInstallPollTimer = setInterval(updateVoicevoxInstallUi, 1500);
    } else {
      if (voicevoxInstallPollTimer) { clearInterval(voicevoxInstallPollTimer); voicevoxInstallPollTimer = null; }
      status.textContent = data.error ? `${t("ui.web.voicevoxInstall.error")}: ${data.error}`
        : data.installed ? "✓"
        : t("ui.web.voicevoxInstall.notInstalled");
    }
  } catch (e) {
    status.textContent = e.message;
  }
}

async function installVoicevoxLinux(skipConfirm = false) {
  await openVoicevoxOfficialSite();
  if (!skipConfirm && !confirm(t("ui.web.voicevoxInstall.confirm"))) return;
  const status = document.getElementById("cfg-voicevox-install-status");
  if (status) status.textContent = t("ui.web.voicevoxInstall.starting");
  try {
    const res = await fetch("/api/voicevox/install", { method: "POST" });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, res.status));
    await updateVoicevoxInstallUi();
  } catch (e) {
    if (status) status.textContent = `${t("ui.web.voicevoxInstall.error")}: ${e.message}`;
  }
}

async function maybeShowInitialTtsSetupPrompt() {
  if (!uiSettings.isReady() || !linuxCoreOnly() || initialTtsSetupPromptShown) return;
  initialTtsSetupPromptShown = true;
  const promptKey = `mobmate.initialTtsSetupPrompt.${currentI18nLanguage}`;
  if (localStorage.getItem(promptKey)) return;

  const engines = ttsEnginesCache?.engines || [];
  if (Array.isArray(engines) && engines.some(e => e.available === true)) {
    localStorage.setItem(promptKey, "available");
    return;
  }

  if (currentI18nLanguage === "ja") {
    if (confirm(t("ui.web.initialTts.voicevoxPrompt"))) {
      localStorage.setItem(promptKey, "voicevox");
      await installVoicevoxLinux(true);
    } else {
      localStorage.setItem(promptKey, "dismissed");
    }
    return;
  }

  const voice = selectInitialPiperVoice();
  if (!voice) return;
  if (confirm(t("ui.web.initialTts.piperPrompt"))) {
    localStorage.setItem(promptKey, "piper");
    openPiperVoiceDownloadGuide(voice.id);
  } else {
    localStorage.setItem(promptKey, "dismissed");
  }
}

function selectInitialPiperVoice() {
  const lang = uiLanguageToRealtimeLanguage(currentI18nLanguage);
  const candidates = _piperVoiceCatalog.filter(v => !v.installed && v.platform !== "windows" && v.onnxUrl);
  return candidates.find(v => v.language === lang)
    || candidates.find(v => v.language === "en")
    || candidates[0]
    || null;
}

function openPiperVoiceDownloadGuide(id) {
  _piperDlSelectedId = id;
  _piperDlRenderList("");
  _piperDlUpdateInfo();
  const details = document.getElementById("piper-voice-dl-settings");
  if (details) details.open = true;
  const section = document.querySelector('[data-accordion="voice"]');
  section?.classList.add("open");
  details?.scrollIntoView({ behavior: "smooth", block: "center" });
  document.getElementById("piper-dl-download-btn")?.focus();
}

async function openVoicevoxOfficialSite() {
  try {
    const res = await fetch("/api/voicevox/official-site", { method: "POST" });
    if (res.ok) return;
  } catch {
    // Fall back to the browser below.
  }
  window.open(VOICEVOX_OFFICIAL_SITE_URL, "_blank", "noopener,noreferrer");
}

async function detectLinuxRuntimeFromHealth() {
  try {
    const res = await fetch("/api/health");
    if (!res.ok) return false;
    const data = await res.json();
    return data.platform === "linux";
  } catch {
    return false;
  }
}

function linuxCoreOnly() {
  return isLinuxRuntime;
}

// ===== Volume Meter & Waveform =====
const WAVE_COLS = 120;
const _waveRing = new Float32Array(WAVE_COLS);
let _waveHead = 0;

let _meterRaf = null;
let _meterTarget = 0, _meterDisp = 0;
let _meterDbTarget = -60, _meterDbDisp = -60;
let _meterPeak = 0, _meterPeakAt = 0;
let _meterLastSpeechMs = 0;

function _meterColorFor(lvl) {
  if (lvl >= 80) return "#dc3c3c";
  if (lvl >= 60) return "#ff9628";
  if (lvl >= 30) return "#3cb45a";
  return "#787878";
}

function _meterStep() {
  const ATK = 0.35, REL = 0.12;
  const a = _meterTarget > _meterDisp ? ATK : REL;
  _meterDisp += (_meterTarget - _meterDisp) * a;
  if (_meterTarget === 0 && _meterDisp < 0.6) _meterDisp = 0;

  const ad = _meterDbTarget > _meterDbDisp ? 0.35 : 0.12;
  _meterDbDisp += (_meterDbTarget - _meterDbDisp) * ad;
  if (_meterDbTarget <= -59.9 && _meterDbDisp < -59.5) _meterDbDisp = -60;

  const now = Date.now();
  const lvl = Math.round(_meterDisp);
  if (lvl >= _meterPeak) { _meterPeak = lvl; _meterPeakAt = now; }
  else if (now - _meterPeakAt > 700) _meterPeak = Math.max(lvl, _meterPeak - 1);

  const fill   = document.getElementById("rt-rms-bar");
  const peakEl = document.getElementById("rt-peak-bar");
  const dbLbl  = document.getElementById("rt-meter-db");
  if (fill)   { fill.style.width = _meterDisp.toFixed(1) + "%"; fill.style.background = _meterColorFor(lvl); }
  if (peakEl) { if (_meterPeak > 0) { peakEl.style.display = "block"; peakEl.style.left = _meterPeak + "%"; } else { peakEl.style.display = "none"; } }
  if (dbLbl)  { dbLbl.textContent = Math.round(Math.max(-60, _meterDbDisp)) + "dB"; }

  _meterRaf = requestAnimationFrame(_meterStep);
}

function meterSetRms(rms, vadState) {
  const now = Date.now();
  if (vadState === "speech") _meterLastSpeechMs = now;
  const recent = (vadState === "speech") || (now - _meterLastSpeechMs <= 180);
  if (!recent && rms < 200) {
    _meterTarget = 0; _meterDbTarget = -60;
  } else {
    _meterTarget = Math.max(0, Math.min(100, rms / 327.68));
    _meterDbTarget = rms > 1 ? Math.max(-60, 20 * Math.log10(rms / 32768)) : -60;
  }
}

function meterStart() {
  if (!_meterRaf) _meterRaf = requestAnimationFrame(_meterStep);
}

function meterStop() {
  if (_meterRaf) { cancelAnimationFrame(_meterRaf); _meterRaf = null; }
  _meterTarget = 0; _meterDisp = 0; _meterDbTarget = -60; _meterDbDisp = -60;
  _meterPeak = 0; _meterLastSpeechMs = 0;
  const fill   = document.getElementById("rt-rms-bar");
  const peakEl = document.getElementById("rt-peak-bar");
  const dbLbl  = document.getElementById("rt-meter-db");
  if (fill)   { fill.style.width = "0%"; fill.style.background = "#787878"; }
  if (peakEl) { peakEl.style.display = "none"; }
  if (dbLbl)  { dbLbl.textContent = "-60dB"; }
}

function waveUpdate(rms) {
  _waveRing[_waveHead] = Math.max(0, Math.min(1, rms / 32767));
  _waveHead = (_waveHead + 1) % WAVE_COLS;
}

function waveDraw() {
  const canvas = document.getElementById("rt-waveform");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const W = canvas.width, H = canvas.height;
  ctx.clearRect(0, 0, W, H);
  const bw = W / WAVE_COLS;
  for (let i = 0; i < WAVE_COLS; i++) {
    const val = _waveRing[(_waveHead + i) % WAVE_COLS];
    const bh = Math.max(1, val * H);
    let c;
    if (val >= 0.80) c = "#dc3c3c";
    else if (val >= 0.60) c = "#ff9628";
    else if (val >= 0.30) c = "#3cb45a";
    else c = "#555";
    ctx.fillStyle = c;
    ctx.fillRect(i * bw, H - bh, bw - 1, bh);
  }
}

function waveClear() {
  _waveRing.fill(0); _waveHead = 0;
  const canvas = document.getElementById("rt-waveform");
  if (canvas) { const ctx = canvas.getContext("2d"); ctx.clearRect(0, 0, canvas.width, canvas.height); }
}

// ===== Speaker Meter (TTS isSpeaking) =====
let _spkRaf = null;
let _spkTarget = 0, _spkDisp = 0;
let _ttsPollToken = null;

function _spkStep() {
  const a = _spkTarget > _spkDisp ? 0.30 : 0.10;
  _spkDisp += (_spkTarget - _spkDisp) * a;
  if (_spkTarget === 0 && _spkDisp < 0.5) _spkDisp = 0;
  const bar = document.getElementById("rt-spk-bar");
  if (bar) {
    const w = Math.min(50, _spkDisp * 0.5).toFixed(1);
    bar.style.width = w + "%";
    bar.style.background = _spkDisp >= 60 ? "#cc5088" : _spkDisp >= 30 ? "#a03060" : "#784858";
  }
  _spkRaf = requestAnimationFrame(_spkStep);
}

function spkStart() {
  if (!_spkRaf) _spkRaf = requestAnimationFrame(_spkStep);
  if (_ttsPollToken) return;
  const tok = {};
  _ttsPollToken = tok;
  (async function loop() {
    if (_ttsPollToken !== tok) return;
    try {
      const r = await fetch("/api/tts/status");
      if (r.ok) { const d = await r.json(); _spkTarget = d.isSpeaking ? 65 : 0; }
    } catch {}
    setTimeout(loop, 500);
  })();
}

function spkStop() {
  _ttsPollToken = null;
  if (_spkRaf) { cancelAnimationFrame(_spkRaf); _spkRaf = null; }
  _spkTarget = 0; _spkDisp = 0;
  const bar = document.getElementById("rt-spk-bar");
  if (bar) bar.style.width = "0%";
}
let lastRealtimeFinal = "";
let lastRealtimeHistoryFinalDetectedAtMs = 0;
let lastRealtimePartial = "";
let lastRealtimeFinalDetectedAtMs = 0;
let lastRealtimeFinalUiAgeMs = null;
let lastTranslatedFinalShown = null;  // 履歴に反映済みの translatedFinal
let lastHistoryOriginalText  = null;  // 現 final の履歴表示用原文
let realtimeFinalTabs = [];
let defaultRealtimeEngine = REALTIME_ENGINE.MOONSHINE;
let realtimeLanguagesByEngine = {
  [REALTIME_ENGINE.WHISPER]: [{ code: "ja", label: "Japanese (ja)" }, { code: "en", label: "English (en)" }, { code: "zh", label: "Chinese (zh)" }, { code: "ko", label: "Korean (ko)" }],
  [REALTIME_ENGINE.MOONSHINE]: [{ code: "ja", label: "Japanese (ja)" }, { code: "en", label: "English (en)" }, { code: "zh", label: "Chinese (zh)" }, { code: "ko", label: "Korean (ko)" }],
  [REALTIME_ENGINE.JOINT_AUTO]: [{ code: "ja", label: "Japanese (ja)" }, { code: "en", label: "English (en)" }, { code: "zh", label: "Chinese (zh)" }, { code: "ko", label: "Korean (ko)" }],
  [REALTIME_ENGINE.SAFE_AUTO]: [{ code: "ja", label: "Japanese (ja)" }, { code: "en", label: "English (en)" }, { code: "zh", label: "Chinese (zh)" }, { code: "ko", label: "Korean (ko)" }],
  [REALTIME_ENGINE.SENSE_VOICE]: [{ code: "ja", label: "Japanese (ja)" }, { code: "en", label: "English (en)" }, { code: "zh", label: "Chinese (zh)" }, { code: "ko", label: "Korean (ko)" }, { code: "yue", label: "Cantonese (yue)" }],
  [REALTIME_ENGINE.PARAFORMER_ZH]: [{ code: "zh", label: "Chinese (zh)" }],
  [REALTIME_ENGINE.HAAS]: [{ code: "ja", label: "Japanese (ja)" }],
};

function normalizeRealtimeEngine(value) {
  return REALTIME_ENGINES.includes(value) ? value : REALTIME_ENGINE.WHISPER;
}

const ENGINE_DISPLAY_NAMES = {
  whisper: "Whisper", moonshine: "Moonshine", joint_auto: "JointAUTO", safe_auto: "Safe Auto", sense_voice: "SenseVoice", paraformer_zh: "Paraformer-zh", haas_ja: "Haas-ja-v1.0", phoneme: "Phoneme"
};
function engineDisplayName(e) { return ENGINE_DISPLAY_NAMES[e] || e || "-"; }

function normalizeSetting(value, allowed, fallback) {
  return allowed.includes(value) ? value : fallback;
}

function normalizeUiLanguage(value) {
  return normalizeSetting(value || "en", UI_LANGUAGES, "en");
}

function uiLanguageToRealtimeLanguage(value) {
  const lang = normalizeUiLanguage(value);
  if (lang === "zh_cn" || lang === "zh_tw") return "zh";
  return lang;
}

/* --- Review prompt (P2) --- */
let reviewPromptTimer = null;
let reviewPromptVisible = false;

async function checkReviewPrompt() {
  if (reviewPromptVisible) return;
  try {
    const res = await fetch("/api/review-prompt/status");
    if (!res.ok) return;
    const data = await res.json();
    if (!data.shouldShow) return;
    const card = document.getElementById("review-prompt-card");
    if (!card) return;
    reviewPromptVisible = true;
    card.style.display = "";
  } catch { /* ignore */ }
}

async function ackReviewPrompt(action) {
  const card = document.getElementById("review-prompt-card");
  if (card) card.style.display = "none";
  reviewPromptVisible = false;
  if (reviewPromptTimer) { clearInterval(reviewPromptTimer); reviewPromptTimer = null; }
  try {
    await fetch("/api/review-prompt/ack", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ action })
    });
  } catch { /* ignore */ }
  if (action === "review") {
    try { await fetch("/api/external/steam-review", { method: "POST" }); } catch { /* ignore */ }
  }
}

function initReviewPromptUi() {
  document.getElementById("review-prompt-write-btn")?.addEventListener("click", () => ackReviewPrompt("review"));
  document.getElementById("review-prompt-later-btn")?.addEventListener("click", () => ackReviewPrompt("later"));
  document.getElementById("review-prompt-never-btn")?.addEventListener("click", () => ackReviewPrompt("never"));
  reviewPromptTimer = setInterval(checkReviewPrompt, 5 * 60 * 1000); // 5分毎
  checkReviewPrompt();
}

function getRealtimeLanguageOptions(engine) {
  const normalized = normalizeRealtimeEngine(engine || document.getElementById("rt-engine-select")?.value || defaultRealtimeEngine);
  const existing = realtimeLanguagesByEngine[normalized] || realtimeLanguagesByEngine[REALTIME_ENGINE.WHISPER] || [];
  return typeof AddonLanguageUi !== "undefined" ? AddonLanguageUi.languageOptions(existing) : existing;
}

function normalizeRealtimeLanguage(value, engine) {
  const options = getRealtimeLanguageOptions(engine);
  const allowed = options.map(x => x.code);
  if (allowed.includes(value)) return value;
  const uiDefault = uiLanguageToRealtimeLanguage(localStorage.getItem("mobmate.uiLanguage"));
  if (allowed.includes(uiDefault)) return uiDefault;
  if (allowed.includes("ja")) return "ja";
  if (allowed.includes("en")) return "en";
  return allowed[0] || "en";
}

/* --- UI i18n --- */
let currentI18n = {};
let currentI18nLanguage = "en";

function t(key, vars = {}) {
  const template = currentI18n[key] || key;
  return template.replace(/\{(\w+)\}/g, (_, name) => vars[name] ?? `{${name}}`);
}

function apiMessage(data, fallback = "") {
  const body = data && typeof data === "object" ? data : {};
  const code = body.code || body.errorCode;
  if (code) {
    const key = `ui.web.api.${code}`;
    const translated = t(key, body.params || {});
    if (translated !== key) return translated;
  }
  return body.detail || body.message || body.error || fallback || t("ui.web.status.unknown");
}

let currentRealtimeGuideKey = "ui.web.guide.start";
let currentRealtimeGuideVars = {};

function setRealtimeGuide(key, vars = {}) {
  currentRealtimeGuideKey = key;
  currentRealtimeGuideVars = vars;
  refreshRealtimeGuide();
}

function refreshRealtimeGuide() {
  let message = t(currentRealtimeGuideKey, currentRealtimeGuideVars);
  const engines = ttsEnginesCache?.engines || [];
  if (linuxCoreOnly() && Array.isArray(engines) && engines.length > 0 && !engines.some(e => e.available === true)) {
    message += ` / ${t("ui.web.guide.ttsNotReady")}`;
  }
  setText("rt-guide", message);
}

async function loadI18n(lang) {
  const language = normalizeUiLanguage(lang || document.getElementById("ui-language-select")?.value || localStorage.getItem("mobmate.uiLanguage"));
  try {
    const res = await fetch(`/api/ui/i18n?lang=${encodeURIComponent(language)}`);
    if (!res.ok) return;
    const data = await res.json();
    currentI18nLanguage = normalizeUiLanguage(data.language || language);
    currentI18n = data.strings || {};
  } catch {
    /* keep existing strings */
  }
}

function applyI18n() {
  document.querySelectorAll("[data-i18n]").forEach((el) => {
    el.textContent = t(el.dataset.i18n);
  });
  document.querySelectorAll("[data-i18n-placeholder]").forEach((el) => {
    el.setAttribute("placeholder", t(el.dataset.i18nPlaceholder));
  });
  document.querySelectorAll("[data-i18n-aria-label]").forEach((el) => {
    el.setAttribute("aria-label", t(el.dataset.i18nAriaLabel));
  });
  updateRadioSpeakButtonLabel();
  renderWhisperRuntimeDiagnostic();
  if (typeof AddonLanguageUi !== "undefined") AddonLanguageUi.translate();
  if (typeof AsrAcceptanceCue !== "undefined") AsrAcceptanceCue.translate();
}

function ensureAutoSceneOption() {
  const el = document.getElementById("rt-scene-select");
  if (!el || Array.from(el.options).some((opt) => opt.value === "auto")) return;
  const opt = document.createElement("option");
  opt.value = "auto";
  opt.textContent = "Auto";
  el.insertBefore(opt, el.firstChild);
}

function ensureJointAutoEngineOption() {
  const addOption = (id) => {
    const el = document.getElementById(id);
    if (!el || Array.from(el.options).some((opt) => opt.value === REALTIME_ENGINE.JOINT_AUTO)) return;
    const opt = document.createElement("option");
    opt.value = REALTIME_ENGINE.JOINT_AUTO;
    opt.textContent = "JointAUTO";
    el.appendChild(opt);
  };
  addOption("rt-engine-select");
  addOption("bench-engine-select");
}

let addonAsrTimer = null;
let addonAsrCatalog = [];
let addonAsrModelTouched = false;
let addonAsrLoadedSelectionApplied = false;
let addonAsrEnabled = false;
let addonAsrJointRequested = false;
let addonAsrActiveEngine = null;
const addonEngineNames = { haas_ja: "Haas-ja-v1.0", parakeet_ja: "Parakeet JA", qwen3_asr_ja: "Qwen3-ASR JA", nemotron_3_5: "Nemotron 3.5" };
function ensureAddonEngineOptions(languages) {
  for (const id of ["rt-engine-select", "bench-engine-select"]) {
    const select = document.getElementById(id);
    if (!select) continue;
    for (const [engine, name] of Object.entries(addonEngineNames)) {
      const available = Array.isArray(languages?.[engine]) && languages[engine].length > 0;
      const existing = Array.from(select.options).find(option => option.value === engine);
      if (available && !existing) {
        const option = document.createElement("option");
        option.value = engine; option.textContent = name;
        select.appendChild(option);
        if (id === "rt-engine-select" && !select.value && localStorage.getItem("mobmate.realtimeEngine") === engine) select.value = engine;
      } else if (!available && existing) existing.remove();
    }
  }
}
function addonHintSettings(engine, language, mixedManifest = false) {
  if (typeof AddonLanguageUi !== "undefined") return AddonLanguageUi.hintSettings(engine, language, mixedManifest);
  const used = engine === "qwen3_asr_ja" || (engine === "joint_auto" && (language === "ja" || mixedManifest) && addonAsrActiveEngine === "qwen3_asr_ja" && addonAsrEnabled && addonAsrJointRequested);
  return used ? { addonLanguageMode: document.getElementById("addon-asr-language-mode")?.value || "" } : {};
}
function addonBenchSettings(engine, language) {
  const individual = engine !== "haas_ja" && Object.hasOwn(addonEngineNames, engine);
  const joint = engine === "joint_auto" && addonAsrActiveEngine !== "haas_ja" && Object.hasOwn(addonEngineNames, addonAsrActiveEngine) && addonAsrEnabled && addonAsrJointRequested;
  return individual || joint ? { mode: "production_replay", ...addonHintSettings(engine, language, true) } : {};
}
function updateAddonAsrDetails() {
  const select = document.getElementById("addon-asr-model");
  const details = document.getElementById("addon-asr-details");
  if (!select || !details) return;
  const model = addonAsrCatalog.find(item => `${item.engineId}/${item.id}` === select.value);
  details.textContent = model ? `${t("addon.asr.languages", { languages: (model.languages || []).join(", "), platform: model.platform || "-" })} · ${model.license} · ${model.runtimeName} ${model.runtimeVersion} (${model.runtimeLicense})${formatBytes(model.runtimeBytes)} + ${model.name}${formatBytes(model.bytes)}` : "";
  const modelSource = document.getElementById("addon-asr-model-source");
  const runtimeSource = document.getElementById("addon-asr-runtime-source");
  if (modelSource) { modelSource.href = model?.sourcePageUrl || "#"; modelSource.hidden = !model?.sourcePageUrl; }
  if (runtimeSource) { runtimeSource.href = model?.runtimeSource || "#"; runtimeSource.hidden = !model?.runtimeSource; }
}

async function refreshAddonAsr() {
  if (typeof AddonLanguageUi !== "undefined") return AddonLanguageUi.refresh();
  const select = document.getElementById("addon-asr-model");
  const status = document.getElementById("addon-asr-status");
  if (!select || !status) return;
  try {
    const res = await fetch("/api/addon-asr");
    if (res.status === 404) { document.getElementById("addon-asr").hidden = true; return; }
    if (!res.ok) throw new Error(String(res.status));
    const data = await res.json();
    addonAsrCatalog = (data.engines || []).flatMap(engine => (engine.models || []).map(model => ({
      ...model, engineId: engine.id, platform: engine.platform, runtimeName: engine.runtime.id,
      runtimeVersion: engine.runtime.version, runtimeBytes: engine.runtime.bytes,
      runtimeLicense: engine.runtime.license, runtimeSource: engine.runtime.sourcePageUrl
    })));
    let previous = select.value;
    if (!data.loading && !addonAsrLoadedSelectionApplied) {
      if (!addonAsrModelTouched && data.state?.engineId && data.state?.modelId) previous = `${data.state.engineId}/${data.state.modelId}`;
      addonAsrLoadedSelectionApplied = true;
    }
    select.replaceChildren();
    for (const model of addonAsrCatalog) {
      const option = document.createElement("option");
      option.value = `${model.engineId}/${model.id}`;
      option.textContent = `${model.name}${formatBytes(model.bytes)}`;
      select.appendChild(option);
    }
    if (addonAsrCatalog.some(model => `${model.engineId}/${model.id}` === previous)) select.value = previous;
    updateAddonAsrDetails();
    const currentKey = data.state?.engineId && data.state?.modelId ? `${data.state.engineId}/${data.state.modelId}` : "";
    const currentModel = addonAsrCatalog.find(model => `${model.engineId}/${model.id}` === currentKey);
    const selectedModel = addonAsrCatalog.find(model => `${model.engineId}/${model.id}` === select.value);
    const currentName = currentModel?.name || currentKey;
    const activeLabel = document.getElementById("addon-asr-active");
    if (activeLabel) activeLabel.textContent = currentName
      ? t(data.state?.ready && data.state?.enabled ? "addon.asr.activeModel" : "addon.asr.configuredModel", { model: currentName })
      : t("addon.asr.noConfiguredModel");
    const selectionLabel = document.getElementById("addon-asr-selection");
    if (selectionLabel) selectionLabel.textContent = selectedModel
      ? t(select.value === currentKey ? "addon.asr.selectedCurrent" : "addon.asr.selectedNeedsSetup", { model: selectedModel.name }) : "";
    const controlsTarget = document.getElementById("addon-asr-controls-target");
    if (controlsTarget) controlsTarget.textContent = currentName
      ? t("addon.asr.controlsFor", { model: currentName }) : t("addon.asr.controlsUnavailable");
    const running = data.operationRunning === true || data.state?.downloading === true || data.loading === true;
    const selectedInstalled = data.state?.installed && `${data.state.engineId}/${data.state.modelId}` === select.value;
    document.getElementById("addon-asr-setup").disabled = !data.supported || !data.setupAvailable || running || !select.value || selectedInstalled;
    document.getElementById("addon-asr-probe").disabled = !data.supported || !data.setupAvailable || running || !data.state?.installed || data.state?.ready;
    addonAsrEnabled = data.state?.enabled === true;
    addonAsrActiveEngine = data.state?.engineId || null;
    const hintControls = document.getElementById("addon-asr-hint-controls");
    if (hintControls) hintControls.hidden = addonAsrActiveEngine !== "qwen3_asr_ja";
    const enabledButton = document.getElementById("addon-asr-enabled");
    enabledButton.disabled = !data.supported || running || (!addonAsrEnabled && !data.state?.ready);
    enabledButton.textContent = t(addonAsrEnabled ? "addon.asr.disable" : "addon.asr.enable");
    addonAsrJointRequested = data.state?.jointRequested === true;
    const jointButton = document.getElementById("addon-asr-joint");
    jointButton.disabled = !data.supported || running || (!addonAsrJointRequested && (!data.jointAvailable || !data.state?.ready || !addonAsrEnabled));
    jointButton.textContent = t(addonAsrJointRequested ? "addon.asr.jointOff" : "addon.asr.jointOn");
    jointButton.setAttribute("aria-pressed", String(addonAsrJointRequested));
    document.getElementById("addon-asr-cancel").disabled = !data.operationRunning;
    const routeAvailable = data.supported && data.routeAvailable === true && data.state?.ready === true && addonAsrEnabled;
    // Probe completion can restore a persisted-enabled engine without an enable click.
    const publishedEngine = Object.keys(addonEngineNames).find(engine => realtimeLanguagesByEngine[engine]?.length > 0) || null;
    if ((routeAvailable ? addonAsrActiveEngine : null) !== publishedEngine) {
      await fetchRealtimeLanguages();
    }
    ensureAddonEngineOptions(realtimeLanguagesByEngine);
    select.disabled = !data.supported || running;
    const state = !data.supported ? "unsupported" : running ? "working" : data.state?.ready && addonAsrEnabled ? "enabled" : data.state?.ready ? "ready" : data.state?.installed ? "installed" : data.state?.present ? "invalid" : "notInstalled";
    status.textContent = t(`addon.asr.${state}`);
    const error = data.operationError || data.state?.error;
    if (error) status.textContent += ` · ${t("addon.asr.error")}: ${error}`;
    if (running && !addonAsrTimer) addonAsrTimer = setInterval(refreshAddonAsr, 1500);
    if (!running && addonAsrTimer) { clearInterval(addonAsrTimer); addonAsrTimer = null; }
  } catch (error) { status.textContent = `${t("addon.asr.error")}: ${error.message}`; }
}

function initAddonAsrUi() {
  if (typeof AddonLanguageUi !== "undefined") return AddonLanguageUi.init();
  document.getElementById("addon-asr-model")?.addEventListener("change", () => { addonAsrModelTouched = true; updateAddonAsrDetails(); refreshAddonAsr(); });
  document.getElementById("addon-asr-setup")?.addEventListener("click", async () => {
    const value = document.getElementById("addon-asr-model")?.value || "";
    const model = addonAsrCatalog.find(item => `${item.engineId}/${item.id}` === value);
    if (!model || !confirm(`${t("addon.asr.confirm")}\n${model.name}${formatBytes(model.bytes)} + ${model.runtimeName}${formatBytes(model.runtimeBytes)}\n${model.license}`)) return;
    const res = await fetch("/api/addon-asr/setup", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ engineId: model.engineId, modelId: model.id }) });
    if (!res.ok) document.getElementById("addon-asr-status").textContent = `${t("addon.asr.error")}: ${res.status}`;
    else await refreshAddonAsr();
  });
  document.getElementById("addon-asr-probe")?.addEventListener("click", async () => {
    const res = await fetch("/api/addon-asr/probe", { method: "POST" });
    if (res.ok) await refreshAddonAsr();
    else document.getElementById("addon-asr-status").textContent = `${t("addon.asr.error")}: ${res.status}`;
  });
  document.getElementById("addon-asr-enabled")?.addEventListener("click", async () => {
    const res = await fetch("/api/addon-asr/enabled", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: !addonAsrEnabled }) });
    if (res.ok) { await refreshAddonAsr(); await fetchRealtimeLanguages(); }
    else document.getElementById("addon-asr-status").textContent = `${t("addon.asr.error")}: ${res.status}`;
  });
  document.getElementById("addon-asr-joint")?.addEventListener("click", async () => {
    const res = await fetch("/api/addon-asr/joint", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ jointRequested: !addonAsrJointRequested }) });
    if (res.ok) await refreshAddonAsr();
    else document.getElementById("addon-asr-status").textContent = `${t("addon.asr.error")}: ${res.status}`;
  });
  document.getElementById("addon-asr-cancel")?.addEventListener("click", async () => {
    await fetch("/api/addon-asr/cancel", { method: "POST" });
    await refreshAddonAsr();
  });
  refreshAddonAsr();
}

function ensureSafeAutoEngineOption() {
  const el = document.getElementById("rt-engine-select");
  if (!el || Array.from(el.options).some((opt) => opt.value === REALTIME_ENGINE.SAFE_AUTO)) return;
  const opt = document.createElement("option");
  opt.value = REALTIME_ENGINE.SAFE_AUTO;
  opt.textContent = "Safe Auto (CPU, no Whisper)";
  el.appendChild(opt);
}

function ensureSenseVoiceEngineOption(available) {
  const el = document.getElementById("rt-engine-select");
  if (!el) return;
  const exists = Array.from(el.options).some((opt) => opt.value === REALTIME_ENGINE.SENSE_VOICE);
  if (available && !exists) {
    const opt = document.createElement("option");
    opt.value = REALTIME_ENGINE.SENSE_VOICE;
    opt.textContent = "SenseVoice";
    el.appendChild(opt);
  } else if (!available && exists) {
    Array.from(el.options).filter(o => o.value === REALTIME_ENGINE.SENSE_VOICE).forEach(o => o.remove());
  }
}

function ensureParaformerZhEngineOption() {
  const el = document.getElementById("rt-engine-select");
  if (!el || Array.from(el.options).some((opt) => opt.value === REALTIME_ENGINE.PARAFORMER_ZH)) return;
  const opt = document.createElement("option");
  opt.value = REALTIME_ENGINE.PARAFORMER_ZH;
  opt.textContent = "Paraformer-zh";
  el.appendChild(opt);
}

function applyDefaultRealtimeEngine(value) {
  defaultRealtimeEngine = normalizeRealtimeEngine(value || REALTIME_ENGINE.MOONSHINE);
  if (localStorage.getItem("mobmate.realtimeEngine")) return;
  const rtEngine = document.getElementById("rt-engine-select");
  if (rtEngine) rtEngine.value = defaultRealtimeEngine;
  const benchEngine = document.getElementById("bench-engine-select");
  if (benchEngine) benchEngine.value = defaultRealtimeEngine;
  updateRealtimeLanguageOptions();
}

function getRealtimeSettings() {
  const engine = normalizeRealtimeEngine(document.getElementById("rt-engine-select")?.value || defaultRealtimeEngine);
  const language = normalizeRealtimeLanguage(document.getElementById("rt-language-select")?.value || "en", engine);
  return {
    engine,
    // Keep existing saved profiles until the user explicitly chooses a base
    // engine. This existing Start API flag never edits the saved profile.
    useLanguageProfile: localStorage.getItem("mobmate.useLanguageProfile") !== "false",
    ...addonHintSettings(engine, language),
    language,
    scene: normalizeSetting(document.getElementById("rt-scene-select")?.value || "auto", REALTIME_SCENES, "auto"),
    prefilterProfile: normalizeSetting(document.getElementById("rt-prefilter-select")?.value || "auto", REALTIME_PREFILTERS, "auto"),
    initialPrompt: document.getElementById("rt-initial-prompt")?.value || "",
    ttsEnabled: document.getElementById("rt-tts-enabled")?.checked ?? true,
    fillerEnabled: document.getElementById("rt-filler-enabled")?.checked === true &&
      (typeof OnsetFillerUi === "undefined" || OnsetFillerUi.canStart())
  };
}

function saveRealtimeSettings(settings) {
  localStorage.setItem("mobmate.realtimeEngine", settings.engine);
  localStorage.setItem("mobmate.realtimeLanguage", settings.language);
  localStorage.setItem("mobmate.realtimeScene", settings.scene);
  localStorage.setItem("mobmate.realtimePrefilter", settings.prefilterProfile);
  localStorage.setItem("mobmate.realtimeInitialPrompt", settings.initialPrompt);
  localStorage.setItem("mobmate.ttsEnabled", JSON.stringify(settings.ttsEnabled !== false));
}

function restoreRealtimeSettings() {
  ensureJointAutoEngineOption();
  ensureSafeAutoEngineOption();
  ensureParaformerZhEngineOption();
  const engine = normalizeRealtimeEngine(localStorage.getItem("mobmate.realtimeEngine") || defaultRealtimeEngine);
  const values = {
    "rt-engine-select": engine,
    "rt-language-select": normalizeRealtimeLanguage(localStorage.getItem("mobmate.realtimeLanguage") || uiLanguageToRealtimeLanguage(localStorage.getItem("mobmate.uiLanguage")), engine),
    "rt-scene-select": normalizeSetting(localStorage.getItem("mobmate.realtimeScene") || "auto", REALTIME_SCENES, "auto"),
    "rt-prefilter-select": normalizeSetting(localStorage.getItem("mobmate.realtimePrefilter") || "auto", REALTIME_PREFILTERS, "auto")
  };
  for (const [id, value] of Object.entries(values)) {
    const el = document.getElementById(id);
    if (el) el.value = value;
  }
  updateRealtimeLanguageOptions(values["rt-language-select"]);
  const prompt = document.getElementById("rt-initial-prompt");
  if (prompt) prompt.value = localStorage.getItem("mobmate.realtimeInitialPrompt") || "";
  const ttsCheckbox = document.getElementById("rt-tts-enabled");
  if (ttsCheckbox) {
    const saved = localStorage.getItem("mobmate.ttsEnabled");
    ttsCheckbox.checked = saved === null ? true : JSON.parse(saved);
  }
  const fillerCheckbox = document.getElementById("rt-filler-enabled");
  if (fillerCheckbox) {
    const saved = localStorage.getItem("mobmate.fillerEnabled");
    fillerCheckbox.checked = uiSettings.isKnown("mobmate.fillerEnabled") && (saved === null || saved === "true");
  }
  if (typeof OnsetFillerUi !== "undefined") void OnsetFillerUi.prepare();
  // The first addon GET may have described the initial HTML language and been
  // discarded during restoration. Read the restored profile; do not apply it.
  if (typeof AddonLanguageUi !== "undefined") refreshAddonAsr();
}

function updateRealtimeLanguageOptions(preferredValue = null) {
  const select = document.getElementById("rt-language-select");
  if (!select) return;
  const engine = normalizeRealtimeEngine(document.getElementById("rt-engine-select")?.value || defaultRealtimeEngine);
  const options = getRealtimeLanguageOptions(engine);
  const current = preferredValue || select.value || localStorage.getItem("mobmate.realtimeLanguage") || uiLanguageToRealtimeLanguage(localStorage.getItem("mobmate.uiLanguage"));
  const normalized = normalizeRealtimeLanguage(current, engine);
  select.innerHTML = "";
  for (const item of options) {
    const opt = document.createElement("option");
    opt.value = item.code;
    opt.textContent = item.label || item.code;
    select.appendChild(opt);
  }
  select.value = normalized;
}

async function fetchRealtimeLanguages() {
  try {
    const res = await fetch("/api/realtime/languages");
    if (!res.ok) return;
    const data = await res.json();
    if (data.languagesByEngine) realtimeLanguagesByEngine = data.languagesByEngine;
    ensureAddonEngineOptions(data.languagesByEngine);
    // SenseVoice エンジン選択肢: モデルがある場合のみ表示
    ensureSenseVoiceEngineOption(data.senseVoiceAvailable === true);
    ensureParaformerZhEngineOption();
    updateRealtimeLanguageOptions();
  } catch {
    updateRealtimeLanguageOptions();
  }
}

function setText(id, text) {
  const el = document.getElementById(id);
  if (el) el.textContent = text;
}

function setRecordingButtons(isRecording) {
  if (!isRecording) setText("rt-active-engine", "-");
  const startBtn = document.getElementById("rt-start-btn");
  const stopBtn = document.getElementById("rt-stop-btn");
  if (startBtn) {
    startBtn.disabled = isRecording;
    startBtn.style.display = isRecording ? "none" : "block";
  }
  if (stopBtn) {
    stopBtn.disabled = !isRecording;
    stopBtn.style.display = isRecording ? "block" : "none";
  }
  // warmup中に録音停止した場合はタイマーをキャンセルし、stopボタンを正常に戻す
  if (!isRecording && noiseWarmupTimer !== null) {
    noiseWarmupGeneration++;
    clearInterval(noiseWarmupTimer);
    noiseWarmupTimer = null;
    if (stopBtn) {
      stopBtn.disabled = false;
      stopBtn.style.background = "";
      stopBtn.style.opacity = "";
      stopBtn.style.cursor = "";
      updateMainActionLabels();
    }
  }
  updateMainActionLabels();
}

function formatVcclientInstallStatus(data) {
  const progress = Number(data.progress || 0);
  const pct = data.totalBytes ? (progress * 100).toFixed(1) : "0.0";
  const receivedMb = data.bytesReceived ? (Number(data.bytesReceived) / 1024 / 1024).toFixed(1) : "0.0";
  const totalMb = data.totalBytes ? (Number(data.totalBytes) / 1024 / 1024).toFixed(1) : "?";
  return `${t("vc.model.vcclientInstalling")} ${pct}% (${receivedMb}/${totalMb} MB)`;
}

async function updateVcclientInstallUi() {
  const btn = document.getElementById("vcclient-install-btn");
  const status = document.getElementById("vc-model-status");
  if (!btn) return;
  try {
    const res = await fetch("/api/vcclient/install-status");
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, res.status));
    btn.disabled = data.running === true;
    if (data.supported === false) {
      btn.textContent = t("vc.model.vcclientOpenOfficial");
      if (status && data.error) status.textContent = data.error;
      return;
    }
    btn.textContent = data.engineRunning ? "✓ VCClient" : ((data.installed || data.extracted) ? t("vc.model.vcclientStartBtn") : t("vc.model.vcclientInstallBtn"));
    if (data.running) {
      if (status) status.textContent = formatVcclientInstallStatus(data);
      if (!vcclientInstallPollTimer) vcclientInstallPollTimer = setInterval(updateVcclientInstallUi, 1500);
    } else {
      if (vcclientInstallPollTimer) { clearInterval(vcclientInstallPollTimer); vcclientInstallPollTimer = null; }
      if (data.error && status) status.textContent = `${t("vc.model.vcclientInstallError")}: ${data.error}`;
    }
  } catch (e) {
    if (status) status.textContent = e.message;
  }
}

async function installVcclient() {
  const status = document.getElementById("vc-model-status");
  try {
    const sr = await fetch("/api/vcclient/install-status");
    const sd = await sr.json().catch(() => ({}));
    if (sd.supported === false) {
      await fetch("/api/vcclient/official-site", { method: "POST" }).catch(() => {});
      if (status) status.textContent = sd.error || t("vc.model.vcclientLinuxGuide");
      return;
    }
    if (sd.engineRunning === true) {
      if (status) status.textContent = "✓ VCClient";
      return;
    }
    const alreadyInstalled = sd.installed === true || sd.extracted === true;
    if (!confirm(alreadyInstalled ? t("vc.model.vcclientStartConfirm") : t("vc.model.vcclientInstallConfirm"))) return;
    if (status) status.textContent = alreadyInstalled ? t("vc.model.vcclientStarting") : t("vc.model.vcclientInstalling");
    const res = await fetch("/api/vcclient/install", { method: "POST" });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, res.status));
    await updateVcclientInstallUi();
  } catch (e) {
    if (status) status.textContent = `${t("vc.model.vcclientInstallError")}: ${e.message}`;
  }
}

function spkSetLevel(level) {
  if (!_spkRaf) _spkRaf = requestAnimationFrame(_spkStep);
  _spkTarget = Math.max(0, Math.min(100, Number(level) || 0));
}

function selectedDeviceName(selectId) {
  const select = document.getElementById(selectId);
  const option = select?.selectedOptions?.[0];
  return option?.dataset?.deviceName || "";
}

function selectedDeviceIdOrName(selectId) {
  const select = document.getElementById(selectId);
  const option = select?.selectedOptions?.[0];
  return option?.value || option?.dataset?.deviceName || "";
}

function vcCleanDeviceName(name) {
  return String(name || "").replace(/\s*\[[^\]]+\]\s*$/u, "").trim();
}

function formatTtsDeviceLabel(device) {
  const unnamed = t("ui.web.device.unnamed");
  const baseName = (device?.name || "").trim();
  const friendly = (device?.friendlyName || "").trim();
  const cleanBase = baseName.replace(/\s*\[[^\]]+\]\s*$/u, "").trim();
  if (cleanBase && friendly && cleanBase !== friendly) {
    return `${cleanBase}（${friendly}）`;
  }
  return cleanBase || friendly || (device?.id || "").trim() || unnamed;
}

function appendTtsOutputDeviceOptions(select, devices, includeDefault = true) {
  if (!select) return;
  select.innerHTML = "";
  if (includeDefault) {
    const defaultOption = document.createElement("option");
    defaultOption.value = "";
    defaultOption.dataset.deviceName = "";
    defaultOption.textContent = t("ui.web.device.defaultOutput");
    select.appendChild(defaultOption);
  }
  for (const device of devices || []) {
    const option = document.createElement("option");
    option.value = device.id || "";
    option.dataset.deviceName = vcCleanDeviceName(device.name || device.friendlyName || "");
    option.textContent = `${formatTtsDeviceLabel(device)}${device.isDefault ? ` (${t("ui.web.device.default")})` : ""}${device.wirelessLikely ? ` (${t("ui.web.device.wireless")})` : ""}`;
    select.appendChild(option);
  }
}

function showLegacyWindowsCpuOnlyNotice(data) {
  if (!data?.legacyWindowsCpuOnly) return;
  const key = "mobmate.legacyWindowsCpuOnlyNoticeShown";
  if (sessionStorage.getItem(key) === "1") return;
  sessionStorage.setItem(key, "1");
  setTimeout(() => alert(t("ui.web.runtime.legacyWindowsCpuOnly")), 200);
}

function applyRealtimeStartRecovery(data) {
  if (!data?.realtimeStartRecoveryRequired) return;
  ensureSafeAutoEngineOption();
  localStorage.setItem("mobmate.realtimeEngine", REALTIME_ENGINE.SAFE_AUTO);
  const select = document.getElementById("rt-engine-select");
  if (select) select.value = REALTIME_ENGINE.SAFE_AUTO;
  const key = "mobmate.realtimeStartRecoveryNoticeShown";
  if (sessionStorage.getItem(key) === "1") return;
  sessionStorage.setItem(key, "1");
  setTimeout(() => alert(t("ui.web.realtime.safeAutoRecovery")), 200);
}

function setRealtimeWasRecording(value) {
  localStorage.setItem(REALTIME_WAS_RECORDING_KEY, value ? "1" : "0");
}

const MOBFY_WAS_RUNNING_KEY = "mobmate.vc.wasRunning";

function setMobfyWasRunning(value) {
  localStorage.setItem(MOBFY_WAS_RUNNING_KEY, value ? "1" : "0");
}

function shouldRestoreMobfyVoice() {
  return isMobfyMode() && localStorage.getItem(MOBFY_WAS_RUNNING_KEY) === "1";
}

async function restoreMobfyVoiceIfNeeded() {
  if (!shouldRestoreMobfyVoice()) return;
  try {
    const res = await fetch("/api/voice-conversion/status");
    if (res.ok) {
      const vc = await res.json();
      const vcStatus = vc.status || vc.nativeStatus || vc;
      if (!!(vcStatus.running ?? vc.running)) return;
    }
  } catch { /* ignore */ }
  await startMobfyVoice();
}

async function syncStartupRecordingState() {
  try {
    const vcRes = await fetch("/api/voice-conversion/status");
    if (vcRes.ok) {
      const vc = await vcRes.json();
      const vcStatus = vc.status || vc.nativeStatus || vc;
      if (!!(vcStatus.running ?? vc.running)) {
        switchMode("mobfy", { skipExclusion: true });
        setRecordingButtons(true);
        vcFetchStatus();
        return true;
      }
    }
  } catch { /* ignore */ }

  try {
    const rt = await RealtimeSessionClient.getStartupStatus();
    if (rt?.state === "recording") {
      switchMode("stt", { skipExclusion: true });
      setRecordingButtons(true);
      setRealtimeWasRecording(true);
      setRealtimeGuide("ui.web.guide.recording");
      meterStart();
      spkStart();
      pollRealtime(++realtimePollToken);
      updateRealtimeResults(rt);
      return true;
    }
  } catch { /* ignore */ }
  return false;
}

function resetRealtimeLiveUi() {
  lastRealtimeFinal = "";
  lastRealtimeHistoryFinalDetectedAtMs = 0;
  lastRealtimePartial = "";
  lastTranslatedFinalShown = null;
  lastHistoryOriginalText  = null;
  realtimeFinalTabs = [];
  setText("rt-live-vad", "-");
  setText("rt-live-final-age", "-");
  setText("rt-live-engine", "-");
  setText("rt-active-engine", "-");
  setText("rt-live-rms", "0.0000");
  setText("rt-partial", "");
  renderRealtimeTabs();
  setRealtimeGuide("ui.web.guide.recording");
  meterStop();
  waveClear();
  spkStop();
  const progress = document.getElementById("rt-progress-bar");
  if (progress) progress.style.width = "0%";
  const history = document.getElementById("rt-final-history");
  if (history) history.innerHTML = "";
  const panel = document.getElementById("rt-live-panel");
  if (panel) panel.style.display = "block";
}

function renderRealtimeTabs(partialText = "") {
  const tabs = document.getElementById("rt-final-tabs");
  if (!tabs) return;
  tabs.innerHTML = "";
  const latest = partialText || realtimeFinalTabs[0] || t("ui.web.latest.empty");
  const first = document.createElement("div");
  first.id = "rt-latest-tab";
  first.className = `tab active${partialText ? " partial" : ""}`;
  first.textContent = latest;
  tabs.appendChild(first);

  const rest = (partialText ? realtimeFinalTabs : realtimeFinalTabs.slice(1)).slice(0, 7);
  for (const text of rest) {
    const item = document.createElement("div");
    item.className = "tab";
    item.textContent = text;
    tabs.appendChild(item);
  }
  while (tabs.children.length < 4) {
    const waiting = document.createElement("div");
    waiting.className = "tab";
    waiting.textContent = t("ui.web.waiting");
    tabs.appendChild(waiting);
  }
}

function appendRealtimeFinalHistory(text, data) {
  const history = document.getElementById("rt-final-history");
  if (!history) return null;
  const item = document.createElement("li");
  const main = document.createElement("div");
  const meta = document.createElement("div");
  main.className = "rt-history-main";
  meta.className = "rt-history-meta";
  main.textContent = `✓ ${text}`;
  const now = new Date().toLocaleTimeString();
  const rms = data?.captureRms == null ? "-" : Number(data.captureRms).toFixed(1);
  const latency = data?.latencyMs == null ? "-" : `${data.latencyMs}ms`;
  const finalAge = formatRealtimeLatencyMs(getRealtimeFinalAgeMs(data));
  const ttsStart = formatRealtimeLatencyMs(data?.finalToTtsSpeakMs);
  const playback = formatRealtimeLatencyMs(data?.finalToPlaybackMs);
  meta.textContent = `${now}  final  state=${data?.state || "-"}  vad=${data?.vadState || "-"}  rms=${rms}  asr=${latency}  finalAge=${finalAge}  ttsStart=${ttsStart}  playback=${playback}`;
  item.appendChild(main);
  item.appendChild(meta);
  history.prepend(item);
  while (history.children.length > 30) history.removeChild(history.lastChild);
  return item;
}

/* ── Translation ─────────────────────────────────────────── */
async function loadTranslationConfig() {
  try {
    const res = await fetch("/api/translation/config");
    if (!res.ok) return;
    const data = await res.json();
    const sel = document.getElementById("translation-target-select");
    if (sel && sel.options.length <= 1) {
      // populate if empty
      await populateTranslationTargets();
    }
    const target = data.talkTarget || "OFF";
    if (sel) sel.value = target;
    const unavailNote = document.getElementById("translation-unavailable-note");
    if (unavailNote) unavailNote.style.display = (!data.modelAvailable && target !== "OFF") ? "" : "none";
  } catch (e) { /* ignore */ }
}

async function populateTranslationTargets() {
  const sel = document.getElementById("translation-target-select");
  if (!sel) return;
  try {
    const res = await fetch("/api/translation/languages");
    if (!res.ok) return;
    const langs = await res.json();
    sel.innerHTML = "";
    for (const lang of langs) {
      const opt = document.createElement("option");
      opt.value = lang.code;
      opt.textContent = lang.label;
      sel.appendChild(opt);
    }
  } catch (e) { /* ignore */ }
}

async function saveTranslationTarget(value) {
  try {
    await fetch("/api/translation/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ talkTarget: value })
    });
    if (value === "OFF") {
      const unavailNote = document.getElementById("translation-unavailable-note");
      if (unavailNote) unavailNote.style.display = "none";
    } else {
      await loadTranslationConfig();
    }
  } catch (e) { /* ignore */ }
}

function formatRealtimeLatencyMs(value) {
  if (value == null || value === "") return "-";
  const n = Number(value);
  return Number.isFinite(n) && n >= 0 ? `${Math.round(n)}ms` : "-";
}

function getRealtimeFinalAgeMs(data) {
  const ts = Number(data?.finalDetectedAtMs || 0);
  if (!Number.isFinite(ts) || ts <= 0) return null;
  if (ts !== lastRealtimeFinalDetectedAtMs) {
    lastRealtimeFinalDetectedAtMs = ts;
    lastRealtimeFinalUiAgeMs = Math.max(0, Date.now() - ts);
  }
  return lastRealtimeFinalUiAgeMs;
}

function getRealtimeDisplayEngine(data) {
  const selected = data?.selectedEngine || data?.selected_engine || "";
  // This card is the last result engine, not the requested or resident engine.
  return selected && selected !== "none" ? selected : "";
}

function updateTranslationSourceDisplay(lang) {
  const el = document.getElementById("translation-source-display");
  if (el && lang) el.textContent = lang;
}

function addRealtimeFinalHistory(text, data) {
  if (!text) return;
  realtimeFinalTabs.unshift(text);
  realtimeFinalTabs = realtimeFinalTabs.slice(0, 8);
  renderRealtimeTabs();
  appendRealtimeFinalHistory(text, data);
}

function getNewFinalText(finalText, wholeSegment = false) {
  if (!finalText || finalText === lastRealtimeFinal) return "";
  if (wholeSegment) return finalText;
  if (lastRealtimeFinal && finalText.startsWith(lastRealtimeFinal)) {
    return finalText.substring(lastRealtimeFinal.length).replace(/^\s+/, "");
  }
  const lines = finalText.split(/\r?\n/).map(s => s.trim()).filter(Boolean);
  return lines.length ? lines[lines.length - 1] : finalText;
}

function updateRealtimeLiveUi(data) {
  if (!data) return;
  // Native finals are per utterance. Only legacy/same-boundary updates may be cumulative.
  // Keep this separate from the age clock, which can advance before a final arrives.
  const finalDetectedAtMs = Number(data.finalDetectedAtMs || 0);
  const historyFinalDetectedAtMs = Number.isFinite(finalDetectedAtMs) && finalDetectedAtMs > 0 ? finalDetectedAtMs : 0;
  const wholeSegment = historyFinalDetectedAtMs > 0 && historyFinalDetectedAtMs !== lastRealtimeHistoryFinalDetectedAtMs;
  const rms = Number(data.captureRms ?? 0);
  const samples = Number(data.captureSamples ?? 0);
  if (data.vadState || data.vadSpeechCount != null) {
    setText("rt-live-vad", `${data.vadState || "-"}${data.vadSpeechCount != null ? ` (${data.vadSpeechCount})` : ""}`);
  }
  setText("rt-live-final-age", formatRealtimeLatencyMs(getRealtimeFinalAgeMs(data)));
  const displayEngine = getRealtimeDisplayEngine(data);
  const isActive = ["recording", "running", "processing"].includes(data.state);
  if (!isActive) setText("rt-active-engine", "-");
  else if ((data.final || data.selectedText || data.selected_text) && displayEngine)
    setText("rt-active-engine", engineDisplayName(displayEngine));
  if (data.options) {
    setText("opt-engine", engineDisplayName(data.options.engine));
    setText("opt-lang", data.options.language || "-");
    const requestedScene = data.options.scene || "-";
    const resolvedScene = data.resolvedScene || "";
    setText("opt-scene", resolvedScene && resolvedScene !== requestedScene ? `${requestedScene} → ${resolvedScene}` : requestedScene);
    setText("opt-prefilter", data.options.prefilterProfile || "-");
    setText("opt-prompt", data.options.initialPromptLength != null ? `${data.options.initialPromptLength}ch` : "-");
    // SenseVoice エンジン選択肢: モデルがある場合のみ表示
    ensureSenseVoiceEngineOption(data.options.senseVoiceAvailable === true);
  }
  if (displayEngine) {
    setText("rt-live-engine", engineDisplayName(displayEngine));
  }
  if (data.captureRms != null) setText("rt-live-rms", rms.toFixed(4));
  const partial = data.partial || "";
  setText("rt-partial", partial);
  // Volume meter + waveform update
  if (data.captureRms != null) {
    meterSetRms(Number(data.captureRms), data.vadState || "");
    waveUpdate(Number(data.captureRms));
    waveDraw();
  }
  const progress = document.getElementById("rt-progress-bar");
  if (progress) progress.style.width = `${Math.max(0, Math.min(100, samples / 128000))}%`;

  if (partial && partial !== lastRealtimePartial && partial !== data.final) {
    lastRealtimePartial = partial;
    renderRealtimeTabs(partial);
  }

  if (data.final && data.final !== lastRealtimeFinal) {
    const newFinal = getNewFinalText(data.final, wholeSegment);
    lastRealtimeFinal = data.final;
    lastRealtimeHistoryFinalDetectedAtMs = historyFinalDetectedAtMs;
    lastTranslatedFinalShown = null;
    // Use filtered text if available (skip NG/dropped text in history)
    let baseText = data.processedFinal ? (getNewFinalText(data.processedFinal, wholeSegment) || data.processedFinal) : (newFinal || data.final);
    lastHistoryOriginalText = baseText;
    let displayText = baseText;
    // 翻訳が既に同ポーリングで届いている場合
    if (data.translatedFinal && !data.textFilter?.dropped) {
      displayText = data.translatedFinal + " (" + baseText + ")";
      lastTranslatedFinalShown = data.translatedFinal;
    }
    if (displayText && !data.textFilter?.dropped && !data.loopbackSuspected) {
      addRealtimeFinalHistory(displayText, data);
      setRealtimeGuide("ui.web.guide.recognized");
    }
  } else if (data.translatedFinal && data.translatedFinal !== lastTranslatedFinalShown
             && lastHistoryOriginalText && data.final === lastRealtimeFinal) {
    // 翻訳が遅れて届いた場合: 履歴の先頭エントリと tabs を書き換える
    lastTranslatedFinalShown = data.translatedFinal;
    const updatedText = data.translatedFinal + " (" + lastHistoryOriginalText + ")";
    // 履歴 DOM の先頭エントリを更新
    const history = document.getElementById("rt-final-history");
    if (history) {
      const mainEl = history.querySelector("li:first-child .rt-history-main");
      if (mainEl) mainEl.textContent = "✓ " + updatedText;
    }
    // タブ配列も更新
    if (realtimeFinalTabs.length > 0) {
      realtimeFinalTabs[0] = updatedText;
      renderRealtimeTabs();
    }
  }
  // 翻訳先選択に表示する認識言語を更新
  if (data.options?.language) updateTranslationSourceDisplay(data.options.language);
}

function updateRealtimeResults(data) {
  if (!data) return;
  updateRealtimeLiveUi(data);
}

// Browser-local permission/device state: deliberately not migrated to server UI settings.
// This player never calls TTS, captures a microphone, or plays to a default sink.
const AsrAcceptanceCue = (() => {
  const key = "mobmate.asrCue.";
  let enabled = false, sink = "", volume = 20, confirmed = false, ready = false;
  let devices = [], channel = null, session = null, revision = 0, setupRevision = 0, playback = null, busy = false;
  let message = "setup";
  const el = name => document.getElementById("asr-cue-" + name);
  const explicit = id => typeof id === "string" && !!id && !["default", "communications"].includes(id);
  const usable = d => d?.kind === "audiooutput" && explicit(d.deviceId) && !!d.label &&
    !/virtual|cable|voicemeeter|vb-audio|blackhole|soundflower|monitor|loopback|仮想/i.test(d.label);
  const supported = () => typeof Audio !== "undefined" && typeof Audio.prototype.setSinkId === "function";
  const render = () => {
    const state = !enabled ? (ready ? "offReady" : message === "unavailable" ? "offUnavailable" : "off")
      : message === "otherTab" && session?.locked ? (ready ? "ready" : "setup") : message;
    if (el("enabled")) el("enabled").checked = enabled;
    if (el("status")) el("status").textContent = t("ui.web.asrCue." + state);
    if (el("output")?.options?.[0]) el("output").options[0].textContent = t("ui.web.asrCue.choose");
    if (el("preview")) el("preview").disabled = !supported() || !sink || !confirmed || busy;
  };
  const cancelPlayback = () => {
    if (playback) { playback.pause(); playback.removeAttribute?.("src"); playback.load?.(); playback.cancel?.(); playback = null; }
    busy = false;
  };
  const end = () => {
    revision++;
    cancelPlayback();
    session?.release?.();
    session = null;
  };
  const invalidate = (status = "setup") => {
    setupRevision++;
    ready = false;
    cancelPlayback();
    message = status;
    render();
  };
  const refresh = async () => {
    const list = await navigator.mediaDevices.enumerateDevices(); // Existing permission only.
    devices = list.filter(usable);
    const select = el("output");
    if (select) {
      select.replaceChildren(new Option(t("ui.web.asrCue.choose"), ""));
      for (const d of devices) select.appendChild(new Option(d.label, d.deviceId));
      select.value = devices.some(d => d.deviceId === sink) ? sink : "";
    }
    return devices.some(d => d.deviceId === sink);
  };
  const selectSink = id => {
    invalidate();
    confirmed = false;
    if (el("confirmed")) el("confirmed").checked = false;
    sink = devices.some(d => d.deviceId === id) ? id : "";
    if (el("output")) el("output").value = sink;
    localStorage.setItem(key + "sink", sink);
    render();
  };
  const choose = async () => {
    invalidate();
    const attempt = setupRevision;
    try {
      if (!supported()) throw Error("unsupported");
      // Invoke directly in the click gesture; never request new microphone permission.
      const selected = navigator.mediaDevices?.selectAudioOutput
        ? await navigator.mediaDevices.selectAudioOutput({ deviceId: sink }) : null;
      if (attempt !== setupRevision) return;
      await refresh();
      if (attempt !== setupRevision) return;
      selectSink(selected?.deviceId || sink);
      message = selected && !sink ? "unavailable" : devices.length ? "setup" : "unavailable";
    } catch { if (attempt === setupRevision) message = "unavailable"; }
    render();
  };
  const play = async (preview, valid = () => true) => {
    if (busy || !supported() || !confirmed || !explicit(sink) || (!preview && !ready)) return false;
    const selected = sink;
    const audio = new Audio("audio/asr-accepted-a.wav");
    playback = audio;
    busy = true;
    render();
    const current = () => playback === audio && sink === selected && confirmed && valid();
    let timer;
    try {
      const done = new Promise(resolve => {
        timer = setTimeout(() => resolve(false), 1500);
        audio.addEventListener("ended", () => resolve(true), { once: true });
        audio.addEventListener("error", () => resolve(false), { once: true });
        audio.cancel = () => resolve(false);
      });
      const work = async () => {
        if (!await refresh()) throw Error("output unavailable");
        if (!current()) return false;
        await audio.setSinkId(selected);
        if (!current()) return false;
        if (audio.sinkId !== selected) throw Error("output not applied");
        audio.volume = volume / 100;
        await audio.play();
        return done;
      };
      if (!await Promise.race([work(), done])) throw Error("playback not completed");
      if (!current()) return false;
      if (preview) { ready = true; message = "ready"; }
      return true;
    } catch {
      if (current()) { ready = false; message = "unavailable"; }
      return false;
    } finally {
      clearTimeout(timer);
      audio.pause();
      audio.removeAttribute?.("src");
      audio.load?.();
      audio.cancel?.();
      if (playback === audio) { playback = null; busy = false; render(); }
    }
  };
  const accept = (data, token, stopping = false) => {
    const owner = session;
    const ts = Number(data?.finalDetectedAtMs);
    const final = data?.final?.trim();
    // No stable backend utterance/session ID exists. Skip ambiguous/old boundaries.
    if (!owner?.locked || owner.token !== token || (!stopping && token !== realtimePollToken) || owner.stopping !== stopping ||
        !final ||
        !Number.isSafeInteger(ts) || ts <= owner.started || ts <= owner.last || ts > Date.now()) return Promise.resolve();
    owner.last = ts; // Consume even when OFF/not ready/busy: never replay on enable or a later poll.
    const duplicate = final === owner.lastFinal;
    owner.lastFinal = final; // Native may retain a failed segment's previous final with a newer VAD timestamp.
    if (duplicate) return Promise.resolve();
    if (data.discarded || data.error || data.textFilter?.dropped || data.loopbackSuspected ||
        (data.processedFinal != null && !data.processedFinal.trim())) return Promise.resolve();
    if (!enabled || !ready) return Promise.resolve();
    return play(false, () => session === owner && owner.token === token && owner.last === ts && owner.stopping === stopping);
  };
  return {
    translate: render,
    init() {
      if (!el("enabled")) return;
      enabled = localStorage.getItem(key + "enabled") === "true";
      sink = localStorage.getItem(key + "sink") || "";
      const savedVolume = Number(localStorage.getItem(key + "volume") ?? 20);
      volume = Number.isFinite(savedVolume) ? Math.max(0, Math.min(100, savedVolume)) : 20;
      el("volume").value = String(volume);
      message = supported() ? "setup" : "unsupported";
      try {
        channel = new BroadcastChannel("mobmate-asr-cue-session");
        channel.onmessage = () => { end(); message = "otherTab"; render(); };
      } catch { channel = null; }
      el("enabled").addEventListener("change", e => {
        enabled = e.target.checked;
        localStorage.setItem(key + "enabled", String(enabled));
        if (!enabled) cancelPlayback();
        render();
      });
      el("choose").addEventListener("click", choose);
      el("output").addEventListener("change", e => selectSink(e.target.value));
      el("confirmed").addEventListener("change", e => { invalidate(); confirmed = e.target.checked; render(); });
      el("volume").addEventListener("change", e => {
        volume = Math.max(0, Math.min(100, Number(e.target.value) || 0));
        localStorage.setItem(key + "volume", String(volume));
        cancelPlayback();
        render();
      });
      el("preview").addEventListener("click", () => { void play(true); });
      navigator.mediaDevices?.addEventListener("devicechange", () => { invalidate("unavailable"); void refresh().catch(() => {}); });
      window.addEventListener("storage", e => {
        if (e.key !== null && !e.key.startsWith(key)) return;
        enabled = localStorage.getItem(key + "enabled") === "true";
        confirmed = false;
        el("confirmed").checked = false;
        invalidate(); // Another tab changing sound preferences must not leave this owner playing.
      });
      window.addEventListener("pagehide", () => { end(); channel?.close(); channel = null; });
      render();
    },
    change(source, action) {
      const normalStop = source === "main" && action === "stop" && session?.locked && !session.stopping;
      if (normalStop) { revision++; cancelPlayback(); session.stopping = true; }
      else end();
      try { channel?.postMessage("changed"); } catch { channel = null; }
      const ticket = revision;
      if (source === "main" && action === "start") {
        session = { token: realtimePollToken, started: Date.now(), last: 0, lastFinal: "", locked: false, stopping: false };
      }
      return { ticket, source, action, normalStop };
    },
    completed(op, data) {
      if (op.ticket !== revision) return;
      if (op.source === "main" && op.action === "start" && session) {
        if (!channel || !navigator.locks?.request) { end(); message = "unsupported"; render(); return; }
        const owner = session;
        void Promise.resolve().then(() => navigator.locks.request("mobmate-asr-cue-owner", { ifAvailable: true }, async lock => {
          if (op.ticket !== revision) return;
          if (!lock) { end(); message = "otherTab"; render(); return; }
          owner.locked = true;
          render();
          await new Promise(resolve => { owner.release = resolve; });
        })).catch(() => { if (session === owner) { end(); message = "unsupported"; render(); } });
      } else if (op.normalStop && session) {
        void accept(data, session.token, true).finally(() => { if (op.ticket === revision) end(); });
      }
    },
    failed(op) { if (op.ticket === revision) end(); },
    accept,
  };
})();

const RealtimeSessionClient = (() => {
  let state = "idle", owner = null, operationId = 0, serial = Promise.resolve();
  let requestedAction = null;
  let reconnectRequired = false;
  const setReconnectRequired = value => {
    reconnectRequired = value;
    const warning = document.getElementById("addon-asr-reconnect-warning");
    if (warning) warning.hidden = !value;
    if (typeof AddonLanguageUi !== "undefined") AddonLanguageUi.translate();
  };
  const run = (source, action, work) => {
    const op = ++operationId;
    const cueOperation = AsrAcceptanceCue.change(source, action);
    // Stop intent takes effect at acceptance, even behind a pending Start.
    // Its earlier response must not grant language-follow a new resume right.
    const previousAction = requestedAction;
    requestedAction = action;
    const pending = serial.catch(() => {}).then(async () => {
      console.info(`[Realtime] op=${op} source=${source} action=${action}`);
      try {
        const result = await work(op, previousAction);
        AsrAcceptanceCue.completed(cueOperation, result);
        return result;
      } catch (error) { AsrAcceptanceCue.failed(cueOperation); throw error; }
    });
    serial = pending.catch(() => {});
    return pending;
  };
  const json = async (url, init) => {
    const response = await fetch(url, init);
    const body = await response.json().catch(() => ({}));
    if (!response.ok || body?.ok === false) {
      const error = new Error(apiMessage(body, response.status));
      error.realtimeRequestRejected = true;
      throw error;
    }
    return body;
  };
  return {
    listAudioInputDevices: () => json("/api/audio/input-devices"),
    getSelectedCaptureDeviceId: () => localStorage.getItem("mobmate.captureDeviceId") || "",
    setSelectedCaptureDeviceId: (deviceId, sourceSelect = null) => {
      const value = deviceId || "";
      if (sourceSelect && value !== (localStorage.getItem("mobmate.captureDeviceId") || "") && typeof OnsetFillerUi !== "undefined") void OnsetFillerUi.inputChanged();
      localStorage.setItem("mobmate.captureDeviceId", value);
      const mainSelect = document.getElementById("rt-device-select");
      if (mainSelect) {
        if (sourceSelect) captureInputSelectionChanged();
        const option = Array.from(mainSelect.options).find((item) => item.value === value);
        if (value && sourceSelect?.selectedOptions[0]) copyCaptureInputOption(mainSelect, sourceSelect.selectedOptions[0]);
        else if (value && !option) appendUnavailableCaptureInput(mainSelect, value, true);
        mainSelect.value = value;
      }
    },
    buildRealtimeStartRequest: (captureDeviceId) => ({ captureDeviceId, ...getRealtimeSettings(), ttsEnabled: false }),
    startRealtimeSession: (source, options) => {
      return run(source, "start", async (op, previousAction) => {
        const previousOwner = owner, previousState = state;
        owner = source;
        state = "starting";
        try {
          // The queue may have outlived a preference or TTS selection change.
          // Choose filler permission at dispatch, not when Start was queued.
          const fillerStart = source === "main" && typeof OnsetFillerUi !== "undefined" ? OnsetFillerUi.sessionStarting() : null;
          const body = await json("/api/realtime/start", { method: "POST", headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ ...options, ...(fillerStart ? { fillerEnabled: fillerStart.enabled } : {}), clientSource: source, clientOperationId: op }) });
          state = "running";
          setReconnectRequired(false);
          if (fillerStart) OnsetFillerUi.sessionStarted(fillerStart);
          return body;
        }
        catch (error) {
          if (error?.realtimeRequestRejected) {
            owner = previousOwner; state = previousState;
            // A later accepted Stop remains authoritative, even when queued.
            if (operationId === op) requestedAction = previousAction;
          } else state = "failed";
          throw error;
        }
      });
    },
    stopRealtimeSession: (source) => {
      if (source !== "language-switch" && typeof AddonLanguageUi !== "undefined") AddonLanguageUi.cancelFollow();
      if (typeof OnsetFillerUi !== "undefined") OnsetFillerUi.sessionStopping();
      return run(source, "stop", async () => {
      state = "stopping";
      try {
        const body = await json(`/api/realtime/stop${source === "language-switch" || source === "capture-loss" ? "?discard=true" : ""}`, { method: "POST" });
        // Linux confirms with ok=true; Windows returns its final poll state.
        // An empty or malformed success response does not confirm Stop.
        if (!body || typeof body !== "object" || Array.isArray(body) ||
            !(body.ok === true || ["idle", "recording", "processing", "completed", "failed", "stopped"].includes(body.state)))
          throw new Error(t("ui.web.guide.stopUnconfirmed"));
        setReconnectRequired(false);
        return body;
      }
      finally { state = "idle"; }
      });
    },
    isMainActive: () => requestedAction === "start" && owner === "main" && (state === "running" || state === "starting"),
    isMainRunning: () => requestedAction === "start" && owner === "main" && state === "running",
    requiresReconnect: () => reconnectRequired,
    restartRealtimeSession: async (source, options) => { await RealtimeSessionClient.stopRealtimeSession(source); return RealtimeSessionClient.startRealtimeSession(source, options); },
    getStatus: () => json("/api/realtime/status"),
    getStartupStatus: async () => {
      // Any accepted Start/Stop takes precedence over startup observation,
      // including an operation still waiting in this client's queue.
      if (operationId !== 0) return null;
      try {
        const body = await json("/api/realtime/status");
        if (operationId !== 0) return null;
        // Neither host exposes a session/lease identity in status. Display an
        // existing recording, but never claim it as this client's main session.
        setReconnectRequired(!!body.error || !["idle", "none", "stopped"].includes(body.state));
        return body;
      } catch (error) {
        if (operationId !== 0) return null;
        setReconnectRequired(true);
        throw error;
      }
    },
  };
})();

const OnsetFillerUi = (() => {
  const toggle = document.getElementById("rt-filler-enabled");
  const status = document.getElementById("rt-filler-status");
  let serial = Promise.resolve(), revision = 0, preferenceRevision = 0, lastRefresh = 0, lastSettingsRetry = 0;
  let ttsRecovery = null, lastConfirmedTtsRevision = -1, voiceRevision = 0, voiceConfirmed = false, waitingForTts = false;
  let pendingRetirements = 0, retirementUnconfirmed = false;
  let ownedMobfy = false;
  const mobfyMode = () => typeof isMobfyMode === "function" && isMobfyMode();
  const mainActive = () => mobfyMode() ? ownedMobfy : RealtimeSessionClient.isMainActive();
  const mainRunning = () => mobfyMode()
    ? ownedMobfy && typeof vcRunning !== "undefined" && vcRunning
    : RealtimeSessionClient.isMainRunning();
  const preferenceKnown = () => uiSettings.isKnown("mobmate.fillerEnabled");
  const canPrepare = () => uiSettings.isReady() && voiceConfirmed && !isSelectedTtsVoiceUnavailable() &&
    ttsOutputConfirmed === currentTtsOutputDevice();
  const canStart = () => canPrepare() &&
    (!ttsRecovery || ttsRecovery.confirmed);
  const show = key => {
    if (status) status.textContent = preferenceKnown()
      ? `${toggle?.checked ? "ON" : "OFF"} · ${t(`ui.web.onsetFiller.${key}`)}`
      : t("ui.web.onsetFiller.settingsUnavailable");
  };
  const stateKeys = {
    "start-required": "startRequired", "input-required": "inputRequired", "input-changed": "inputChanged",
    "output-required": "outputRequired",
    "approval-pending": "approvalPending", "translation-on": "translationOn", "tts-disabled": "ttsDisabled",
    "permission-blocked": "permissionBlocked", "route-changing": "changing", "waiting-onset": "waitingOnset", "onset-received": "waitingStart",
    "onset-unavailable": "onsetUnavailable", "selection-rejected": "selectionRejected", "reservation-rejected": "reservationRejected",
    "waiting-start": "waitingStart", "started": "started", "body-priority": "bodyPriority", "playback-failed": "playbackFailed",
    "native-unavailable": "nativeUnavailable", "cancel-unconfirmed": "cancelUnconfirmed"
  };
  const render = body => {
    if (!status) return;
    if (!preferenceKnown()) { status.textContent = t("ui.web.onsetFiller.settingsUnavailable"); return; }
    const preference = toggle?.checked ? "ON" : "OFF";
    const prepared = body?.prepared > 0
      ? t("ui.web.onsetFiller.ready").replace("{count}", String(body.prepared))
      : body?.reason === "preparing" ? t("ui.web.onsetFiller.changing")
      : toggle?.checked ? t("ui.web.onsetFiller.unprepared") : "";
    const state = toggle?.checked && (stateKeys[body?.reason] ||
      (body?.enabled ? stateKeys[body?.playbackReason] : !mainActive() ? "startRequired" : stateKeys[body?.playbackReason]));
    status.textContent = [preference, prepared, state ? t(`ui.web.onsetFiller.${state}`) : ""].filter(Boolean).join(" · ");
  };
  const request = async init => {
    const response = await fetch("/api/realtime/filler", init);
    if (!response.ok) throw new Error("filler control unavailable");
    const body = await response.json();
    if (!body || typeof body.enabled !== "boolean" || !Number.isInteger(body.prepared)) throw new Error("invalid filler state");
    return body;
  };
  const prepare = () => {
    const language = getRealtimeSettings().language, ticket = ++revision;
    const forMobfy = mobfyMode();
    if (!canPrepare()) { show("unprepared"); return serial; }
    show("changing");
    serial = serial.catch(() => {}).then(async () => {
      if (ticket !== revision || !canPrepare()) return;
      try {
        // Reuse saved selections without adding fields to the filler contract.
        // Serialize earlier PUTs before preparing, then recheck the winning ticket.
        await uiSettings.flush();
        if (ticket !== revision || forMobfy !== mobfyMode() || !canPrepare()) return;
        const body = await request({ method: "POST", headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ language }) });
        if (ticket === revision) render(body);
      } catch { if (ticket === revision) show("unavailable"); }
    });
    return serial;
  };
  const applyPreference = (enabled, retireAfterQueued = false) => {
    if (enabled && (pendingRetirements || retirementUnconfirmed)) {
      // Do not supersede the OFF that must retire an older preparation.
      if (retirementUnconfirmed && !pendingRetirements && mainActive())
        applyPreference(false, true);
      waitingForTts = true;
      show(retirementUnconfirmed && !pendingRetirements ? "unavailable" : "changing");
      return;
    }
    // A newer explicit OFF must also retire preparation still queued before it.
    if (!enabled && (pendingRetirements || retirementUnconfirmed)) retireAfterQueued = true;
    const ticket = ++revision, choice = ++preferenceRevision;
    if (!enabled) waitingForTts = false;
    if (!mainActive() && enabled) { void prepare(); return; }
    if (enabled && !uiSettings.isReady()) { waitingForTts = true; show("settingsUnavailable"); return; }
    if (enabled && !canStart()) { waitingForTts = true; show("changing"); return; }
    if (enabled && !mainRunning()) { waitingForTts = true; show("changing"); return; }
    show("changing");
    const send = async () => {
      try {
        const body = await request({ method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled, language: getRealtimeSettings().language }) });
        if (ticket === revision) render(body);
        return enabled ? body.enabled : !body.enabled && body.prepared === 0 && body.reason !== "cancel-unconfirmed";
      } catch {
        if (ticket === revision) show("unavailable");
        return false;
      }
    };
    if (!enabled) {
      // Stop immediately, even if a prior ON or preparation request is pending.
      // A second OFF after older work drains retires a late ON or preparation.
      const previous = serial, immediate = send();
      if (retireAfterQueued) pendingRetirements++;
      serial = Promise.allSettled([previous, immediate]).then(async () => {
        if (choice !== preferenceRevision || (toggle.checked && !retireAfterQueued)) return;
        const confirmed = await send();
        if (retireAfterQueued && choice === preferenceRevision) retirementUnconfirmed = !confirmed;
      }).finally(() => {
        if (!retireAfterQueued || --pendingRetirements !== 0) return;
        // Let preparation already queued for the confirmed configuration run
        // before a resumed ON advances its revision.
        void serial.catch(() => {}).then(() => {
          if (pendingRetirements || !waitingForTts || !toggle?.checked) return;
          if (retirementUnconfirmed) { show("unavailable"); return; }
          if (mainActive()) resumeWaitingPreference();
          else { waitingForTts = false; void prepare(); }
        });
      });
    } else {
      serial = serial.catch(() => {}).then(async () => {
        const recovery = ttsRecovery;
        if (recovery && !(await recovery.promise)) {
          if (choice === preferenceRevision) show("unavailable");
          return;
        }
        if (recovery === ttsRecovery && choice === preferenceRevision && toggle.checked && mainRunning()) {
          if (canStart()) return send();
          waitingForTts = true;
        }
      });
    }
  };
  toggle?.addEventListener("change", () => {
    const enabled = toggle.checked;
    localStorage.setItem("mobmate.fillerEnabled", String(enabled));
    applyPreference(enabled);
  });
  document.addEventListener("mobmate-ui-settings-ready", () => {
    if (!toggle) return;
    const saved = localStorage.getItem("mobmate.fillerEnabled");
    toggle.checked = saved === null || saved === "true";
    // A restored preference is not proof that the restored voice and output
    // have reached the TTS host. Keep ON silent until that configuration wins.
    const recovery = { pending: true, confirmed: false, promise: null };
    ttsRecovery = recovery;
    recovery.promise = Promise.resolve().then(() => restoreTtsSettingsAfterRead()).then(confirmed => {
      recovery.pending = false;
      recovery.confirmed = confirmed === true ||
        (lastConfirmedTtsRevision === ttsOutputRevision && ttsOutputConfirmed === currentTtsOutputDevice());
      if (ttsRecovery === recovery) {
        if (recovery.confirmed && toggle.checked && mainActive()) applyPreference(true);
        else if (!recovery.confirmed) show("unavailable");
      }
      return recovery.confirmed;
    }).catch(() => {
      recovery.pending = false;
      if (ttsRecovery === recovery) show("unavailable");
      return false;
    });
    // OFF takes effect immediately. Never control another client's session.
    if (!mainActive()) show("startRequired");
    else if (!toggle.checked) applyPreference(false);
    else { waitingForTts = true; show("changing"); }
  });
  const resumeWaitingPreference = () => {
    if (!waitingForTts || pendingRetirements || retirementUnconfirmed || !canStart() || !mainRunning()) return;
    waitingForTts = false;
    if (toggle?.checked) applyPreference(true);
  };
  const sessionStarted = start => {
    if (!mainRunning()) return;
    if (!toggle?.checked) {
      if (start.enabled) applyPreference(false);
    } else if (start.enabled && start.choice === preferenceRevision && canStart()) {
      waitingForTts = false;
    }
    else {
      waitingForTts = true;
      resumeWaitingPreference();
    }
  };
  const sessionStarting = () => {
    if (retirementUnconfirmed && !pendingRetirements) applyPreference(false, true);
    const enabled = toggle?.checked === true && !pendingRetirements && !retirementUnconfirmed && canStart() && mainActive();
    waitingForTts = toggle?.checked === true && !enabled;
    return { enabled, choice: preferenceRevision };
  };
  const sessionStopping = () => {
    // An older prepare-only request can finish after Stop and a later Start.
    if (mainActive()) applyPreference(false, true);
  };
  const ttsChanging = () => {
    // A failed configuration leaves the host's old voice/output intact. Retire
    // its permission now, including a Start still creating the native session.
    // This changes active control only, never the user's saved preference.
    if (mainActive()) applyPreference(false, true);
    waitingForTts = toggle?.checked === true;
  };
  const ttsConfigConfirmed = (revision, voiceTicket) => {
    if (voiceTicket !== voiceRevision) return;
    voiceConfirmed = true;
    if (revision === ttsOutputRevision && ttsOutputConfirmed === currentTtsOutputDevice()) lastConfirmedTtsRevision = revision;
    if (ttsRecovery?.pending) return;
    if (ttsRecovery && !ttsRecovery.confirmed) ttsRecovery = null;
    resumeWaitingPreference();
  };
  const ttsOutputConfigConfirmed = revision => {
    if (voiceConfirmed) lastConfirmedTtsRevision = revision;
    resumeWaitingPreference();
  };
  const inputChanged = async () => {
    if (!mainActive()) return;
    const ticket = ++revision;
    try {
      const body = await request({ method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ inputChanged: true }) });
      if (ticket === revision) render(body);
    } catch { if (ticket === revision) show("unavailable"); }
  };
  const refresh = async () => {
    if (!toggle || Date.now() - lastRefresh < 1000) return;
    lastRefresh = Date.now();
    if (!uiSettings.isReady() && Date.now() - lastSettingsRetry >= 5000) {
      lastSettingsRetry = Date.now();
      try { await uiSettings.retryLoad(); } catch { /* keep the saved preference unknown */ }
    }
    const ticket = revision;
    try { const body = await request(); if (ticket === revision) render(body); }
    catch { if (ticket === revision) show("unavailable"); }
  };
  const mobfyStarting = async () => {
    try {
      await serial.catch(() => {});
      await uiSettings.flush();
      // No synthesis/conversion is awaited here. Unready starts remain silent.
      const start = { enabled: toggle?.checked === true && !pendingRetirements && !retirementUnconfirmed && canStart(), choice: preferenceRevision };
      await request({ method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled: start.enabled, language: getRealtimeSettings().language }) });
      return start;
    } catch {
      show("unavailable");
      return { enabled: false, choice: preferenceRevision };
    }
  };
  const mobfyStarted = start => {
    ownedMobfy = true;
    sessionStarted(start);
  };
  const mobfyStopped = () => { ownedMobfy = false; };
  return { prepare, inputChanged, refresh, ttsConfigConfirmed, ttsOutputConfigConfirmed, canStart, sessionStarting, sessionStarted, sessionStopping, ttsChanging,
    mobfyStarting, mobfyStarted, mobfyStopped,
    isVoiceConfirmed: () => voiceConfirmed,
    ttsConfigChanging: () => { voiceConfirmed = false; return ++voiceRevision; } };
})();

let audioInputRefreshSeq = 0;
let audioInputRefreshPending = false;

function captureInputSelectionChanged() {
  if (!audioInputRefreshPending) return;
  audioInputRefreshPending = false;
  audioInputRefreshSeq += 1;
  const status = document.getElementById("rt-device-status");
  if (status) { status.textContent = ""; status.className = "status"; }
}

function appendUnavailableCaptureInput(select, id, unverified) {
  const option = document.createElement("option");
  option.value = id;
  option.dataset.captureLabel = id;
  option.dataset.captureUnavailable = unverified ? "unverified" : "missing";
  option.textContent = t(unverified ? "ui.web.device.inputUnverified" : "ui.web.device.inputMissing", { id });
  select.appendChild(option);
}

function copyCaptureInputOption(select, source) {
  if (!source?.value) return;
  let option = Array.from(select.options).find(item => item.value === source.value);
  if (!option) { option = document.createElement("option"); option.value = source.value; select.appendChild(option); }
  option.textContent = source.textContent;
  option.dataset.deviceName = source.dataset.deviceName || "";
  if (source.dataset.captureUnavailable) {
    option.dataset.captureUnavailable = source.dataset.captureUnavailable;
    option.dataset.captureLabel = source.dataset.captureLabel || source.value;
  } else {
    delete option.dataset.captureUnavailable;
    delete option.dataset.captureLabel;
  }
}

function markCaptureInputsUnverified(select) {
  for (const option of Array.from(select.options)) {
    if (!option.value) continue;
    option.dataset.captureLabel ||= option.textContent || option.value;
    option.dataset.captureUnavailable = "unverified";
    option.textContent = t("ui.web.device.inputUnverified", { id: option.dataset.captureLabel });
  }
}

function renderCaptureInputs(select, devices, selected, unverified = false) {
  select.innerHTML = "";
  const defaultOption = document.createElement("option");
  defaultOption.value = "";
  defaultOption.textContent = t("ui.web.device.defaultMicrophone");
  select.appendChild(defaultOption);
  for (const device of devices) {
    const option = document.createElement("option");
    option.value = device.id ?? device.Id ?? "";
    option.dataset.deviceName = device.name ?? device.Name ?? "";
    option.textContent = `${device.name || device.Name || option.value || t("ui.web.device.unnamed")}${device.isDefault ? ` (${t("ui.web.device.default")})` : ""}`;
    select.appendChild(option);
  }
  if (selected && !Array.from(select.options).some(option => option.value === selected)) appendUnavailableCaptureInput(select, selected, unverified);
  select.value = selected;
}

function captureInputUnavailable(select) {
  if (!select?.value) return false;
  const option = Array.from(select.options).find(item => item.value === select.value);
  return !option || !!option.dataset.captureUnavailable;
}

function validCaptureInputDevices(data) {
  return Array.isArray(data?.devices) && data.devices.every(device => device && typeof device === "object" && !Array.isArray(device)
    && [device.id, device.Id, device.name, device.Name].every(value => value == null || typeof value === "string"));
}

function rejectUnavailableCaptureInput(select, status) {
  if (!captureInputUnavailable(select)) return false;
  if (status) {
    status.textContent = t("ui.web.device.inputCannotStart");
    status.className = "status err";
  }
  return true;
}

async function fetchAudioInputDevices() {
  const select = document.getElementById("rt-device-select");
  const status = document.getElementById("rt-device-status");
  const seq = ++audioInputRefreshSeq;
  audioInputRefreshPending = true;
  if (!select.options.length) renderCaptureInputs(select, [], RealtimeSessionClient.getSelectedCaptureDeviceId(), true);
  else markCaptureInputsUnverified(select);
  try {
    status.textContent = t("ui.web.status.loadingDevices");
    const data = await RealtimeSessionClient.listAudioInputDevices();
    if (seq !== audioInputRefreshSeq) return;
    audioInputRefreshPending = false;
    if (!validCaptureInputDevices(data)) throw new Error(t("ui.web.device.inputEnumerationFailed"));
    // The current select may be newer than storage: main saves only on Start.
    renderCaptureInputs(select, data.devices, select.value);
    status.textContent = t("ui.web.device.count", { count: data.devices.length });
    status.className = "status ok";
  } catch (e) {
    if (seq !== audioInputRefreshSeq) return;
    audioInputRefreshPending = false;
    status.textContent = t("ui.web.status.error", { message: e.message });
    status.className = "status err";
  }
}

async function fetchRealtimeStatus() {
  const token = realtimePollToken;
  const el = document.getElementById("rt-status");
  try {
    const data = await RealtimeSessionClient.getStatus();
    if (token !== realtimePollToken) return;
    el.textContent = `${data.state}  samples=${data.captureSamples ?? 0}  rms=${(data.captureRms ?? 0).toFixed(4)}`;
    updateRealtimeResults(data);
    void AsrAcceptanceCue.accept(data, token);
    if (data.error) {
      el.textContent += `  [${data.error}]`;
      el.className = "status err";
    } else {
      el.className = "status ok";
    }
  } catch {
    if (token === realtimePollToken) el.textContent = t("ui.web.status.noSession");
  }
}

async function startRealtime(fromLanguageFollow = false) {
  if (fromLanguageFollow !== true && typeof AddonLanguageUi !== "undefined") AddonLanguageUi.cancelFollow();
  if (setupMicBlocksOtherCapture()) return;
  const el = document.getElementById("rt-status");
  const startBtn = document.getElementById("rt-start-btn");
  const stopBtn = document.getElementById("rt-stop-btn");
  const captureSelect = document.getElementById("rt-device-select");
  if (rejectUnavailableCaptureInput(captureSelect, el)) return;
  try {
    await stopMobfyDirectForModeSwitch();
    if (setupMicBlocksOtherCapture()) return;
    if (rejectUnavailableCaptureInput(captureSelect, el)) return;
    const pollToken = ++realtimePollToken;
    setRecordingButtons(true);
    resetRealtimeLiveUi();
    el.textContent = t("ui.web.status.starting");
    const captureDeviceId = captureSelect?.value || "";
    const settings = getRealtimeSettings();
    RealtimeSessionClient.setSelectedCaptureDeviceId(captureDeviceId);
    saveRealtimeSettings(settings);
    await RealtimeSessionClient.startRealtimeSession("main", { captureDeviceId, ...settings });
    if (pollToken !== realtimePollToken) return;
    setRealtimeWasRecording(true);
    // ノイズフロア計測 warmup UI (8秒カウントダウン)
    startNoiseWarmupUi(8, "ui.web.guide.noiseWarmup", { allowStop: true });

    meterStart();
    spkStart();
    pollRealtime(pollToken);
  } catch (e) {
    el.textContent = t("ui.web.status.error", { message: e.message });
    el.className = "status err";
    setRecordingButtons(false);
    setRealtimeWasRecording(false);
  }
}

async function stopRealtime() {
  if (setupMicBlocksOtherCapture()) return;
  const el = document.getElementById("rt-status");
  const startBtn = document.getElementById("rt-start-btn");
  const stopBtn = document.getElementById("rt-stop-btn");
  try {
    realtimePollToken++;
    clearNoiseWarmupUi();
    if (stopBtn) stopBtn.disabled = true;
    setText("rt-live-state", t("ui.web.status.stoppingState"));
    el.textContent = t("ui.web.status.stopping");
    setRealtimeGuide("ui.web.status.stopping");
    const data = await RealtimeSessionClient.stopRealtimeSession("main");
    const latency = formatRealtimeLatencyMs(data?.latencyMs);
    el.textContent = data?.error ? t("ui.web.status.error", { message: data.error })
      : t(latency === "-" ? "ui.web.status.stopped" : "ui.web.status.stoppedLatency", { latency });
    setRealtimeGuide("ui.web.guide.stopped");
    el.className = data?.error ? "status err" : "status ok";
    updateRealtimeResults(data);
    // A resolved Stop ended capture, even when its final recognition reports an error.
    setText("rt-live-vad", "-");
    setText("rt-live-rms", "0.0000");
    setRecordingButtons(false);
    setRealtimeWasRecording(false);
    meterStop();
    spkStop();
    waveClear();
    checkReviewPrompt();
  } catch (e) {
    el.textContent = t("ui.web.status.error", { message: e.message });
    el.className = "status err";
    if (stopBtn) stopBtn.disabled = false;
    setRealtimeGuide("ui.web.guide.stopUnconfirmed");
    setRealtimeWasRecording(false);
    meterStop(); spkStop(); waveClear();
  }
}

// The drawer and setup wizard are exclusive modals; audio jobs keep running.
let activeUiModal = null;

function visibleUiControl(el) {
  return !!el?.isConnected && !el.disabled && !el.closest("[inert]") && el.getClientRects().length > 0;
}

function recoverUiModalFocus() {
  if (!activeUiModal || document.querySelector("dialog[open]")) return;
  const focused = document.activeElement;
  if (!activeUiModal.root.contains(focused) || !visibleUiControl(focused)) activeUiModal.title.focus();
}

function closeUiModal(rootId, restoreFocus = true) {
  if (activeUiModal?.root.id !== rootId) return;
  const modal = activeUiModal;
  activeUiModal = null;
  modal.observer.disconnect();
  modal.root.classList.remove("open");
  modal.root.setAttribute("aria-hidden", "true");
  for (const [el, inert] of modal.background) el.inert = inert;
  if (restoreFocus && visibleUiControl(modal.returnFocus)) modal.returnFocus.focus();
}

function openUiModal(rootId, titleId, onEscape, returnFocus) {
  const root = document.getElementById(rootId), title = document.getElementById(titleId);
  if (!root || !title) return;
  if (activeUiModal?.root === root) return;
  if (activeUiModal) closeUiModal(activeUiModal.root.id, false);
  const background = Array.from(document.body.children)
    .filter(el => el !== root && !["SCRIPT", "STYLE", "LINK"].includes(el.tagName))
    .map(el => [el, el.inert]);
  for (const [el] of background) el.inert = true;
  root.classList.add("open");
  root.setAttribute("aria-hidden", "false");
  const observer = new MutationObserver(recoverUiModalFocus);
  activeUiModal = { root, title, background, observer, onEscape, returnFocus };
  observer.observe(root, { subtree: true, childList: true, attributes: true, attributeFilter: ["disabled", "hidden", "class", "style"] });
  title.focus();
}

function handleUiModalKey(event) {
  if (!activeUiModal || document.querySelector("dialog[open]")) return;
  if (event.key === "Escape") {
    event.preventDefault();
    event.stopPropagation();
    activeUiModal.onEscape();
  } else if (event.key === "Tab") {
    const controls = Array.from(activeUiModal.root.querySelectorAll("button, [href], input, select, textarea, [tabindex]"))
      .filter(el => el.tabIndex >= 0 && visibleUiControl(el));
    const index = controls.indexOf(document.activeElement);
    if (!controls.length || index < 0 || (!event.shiftKey && index === controls.length - 1) || (event.shiftKey && index === 0)) {
      event.preventDefault();
      (controls.length ? controls[event.shiftKey ? controls.length - 1 : 0] : activeUiModal.title).focus();
    }
  }
}

function confirmSpeakerReset() {
  const dialog = document.getElementById("speaker-reset-dialog");
  if (!dialog || dialog.open) return Promise.resolve(false);
  return new Promise(resolve => {
    dialog.returnValue = "cancel";
    dialog.addEventListener("close", () => {
      document.getElementById("speaker-reset-btn")?.focus();
      resolve(dialog.returnValue === "reset");
    }, { once: true });
    dialog.showModal();
    document.getElementById("speaker-reset-cancel")?.focus();
  });
}

function initAppChrome() {
  document.addEventListener("keydown", handleUiModalKey);
  document.addEventListener("focusin", recoverUiModalFocus);
  document.querySelectorAll(".acc-head").forEach(btn => {
    const section = btn.closest(".accordion");
    const key = section?.dataset.accordion ? `mobmate.accordion.${section.dataset.accordion}` : "";
    if (section && key) section.classList.toggle("open", localStorage.getItem(key) !== "closed");
    btn.addEventListener("click", () => {
      if (!section) return;
      section.classList.toggle("open");
      if (key) localStorage.setItem(key, section.classList.contains("open") ? "open" : "closed");
    });
  });
  const closeDrawer = () => closeUiModal("settings-drawer");
  const openDrawer = (returnFocus, titleId = "drawer-title") => {
    openUiModal("settings-drawer", titleId, closeDrawer, returnFocus);
    updateVoicevoxInstallUi();
    refreshAddonAsr();
  };
  const menuButton = document.getElementById("menu-btn");
  menuButton?.addEventListener("click", () => openDrawer(menuButton));
  document.getElementById("drawer-close-btn")?.addEventListener("click", closeDrawer);
  document.getElementById("drawer-discord-btn")?.addEventListener("click", () => {
    window.open("https://discord.gg/nrXFheFWqP", "_blank", "noopener,noreferrer");
  });
  document.getElementById("drawer-backdrop")?.addEventListener("click", closeDrawer);
  // Linux: system soft-restart
  const systemRestartBtn = document.getElementById("system-restart-btn");
  if (systemRestartBtn) {
    systemRestartBtn.addEventListener("click", async () => {
      const orig = systemRestartBtn.textContent;
      systemRestartBtn.disabled = true;
      systemRestartBtn.textContent = "…";
      try {
        const res = await fetch("/api/system/restart", { method: "POST" });
        if (res.ok) {
          const result = await res.json();
          systemRestartBtn.textContent = result?.ok === true ? "✓" : t("ui.web.system.restartFailed");
        } else {
          systemRestartBtn.textContent = `HTTP ${res.status}`;
        }
      } catch(e) {
        console.error("system restart failed", e);
        systemRestartBtn.textContent = t("ui.web.system.restartFailed");
      } finally {
        setTimeout(() => { systemRestartBtn.textContent = orig; systemRestartBtn.disabled = false; }, 2000);
      }
    });
  }
  // Phase 7A: Speaker ID reset (enrollment is always-on in background, no toggle)
  const speakerResetBtn = document.getElementById("speaker-reset-btn");
  if (speakerResetBtn) {
    let resetPending = false;
    const showResetResult = success => {
      setText("speaker-reset-status", t(success ? "ui.web.speaker.resetDone" : "ui.web.speaker.resetFailed"));
      if (!success && activeUiModal?.root.id === "settings-drawer") document.getElementById("speaker-reset-status")?.focus();
    };
    speakerResetBtn.addEventListener("click", async () => {
      if (resetPending) return;
      resetPending = true;
      try {
        if (!await confirmSpeakerReset()) return;
        speakerResetBtn.disabled = true;
        setText("speaker-reset-status", t("ui.web.status.loading"));
        const res = await fetch("/api/speaker/reset", { method: "POST" });
        const data = await res.json();
        showResetResult(res.ok && data?.success === true);
      } catch(e) {
        console.error("speaker reset failed", e);
        showResetResult(false);
      } finally {
        resetPending = false;
        speakerResetBtn.disabled = false;
        recoverUiModalFocus();
      }
    });
  }
}

async function pollRealtime(token) {
  const el = document.getElementById("rt-status");
  let captureLost = false;
  while (token === realtimePollToken) {
    await new Promise(r => setTimeout(r, 100));
    if (token !== realtimePollToken) return;
    // A pending wizard Start may be explicitly rejected, leaving main active.
    // Continue polling but hold publication until it resolves; success or
    // uncertainty retire this token in startSetupMicTest instead.
    try {
      const data = await RealtimeSessionClient.getStatus();
      if (token !== realtimePollToken) return;
      if (setupMicStarting) continue;
      if (setupMicBlocksOtherCapture()) return;
      if (data.error === "capture_device_lost") {
        captureLost = true;
        el.textContent = t("ui.web.status.stopping");
        el.className = "status err";
        break;
      }
      el.textContent = `${data.state}  samples=${data.captureSamples ?? 0}  rms=${(data.captureRms ?? 0).toFixed(4)}`;
      updateRealtimeResults(data);
      void AsrAcceptanceCue.accept(data, token);
      if (typeof OnsetFillerUi !== "undefined") void OnsetFillerUi.refresh();
      if (data.error) {
        el.className = "status err";
        break;
      }
      if (data.state && data.state !== "recording") {
        break;
      }
    } catch {
      if (token !== realtimePollToken) return;
      if (setupMicStarting) continue;
      break;
    }
  }
  // break で抜けた場合（token が自分のまま = stopRealtime 経由でない異常終了）
  if (token === realtimePollToken && !setupMicBlocksOtherCapture()) {
    // Release native ownership and its ASR reservation before showing the stopped UI.
    try { await RealtimeSessionClient.stopRealtimeSession(captureLost ? "capture-loss" : "main-poll-exit"); }
    catch (error) {
      console.warn("[Realtime] poll exit cleanup failed", error);
      if (captureLost && token === realtimePollToken) el.textContent = t("ui.web.status.error", { message: error.message });
      return;
    }
    if (token !== realtimePollToken) return;
    setRecordingButtons(false);
    if (captureLost) {
      setRealtimeWasRecording(false);
      lastRealtimePartial = "";
      setText("rt-partial", "");
      setText("rt-live-vad", "-");
      setText("rt-live-rms", "0.0000");
      renderRealtimeTabs();
      setRealtimeGuide("ui.web.guide.stopped");
      el.textContent = t("ui.web.status.captureLost");
    }
    meterStop(); spkStop(); waveClear();
  }
}

/* --- Realtime Route Bench --- */
let benchPollTimer = null;

async function startBenchRealtimeRoute() {
  const statusEl = document.getElementById("bench-status");
  const startBtn = document.getElementById("bench-start-btn");
  const progressWrap = document.getElementById("bench-progress-wrap");
  const progressBar = document.getElementById("bench-progress-bar");
  const resultsDiv = document.getElementById("bench-results");

  if (startBtn) startBtn.disabled = true;
  resultsDiv.style.display = "none";
  progressWrap.style.display = "block";
  progressBar.style.width = "0%";
  statusEl.textContent = t("ui.web.status.starting");
  statusEl.className = "status";

  const route = document.getElementById("bench-route-select")?.value || "realtime";
  const engine = document.getElementById("bench-engine-select")?.value || REALTIME_ENGINE.WHISPER;
  const settings = getRealtimeSettings();
  const endpoint = route === "offline" ? "/api/bench/start" : "/api/bench-realtime-route/start";

  try {
    const res = await fetch(endpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        engine,
        language: settings.language,
        scene: settings.scene,
        prefilter: settings.prefilterProfile,
        initialPrompt: settings.initialPrompt,
        ...(route === "offline" ? {} : addonBenchSettings(engine, settings.language)),
      }),
    });

    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      statusEl.textContent = t("ui.web.status.failed", { message: apiMessage(body, res.status) });
      statusEl.className = "status err";
      if (startBtn) startBtn.disabled = false;
      progressWrap.style.display = "none";
      return;
    }

    statusEl.textContent = t("ui.web.status.runningRoute", { route });
    pollBenchStatus(route);
  } catch (e) {
    statusEl.textContent = t("ui.web.status.error", { message: e.message });
    statusEl.className = "status err";
    if (startBtn) startBtn.disabled = false;
    progressWrap.style.display = "none";
  }
}

async function pollBenchStatus(route = "realtime") {
  const statusEl = document.getElementById("bench-status");
  const startBtn = document.getElementById("bench-start-btn");
  const progressWrap = document.getElementById("bench-progress-wrap");
  const progressBar = document.getElementById("bench-progress-bar");
  const resultsDiv = document.getElementById("bench-results");

  for (let i = 0; i < 600; i++) {
    await new Promise(r => setTimeout(r, 1000));
    try {
      const res = await fetch(route === "offline" ? "/api/bench/status" : "/api/bench-realtime-route/status");
      if (!res.ok) break;
      const data = await res.json();

      if (data.completed > 0 && data.total > 0) {
        const pct = Math.round((data.completed / data.total) * 100);
        progressBar.style.width = pct + "%";
        statusEl.textContent = t("ui.web.status.runningProgress", { completed: data.completed, total: data.total });
      }

      if (data.state === "completed") {
        progressWrap.style.display = "none";
        statusEl.textContent = t("ui.web.status.completed");
        statusEl.className = "status ok";
        if (startBtn) startBtn.disabled = false;

        /* Fetch detailed results */
        try {
          const rres = await fetch(route === "offline" ? "/api/bench/results" : "/api/bench-realtime-route/results");
          if (rres.ok) {
            const rdata = await rres.json();
            document.getElementById("bench-exact").textContent = `${rdata.exactCount}/${rdata.completed}`;
            document.getElementById("bench-cer").textContent = rdata.avgCer != null ? rdata.avgCer.toFixed(4) : "-";
            document.getElementById("bench-latency").textContent = "-";
            const opts = rdata.options || { route };
            const summary = [opts.route || route, opts.engine, opts.language, opts.scene, opts.prefilter].filter(Boolean).join(" / ");
            document.getElementById("bench-options-summary").textContent = summary ? `options: ${summary}` : "";
            document.getElementById("bench-result-path").textContent = rdata.resultPath || "";
            resultsDiv.style.display = "block";
          }
        } catch { /* ignore */ }
        break;
      }

      if (data.state === "failed") {
        progressWrap.style.display = "none";
        statusEl.textContent = t("ui.web.status.failed", { message: apiMessage(data) });
        statusEl.className = "status err";
        if (startBtn) startBtn.disabled = false;
        break;
      }
    } catch { break; }
  }
}

/* --- TTS --- */
let ttsStatusInterval = null;
let lastVirtualAudioStatus = null;
let virtualAudioInstallTimer = null;

function startTtsStatusPoll() {
  if (ttsStatusInterval) return;
  fetchTtsStatus();
  ttsStatusInterval = setInterval(fetchTtsStatus, 2000);
}

function stopTtsStatusPoll() {
  if (ttsStatusInterval) {
    clearInterval(ttsStatusInterval);
    ttsStatusInterval = null;
  }
}

async function fetchTtsOutputDevices() {
  const select = document.getElementById("tts-output-device-select");
  const status = document.getElementById("tts-device-status");
  if (!select) return;
  const restoreSettings = uiSettings.isReady();
  try {
    status.textContent = t("ui.web.status.loadingTtsDevices");
    const res = await fetch("/api/tts/devices");
    const data = await res.json();
    appendTtsOutputDeviceOptions(select, data.devices || [], true);
    const saved = localStorage.getItem("mobmate.ttsOutputDeviceId") || "";
    const hasSaved = Array.from(select.options).some(option => option.value === saved);
    select.value = hasSaved ? saved : "";
    if (restoreSettings && saved && hasSaved) {
      await setTtsOutputDevice(saved);
    }
    status.textContent = data.ok ? t("ui.web.device.ttsCount", { count: (data.devices || []).length }) : t("ui.web.status.failed", { message: apiMessage(data) });
    status.className = data.ok ? "status ok" : "status err";
  } catch (e) {
    if (status) { status.textContent = t("ui.web.status.error", { message: e.message }); status.className = "status err"; }
  }
}

function setVirtualAudioPopoverOpen(open) {
  const popover = document.getElementById("virtual-audio-popover");
  const button = document.getElementById("virtual-audio-help-btn");
  if (!popover || !button) return;
  popover.classList.toggle("open", open);
  button.setAttribute("aria-expanded", open ? "true" : "false");
  if (open) fetchVirtualAudioStatus();
}

function setVirtualAudioBusy(busy) {
  ["virtual-audio-install-btn"].forEach(id => {
    const el = document.getElementById(id);
    if (el) el.disabled = busy;
  });
}

function renderVirtualAudioStatus(data) {
  const statusEl = document.getElementById("virtual-audio-status");
  if (!statusEl) return;
  const info = data?.virtualAudio || data || {};
  lastVirtualAudioStatus = info;
  if (info.detected) {
    statusEl.textContent = t("ui.web.virtualAudio.status.detected", { device: info.preferredOutputDeviceName || t("ui.web.device.unnamed") });
    statusEl.className = "va-status status ok";
  } else {
    statusEl.textContent = t("ui.web.virtualAudio.status.notDetected");
    statusEl.className = "va-status status err";
  }
}

async function fetchVirtualAudioStatus() {
  const statusEl = document.getElementById("virtual-audio-status");
  try {
    if (statusEl) statusEl.textContent = t("ui.web.virtualAudio.status.loading");
    const res = await fetch("/api/virtual-audio/status");
    const data = await res.json();
    renderVirtualAudioStatus(data);
  } catch (e) {
    if (statusEl) { statusEl.textContent = t("ui.web.status.error", { message: e.message }); statusEl.className = "va-status status err"; }
  }
}

async function pollVirtualAudioInstallStatus() {
  const statusEl = document.getElementById("virtual-audio-status");
  try {
    const res = await fetch("/api/virtual-audio/install-status");
    const data = await res.json();
    const install = data.install || {};
    renderVirtualAudioStatus(data);
    if (install.running) {
      if (statusEl) { statusEl.textContent = install.lastMessage || t("ui.web.virtualAudio.status.installing"); statusEl.className = "va-status status"; }
      return;
    }
    clearInterval(virtualAudioInstallTimer);
    virtualAudioInstallTimer = null;
    setVirtualAudioBusy(false);
    await fetchTtsOutputDevices();
    if (!data.virtualAudio?.detected && statusEl) {
      statusEl.textContent = install.error ? t("ui.web.virtualAudio.status.installError", { message: install.error }) : t("ui.web.virtualAudio.status.restartRequired");
      statusEl.className = "va-status status err";
    }
  } catch (e) {
    clearInterval(virtualAudioInstallTimer);
    virtualAudioInstallTimer = null;
    setVirtualAudioBusy(false);
    if (statusEl) { statusEl.textContent = t("ui.web.status.error", { message: e.message }); statusEl.className = "va-status status err"; }
  }
}

async function installVirtualAudio() {
  const statusEl = document.getElementById("virtual-audio-status");
  try {
    setVirtualAudioBusy(true);
    if (statusEl) { statusEl.textContent = t("ui.web.virtualAudio.status.installing"); statusEl.className = "va-status status"; }
    const res = await fetch("/api/virtual-audio/install", { method: "POST" });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, res.status));
    if (virtualAudioInstallTimer) clearInterval(virtualAudioInstallTimer);
    virtualAudioInstallTimer = setInterval(pollVirtualAudioInstallStatus, 1000);
    pollVirtualAudioInstallStatus();
  } catch (e) {
    setVirtualAudioBusy(false);
    if (statusEl) { statusEl.textContent = t("ui.web.virtualAudio.status.installError", { message: e.message }); statusEl.className = "va-status status err"; }
  }
}

async function applyVirtualAudioOutput() {
  const statusEl = document.getElementById("virtual-audio-status");
  try {
    const deviceId = lastVirtualAudioStatus?.preferredOutputDeviceId || "";
    const wasUnconfirmed = ttsOutputAttempted && ttsOutputConfirmed === null;
    const data = await queueTtsOutputChange(async revision => {
      const res = await fetch("/api/virtual-audio/apply", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceId })
      });
      const result = await res.json().catch(() => ({}));
      if (!res.ok || result?.ok !== true) throw new Error(apiMessage(result, res.status));
      if (revision !== ttsOutputRevision) return null;
      localStorage.setItem("mobmate.ttsOutputDeviceId", result.outputDeviceId || deviceId);
      // Linux's virtual-audio endpoint is a no-op. When it did not follow an
      // uncertain config, permit a fresh status check if no config follows.
      if (linuxCoreOnly() && typeof result.outputDeviceId !== "string" && !wasUnconfirmed) ttsOutputAttempted = false;
      return result;
    });
    if (!data) return;
    await fetchTtsOutputDevices();
    await fetchTtsStatus();
    if (statusEl) { statusEl.textContent = t("ui.web.virtualAudio.status.applied", { device: data.outputDeviceName || lastVirtualAudioStatus?.preferredOutputDeviceName || "" }); statusEl.className = "va-status status ok"; }
  } catch (e) {
    if (statusEl) { statusEl.textContent = t("ui.web.status.error", { message: e.message }); statusEl.className = "va-status status err"; }
  }
}

async function openWindowsSoundSettings() {
  await fetch("/api/virtual-audio/open-sound-settings", { method: "POST" }).catch(() => {});
}

/* --- TTS output confirmation --- */
let ttsOutputRevision = 0;
let ttsOutputPending = 0;
let ttsOutputQueue = Promise.resolve();
let ttsOutputAttempted = false;
let ttsOutputConfirmed = null; // null is unknown; the empty string is system default.

function isSelectedVoicevoxUnavailable() {
  const engine = document.getElementById("tts-engine-select")?.value;
  const savedEngine = uiSettings.isKnown("mobmate.ttsEngine") ? localStorage.getItem("mobmate.ttsEngine") : null;
  if (engine !== "voicevox" && !(engine === "auto" && savedEngine === "voicevox")) return false;
  if (!uiSettings.isKnown("mobmate.voicevoxSpeaker")) return true;
  const saved = localStorage.getItem("mobmate.voicevoxSpeaker");
  const selected = document.getElementById("tts-voicevox-speaker-select")?.selectedOptions?.[0];
  return !selected || selected.disabled || (!!saved && selected.value !== saved);
}

function isSelectedMossUnavailable() {
  const engine = document.getElementById("tts-engine-select")?.value;
  const savedEngine = uiSettings.isKnown("mobmate.ttsEngine") ? localStorage.getItem("mobmate.ttsEngine") : null;
  if (engine !== "moss" && !(engine === "auto" && savedEngine === "moss")) return false;
  if (!uiSettings.isKnown("mobmate.mossModelVoice")) return true;
  const saved = localStorage.getItem("mobmate.mossModelVoice");
  const selected = document.getElementById("tts-moss-model-select")?.selectedOptions?.[0];
  return !selected?.value || selected.disabled || (!!saved && selected.value !== saved);
}

function isSelectedTtsVoiceUnavailable() {
  return isSelectedVoicevoxUnavailable() || isSelectedMossUnavailable();
}

function currentTtsOutputDevice() {
  return document.getElementById("tts-output-device-select")?.value || localStorage.getItem("mobmate.ttsOutputDeviceId") || "";
}

function queueTtsOutputChange(action, preservesOutput = false) {
  const revision = preservesOutput ? ttsOutputRevision : ++ttsOutputRevision;
  if (!preservesOutput) {
    ttsOutputAttempted = true;
    ttsOutputConfirmed = null;
    if (typeof OnsetFillerUi !== "undefined") OnsetFillerUi.ttsChanging();
  }
  ttsOutputPending++;
  const request = ttsOutputQueue.then(() => action(revision));
  ttsOutputQueue = request.catch(() => {});
  return request.finally(() => { ttsOutputPending--; });
}

function hasCompleteTtsVoice(body) {
  if (body.engine === "voicevox") return typeof body.voicevoxSpeakerId === "string" && body.voicevoxSpeakerId !== "";
  if (body.engine === "kokoro") return !!body.kokoroModelId && Number.isInteger(body.kokoroSpeakerId);
  if (body.engine === "moss") return !!body.mossModelId && !!body.mossVoiceId;
  return typeof body.engine === "string" && body.engine !== "";
}

function postTtsConfig(body) {
  const includesOutput = Object.hasOwn(body, "outputDeviceId");
  const includesEngine = Object.hasOwn(body, "engine");
  const voiceTicket = includesEngine && typeof OnsetFillerUi !== "undefined" ? OnsetFillerUi.ttsConfigChanging() : null;
  if (includesEngine && !includesOutput && typeof OnsetFillerUi !== "undefined") OnsetFillerUi.ttsChanging();
  return queueTtsOutputChange(async revision => {
    const res = await fetch("/api/tts/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    const data = await res.json().catch(() => ({}));
    // Linux returns {ok:true}; Windows returns {code:"tts.config_applied"}.
    const confirmed = res.ok && data?.ok !== false && (data?.ok === true || data?.code === "tts.config_applied");
    if (confirmed && revision === ttsOutputRevision && currentTtsOutputDevice() === body.outputDeviceId) {
      ttsOutputConfirmed = body.outputDeviceId;
    }
    if (confirmed && typeof OnsetFillerUi !== "undefined") {
      if (includesEngine && hasCompleteTtsVoice(body)) OnsetFillerUi.ttsConfigConfirmed(revision, voiceTicket);
      if (uiSettings.isReady() && includesOutput) {
        if (revision === ttsOutputRevision && ttsOutputConfirmed === currentTtsOutputDevice())
          OnsetFillerUi.ttsOutputConfigConfirmed(revision);
      }
      void OnsetFillerUi.prepare();
    }
    return { res, data, confirmed, revision };
  }, !includesOutput);
}

function comparableTtsOutputDevice(id) {
  // Match the Linux host's NormalizeTtsOutputSink, not a display name.
  return linuxCoreOnly() && /^MobMate/i.test(id) && id !== "MobMateTTSVirtualMic" ? "MobMateTTSVirtualMic" : id;
}

async function requestTtsSpeak(body) {
  const voiceUnavailable = () => {
    const engine = document.getElementById("tts-engine-select")?.value;
    const mossSelected = engine === "moss" ||
      (engine === "auto" && uiSettings.isKnown("mobmate.ttsEngine") && localStorage.getItem("mobmate.ttsEngine") === "moss");
    // A late catalog makes the voice selectable before it reaches the host.
    return (!body.engine || body.engine === engine) && (isSelectedTtsVoiceUnavailable() ||
      (mossSelected && !OnsetFillerUi.isVoiceConfirmed()));
  };
  if (voiceUnavailable()) throw new Error(t("tts.kokoro.unavailable"));
  const deviceId = currentTtsOutputDevice();
  const revision = ttsOutputRevision;
  if (ttsOutputPending) return null;
  if (ttsOutputConfirmed === null && !ttsOutputAttempted) {
    try {
      const res = await fetch("/api/tts/status", { cache: "no-store" });
      const data = await res.json();
      if (res.ok && data?.ok !== false && typeof data?.outputDeviceId === "string" &&
          comparableTtsOutputDevice(data.outputDeviceId) === comparableTtsOutputDevice(deviceId) &&
          revision === ttsOutputRevision && !ttsOutputPending && currentTtsOutputDevice() === deviceId) {
        ttsOutputConfirmed = deviceId;
      }
    } catch { /* A missing observation does not confirm the output. */ }
  }
  if (ttsOutputPending || revision !== ttsOutputRevision || currentTtsOutputDevice() !== deviceId || ttsOutputConfirmed !== deviceId) return null;
  if (voiceUnavailable()) throw new Error(t("tts.kokoro.unavailable"));
  // Invoke fetch without yielding again after checking the selected output.
  return fetch("/api/tts/speak", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
}

async function setTtsOutputDevice(deviceId, selectId = "tts-output-device-select") {
  const status = document.getElementById("tts-device-status");
  const select = document.getElementById(selectId);
  const selectedName = select?.selectedOptions?.[0]?.dataset?.deviceName || select?.selectedOptions?.[0]?.textContent || "";
  const monitorVolume = Number(document.getElementById("tts-monitor-volume-select")?.value || localStorage.getItem("mobmate.ttsMonitorVolume") || 0);
  localStorage.setItem("mobmate.ttsOutputDeviceId", deviceId);
  const mainSelect = document.getElementById("tts-output-device-select");
  if (mainSelect && selectId !== "tts-output-device-select" && select?.selectedOptions?.[0] &&
      !Array.from(mainSelect.options).some(option => option.value === deviceId)) {
    // The wizard may have enumerated a device after the main list was loaded.
    const option = document.createElement("option");
    option.value = deviceId;
    option.textContent = select.selectedOptions[0].textContent;
    option.dataset.deviceName = select.selectedOptions[0].dataset.deviceName || "";
    mainSelect.appendChild(option);
  }
  if (mainSelect && Array.from(mainSelect.options).some((option) => option.value === deviceId)) mainSelect.value = deviceId;
  try {
    const body = { outputDeviceId: deviceId, outputDeviceName: selectedName };
    if (uiSettings.isKnown("mobmate.ttsMonitorVolume")) body.monitorVolumePercent = monitorVolume;
    const result = await postTtsConfig(body);
    if (!result.confirmed) {
      if (status) { status.textContent = t("ui.web.status.configFailed", { message: apiMessage(result.data, result.res.status) }); status.className = "status err"; }
      return false;
    } else if (isMobfyMode() && vcRunning) {
      await startVoiceConversionRequest(vcGetOptions());
      vcFetchStatus();
    }
    if (status && !ttsOutputPending && ttsOutputConfirmed === deviceId && currentTtsOutputDevice() === deviceId) {
      status.textContent = "";
      status.className = "status";
    }
    return true;
  } catch (e) {
    if (status) { status.textContent = t("ui.web.status.error", { message: e.message }); status.className = "status err"; }
    return false;
  }
}

async function setMonitorVolume(volume) {
  const status = document.getElementById("tts-device-status");
  const select = document.getElementById("tts-output-device-select");
  const deviceId = select?.value || localStorage.getItem("mobmate.ttsOutputDeviceId") || "";
  const selectedName = select?.selectedOptions?.[0]?.textContent || "";
  localStorage.setItem("mobmate.ttsMonitorVolume", volume);
  try {
    const body = { monitorVolumePercent: Number(volume) };
    if (uiSettings.isKnown("mobmate.ttsOutputDeviceId")) {
      body.outputDeviceId = deviceId;
      body.outputDeviceName = selectedName;
    }
    const result = await postTtsConfig(body);
    if (!result.confirmed) {
      if (status) { status.textContent = t("ui.web.status.configFailed", { message: apiMessage(result.data, result.res.status) }); status.className = "status err"; }
    } else if (isMobfyMode() && vcRunning) {
      await startVoiceConversionRequest(vcGetOptions());
      vcFetchStatus();
    }
  } catch (e) {
    if (status) { status.textContent = t("ui.web.status.error", { message: e.message }); status.className = "status err"; }
  }
}

async function setWavOutputEnabled(enabled) {
  localStorage.setItem("mobmate.wavOutputEnabled", String(enabled));
  await setTtsEngineConfig();
  if (isMobfyMode() && vcRunning) {
    await startVoiceConversionRequest(vcGetOptions());
    vcFetchStatus();
  }
}

async function fetchTtsStatus() {
  const statusEl = document.getElementById("tts-status");
  const detailsEl = document.getElementById("tts-status-details");
  if (!statusEl) return;
  try {
    const res = await fetch("/api/tts/status");
    if (!res.ok) { statusEl.textContent = t("ui.web.status.errorShort"); statusEl.className = "status err"; return; }
    const data = await res.json();
    if (!data.available) {
      statusEl.textContent = t("ui.web.status.unavailable", { message: apiMessage(data, "SAPI not found") });
      statusEl.className = "status err";
    } else if (data.isSpeaking) {
      statusEl.textContent = t("ui.web.status.speaking");
      statusEl.className = "status ok";
    } else {
      statusEl.textContent = t("ui.web.ready");
      statusEl.className = "status ok";
    }
    // Show status details
    if (detailsEl) {
      const parts = [];
      if (data.outputDeviceName) parts.push(`${t("ui.web.audio.outputPrefix")}: ${data.outputDeviceName}`);
      if (data.wirelessLikely) parts.push(`${t("ui.web.device.wireless")} (pre-silence ${data.preSilenceMs || 600}ms)`);
      if (data.fallbackReason) parts.push(`⚠ ${data.fallbackReason}`);
      if (data.droppedCount > 0) parts.push(`dropped: ${data.droppedCount}`);
      if (data.monitorEnabled) parts.push(`monitor: ${data.monitorVolumePercent}%`);
      if (data.monitorFallbackReason) parts.push(`⚠ monitor: ${data.monitorFallbackReason}`);
      if (parts.length) {
        detailsEl.textContent = parts.join(" / ");
        detailsEl.style.display = "block";
      } else {
        detailsEl.style.display = "none";
      }
    }
  } catch (e) {
    statusEl.textContent = t("ui.web.status.error", { message: e.message });
    statusEl.className = "status err";
  }
}

/* --- TTS Engine Selection --- */
let ttsEnginesCache = null;

async function restoreTtsSettingsAfterRead() {
  const output = document.getElementById("tts-output-device-select");
  if (output) output.value = localStorage.getItem("mobmate.ttsOutputDeviceId") || "";
  const monitor = document.getElementById("tts-monitor-volume-select");
  const savedMonitor = localStorage.getItem("mobmate.ttsMonitorVolume");
  if (monitor && savedMonitor !== null && Array.from(monitor.options).some(o => o.value === savedMonitor)) monitor.value = savedMonitor;
  const wav = document.getElementById("wav-output-enabled");
  if (wav) wav.checked = localStorage.getItem("mobmate.wavOutputEnabled") === "true";
  const moss = document.getElementById("tts-moss-model-select");
  if (moss) moss.value = localStorage.getItem("mobmate.mossModelVoice") || "";
  await fetchTtsOutputDevices();
  return await fetchTtsEngines();
}

async function fetchTtsEngines() {
  // A read that began while settings were unavailable is never an apply request,
  // even if the settings recover before this enumeration returns.
  const restoreSettings = uiSettings.isReady();
  try {
    const res = await fetch("/api/tts/engines");
    if (!res.ok) return false;
    ttsEnginesCache = await res.json();
    populateTtsEngineSelect();
    populateVoicevoxSpeakerSelect();
    populatePiperModelSelect();
    _kokoroDlRenderList();
    _kokoroPopulateModelSelect();
    const confirmed = restoreSettings && await setTtsEngineConfig();
    refreshRealtimeGuide();
    return confirmed === true;
  } catch (e) {
    return false;
  }
}

function populateTtsEngineSelect() {
  const el = document.getElementById("tts-engine-select");
  if (!el || !ttsEnginesCache) return;
  const engines = ttsEnginesCache.engines || [];
  if (linuxCoreOnly() && Array.isArray(engines)) {
    el.innerHTML = "";
    for (const info of engines) {
      const opt = document.createElement("option");
      opt.value = info.id;
      opt.disabled = info.available === false;
      opt.textContent = (info.label || info.name || info.id) + (info.available === false ? " (unavailable)" : "");
      el.appendChild(opt);
    }
    const saved = localStorage.getItem("mobmate.ttsEngine");
    if (saved && Array.from(el.options).some(o => o.value === saved)) {
      el.value = saved;
    } else if (el.options.length > 0) {
      el.value = el.options[0].value;
      // An unread saved selection is not an absent selection.
      if (uiSettings.isReady()) localStorage.setItem("mobmate.ttsEngine", el.value);
    }
    updateVoicevoxSpeakerRow();
    return;
  }
  // Update option labels with availability
  for (const opt of el.options) {
    const key = opt.value;
    if (key === "auto") continue;
    const info = Array.isArray(engines) ? engines.find(e => e.id === key) : engines[key];
    if (info) {
      opt.disabled = false;
      opt.textContent = (info.label || info.name || key) + (info.available ? "" : " (unavailable)");
    }
  }
  // Restore saved engine
  const saved = localStorage.getItem("mobmate.ttsEngine");
  if (saved) el.value = saved;
}

function populateVoicevoxSpeakerSelect() {
  const el = document.getElementById("tts-voicevox-speaker-select");
  if (!el || !ttsEnginesCache) return;
  const speakers = ttsEnginesCache.voicevoxSpeakers || [];
  el.innerHTML = "";
  const saved = uiSettings.isKnown("mobmate.voicevoxSpeaker") ? localStorage.getItem("mobmate.voicevoxSpeaker") : null;
  if (saved && !speakers.some(s => String(s.id) === saved)) {
    const opt = document.createElement("option");
    opt.value = saved; opt.textContent = `${saved} — ${t("tts.kokoro.unavailable")}`;
    opt.disabled = true; opt.selected = true; el.appendChild(opt);
  }
  for (const s of speakers) {
    const opt = document.createElement("option");
    opt.value = s.id;
    opt.textContent = `${s.id}:${s.name}`;
    el.appendChild(opt);
  }
  // Restore saved speaker
  if (saved && Array.from(el.options).some(o => o.value === saved)) {
    el.value = saved;
  }
  updateVoicevoxSpeakerRow();
}

function populatePiperModelSelect() {
  const el = document.getElementById("tts-piper-model-select");
  if (!el || !ttsEnginesCache) return;
  const engineSelect = document.getElementById("tts-engine-select");
  const engine = engineSelect?.value || TTS_ENGINE.PIPER_PLUS;
  const models = engine === TTS_ENGINE.PIPER ? (ttsEnginesCache.piperModels || []) : (ttsEnginesCache.piperPlusModels || []);
  el.innerHTML = "";
  for (const model of models) {
    const opt = document.createElement("option");
    opt.value = model.id;
    const lang = model.language ? ` [${model.language}]` : "";
    opt.textContent = `${model.name || model.id}${lang}`;
    opt.title = [model.license, model.sourcePageUrl].filter(Boolean).join(" | ");
    el.appendChild(opt);
  }
  const saved = localStorage.getItem("mobmate.piperPlusModel");
  if (saved && Array.from(el.options).some(o => o.value === saved)) {
    el.value = saved;
  } else if (el.options.length > 0) {
    if (uiSettings.isReady()) localStorage.setItem("mobmate.piperPlusModel", el.value);
  }
  updateVoicevoxSpeakerRow();
}

function updateVoicevoxSpeakerRow() {
  const engineSelect = document.getElementById("tts-engine-select");
  const row = document.getElementById("tts-voicevox-speaker-row");
  const speakerSelect = document.getElementById("tts-voicevox-speaker-select");
  const piperModelSelect = document.getElementById("tts-piper-model-select");
  const selects = document.getElementById("tts-voice-selects");
  if (!engineSelect) return;
  const kokoroModelSelect = document.getElementById("tts-kokoro-model-select");
  const mossModelSelect = document.getElementById("tts-moss-model-select");
  const showVoicevoxSpeaker = engineSelect.value === TTS_ENGINE.VOICEVOX;
  const showPiperModel = engineSelect.value === TTS_ENGINE.PIPER || engineSelect.value === TTS_ENGINE.PIPER_PLUS;
  const showKokoroModel = engineSelect.value === TTS_ENGINE.KOKORO;
  const showMossModel = engineSelect.value === TTS_ENGINE.MOSS;
  if (row) row.style.display = "none";
  if (speakerSelect) speakerSelect.style.display = showVoicevoxSpeaker ? "block" : "none";
  if (piperModelSelect) piperModelSelect.style.display = showPiperModel ? "block" : "none";
  if (kokoroModelSelect) kokoroModelSelect.style.display = showKokoroModel ? "block" : "none";
  if (mossModelSelect) mossModelSelect.style.display = showMossModel ? "block" : "none";
  if (showMossModel) _mossPopulateModelSelect();
  if (selects) selects.style.gridTemplateColumns = (showVoicevoxSpeaker || showPiperModel || showKokoroModel || showMossModel) ? "minmax(0,1fr) minmax(0,1fr)" : "1fr";
}

async function setTtsEngineConfig() {
  const engineSelect = document.getElementById("tts-engine-select");
  const speakerSelect = document.getElementById("tts-voicevox-speaker-select");
  const piperModelSelect = document.getElementById("tts-piper-model-select");
  const outputDeviceSelect = document.getElementById("tts-output-device-select");
  const monitorVolumeSelect = document.getElementById("tts-monitor-volume-select");
  if (!engineSelect) return false;
  const engine = engineSelect.value || TTS_ENGINE.AUTO;
  // Both hosts accept partial config updates. While the saved document is
  // unknown, only fields explicitly changed by this user are known.
  const body = {};
  if (uiSettings.isKnown("mobmate.ttsEngine")) body.engine = engine;
  if (uiSettings.isKnown("mobmate.ttsOutputDeviceId")) {
    body.outputDeviceId = outputDeviceSelect?.value || localStorage.getItem("mobmate.ttsOutputDeviceId") || "";
    body.outputDeviceName = outputDeviceSelect?.selectedOptions?.[0]?.textContent || "";
  }
  if (uiSettings.isKnown("mobmate.ttsMonitorVolume"))
    body.monitorVolumePercent = Number(monitorVolumeSelect?.value || localStorage.getItem("mobmate.ttsMonitorVolume") || 0);
  if (uiSettings.isKnown("mobmate.wavOutputEnabled"))
    body.wavOutputEnabled = document.getElementById("wav-output-enabled")?.checked === true;
  if (engine === TTS_ENGINE.VOICEVOX && isSelectedVoicevoxUnavailable()) {
    // An unavailable saved style is not permission to reuse the host's old voice.
    delete body.engine;
    if (typeof OnsetFillerUi !== "undefined") {
      OnsetFillerUi.ttsConfigChanging();
      OnsetFillerUi.ttsChanging();
    }
  } else if (engine === TTS_ENGINE.VOICEVOX && uiSettings.isKnown("mobmate.voicevoxSpeaker") && speakerSelect?.value) {
    body.voicevoxSpeakerId = speakerSelect.value;
  }
  if ((engine === TTS_ENGINE.PIPER || engine === TTS_ENGINE.PIPER_PLUS) && uiSettings.isKnown("mobmate.piperPlusModel") && piperModelSelect?.value) {
    body.piperPlusModelId = piperModelSelect.value;
  }
  if (engine === TTS_ENGINE.KOKORO && uiSettings.isKnown("mobmate.kokoroModelSpeaker")) {
    const kokoroModelSelect = document.getElementById("tts-kokoro-model-select");
    const parts = (kokoroModelSelect?.value || "").split("|");
    if (parts[0]) body.kokoroModelId = parts[0];
    const sid = parseInt(parts[1] || "3", 10);
    if (!isNaN(sid)) body.kokoroSpeakerId = sid;
  }
  if (engine === TTS_ENGINE.MOSS && uiSettings.isKnown("mobmate.mossModelVoice")) {
    const sel = document.getElementById("tts-moss-model-select");
    if (isSelectedMossUnavailable()) {
      // Never turn an unavailable saved voice into the first available voice.
      delete body.engine;
      if (typeof OnsetFillerUi !== "undefined") {
        OnsetFillerUi.ttsConfigChanging();
        OnsetFillerUi.ttsChanging();
      }
    } else {
      const parts = (sel?.value || localStorage.getItem("mobmate.mossModelVoice") || "").split("|");
      if (parts[0]) body.mossModelId = parts[0];
      if (parts[1]) body.mossVoiceId = parts[1];
    }
  }
  if (!Object.keys(body).length) return false;
  try {
    const result = await postTtsConfig(body);
    await fetchTtsStatus();
    if (!result.confirmed) {
      const status = document.getElementById("tts-status");
      if (status) { status.textContent = t("ui.web.status.configFailed", { message: apiMessage(result.data, result.res.status) }); status.className = "status err"; }
    }
    return result.confirmed && hasCompleteTtsVoice(body) && Object.hasOwn(body, "outputDeviceId") &&
      result.revision === ttsOutputRevision && !ttsOutputPending && ttsOutputConfirmed === currentTtsOutputDevice();
  } catch (e) {
    console.warn("setTtsEngineConfig failed", e);
    return false;
  }
}

/* --- TTS Speak body builder (shared) --- */
function buildTtsSpeakBody(text) {
  const engineSelect = document.getElementById("tts-engine-select");
  const speakerSelect = document.getElementById("tts-voicevox-speaker-select");
  const piperModelSelect = document.getElementById("tts-piper-model-select");
  const body = { text, engine: engineSelect?.value || TTS_ENGINE.AUTO, interrupt: true };
  if (body.engine === TTS_ENGINE.VOICEVOX && speakerSelect?.value) body.voicevoxSpeakerId = speakerSelect.value;
  if (body.engine === TTS_ENGINE.PIPER_PLUS && piperModelSelect?.value) body.piperPlusModelId = piperModelSelect.value;
  if (body.engine === TTS_ENGINE.KOKORO) {
    const sel = document.getElementById("tts-kokoro-model-select");
    const parts = (sel?.value || "").split("|");
    if (parts[0]) body.kokoroModelId = parts[0];
    const sid = parseInt(parts[1] || "3", 10);
    if (!isNaN(sid)) body.kokoroSpeakerId = sid;
  }
  if (body.engine === TTS_ENGINE.MOSS) {
    const sel = document.getElementById("tts-moss-model-select");
    const parts = (sel?.value || "").split("|");
    if (parts[0]) body.mossModelId = parts[0];
    if (parts[1]) body.mossVoiceId = parts[1];
  }
  return body;
}

/* --- Text Speech (テキスト発話) --- */
const tsState = { history: [], histIdx: -1, composing: false, target: "tts", sending: false };

function tsSetStatus(cls, key, params) {
  const wrap = document.getElementById("ts-wrap");
  const el = document.getElementById("ts-status");
  if (wrap) { wrap.classList.remove("sending","success","error"); if (cls) wrap.classList.add(cls); }
  if (el) { el.textContent = t(key, params); el.className = "status" + (cls === "error" ? " err" : cls === "success" ? " ok" : ""); }
  if (cls === "success" || cls === "error") setTimeout(() => { if (wrap) wrap.classList.remove(cls); }, 800);
}

function tsAutoGrow(ta) {
  ta.style.height = "auto";
  ta.style.height = Math.min(ta.scrollHeight, 120) + "px";
}

function tsUpdateCounter(ta) {
  const el = document.getElementById("ts-counter");
  if (el) el.textContent = ta.value.length > 0 ? ta.value.length : "";
}

function tsUpdateSendBtn(ta) {
  const btn = document.getElementById("ts-send-btn");
  if (btn) btn.disabled = tsState.sending || !ta.value.trim();
}

function tsShowHistory() {
  const el = document.getElementById("ts-counter");
  if (el && tsState.histIdx >= 0) {
    el.textContent = t("ui.web.textSpeech.history", { current: tsState.histIdx + 1, total: tsState.history.length });
  }
}

async function textSpeechSend() {
  const ta = document.getElementById("ts-input");
  if (!ta || tsState.sending) return;
  const text = ta.value.trim();
  if (!text) return;
  const target = tsState.target;
  if (!["tts", "chatbox", "both"].includes(target)) {
    tsSetStatus("error", "ui.web.textSpeech.status.error", { message: t("ui.web.status.unknown") });
    return;
  }
  const submittedValue = ta.value;
  tsState.sending = true;

  // history
  if (tsState.history[tsState.history.length - 1] !== text) tsState.history.push(text);
  if (tsState.history.length > 50) tsState.history.shift();
  tsState.histIdx = -1;

  tsSetStatus("sending", "ui.web.textSpeech.status.sending");
  const sendBtn = document.getElementById("ts-send-btn");
  if (sendBtn) sendBtn.disabled = true;

  const errors = [];
  try {
    if (target === "tts" || target === "both") {
      const body = buildTtsSpeakBody(text);
      const res = await requestTtsSpeak(body);
      if (!res) errors.push(`TTS: ${t("ui.web.tts.outputUnconfirmed")}`);
      else {
        const data = await res.json();
        if (!res.ok || !(data?.success === true || data?.ok === true)) errors.push(`TTS: ${apiMessage(data, res.ok ? t("ui.web.status.unknown") : `HTTP ${res.status}`)}`);
      }
    }
    if (target === "chatbox" || target === "both") {
      const res = await fetch("/api/vrchat-osc/test", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ text }) });
      const data = await res.json();
      if (!res.ok || data?.ok !== true) errors.push(`Chatbox: ${apiMessage(data, res.ok ? t("ui.web.status.unknown") : `HTTP ${res.status}`)}`);
    }
    if (!errors.length) {
      const key = target === "tts" ? "ui.web.textSpeech.status.spoke"
                : target === "chatbox" ? "ui.web.textSpeech.status.chatboxSent"
                : "ui.web.textSpeech.status.done";
      tsSetStatus("success", key);
      if (ta.value === submittedValue) ta.value = "";
    } else {
      tsSetStatus("error", "ui.web.textSpeech.status.error", { message: errors.join(" / ") });
    }
  } catch (e) {
    tsSetStatus("error", "ui.web.textSpeech.status.error", { message: e.message });
  }
  tsState.sending = false;
  tsAutoGrow(ta);
  tsUpdateCounter(ta);
  tsUpdateSendBtn(ta);
  ta.focus();
}

function initTextSpeech() {
  const ta = document.getElementById("ts-input");
  const sendBtn = document.getElementById("ts-send-btn");
  const targetsDiv = document.getElementById("ts-targets");
  if (!ta) return;

  // target chips
  if (targetsDiv) {
    const saved = localStorage.getItem("mobmate.textSpeech.target") || "tts";
    tsState.target = saved;
    targetsDiv.querySelectorAll(".ts-chip").forEach(chip => {
      chip.classList.toggle("active", chip.dataset.tsTarget === saved);
      chip.addEventListener("click", () => {
        targetsDiv.querySelectorAll(".ts-chip").forEach(c => c.classList.remove("active"));
        chip.classList.add("active");
        tsState.target = chip.dataset.tsTarget;
        localStorage.setItem("mobmate.textSpeech.target", tsState.target);
      });
    });
  }

  // input events
  ta.addEventListener("input", () => { tsAutoGrow(ta); tsUpdateCounter(ta); tsUpdateSendBtn(ta); });
  ta.addEventListener("compositionstart", () => { tsState.composing = true; });
  ta.addEventListener("compositionend", () => { tsState.composing = false; });
  ta.addEventListener("keydown", (e) => {
    if (tsState.composing) return;
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      textSpeechSend();
      return;
    }
    if (e.key === "ArrowUp" && ta.selectionStart === 0 && tsState.history.length > 0) {
      e.preventDefault();
      if (tsState.histIdx < 0) tsState.histIdx = tsState.history.length;
      if (tsState.histIdx > 0) { tsState.histIdx--; ta.value = tsState.history[tsState.histIdx]; tsAutoGrow(ta); tsUpdateCounter(ta); tsUpdateSendBtn(ta); tsShowHistory(); }
      return;
    }
    if (e.key === "ArrowDown" && tsState.histIdx >= 0) {
      e.preventDefault();
      tsState.histIdx++;
      if (tsState.histIdx >= tsState.history.length) { tsState.histIdx = -1; ta.value = ""; } else { ta.value = tsState.history[tsState.histIdx]; }
      tsAutoGrow(ta); tsUpdateCounter(ta); tsUpdateSendBtn(ta);
      if (tsState.histIdx >= 0) tsShowHistory(); else { const c = document.getElementById("ts-counter"); if (c) c.textContent = ""; }
      return;
    }
  });

  // send button
  if (sendBtn) sendBtn.addEventListener("click", () => textSpeechSend());
}

/* --- TTS Speak --- */
async function ttsSpeak() {
  const input = document.getElementById("tts-text-input");
  const statusEl = document.getElementById("tts-status");
  const engineSelect = document.getElementById("tts-engine-select");
  const speakerSelect = document.getElementById("tts-voicevox-speaker-select");
  const piperModelSelect = document.getElementById("tts-piper-model-select");
  if (!input || !statusEl) return;
  const text = input.value.trim();
  if (!text) { statusEl.textContent = t("ui.web.tts.textRequired"); statusEl.className = "status err"; return; }
  const body = {
    text,
    engine: engineSelect?.value || TTS_ENGINE.AUTO
  };
  if (body.engine === TTS_ENGINE.VOICEVOX && speakerSelect?.value) {
    body.voicevoxSpeakerId = speakerSelect.value;
  }
  if (body.engine === TTS_ENGINE.PIPER_PLUS && piperModelSelect?.value) {
    body.piperPlusModelId = piperModelSelect.value;
  }
  if (body.engine === TTS_ENGINE.KOKORO) {
    const sel = document.getElementById("tts-kokoro-model-select");
    const parts = (sel?.value || "").split("|");
    if (parts[0]) body.kokoroModelId = parts[0];
    const sid = parseInt(parts[1] || "3", 10);
    if (!isNaN(sid)) body.kokoroSpeakerId = sid;
  }
  if (body.engine === TTS_ENGINE.MOSS) {
    const sel = document.getElementById("tts-moss-model-select");
    const parts = (sel?.value || "").split("|");
    if (parts[0]) body.mossModelId = parts[0];
    if (parts[1]) body.mossVoiceId = parts[1];
  }
  try {
    const res = await requestTtsSpeak(body);
    if (!res) { statusEl.textContent = t("ui.web.tts.outputUnconfirmed"); statusEl.className = "status err"; return; }
    const data = await res.json();
    if (data.success || data.ok) {
      statusEl.textContent = t("ui.web.status.speaking");
      statusEl.className = "status ok";
      // Poll for completion
      let attempts = 0;
      const poll = async () => {
        attempts++;
        if (attempts > 60) return; // max ~15s
        await new Promise(r => setTimeout(r, 250));
        try {
          const sres = await fetch("/api/tts/status");
          if (sres.ok) {
            const sdata = await sres.json();
            if (!sdata.isSpeaking) {
              statusEl.textContent = t("ui.web.ready");
              statusEl.className = "status ok";
              return;
            }
          }
        } catch { /* ignore */ }
        await poll();
      };
      await poll();
    } else {
      statusEl.textContent = t("ui.web.status.failed", { message: apiMessage(data) });
      statusEl.className = "status err";
    }
  } catch (e) {
    statusEl.textContent = t("ui.web.status.error", { message: e.message });
    statusEl.className = "status err";
  }
}

async function ttsStop() {
  const statusEl = document.getElementById("tts-status");
  if (!statusEl) return;
  try {
    const res = await fetch("/api/tts/stop", { method: "POST" });
    const data = await res.json();
    if (data.success) {
      statusEl.textContent = t("ui.web.ready");
      statusEl.className = "status ok";
    } else {
      statusEl.textContent = t("ui.web.status.failed", { message: apiMessage(data) });
      statusEl.className = "status err";
    }
  } catch (e) {
    statusEl.textContent = t("ui.web.status.error", { message: e.message });
    statusEl.className = "status err";
  }
}

/* --- Pending Speech / Radio Chat --- */
function setPendingStatus(message, ok = true) {
  const el = document.getElementById("pending-status");
  if (!el) return;
  el.textContent = message;
  el.className = ok ? "status ok" : "status err";
}

function setRadioStatus(message, ok = true) {
  const el = document.getElementById("radio-status");
  if (!el) return;
  el.textContent = message;
  el.className = ok ? "status ok" : "status err";
}

let radioCurrentPage = 0;
let radioCommandMap = new Map();
let radioPlatform = "";
let pendingStatusCache = { mode: "instant", pendingCount: 0 };
let pendingModeKnown = false;
let pendingConfigGeneration = 0;
let pendingConfigSaves = 0;
let pendingConfigQueue = Promise.resolve();
let pendingStatusInterval = null;
let pendingStatusInFlight = false;

function radioDisabledText() {
  return t("ui.web.speaker.disabled") || "Disabled";
}

function updateRadioSpeakButtonLabel() {
  const btn = document.getElementById("radio-speak-btn");
  if (!btn) return;
  const usePending = pendingStatusCache.mode === "wait" && (pendingStatusCache.pendingCount || 0) > 0;
  btn.textContent = t(usePending ? "ui.web.pending.approve" : "ui.web.radio.speak");
}

function radioKey(page, digit) {
  return `p${page}.${digit}`;
}

function ensureRadioSlotsUi() {
  const host = document.getElementById("radio-slots");
  if (!host || host.childElementCount > 0) return;
  for (let d = 1; d <= 9; d++) {
    const label = document.createElement("div");
    label.textContent = `${d}:`;
    const input = document.createElement("input");
    input.type = "text";
    input.id = `radio-slot-${d}`;
    host.appendChild(label);
    host.appendChild(input);
  }
}

function updateRadioPageButtons() {
  [0, 1, 2].forEach((p) => {
    const btn = document.getElementById(`radio-page-${p}`);
    if (!btn) return;
    btn.style.background = p === radioCurrentPage ? "#687071" : "";
    btn.style.fontWeight = p === radioCurrentPage ? "700" : "400";
  });
}

function renderRadioPage() {
  ensureRadioSlotsUi();
  updateRadioPageButtons();
  for (let d = 1; d <= 9; d++) {
    const key = radioKey(radioCurrentPage, d);
    const input = document.getElementById(`radio-slot-${d}`);
    if (input) input.value = radioCommandMap.get(key) || "";
  }
}

function persistCurrentRadioPageInputs() {
  for (let d = 1; d <= 9; d++) {
    const key = radioKey(radioCurrentPage, d);
    const input = document.getElementById(`radio-slot-${d}`);
    if (!input) continue;
    radioCommandMap.set(key, input.value || "");
  }
}

function renderRadioCommandsText(commands = []) {
  return (commands || [])
    .map((x) => `${x.key || ""}=${x.text || ""}`)
    .join("\n");
}

function parseRadioCommandsText(text) {
  const lines = String(text || "").split(/\r?\n/);
  const commands = [];
  for (const raw of lines) {
    const line = raw.trim();
    if (!line || line.startsWith("#") || line.startsWith("//") || line.startsWith(";")) continue;
    const m = line.match(/^(p[0-2]\.[1-9])\s*=\s*(.*)$/i);
    if (!m) continue;
    commands.push({ key: m[1].toLowerCase(), text: m[2] || "" });
  }
  return commands;
}

function parseRadioKey(key) {
  const m = String(key || "").match(/^p([0-2])\.([1-9])$/i);
  if (!m) return null;
  return { page: Number(m[1]), digit: Number(m[2]) };
}

function updatePendingModeUi(mode) {
  pendingModeKnown = mode === "wait" || mode === "instant";
  const normalized = mode === "wait" ? "wait" : "instant";
  const modeEl = document.getElementById("pending-mode-select");
  if (modeEl) modeEl.value = normalized;
  document.getElementById("pending-mode-instant-btn")?.classList.toggle("active", normalized === "instant");
  document.getElementById("pending-mode-wait-btn")?.classList.toggle("active", normalized === "wait");
}

function updatePendingActionUi(status) {
  const row = document.getElementById("pending-action-row");
  const text = document.getElementById("pending-current-text");
  const radioRow = document.getElementById("radio-pending-action-row");
  const radioText = document.getElementById("radio-pending-current-text");
  const count = Number(status?.pendingCount) || 0;
  if (row) row.style.display = count > 0 ? "flex" : "none";
  if (radioRow) radioRow.style.display = count > 0 ? "flex" : "none";
  const first = Array.isArray(status?.items) ? status.items[0] : null;
  const remaining = Number(first?.remainingSeconds) || Number(status?.remainingSeconds) || 0;
  const label = first?.text ? `1: ${first.text}${remaining > 0 ? `  T-${remaining}s` : ""}` : "";
  if (text) {
    text.textContent = label;
  }
  if (radioText) {
    radioText.textContent = label;
  }
}

function startPendingStatusPoll() {
  if (pendingStatusInterval) return;
  pendingStatusInterval = setInterval(() => {
    if (pendingStatusInFlight) return;
    pendingStatusInFlight = true;
    fetchPendingSpeechStatus().finally(() => { pendingStatusInFlight = false; });
  }, 500);
}

async function setPendingMode(mode) {
  updatePendingModeUi(mode);
  await savePendingSpeechConfig();
}

async function fetchPendingSpeechConfig() {
  if (pendingConfigSaves) return;
  const generation = pendingConfigGeneration;
  try {
    const res = await fetch("/api/pending-speech/config");
    const data = await res.json().catch(() => ({}));
    if (generation !== pendingConfigGeneration || pendingConfigSaves) return;
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    if (data?.mode !== "wait" && data?.mode !== "instant") throw new Error(t("ui.web.status.unknown"));
    const modeEl = document.getElementById("pending-mode-select");
    const secEl = document.getElementById("pending-wait-seconds");
    if (modeEl) modeEl.value = data.mode === "wait" ? "wait" : "instant";
    if (secEl) {
      const waitValue = String(Math.max(1, Number(data.waitSeconds) || 3));
      if (![...secEl.options].some((opt) => opt.value === waitValue)) {
        secEl.appendChild(new Option(`${waitValue} sec`, waitValue));
      }
      secEl.value = waitValue;
    }
    updatePendingModeUi(data.mode);
    await fetchPendingSpeechStatus();
  } catch (e) {
    if (generation !== pendingConfigGeneration || pendingConfigSaves) return;
    setPendingStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

async function fetchPendingSpeechStatus() {
  if (pendingConfigSaves) return;
  const generation = pendingConfigGeneration;
  try {
    const res = await fetch("/api/pending-speech/status");
    const data = await res.json().catch(() => ({}));
    if (generation !== pendingConfigGeneration || pendingConfigSaves) return;
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    if (data?.mode !== "wait" && data?.mode !== "instant") throw new Error(t("ui.web.status.unknown"));
    pendingStatusCache = {
      mode: data.mode === "wait" ? "wait" : "instant",
      pendingCount: Number(data.pendingCount) || 0,
      waitSeconds: Number(data.waitSeconds) || 0,
      remainingSeconds: Number(data.remainingSeconds) || 0,
      items: Array.isArray(data.items) ? data.items : []
    };
    updateRadioSpeakButtonLabel();
    updatePendingModeUi(pendingStatusCache.mode);
    updatePendingActionUi(pendingStatusCache);
    setPendingStatus(t("ui.web.pending.status", { mode: data.mode || "-", count: data.pendingCount ?? 0, wait: data.waitSeconds ?? "-" }));
  } catch (e) {
    // An observation failure is not an instruction to change the saved mode.
    if (generation !== pendingConfigGeneration || pendingConfigSaves) return;
    setPendingStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

async function savePendingSpeechConfig() {
  const mode = document.getElementById("pending-mode-select")?.value === "wait" ? "wait" : "instant";
  const waitSeconds = Math.max(1, Math.min(30, Number(document.getElementById("pending-wait-seconds")?.value) || 3));
  // The initial HTML default is not permission to replace the saved mode.
  // Both hosts preserve it when a duration-only update omits mode.
  const body = { waitSeconds };
  if (pendingModeKnown) body.mode = mode;
  // Capture each explicit intent now, and keep old GETs and overlapping POSTs
  // from turning a later duration edit into an unintended mode change.
  const generation = ++pendingConfigGeneration;
  pendingConfigSaves++;
  const save = async () => {
    try {
      const res = await fetch("/api/pending-speech/config", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
      if (generation === pendingConfigGeneration) setPendingStatus(t("ui.web.pending.saved"));
    } catch (e) {
      if (generation === pendingConfigGeneration) setPendingStatus(t("ui.web.status.error", { message: e.message }), false);
    } finally {
      pendingConfigSaves--;
    }
  };
  const saved = pendingConfigQueue.then(save, save);
  pendingConfigQueue = saved;
  await saved;
  if (generation === pendingConfigGeneration) await fetchPendingSpeechStatus();
}

async function runPendingAction(action) {
  try {
    const res = await fetch(`/api/pending-speech/${action}`, { method: "POST" });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    setPendingStatus(t("ui.web.pending.actionDone", { action }));
    await fetchPendingSpeechStatus();
  } catch (e) {
    setPendingStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

async function fetchRadioConfig() {
  try {
    const res = await fetch("/api/radio/config");
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    const shortcut = document.getElementById("radio-shortcut-display");
    if (shortcut) shortcut.value = data.enabled === false ? radioDisabledText() : (data.shortcut?.display || "Ctrl+Shift+F4");
    setValueIfExists("radio-overlay-position", data.overlay?.position || "top_left");
    setValueIfExists("radio-overlay-bg", data.overlay?.background || "#1D6F5A");
    setValueIfExists("radio-overlay-fg", data.overlay?.foreground || "#FFFFFF");
    setValueIfExists("radio-overlay-font-size", String(data.overlay?.fontSize || 16));
    setValueIfExists("radio-overlay-opacity", String(Math.round((Number(data.overlay?.opacity) || 0.78) * 100)));
  } catch (e) {
    setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

function setValueIfExists(id, value) {
  const el = document.getElementById(id);
  if (el) el.value = value;
}

async function saveRadioOverlayConfig() {
  const overlay = {
    position: document.getElementById("radio-overlay-position")?.value || "top_left",
    background: document.getElementById("radio-overlay-bg")?.value || "#1D6F5A",
    foreground: document.getElementById("radio-overlay-fg")?.value || "#FFFFFF",
    fontSize: Math.max(10, Math.min(32, Number(document.getElementById("radio-overlay-font-size")?.value) || 16)),
    opacity: Math.max(0.2, Math.min(1, (Number(document.getElementById("radio-overlay-opacity")?.value) || 78) / 100))
  };
  try {
    const res = await fetch("/api/radio/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ overlay }) });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    setRadioStatus(t("ui.web.radio.saved"));
  } catch (e) {
    setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

async function fetchRadioCommands() {
  try {
    const res = await fetch("/api/radio/commands");
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    radioCommandMap = new Map();
    for (const cmd of (data.commands || [])) {
      radioCommandMap.set((cmd.key || "").toLowerCase(), cmd.text || "");
    }
    renderRadioPage();
    await fetchRadioStatus();
  } catch (e) {
    setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

async function fetchRadioStatus() {
  try {
    const res = await fetch("/api/radio/status");
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    setRadioStatus(t("ui.web.radio.status", { count: data.commandCount ?? 0, hotkey: data.hotkeyRegistered ? "on" : "off" }));
    const hint = document.getElementById("radio-linux-hint");
    const cmdEl = document.getElementById("radio-linux-cmd");
    if (hint) {
      const isLinux = data.platform === "linux";
      radioPlatform = data.platform || "";
      hint.style.display = isLinux ? "" : "none";
      const shortcut = document.getElementById("radio-shortcut-display");
      if (shortcut) shortcut.readOnly = !isLinux;
      if (isLinux && cmdEl && data.triggerCmd) cmdEl.textContent = data.triggerCmd;
    }
  } catch (e) {
    setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

function setupRadioLinuxCopyBtn() {
  document.getElementById("radio-linux-copy-btn")?.addEventListener("click", () => {
    const cmd = document.getElementById("radio-linux-cmd")?.textContent || "";
    if (!cmd) return;
    navigator.clipboard?.writeText(cmd).catch(() => {});
  });
}

async function saveRadioConfigAndCommands() {
  persistCurrentRadioPageInputs();
  const commands = [];
  for (let p = 0; p <= 2; p++) {
    for (let d = 1; d <= 9; d++) {
      commands.push({ key: radioKey(p, d), text: radioCommandMap.get(radioKey(p, d)) || "" });
    }
  }
  try {
    for (const cmd of commands) {
      const parsed = parseRadioKey(cmd.key);
      if (!parsed) continue;
      const cmdRes = await fetch("/api/radio/commands", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ page: parsed.page, digit: parsed.digit, text: cmd.text || "" })
      });
      const cmdData = await cmdRes.json().catch(() => ({}));
      if (!cmdRes.ok) throw new Error(apiMessage(cmdData, `HTTP ${cmdRes.status}`));
    }

    setRadioStatus(t("ui.web.radio.saved"));
    await fetchRadioStatus();
  } catch (e) {
    setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

async function radioPreview() {
  try {
    const res = await fetch("/api/radio/preview", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ show: true, durationMs: 2500 }) });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    setRadioStatus(t("ui.web.radio.previewDone"));
  } catch (e) {
    setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

async function radioSpeakFirst() {
  persistCurrentRadioPageInputs();
  const usePendingPage = pendingStatusCache.mode === "wait" && (pendingStatusCache.pendingCount || 0) > 0;
  if (usePendingPage) {
    try {
      const res = await fetch("/api/radio/speak", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ page: -1, digit: 1 }) });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
      setRadioStatus(t("ui.web.radio.spoken", { key: "pending.1" }));
      await fetchPendingSpeechStatus();
      return;
    } catch (e) {
      setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
      return;
    }
  }

  const text = (radioCommandMap.get(radioKey(radioCurrentPage, 1)) || "").trim();
  if (!text) {
    setRadioStatus(t("ui.web.radio.noCommand"), false);
    return;
  }
  try {
    const res = await fetch("/api/radio/speak", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ page: radioCurrentPage, digit: 1 }) });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
    setRadioStatus(t("ui.web.radio.spoken", { key: radioKey(radioCurrentPage, 1) }));
  } catch (e) {
    setRadioStatus(t("ui.web.status.error", { message: e.message }), false);
  }
}

function radioSetPage(page) {
  persistCurrentRadioPageInputs();
  radioCurrentPage = Math.max(0, Math.min(2, Number(page) || 0));
  renderRadioPage();
}

function beginRadioShortcutCapture() {
  if (radioPlatform === "linux") {
    const display = (document.getElementById("radio-shortcut-display")?.value || "Ctrl+Shift+F4").trim();
    if (!window.confirm(t("ui.web.radio.linuxApplyConfirm", { shortcut: display }))) return;
    fetch("/api/radio/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ shortcut: { display } })
    })
      .then(async (res) => {
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
        return fetch("/api/radio/capture-shortcut", { method: "POST" });
      })
      .then(async (res) => {
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
        setRadioStatus(t("ui.web.radio.captureHint"));
        setTimeout(fetchRadioStatus, 1500);
      })
      .catch((err) => setRadioStatus(t("ui.web.status.error", { message: err.message }), false));
    return;
  }
  const status = document.getElementById("radio-status");
  if (status) {
    status.textContent = t("ui.web.radio.captureHint");
    status.className = "status";
  }
  document.getElementById("radio-capture-dialog")?.remove();
  const dialog = document.createElement("div");
  dialog.id = "radio-capture-dialog";
  dialog.style.cssText = "position:fixed;inset:0;z-index:99999;background:rgba(0,0,0,.45);display:flex;align-items:center;justify-content:center;";
  dialog.innerHTML = `<div style="min-width:320px;max-width:520px;background:#202b33;color:#eef;padding:18px;border:1px solid #50606a;box-shadow:0 8px 28px rgba(0,0,0,.45);font:14px system-ui,sans-serif;">
    <div style="font-weight:700;margin-bottom:10px;">Radio Chat Shortcut</div>
    <div style="margin-bottom:12px;line-height:1.5;">${escapeHtml(t("ui.web.radio.captureHint"))}<br><span style="color:#aab;">Esc: cancel</span></div>
    <input id="radio-capture-field" type="text" readonly style="width:100%;box-sizing:border-box;text-align:center;font-size:18px;padding:8px;background:#e8eef8;color:#111;border:0;" />
    <div style="display:flex;justify-content:center;margin-top:12px;"><button id="radio-disable-shortcut-btn" type="button">${escapeHtml(radioDisabledText())}</button></div>
  </div>`;
  document.body.appendChild(dialog);
  document.getElementById("radio-capture-field")?.focus();
  const close = () => {
    window.removeEventListener("keydown", onKey, true);
    dialog.remove();
  };
  const onKey = (e) => {
    e.preventDefault();
    e.stopPropagation();
    if (e.key === "Escape") {
      close();
      return;
    }
    const parts = [];
    if (e.ctrlKey) parts.push("Ctrl");
    if (e.altKey) parts.push("Alt");
    if (e.shiftKey) parts.push("Shift");
    const key = normalizeRadioShortcutKey(e);
    if (!key || ["Control", "Shift", "Alt", "Meta"].includes(key)) return;
    parts.push(key);
    const display = parts.join("+") || "Ctrl+Shift+F4";
    const input = document.getElementById("radio-shortcut-display");
    if (input) input.value = display;
    const captured = parseRadioShortcutDisplay(display);
    fetch("/api/radio/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: true, shortcut: captured }) })
      .then(async (res) => {
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
        setRadioStatus(t("ui.web.radio.captureDone", { key: display }));
        fetchRadioStatus();
      })
      .catch((err) => setRadioStatus(t("ui.web.status.error", { message: err.message }), false));
    close();
  };
  window.addEventListener("keydown", onKey, true);
  document.getElementById("radio-disable-shortcut-btn")?.addEventListener("click", () => {
    const input = document.getElementById("radio-shortcut-display");
    if (input) input.value = radioDisabledText();
    fetch("/api/radio/config", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ enabled: false }) })
      .then(async (res) => {
        const data = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(apiMessage(data, `HTTP ${res.status}`));
        setRadioStatus(t("ui.web.radio.saved"));
        fetchRadioStatus();
      })
      .catch((err) => setRadioStatus(t("ui.web.status.error", { message: err.message }), false));
    close();
  });
}

function normalizeRadioShortcutKey(e) {
  const key = e.key || "";
  if (/^F([1-9]|1[0-9]|2[0-4])$/i.test(key)) return key.toUpperCase();
  if (key.length === 1 && /^[a-z0-9]$/i.test(key)) return key.toUpperCase();
  const byCode = { ScrollLock: "ScrollLock", Pause: "Pause", Numpad0: "0", Numpad1: "1", Numpad2: "2", Numpad3: "3", Numpad4: "4", Numpad5: "5", Numpad6: "6", Numpad7: "7", Numpad8: "8", Numpad9: "9" };
  return byCode[e.code] || ({ ScrollLock: "ScrollLock", Pause: "Pause" }[key] || key);
}

function parseRadioShortcutDisplay(display) {
  const parts = String(display || "Ctrl+Shift+F4").split("+").map(p => p.trim()).filter(Boolean);
  const mods = [];
  let key = "F4";
  for (const p of parts) {
    const lower = p.toLowerCase();
    if (["ctrl", "control"].includes(lower)) mods.push("ctrl");
    else if (lower === "shift") mods.push("shift");
    else if (lower === "alt") mods.push("alt");
    else key = p;
  }
  const displayText = [...mods.map(m => m === "ctrl" ? "Ctrl" : m === "shift" ? "Shift" : "Alt"), key].join("+");
  return { display: displayText, modifier: mods.join("+"), key };
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>'\"]/g, ch => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[ch]));
}

/* --- Text Filter / NG / Laugh --- */
function initUiLanguageSelect() {
  const select = document.getElementById("ui-language-select");
  if (!select) return;
  const saved = localStorage.getItem("mobmate.uiLanguage");
  if (saved) select.value = normalizeUiLanguage(saved);
  const lang = normalizeUiLanguage(select.value);
  document.documentElement.lang = lang.startsWith("zh") ? "zh" : lang;
}

async function setUiLanguage(value) {
  const lang = normalizeUiLanguage(value);
  localStorage.setItem("mobmate.uiLanguage", lang);
  document.documentElement.lang = lang.startsWith("zh") ? "zh" : lang;
  await loadI18n(lang);
  applyI18n();
  refreshAddonAsr();
  const rtLang = document.getElementById("rt-language-select");
  if (rtLang && !localStorage.getItem("mobmate.realtimeLanguage")) updateRealtimeLanguageOptions(uiLanguageToRealtimeLanguage(lang));
  await saveTextFilterConfig({ uiLanguage: lang });
  fetchAudioInputDevices();
  fetchTtsOutputDevices();
}

function setTextFilterStatus(message, ok = true) {
  const el = document.getElementById("text-filter-status");
  if (!el) return;
  el.textContent = message;
  el.className = ok ? "status ok" : "status err";
}

function mergeLaughTokens(manualTokens, autoTokens) {
  const seen = new Set();
  return [manualTokens || "", autoTokens || ""]
    .join(",")
    .split(/[、,]/)
    .map(s => s.trim())
    .filter(s => {
      if (!s) return false;
      const key = s.toLowerCase();
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    })
    .join(",");
}

function applyTextFilterConfig(data) {
  const uiLang = document.getElementById("ui-language-select");
  if (uiLang && data.uiLanguage) {
    const lang = normalizeUiLanguage(localStorage.getItem("mobmate.uiLanguage") || data.uiLanguage);
    uiLang.value = lang;
    if (uiSettings.isReady()) localStorage.setItem("mobmate.uiLanguage", lang);
  }
  const ngMode = document.getElementById("text-filter-ng-mode");
  if (ngMode && data.ngMode) ngMode.value = data.ngMode;
  const preset = document.getElementById("laugh-preset-select");
  if (preset && data.laughPreset) preset.value = data.laughPreset;
  const enabled = document.getElementById("laugh-replacement-enabled");
  if (enabled) enabled.checked = data.laughReplacementEnabled !== false;
  const laughTokens = document.getElementById("laugh-tokens");
  if (laughTokens) laughTokens.value = mergeLaughTokens(data.manualLaughTokens, data.autoLaughTokens);
  const repl = document.getElementById("laugh-replacement-words");
  if (repl) repl.value = data.laughReplacementWords || "";
  setTextFilterStatus(t("ui.web.textFilter.status", { ng: data.ngRuleCount ?? 0, laugh: data.replacementCount ?? 0 }));
}

async function fetchTextFilterConfig() {
  try {
    const res = await fetch("/api/text-filter/config");
    if (!res.ok) return;
    const data = await res.json();
    const savedLang = localStorage.getItem("mobmate.uiLanguage");
    const lang = normalizeUiLanguage(savedLang || data.uiLanguage);
    const uiLang = document.getElementById("ui-language-select");
    if (uiLang) uiLang.value = lang;
    if (uiSettings.isReady()) localStorage.setItem("mobmate.uiLanguage", lang);
    document.documentElement.lang = lang.startsWith("zh") ? "zh" : lang;
    await loadI18n(lang);
    applyI18n();
    applyTextFilterConfig(data);
    if (uiSettings.isReady() && savedLang && savedLang !== data.uiLanguage) await saveTextFilterConfig({ uiLanguage: normalizeUiLanguage(savedLang) });
  } catch (e) {
    setTextFilterStatus(t("ui.web.status.configError", { message: e.message }), false);
  }
}

async function saveTextFilterConfig(extra = {}) {
  const presetValue = extra.laughPreset || document.getElementById("laugh-preset-select")?.value || "auto";
  const body = {
    uiLanguage: normalizeUiLanguage(document.getElementById("ui-language-select")?.value || localStorage.getItem("mobmate.uiLanguage")),
    ngMode: document.getElementById("text-filter-ng-mode")?.value || "simple",
    laughPreset: presetValue,
    laughReplacementEnabled: document.getElementById("laugh-replacement-enabled")?.checked !== false,
    ...extra,
  };
  if (presetValue === "custom") {
    body.manualLaughTokens = document.getElementById("laugh-tokens")?.value || "";
    body.autoLaughTokens = "";
    body.laughReplacementWords = document.getElementById("laugh-replacement-words")?.value || "";
  }
  try {
    const res = await fetch("/api/text-filter/config", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) { setTextFilterStatus(t("ui.web.status.configFailed", { message: apiMessage(data, res.status) }), false); return; }
    applyTextFilterConfig(data);
  } catch (e) {
    setTextFilterStatus(t("ui.web.status.configError", { message: e.message }), false);
  }
}

async function fetchNgWords() {
  const area = document.getElementById("text-filter-ng-words");
  if (!area) return;
  try {
    const res = await fetch("/api/text-filter/ng-words");
    const data = await res.json();
    if (res.ok) {
      area.value = data.text || "";
      setTextFilterStatus(t("ui.web.textFilter.loaded", { count: data.count ?? 0 }));
    }
  } catch (e) {
    setTextFilterStatus(t("ui.web.textFilter.loadError", { message: e.message }), false);
  }
}

async function saveNgWords() {
  const text = document.getElementById("text-filter-ng-words")?.value || "";
  try {
    const res = await fetch("/api/text-filter/ng-words", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text }),
    });
    const data = await res.json().catch(() => ({}));
    setTextFilterStatus(res.ok ? t("ui.web.textFilter.saved", { count: data.count ?? 0 }) : t("ui.web.textFilter.saveFailed", { message: apiMessage(data, res.status) }), res.ok);
  } catch (e) {
    setTextFilterStatus(t("ui.web.textFilter.saveError", { message: e.message }), false);
  }
}

async function loadNgPreset() {
  if (!confirm(t("ui.web.textFilter.confirmLoadPreset"))) return;
  const language = normalizeUiLanguage(document.getElementById("ui-language-select")?.value || "en");
  try {
    const res = await fetch("/api/text-filter/ng-words", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ loadPreset: true, language }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) { setTextFilterStatus(t("ui.web.textFilter.presetFailed", { message: apiMessage(data, res.status) }), false); return; }
    await fetchNgWords();
  } catch (e) {
    setTextFilterStatus(t("ui.web.textFilter.presetError", { message: e.message }), false);
  }
}

/* --- Init --- */
// ===================================================
// HEARING
// ===================================================
let hearingPollingTimer = null;
let hearingRunning = false;
let hearingLastUpdateMs = 0;

async function initHearingUi() {
  try {
    const langs = await fetch('/api/hearing/languages').then(r => r.json());

    const langSel = document.getElementById('hearing-lang');
    if (langSel && langs.asrLangs) {
      langs.asrLangs.forEach(({ code, label }) => {
        const opt = document.createElement('option');
        opt.value = code; opt.textContent = label;
        langSel.appendChild(opt);
      });
    }

    const transSel = document.getElementById('hearing-translate-to');
    if (transSel && langs.translationLangs) {
      langs.translationLangs.forEach(({ code, label }) => {
        const opt = document.createElement('option');
        opt.value = code; opt.textContent = label;
        transSel.appendChild(opt);
      });
    }
  } catch (e) {
    console.warn('[Hearing] languages load failed', e);
  }

  await loadHearingInputDevices();

  try {
    const cfg = await fetch('/api/hearing/config').then(r => r.json());
    const engineEl = document.getElementById('hearing-engine');
    if (engineEl) engineEl.value = cfg.engine ?? 'joint_auto';
    const transEl = document.getElementById('hearing-translate-to');
    if (transEl) transEl.value = cfg.translateTo ?? 'OFF';
    const langEl = document.getElementById('hearing-lang');
    if (langEl) langEl.value = cfg.lang ?? 'auto';
    const modeEl = document.getElementById('hearing-audio-mode');
    if (modeEl) modeEl.value = cfg.audioMode ?? 'loopback';
    const deviceEl = document.getElementById('hearing-input-device');
    if (deviceEl && cfg.micDeviceHint) deviceEl.value = cfg.micDeviceHint;
    setSelectValue('hearing-overlay-font', cfg.overlayFontSize ?? 36);
    setSelectValue('hearing-overlay-bg', cfg.overlayBackground ?? 'blue');
    setSelectValue('hearing-overlay-opacity', cfg.overlayOpacity ?? 72);
    setSelectValue('hearing-overlay-history', cfg.overlayHistory ?? 6);
    setSelectValue('hearing-overlay-done-keep', cfg.overlayDoneKeepSeconds ?? 12);
    setSelectValue('hearing-overlay-flow', normalizeHearingFlow(cfg.overlayFlow));
  } catch (e) {}

  document.getElementById('hearing-toggle')?.addEventListener('click', toggleHearing);
  initHearingObsRow();
  ['hearing-engine','hearing-translate-to','hearing-lang',
   'hearing-audio-mode','hearing-input-device','hearing-overlay-font','hearing-overlay-bg',
   'hearing-overlay-opacity','hearing-overlay-history','hearing-overlay-done-keep',
   'hearing-overlay-flow'].forEach(id => {
    document.getElementById(id)?.addEventListener('change', saveHearingConfig);
  });

  fetchHearingStatus();
}

async function loadHearingInputDevices() {
  const sel = document.getElementById('hearing-input-device');
  if (!sel) return;
  sel.innerHTML = '';
  sel.appendChild(new Option('Default', ''));
  try {
    const res = await fetch('/api/hearing/input-devices');
    const data = await res.json().catch(() => ({}));
    const devices = Array.isArray(data.devices) ? data.devices : [];
    devices.forEach((d) => {
      const id = d.id || d.name || '';
      if (!id) return;
      const label = d.name && d.name !== id ? `${d.name} (${id})` : id;
      sel.appendChild(new Option(label, id));
    });
  } catch (e) {
    console.warn('[Hearing] input devices load failed', e);
  }
}

function setSelectValue(id, value) {
  const el = document.getElementById(id);
  if (el) el.value = String(value);
}

function normalizeHearingFlow(value) {
  return value === 'horizontalLeft' ? 'horizontalLeft' : 'horizontalRight';
}

function saveHearingConfig() {
  const cfg = {
    engine:      document.getElementById('hearing-engine')?.value ?? 'joint_auto',
    translateTo: document.getElementById('hearing-translate-to')?.value ?? 'OFF',
    lang:        document.getElementById('hearing-lang')?.value ?? 'auto',
    audioMode:   document.getElementById('hearing-audio-mode')?.value ?? 'loopback',
    micDeviceHint: document.getElementById('hearing-input-device')?.value ?? '',
    overlayFontSize:        parseInt(document.getElementById('hearing-overlay-font')?.value ?? '36', 10),
    overlayBackground:      document.getElementById('hearing-overlay-bg')?.value ?? 'blue',
    overlayOpacity:         parseInt(document.getElementById('hearing-overlay-opacity')?.value ?? '72', 10),
    overlayHistory:         parseInt(document.getElementById('hearing-overlay-history')?.value ?? '6', 10),
    overlayDoneKeepSeconds: parseInt(document.getElementById('hearing-overlay-done-keep')?.value ?? '12', 10),
    overlayFlow:            normalizeHearingFlow(document.getElementById('hearing-overlay-flow')?.value),
  };
  fetch('/api/hearing/config', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cfg),
  });
}

function toggleHearing() {
  if (hearingRunning) {
    fetch('/api/hearing/stop', { method: 'POST' })
      .then(() => fetch('/api/hearing/subtitle-close', { method: 'POST' }))
      .then(fetchHearingStatus)
      .catch(() => fetchHearingStatus());
  } else {
    saveHearingConfig();
    fetch('/api/hearing/start', { method: 'POST' })
      .then(r => {
        if (!r.ok) return r.text().then(t => { throw new Error(t); });
        return r;
      })
      .then(() => fetch('/api/hearing/subtitle-open', { method: 'POST' }).catch(() => {}))
      .then(fetchHearingStatus)
      .catch(err => alert('Hearing start failed: ' + err.message));
  }
}

function fetchHearingStatus() {
  fetch('/api/hearing/status').then(r => r.json()).then(st => {
    hearingRunning = st.running;
    const btn   = document.getElementById('hearing-toggle');
    const badge = document.getElementById('hearing-status-badge');

    if (st.running) {
      if (btn) { btn.textContent = t('hearing.btn.stop'); btn.classList.add('btn-active'); }
      if (badge) { badge.textContent = t('hearing.status.on'); badge.className = 'status-badge status-on'; }
      if (!hearingPollingTimer) hearingPollingTimer = setInterval(fetchHearingStatus, 700);
    } else {
      if (btn) { btn.textContent = t('hearing.btn.start'); btn.classList.remove('btn-active'); }
      if (badge) { badge.textContent = t('hearing.status.off'); badge.className = 'status-badge status-off'; }
      if (hearingPollingTimer) { clearInterval(hearingPollingTimer); hearingPollingTimer = null; }
    }

    if (st.updatedAtMs > hearingLastUpdateMs) {
      hearingLastUpdateMs = st.updatedAtMs;
      updateHearingLastText(st.sourceText, st.translatedText);
    }
  }).catch(() => {});
}

function updateHearingLastText(source, translated) {
  const wrap  = document.getElementById('hearing-last-text');
  const srcEl = document.getElementById('hearing-last-source');
  const trEl  = document.getElementById('hearing-last-translated');
  if (!source || !wrap) return;
  wrap.style.display = '';
  if (srcEl) srcEl.textContent  = source;
  if (trEl) { trEl.textContent = translated || ''; trEl.style.display = translated ? '' : 'none'; }
}

function initHearingObsRow() {
  const urlEl = document.getElementById('hearing-obs-url');
  const copyBtn = document.getElementById('hearing-obs-copy');
  if (!urlEl || !copyBtn) return;
  const layoutEl = document.getElementById('hearing-obs-layout');
  const maxEl = document.getElementById('hearing-obs-max');
  const widthEl = document.getElementById('hearing-obs-width');
  if (layoutEl) layoutEl.value = localStorage.getItem('hearing.obs.layout') || 'stack';
  if (maxEl) maxEl.value = localStorage.getItem('hearing.obs.max') || '2';
  if (widthEl) widthEl.value = localStorage.getItem('hearing.obs.width') || '460px';

  const buildObsUrl = () => {
    const params = new URLSearchParams();
    params.set('mode', 'obs');
    params.set('layout', layoutEl?.value || 'stack');
    params.set('max', layoutEl?.value === 'latest' ? '1' : (maxEl?.value || '2'));
    params.set('maxw', widthEl?.value || '460px');
    return location.origin + '/subtitle.html?' + params.toString();
  };
  const refreshObsUrl = () => { urlEl.value = buildObsUrl(); };
  const saveObsUi = () => {
    if (layoutEl) localStorage.setItem('hearing.obs.layout', layoutEl.value);
    if (maxEl) localStorage.setItem('hearing.obs.max', maxEl.value);
    if (widthEl) localStorage.setItem('hearing.obs.width', widthEl.value);
    refreshObsUrl();
  };
  [layoutEl, maxEl, widthEl].forEach(el => el?.addEventListener('change', saveObsUi));
  refreshObsUrl();
  copyBtn.addEventListener('click', async () => {
    const obsUrl = buildObsUrl();
    try {
      await navigator.clipboard.writeText(obsUrl);
    } catch (e) {
      urlEl.select();
      document.execCommand('copy');
    }
    copyBtn.textContent = t('hearing.obs.copied');
    setTimeout(() => { copyBtn.textContent = t('hearing.obs.copy'); }, 1500);
  });
}


// ===================================================
// VRCHAT OSC
// ===================================================
let vrchatOscMinIntervalMs = 1500;

async function initVrchatOscUi() {
  try {
    const cfg = await fetch('/api/vrchat-osc/config').then(r => r.json());
    vrchatOscMinIntervalMs = cfg.minIntervalMs ?? 1500;
    const enabledEl = document.getElementById('vrchat-osc-enabled');
    if (enabledEl) enabledEl.checked = cfg.enabled === true;
    const hostEl = document.getElementById('vrchat-osc-host');
    if (hostEl) hostEl.value = cfg.host ?? '127.0.0.1';
    const portEl = document.getElementById('vrchat-osc-port');
    if (portEl) portEl.value = String(cfg.port ?? 9000);
    const notifyEl = document.getElementById('vrchat-osc-notify');
    if (notifyEl) notifyEl.checked = cfg.playNotificationSound === true;
  } catch (e) {
    console.warn('[VrchatOsc] config load failed', e);
  }
  ['vrchat-osc-enabled', 'vrchat-osc-host', 'vrchat-osc-port',
   'vrchat-osc-notify'].forEach(id => {
    document.getElementById(id)?.addEventListener('change', saveVrchatOscConfig);
  });
  document.getElementById('vrchat-osc-test')?.addEventListener('click', sendVrchatOscTest);
}

function getVrchatOscConfigFromUi() {
  return {
    enabled: document.getElementById('vrchat-osc-enabled')?.checked === true,
    host: document.getElementById('vrchat-osc-host')?.value || '127.0.0.1',
    port: parseInt(document.getElementById('vrchat-osc-port')?.value ?? '9000', 10) || 9000,
    playNotificationSound: document.getElementById('vrchat-osc-notify')?.checked === true,
    sendTranslated: true,
    minIntervalMs: vrchatOscMinIntervalMs,
  };
}

function saveVrchatOscConfig() {
  return fetch('/api/vrchat-osc/config', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(getVrchatOscConfigFromUi()),
  });
}

async function sendVrchatOscTest() {
  const btn = document.getElementById('vrchat-osc-test');
  const statusEl = document.getElementById('vrchat-osc-test-status');
  if (btn) btn.disabled = true;
  if (statusEl) statusEl.textContent = t('vrchat.test.sending');
  try {
    await saveVrchatOscConfig();
    const result = await fetch('/api/vrchat-osc/test', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({}),
    }).then(r => r.json());
    if (statusEl) {
      statusEl.textContent = result.ok
        ? t('vrchat.test.sent').replace('{host}', result.host).replace('{port}', result.port)
        : t('vrchat.test.failed');
    }
  } catch (e) {
    console.warn('[VrchatOsc] test failed', e);
    if (statusEl) statusEl.textContent = t('vrchat.test.failed');
  } finally {
    if (btn) btn.disabled = false;
  }
}


document.addEventListener("DOMContentLoaded", async () => {
  await uiSettings.initialize();
  currentMode = localStorage.getItem("mobmate.mode") || "stt";
  const wavOutputEnabled = document.getElementById("wav-output-enabled");
  if (wavOutputEnabled) wavOutputEnabled.checked = localStorage.getItem("mobmate.wavOutputEnabled") === "true";
  document.addEventListener("contextmenu", (e) => e.preventDefault());
  initAppChrome();
  AsrAcceptanceCue.init();
  initUiLanguageSelect();
  initAddonAsrUi();
  await fetchTextFilterConfig();
  const piperVoiceDlReady = initPiperVoiceDl();
  const kokoroUiReady = initKokoroUi();
  const mossUiReady = initMossUi().catch(e => console.warn("initMossUi failed", e));
  await fetchConfig();
  await fetchReleaseStatus();
  initDemoSessionUi();
  await fetchWhisperModels();
  await fetchRealtimeLanguages();
  initReviewPromptUi();
  await fetchAudioInputDevices();
  ensureAutoSceneOption();
  restoreRealtimeSettings();
  setRecordingButtons(false);
  document.getElementById("mode-stt-tab")?.addEventListener("click", () => switchMode("stt"));
  document.getElementById("mode-mobfy-tab")?.addEventListener("click", () => switchMode("mobfy"));
  switchMode(currentMode, { skipExclusion: true });
  document.getElementById("rt-device-refresh-btn").addEventListener("click", fetchAudioInputDevices);
  document.getElementById("rt-device-select").addEventListener("change", () => { captureInputSelectionChanged(); void OnsetFillerUi.inputChanged(); });
  document.getElementById("rt-start-btn").addEventListener("click", startMainMode);
  document.getElementById("rt-stop-btn").addEventListener("click", stopMainMode);
  // Observe an existing recording only. A persisted UI flag is not permission
  // to start capture when this host is idle, unavailable or unknown.
  await syncStartupRecordingState();
  document.getElementById("bench-start-btn")?.addEventListener("click", startBenchRealtimeRoute);
     document.getElementById("tts-speak-btn")?.addEventListener("click", () => ttsSpeak());
     initTextSpeech();
  document.getElementById("tts-stop-btn")?.addEventListener("click", () => { ttsStop(); fetchTtsStatus(); });
  document.getElementById("tts-output-device-select")?.addEventListener("change", (e) => setTtsOutputDevice(e.target.value));
  document.getElementById("tts-monitor-volume-select")?.addEventListener("change", (e) => setMonitorVolume(e.target.value));
  document.getElementById("wav-output-enabled")?.addEventListener("change", (e) => setWavOutputEnabled(e.target.checked));
  document.getElementById("virtual-audio-help-btn")?.addEventListener("click", (e) => {
    e.stopPropagation();
    const popover = document.getElementById("virtual-audio-popover");
    setVirtualAudioPopoverOpen(!popover?.classList.contains("open"));
  });
  document.getElementById("virtual-audio-close-btn")?.addEventListener("click", () => setVirtualAudioPopoverOpen(false));
  document.getElementById("virtual-audio-install-btn")?.addEventListener("click", installVirtualAudio);
  document.getElementById("virtual-audio-official-btn")?.addEventListener("click", () => window.open("https://vb-audio.com/Cable/", "_blank", "noopener"));
  document.addEventListener("click", (e) => {
    const popover = document.getElementById("virtual-audio-popover");
    const button = document.getElementById("virtual-audio-help-btn");
    if (popover?.classList.contains("open") && !popover.contains(e.target) && !button?.contains(e.target)) setVirtualAudioPopoverOpen(false);
  });
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") setVirtualAudioPopoverOpen(false); });
  document.getElementById("tts-engine-select")?.addEventListener("change", (e) => {
    localStorage.setItem("mobmate.ttsEngine", e.target.value);
    populatePiperModelSelect();
    updateVoicevoxSpeakerRow();
    setTtsEngineConfig();
  });
  document.getElementById("tts-voicevox-speaker-select")?.addEventListener("change", (e) => {
    localStorage.setItem("mobmate.voicevoxSpeaker", e.target.value);
    setTtsEngineConfig();
  });
  document.getElementById("tts-piper-model-select")?.addEventListener("change", (e) => {
    localStorage.setItem("mobmate.piperPlusModel", e.target.value);
    setTtsEngineConfig();
  });
  document.getElementById("ui-language-select")?.addEventListener("change", (e) => setUiLanguage(e.target.value));
  document.getElementById("cfg-defaultModelPath")?.addEventListener("change", (e) => setWhisperModel(e.target.value));
  document.getElementById("whisper-turbo-download")?.addEventListener("click", downloadWhisperTurbo);
  document.getElementById("whisper-turbo-cancel")?.addEventListener("click", cancelWhisperModelRequest);
  document.getElementById("cfg-voicevoxExePath-browse")?.addEventListener("click", browseVoicevoxPath);
  document.getElementById("cfg-voicevoxExePath-save")?.addEventListener("click", saveVoicevoxPath);
  document.getElementById("rt-engine-select")?.addEventListener("change", (e) => {
    localStorage.setItem("mobmate.realtimeEngine", normalizeRealtimeEngine(e.target.value));
    localStorage.setItem("mobmate.useLanguageProfile", "false");
    updateRealtimeLanguageOptions();
    void OnsetFillerUi.prepare();
  });
  document.getElementById("rt-language-select")?.addEventListener("change", (e) => {
    localStorage.setItem("mobmate.realtimeLanguage", normalizeRealtimeLanguage(e.target.value));
    void OnsetFillerUi.prepare();
    if (typeof AddonLanguageUi !== "undefined") AddonLanguageUi.followLanguage();
    // Preserve the selected MOSS voice when the ASR language changes.
    if (document.getElementById("tts-engine-select")?.value === TTS_ENGINE.MOSS) _mossPopulateModelSelect();
  });
  document.getElementById("text-filter-ng-mode")?.addEventListener("change", () => saveTextFilterConfig());
  document.getElementById("laugh-preset-select")?.addEventListener("change", (e) => saveTextFilterConfig({ laughPreset: e.target.value }));
  document.getElementById("laugh-replacement-enabled")?.addEventListener("change", () => saveTextFilterConfig());
  ["laugh-tokens", "laugh-replacement-words"].forEach(id => {
    document.getElementById(id)?.addEventListener("blur", () => saveTextFilterConfig({ laughPreset: "custom" }));
  });
  document.getElementById("text-filter-ng-save-btn")?.addEventListener("click", saveNgWords);
  document.getElementById("text-filter-ng-preset-btn")?.addEventListener("click", loadNgPreset);
  document.getElementById("pending-save-btn")?.addEventListener("click", savePendingSpeechConfig);
  document.getElementById("pending-mode-instant-btn")?.addEventListener("click", () => setPendingMode("instant"));
  document.getElementById("pending-mode-wait-btn")?.addEventListener("click", () => setPendingMode("wait"));
  document.getElementById("pending-wait-seconds")?.addEventListener("change", savePendingSpeechConfig);
  document.getElementById("pending-approve-btn")?.addEventListener("click", () => runPendingAction("approve"));
  document.getElementById("pending-cancel-btn")?.addEventListener("click", () => runPendingAction("cancel"));
  document.getElementById("pending-clear-btn")?.addEventListener("click", () => runPendingAction("clear"));
  document.getElementById("radio-pending-approve-btn")?.addEventListener("click", () => runPendingAction("approve"));
  document.getElementById("radio-pending-cancel-btn")?.addEventListener("click", () => runPendingAction("cancel"));
  document.getElementById("radio-save-btn")?.addEventListener("click", saveRadioConfigAndCommands);
  document.getElementById("radio-reload-btn")?.addEventListener("click", fetchRadioCommands);
  document.getElementById("radio-preview-btn")?.addEventListener("click", radioPreview);
  document.getElementById("radio-speak-btn")?.addEventListener("click", radioSpeakFirst);
  ["radio-overlay-position", "radio-overlay-bg", "radio-overlay-fg", "radio-overlay-font-size", "radio-overlay-opacity"].forEach((id) => {
    document.getElementById(id)?.addEventListener("change", saveRadioOverlayConfig);
  });
  document.getElementById("radio-page-0")?.addEventListener("click", () => radioSetPage(0));
  document.getElementById("radio-page-1")?.addEventListener("click", () => radioSetPage(1));
  document.getElementById("radio-page-2")?.addEventListener("click", () => radioSetPage(2));
  document.getElementById("radio-capture-btn")?.addEventListener("click", beginRadioShortcutCapture);
  setupRadioLinuxCopyBtn();
  startTtsStatusPoll();
  fetchTtsOutputDevices();
  // Restore saved monitor volume before fetchTtsEngines(), because fetchTtsEngines()
  // calls setTtsEngineConfig() and posts the current select value to the server.
  const savedMonVol = localStorage.getItem("mobmate.ttsMonitorVolume");
  if (savedMonVol) {
    const monVolSelect = document.getElementById("tts-monitor-volume-select");
    if (monVolSelect && Array.from(monVolSelect.options).some(o => o.value === savedMonVol)) {
      monVolSelect.value = savedMonVol;
    }
  }
  await kokoroUiReady;
  const initialTtsEngines = fetchTtsEngines();
  // MOSS is optional: reconcile a delayed catalog without holding unrelated startup.
  void mossUiReady.then(async () => {
    await initialTtsEngines;
    if (uiSettings.isReady() && document.getElementById("tts-engine-select")?.value === TTS_ENGINE.MOSS &&
        !OnsetFillerUi.isVoiceConfirmed() && !isSelectedMossUnavailable()) await fetchTtsEngines();
  }).catch(e => console.warn("MOSS startup reconciliation failed", e));
  await initialTtsEngines;
  fetchNgWords();
  await fetchPendingSpeechConfig();
  startPendingStatusPoll();
  await fetchRadioConfig();
  await fetchRadioCommands();
  renderRadioPage();
  // Translation
  await populateTranslationTargets();
  await loadTranslationConfig();
  document.getElementById("translation-target-select")?.addEventListener("change", (e) => {
    saveTranslationTarget(e.target.value);
    if (document.getElementById("tts-engine-select")?.value === TTS_ENGINE.MOSS) _mossPopulateModelSelect();
  });
  // Hearing
  await initHearingUi();
  await initVrchatOscUi();
  // Voice Conversion
  await initVoiceConvUi();
  await restoreMobfyVoiceIfNeeded();
  // Piper Voice DL section
  await piperVoiceDlReady;
  await maybeShowInitialTtsSetupPrompt();
  initSetupWizard();
});

// ============================================================
// Setup Wizard (V2)
// ============================================================
const SETUP_STEP_COUNT = 4;
let setupCurrentStep = 0;
let setupMicTimer = null;
let setupMicTesting = false;
let setupMicInitSeq = 0;
let setupOutputInitSeq = 0;
let setupMicStarting = null;
let setupMicStopping = null;
let setupMicUnconfirmed = false;
let setupMicPollSeq = 0;
let setupMicCaptureGeneration = 0;
let setupClosing = false;
let setupNavigating = false;

function setupEl(id) { return document.getElementById(id); }

function setupMicBlocksOtherCapture() {
  return !!(setupMicStarting || setupMicTesting || setupMicStopping || setupMicUnconfirmed);
}

function setupMicCaptureRevision() { return setupMicCaptureGeneration; }

function renderSetupMicStopState() {
  const warning = setupEl("setup-mic-warning");
  const warningText = setupEl("setup-mic-warning-text");
  const retry = setupEl("setup-mic-retry-stop");
  const test = setupEl("setup-mic-test");
  if (warning) warning.hidden = !(setupMicStopping || setupMicUnconfirmed);
  if (warningText) warningText.textContent = t(setupMicUnconfirmed ? "setup.s2.stopWarning" : "setup.s2.stopping");
  if (retry) { retry.hidden = !setupMicUnconfirmed; retry.disabled = !!setupMicStopping; }
  if (test) test.disabled = !!(setupMicStarting || setupMicTesting || setupMicStopping || setupMicUnconfirmed);
  if (setupMicStopping) setText("setup-mic-result", t("setup.s2.stopping"));
  else if (setupMicUnconfirmed) setText("setup-mic-result", t("setup.s2.stopUnconfirmed"));
  if (typeof AddonLanguageUi !== "undefined") AddonLanguageUi.translate();
}

function openSetupWizard() {
  if (setupClosing) return;
  setupCurrentStep = 0;
  openUiModal("setup-overlay", "setup-title", () => closeSetupWizard(false), setupEl("menu-btn"));
  renderSetupStep();
}

function saveSetupMicSelection() {
  const select = setupEl("setup-mic-select");
  if (select) RealtimeSessionClient.setSelectedCaptureDeviceId(select.value || "", select);
}

async function closeSetupWizard(markCompleted) {
  if (setupClosing || !setupEl("setup-overlay")?.classList.contains("open")) return;
  const completing = markCompleted && setupCurrentStep === SETUP_STEP_COUNT - 1;
  let stopping;
  setupClosing = true;
  try {
    stopping = stopSetupMicTest();
    closeUiModal("setup-overlay");
  } finally { setupClosing = false; }
  if (await stopping && completing) localStorage.setItem("mobmate.wizard.completed", "1");
}

function renderSetupStep(moveFocus = false) {
  document.querySelectorAll(".setup-step").forEach((el) => {
    el.classList.toggle("active", Number(el.getAttribute("data-step")) === setupCurrentStep);
  });
  document.querySelectorAll(".setup-dot").forEach((el) => {
    el.classList.toggle("active", Number(el.getAttribute("data-step")) === setupCurrentStep);
  });
  const backBtn = setupEl("setup-back-btn");
  const nextBtn = setupEl("setup-next-btn");
  if (backBtn) backBtn.disabled = (setupCurrentStep === 0);
  if (nextBtn) nextBtn.textContent = (setupCurrentStep === SETUP_STEP_COUNT - 1) ? t("setup.btn.finish") : t("setup.btn.next");
  const skipBtn = setupEl("setup-skip-btn");
  if (skipBtn) skipBtn.hidden = setupCurrentStep === SETUP_STEP_COUNT - 1;
  if (setupCurrentStep === 0) initSetupMicStep();
  else stopSetupMicTest();
  if (setupCurrentStep === 1) refreshSetupVirtualAudio();
  if (setupCurrentStep === 2) initSetupOutputStep();
  else setupOutputInitSeq += 1;
  if (moveFocus) setupEl(`setup-step-title-${setupCurrentStep}`)?.focus();
  recoverUiModalFocus();
}

async function setupGoNext() {
  if (setupNavigating || setupClosing) return;
  setupNavigating = true;
  try {
    if (setupCurrentStep >= SETUP_STEP_COUNT - 1) { await closeSetupWizard(true); return; }
    if (setupCurrentStep === 0) {
      const stopping = stopSetupMicTest();
      const seq = setupMicInitSeq;
      if (!await stopping || seq !== setupMicInitSeq) return;
    }
    if (setupClosing || !setupEl("setup-overlay")?.classList.contains("open")) return;
    setupCurrentStep += 1;
    renderSetupStep(true);
  } finally { setupNavigating = false; }
}

async function setupGoBack() {
  if (setupCurrentStep <= 0 || setupNavigating || setupClosing || setupMicStopping || setupMicUnconfirmed) return;
  setupCurrentStep -= 1;
  renderSetupStep(true);
}

async function initSetupMicStep() {
  const seq = ++setupMicInitSeq;
  const select = setupEl("setup-mic-select");
  if (!select) return;
  const resultEl = setupEl("setup-mic-result");
  if (resultEl && !setupMicStopping && !setupMicUnconfirmed) resultEl.textContent = "";
  renderSetupMicStopState();
  const fill = setupEl("setup-mic-meter");
  if (fill) fill.style.width = "0%";
  const main = document.getElementById("rt-device-select");
  const selected = main?.options.length ? main.value : RealtimeSessionClient.getSelectedCaptureDeviceId();
  captureInputSelectionChanged();
  const mainSeq = audioInputRefreshSeq;
  const syncMain = () => {
    if (main?.value && main.value === select.value && mainSeq === audioInputRefreshSeq) copyCaptureInputOption(main, select.selectedOptions[0]);
  };
  renderCaptureInputs(select, [], selected, true);
  syncMain();
  try {
    const data = await RealtimeSessionClient.listAudioInputDevices();
    if (seq !== setupMicInitSeq || mainSeq !== audioInputRefreshSeq || setupCurrentStep !== 0 || !setupEl("setup-overlay")?.classList.contains("open")) return;
    if (!validCaptureInputDevices(data)) throw new Error(t("ui.web.device.inputEnumerationFailed"));
    renderCaptureInputs(select, data.devices, select.value);
    syncMain();
  } catch (e) {
    if (seq === setupMicInitSeq && mainSeq === audioInputRefreshSeq && setupCurrentStep === 0 && setupEl("setup-overlay")?.classList.contains("open") && !setupMicStopping && !setupMicUnconfirmed && resultEl)
      resultEl.textContent = t("ui.web.device.inputEnumerationFailed");
  }
}

async function startSetupMicTest() {
  if (setupMicTesting || setupMicStarting || setupMicStopping || setupMicUnconfirmed || setupClosing) return;
  const select = setupEl("setup-mic-select");
  if (captureInputUnavailable(select)) {
    setText("setup-mic-result", t("ui.web.device.inputCannotStart"));
    return;
  }
  const deviceId = select?.value || "";
  RealtimeSessionClient.setSelectedCaptureDeviceId(deviceId, select);
  setupMicCaptureGeneration += 1;
  setupMicStarting = (async () => {
    try {
      await RealtimeSessionClient.startRealtimeSession("wizard", RealtimeSessionClient.buildRealtimeStartRequest(deviceId));
      // A wizard takeover permanently retires the previous main poll, not just
      // while the wizard's local capture guard happens to be set.
      realtimePollToken += 1;
      setupMicTesting = true;
      if (!setupMicStopping && !setupClosing && setupCurrentStep === 0 && setupEl("setup-overlay")?.classList.contains("open")) {
        setupMicPollSeq += 1;
        setupMicTimer = setInterval(pollSetupMic, 500);
        const r = setupEl("setup-mic-result");
        if (r) r.textContent = t("setup.s2.detected");
      }
    } catch (e) {
      if (!e?.realtimeRequestRejected) {
        // Losing the Start response does not establish that capture never began.
        // Retain the existing explicit wizard Stop recovery; do not infer a lease.
        realtimePollToken += 1;
        setupMicUnconfirmed = true;
      }
      const r = setupEl("setup-mic-result");
      if (r && !setupMicStopping && !setupClosing) r.textContent = t("setup.s2.failed");
    }
  })();
  renderSetupMicStopState();
  try { await setupMicStarting; } finally { setupMicStarting = null; renderSetupMicStopState(); }
}

async function pollSetupMic() {
  const seq = setupMicPollSeq;
  try {
    const data = await RealtimeSessionClient.getStatus();
    if (seq !== setupMicPollSeq || !setupMicTesting || setupMicStopping || setupMicUnconfirmed || setupClosing || setupCurrentStep !== 0 || !setupEl("setup-overlay")?.classList.contains("open")) return;
    const rms = Number(data?.captureRms);
    const db = Number.isFinite(rms) && rms > 1 ? Math.max(-60, 20 * Math.log10(rms / 32768)) : -60;
    const pct = Math.max(0, Math.min(100, (db + 60) * 1.6));
    const fill = setupEl("setup-mic-meter");
    if (fill) fill.style.width = pct + "%";
    const text = data?.partial || data?.final || "";
    const r = setupEl("setup-mic-result");
    if (r && text) r.textContent = text;
  } catch (e) { /* ignore */ }
}

async function stopSetupMicTest({ retry = false } = {}) {
  setupMicInitSeq += 1;
  setupMicPollSeq += 1;
  if (setupMicStopping) return setupMicStopping;
  if (setupMicUnconfirmed && !retry) { renderSetupMicStopState(); return false; }
  if (setupMicTimer) { clearInterval(setupMicTimer); setupMicTimer = null; }
  if (!setupMicTesting && !setupMicStarting && !setupMicUnconfirmed) return true;
  setupMicStopping = (async () => {
    if (setupMicStarting) await setupMicStarting;
    if (!setupMicTesting && !setupMicUnconfirmed) return true;
    try {
      const data = await RealtimeSessionClient.stopRealtimeSession("wizard");
      setupMicTesting = false;
      setupMicUnconfirmed = false;
      // Realtime Stop does not stop VC. Preserve running/pending Mobfy controls,
      // but a switch to idle Mobfy still needs its next explicit Start.
      const mobfy = isMobfyMode();
      if (!mobfy || (!vcRunning && vcPendingStarts === 0)) {
        setText("rt-status", mobfy ? t("vc.status.off") : data?.error ? t("ui.web.status.error", { message: data.error }) : t("ui.web.status.stopped"));
        const mainStatus = document.getElementById("rt-status");
        if (mainStatus) mainStatus.className = !mobfy && data?.error ? "status err" : "status ok";
        setText("rt-live-vad", "-");
        setText("rt-live-rms", "0.0000");
        setRealtimeGuide(mobfy ? "vc.guide.stopped" : "ui.web.guide.stopped");
        setRecordingButtons(false);
        meterStop(); spkStop(); waveClear();
      }
      setRealtimeWasRecording(false);
      const fill = setupEl("setup-mic-meter");
      if (fill) fill.style.width = "0%";
      setText("setup-mic-result", "");
      return true;
    } catch (e) {
      setupMicTesting = false;
      setupMicUnconfirmed = true;
      return false;
    }
  })();
  renderSetupMicStopState();
  try { return await setupMicStopping; }
  finally { setupMicStopping = null; renderSetupMicStopState(); }
}

async function refreshSetupVirtualAudio() {
  const statusEl = setupEl("setup-va-status");
  const titleEl = document.querySelector('.setup-step[data-step="1"] [data-i18n="setup.s3.title"]');
  const descEl = document.querySelector('.setup-step[data-step="1"] [data-i18n="setup.s3.desc"]');
  if (linuxCoreOnly()) {
    if (titleEl) titleEl.textContent = t("setup.s3.linuxTitle");
    if (descEl) descEl.textContent = t("setup.s3.linuxDesc");
    setupEl("setup-va-install")?.classList.add("setup-hidden");
  } else {
    if (titleEl) titleEl.textContent = t("setup.s3.title");
    if (descEl) descEl.textContent = t("setup.s3.desc");
  }
  try {
    const res = await fetch("/api/virtual-audio/status");
    const data = await res.json();
    if (linuxCoreOnly() && data?.installed) {
      if (statusEl) { statusEl.textContent = t("setup.s3.linuxReady", { name: data.message || "PipeWire/PulseAudio" }); statusEl.classList.add("setup-ok"); }
      setupEl("setup-va-install")?.classList.add("setup-hidden");
      return;
    }
    if (data?.detected) {
      if (statusEl) { statusEl.textContent = t("setup.s3.detected", { name: data.preferredOutputDeviceName || "" }); statusEl.classList.add("setup-ok"); }
      setupEl("setup-va-install")?.classList.add("setup-hidden");
    } else {
      if (statusEl) { statusEl.textContent = t("setup.s3.notDetected"); statusEl.classList.remove("setup-ok"); }
      setupEl("setup-va-install")?.classList.remove("setup-hidden");
    }
  } catch (e) {
    if (statusEl) statusEl.textContent = t("setup.s3.notDetected");
  }
}

async function installSetupVirtualAudio() {
  const statusEl = setupEl("setup-va-status");
  if (statusEl) { statusEl.textContent = t("setup.s3.installing"); statusEl.classList.remove("setup-ok"); }
  try {
    await fetch("/api/virtual-audio/install", { method: "POST" });
  } catch (e) { /* ignore */ }
  const timer = setInterval(async () => {
    try {
      const res = await fetch("/api/virtual-audio/install-status");
      const data = await res.json();
      if (data?.install?.lastMessage && statusEl) {
        statusEl.textContent = data.install.lastMessage;
        statusEl.classList.remove("setup-ok");
      }
      if (data?.install?.running === false) {
        clearInterval(timer);
        await refreshSetupVirtualAudio();
      }
    } catch (e) { clearInterval(timer); }
  }, 2000);
}

async function initSetupOutputStep() {
  const seq = ++setupOutputInitSeq;
  const select = setupEl("setup-out-select");
  if (!select) return;
  setupEl("setup-out-trouble")?.classList.add("setup-hidden");
  setText("setup-out-result", "");
  try {
    const res = await fetch("/api/tts/devices");
    const data = await res.json();
    if (seq !== setupOutputInitSeq || setupCurrentStep !== 2 || !setupEl("setup-overlay")?.classList.contains("open")) return;
    const devices = data?.devices || [];
    const saved = localStorage.getItem("mobmate.ttsOutputDeviceId") || "";
    appendTtsOutputDeviceOptions(select, devices, true);
    if (saved && Array.from(select.options).some((o) => o.value === saved)) select.value = saved;
  } catch (e) { /* keep empty */ }
}

async function playSetupOutputTest() {
  const button = setupEl("setup-out-play");
  if (button?.disabled) return;
  if (button) button.disabled = true;
  const seq = setupOutputInitSeq;
  const stillCurrent = () => seq === setupOutputInitSeq && !setupClosing && setupCurrentStep === 2 && setupEl("setup-overlay")?.classList.contains("open");
  const deviceId = setupEl("setup-out-select")?.value || "";
  let configConfirmed = false;
  try {
    setText("setup-out-result", t("ui.web.status.loading"));
    if (!await setTtsOutputDevice(deviceId, "setup-out-select")) throw new Error("output configuration not confirmed");
    if (!stillCurrent()) return;
    if ((setupEl("setup-out-select")?.value || "") !== deviceId) throw new Error("output selection changed");
    configConfirmed = true;
    const res = await requestTtsSpeak({ text: t("setup.s4.testPhrase"), interrupt: true });
    if (!res) { configConfirmed = false; throw new Error("output configuration not confirmed"); }
    const data = await res.json();
    if (!res.ok || data?.success !== true) throw new Error("test playback not confirmed");
    if (stillCurrent()) setText("setup-out-result", t("setup.s4.requested"));
  } catch (e) { if (stillCurrent()) setText("setup-out-result", t(configConfirmed ? "setup.s4.failed" : "setup.s4.configFailed")); }
  finally { if (button) button.disabled = false; }
}

function setupAnimateButton(btn) {
  if (!btn) return;
  btn.classList.remove("setup-pulse");
  void btn.offsetWidth;
  btn.classList.add("setup-pulse");
}

async function openSetupDiscord() {
  try { await fetch("/api/external/discord-voice-settings", { method: "POST" }); } catch (e) { /* ignore */ }
}

function initSetupWizard() {
  document.querySelectorAll(".setup-btn").forEach((btn) => btn.addEventListener("click", () => setupAnimateButton(btn)));
  setupEl("setup-next-btn")?.addEventListener("click", async () => {
    await setupGoNext();
  });
  setupEl("setup-back-btn")?.addEventListener("click", setupGoBack);
  setupEl("setup-skip-btn")?.addEventListener("click", async () => { if (setupCurrentStep < SETUP_STEP_COUNT - 1) await setupGoNext(); });
  setupEl("setup-later-btn")?.addEventListener("click", () => closeSetupWizard(false));
  setupEl("setup-close-btn")?.addEventListener("click", () => closeSetupWizard(false));
  setupEl("setup-backdrop")?.addEventListener("click", () => closeSetupWizard(false));
  setupEl("setup-va-install")?.addEventListener("click", installSetupVirtualAudio);
  setupEl("setup-va-rescan")?.addEventListener("click", refreshSetupVirtualAudio);
  setupEl("setup-out-play")?.addEventListener("click", playSetupOutputTest);
  setupEl("setup-mic-test")?.addEventListener("click", startSetupMicTest);
  setupEl("setup-mic-retry-stop")?.addEventListener("click", () => stopSetupMicTest({ retry: true }));
  setupEl("setup-mic-reopen")?.addEventListener("click", openSetupWizard);
  setupEl("setup-mic-select")?.addEventListener("change", async (e) => {
    RealtimeSessionClient.setSelectedCaptureDeviceId(e.target.value, e.target);
    await stopSetupMicTest();
  });
  setupEl("setup-discord-btn")?.addEventListener("click", openSetupDiscord);
  setupEl("drawer-wizard-btn")?.addEventListener("click", () => {
    closeUiModal("settings-drawer", false);
    openSetupWizard();
  });
  if (!localStorage.getItem("mobmate.wizard.completed")) {
    openSetupWizard();
  }
}

// ============================================================
// Piper Voice DL
// ============================================================
// Piper Voice DL — custom searchable dropdown + button progress
// ============================================================

const LANG_FLAG = {
  af:"🇿🇦", ar:"🇯🇴", az:"🇦🇿", be:"🇧🇾", bg:"🇧🇬", bn:"🇧🇩",
  ca:"🇪🇸", cs:"🇨🇿", cy:"🏴󠁧󠁢󠁷󠁬󠁳󠁿", da:"🇩🇰", de:"🇩🇪",
  el:"🇬🇷", en:"🇺🇸", es:"🇪🇸", et:"🇪🇪", eu:"🇪🇸",
  fa:"🇮🇷", fi:"🇫🇮", fr:"🇫🇷", ga:"🇮🇪", gl:"🇪🇸",
  gu:"🇮🇳", he:"🇮🇱", hi:"🇮🇳", hr:"🇭🇷", hu:"🇭🇺",
  hy:"🇦🇲", id:"🇮🇩", is:"🇮🇸", it:"🇮🇹", ja:"🇯🇵",
  ka:"🇬🇪", kk:"🇰🇿", km:"🇰🇭", kn:"🇮🇳", ko:"🇰🇷",
  lb:"🇱🇺", lt:"🇱🇹", lv:"🇱🇻", mk:"🇲🇰", ml:"🇮🇳",
  mn:"🇲🇳", mr:"🇮🇳", ms:"🇲🇾", mt:"🇲🇹", my:"🇲🇲",
  ne:"🇳🇵", nl:"🇳🇱", no:"🇳🇴", pa:"🇮🇳", pl:"🇵🇱",
  pt:"🇧🇷", ro:"🇷🇴", ru:"🇷🇺", si:"🇱🇰", sk:"🇸🇰",
  sl:"🇸🇮", sq:"🇦🇱", sr:"🇷🇸", sv:"🇸🇪", sw:"🇨🇩",
  ta:"🇮🇳", te:"🇮🇳", th:"🇹🇭", tr:"🇹🇷", uk:"🇺🇦",
  ur:"🇵🇰", uz:"🇺🇿", vi:"🇻🇳", zh:"🇨🇳", zu:"🇿🇦",
};

let _piperVoiceCatalog = [];
let _piperDlSelectedId = null;
let _piperDlPollTimer = null;
let _piperDlInited = false;

async function initPiperVoiceDl() {
  try {
    const res = await fetch("/api/tts/voices/catalog");
    if (!res.ok) return;
    const data = await res.json();
    _piperVoiceCatalog = data.voices || [];
  } catch (e) {
    return; // catalog endpoint may not exist on older builds
  }

  _piperDlRenderList("");
  _piperDlUpdateInfo();

  if (_piperDlInited) return; // register events only once
  _piperDlInited = true;

  // Open / close dropdown
  document.getElementById("piper-dl-trigger")?.addEventListener("click", (e) => {
    e.stopPropagation();
    const wrap = document.getElementById("piper-dl-wrap");
    const isOpen = wrap?.classList.toggle("open");
    if (isOpen) document.getElementById("piper-dl-search")?.focus();
  });
  document.addEventListener("click", (e) => {
    if (!document.getElementById("piper-dl-wrap")?.contains(e.target)) {
      document.getElementById("piper-dl-wrap")?.classList.remove("open");
    }
  });
  // Search filter
  document.getElementById("piper-dl-search")?.addEventListener("input", (e) => {
    _piperDlRenderList(e.target.value);
  });
  // Action buttons
  document.getElementById("piper-dl-download-btn")?.addEventListener("click", _piperDlDownload);
  document.getElementById("piper-dl-remove-btn")?.addEventListener("click", _piperDlRemove);
}

function _piperDlRenderList(query) {
  const list = document.getElementById("piper-dl-list");
  if (!list) return;
  const q = query.toLowerCase().trim();
  const filtered = q
    ? _piperVoiceCatalog.filter(v =>
        (v.language||"").toLowerCase().includes(q) ||
        (v.languageDisplay||"").toLowerCase().includes(q) ||
        (v.name||"").toLowerCase().includes(q) ||
        (v.id||"").toLowerCase().includes(q)
      )
    : _piperVoiceCatalog;

  list.innerHTML = "";
  if (filtered.length === 0) {
    const empty = document.createElement("div");
    empty.style.cssText = "padding:0.55rem 0.65rem;color:#8d9a9a;font-size:0.8rem;";
    empty.textContent = "—";
    list.appendChild(empty);
    return;
  }

  // auto-select first if no selection yet
  if (!_piperDlSelectedId || !filtered.some(v => v.id === _piperDlSelectedId)) {
    _piperDlSelectedId = filtered[0].id;
  }

  for (const v of filtered) {
    const flag = LANG_FLAG[v.language] || "🌐";
    const q_cls = v.quality === "medium" ? "pdc-badge-qm"
                : v.quality === "high"   ? "pdc-badge-qh"
                : "pdc-badge-q";

    const item = document.createElement("div");
    item.className = "pdc-item" + (v.id === _piperDlSelectedId ? " pdc-selected" : "");
    item.dataset.id = v.id;
    item.innerHTML =
      `<span class="pdc-iflag">${flag}</span>` +
      `<span class="pdc-imain">` +
        `<div class="pdc-iname">${_esc(v.name || v.id)}</div>` +
        `<div class="pdc-ilang">${_esc(v.languageDisplay || v.language)}${v.quality ? " · " + v.quality : ""}</div>` +
      `</span>` +
      `<span class="pdc-ibadges">` +
        `<span class="pdc-badge ${q_cls}">${v.quality || ""}</span>` +
        (v.platform === "windows" ? `<span class="pdc-badge pdc-badge-win">Win</span>` : "") +
        (v.installed ? `<span class="pdc-badge pdc-badge-ok">✓</span>` : "") +
      `</span>`;
    item.addEventListener("click", () => {
      _piperDlSelectedId = v.id;
      document.getElementById("piper-dl-wrap")?.classList.remove("open");
      document.getElementById("piper-dl-search").value = "";
      _piperDlRenderList("");
      _piperDlUpdateInfo();
    });
    list.appendChild(item);
  }

  _piperDlUpdateTrigger();
}

function _piperDlUpdateTrigger() {
  const voice = _piperVoiceCatalog.find(v => v.id === _piperDlSelectedId);
  const flag = document.getElementById("piper-dl-flag");
  const label = document.getElementById("piper-dl-label");
  if (!voice) {
    if (flag) flag.textContent = "🌐";
    if (label) label.textContent = "—";
    return;
  }
  if (flag) flag.textContent = LANG_FLAG[voice.language] || "🌐";
  if (label) {
    const winTag = voice.platform === "windows" ? " [Win]" : "";
    const okTag  = voice.installed ? " ✓" : "";
    label.textContent = `${voice.languageDisplay || voice.language} — ${voice.name}${winTag}${okTag}`;
  }
}

function _piperDlUpdateInfo() {
  const voice = _piperVoiceCatalog.find(v => v.id === _piperDlSelectedId);
  _piperDlUpdateTrigger();

  const licenseEl  = document.getElementById("piper-dl-license");
  const sourceLink = document.getElementById("piper-dl-source-link");
  const metaEl     = document.getElementById("piper-dl-meta");
  const statusEl   = document.getElementById("piper-dl-status");
  const removeBtn  = document.getElementById("piper-dl-remove-btn");
  const dlBtn      = document.getElementById("piper-dl-download-btn");

  if (!voice) {
    if (metaEl) metaEl.style.display = "none";
    if (statusEl) statusEl.textContent = "";
    if (removeBtn) removeBtn.disabled = true;
    return;
  }

  // license / source
  if (licenseEl) licenseEl.textContent = voice.license || "";
  if (sourceLink) {
    sourceLink.href = voice.sourcePageUrl || "#";
    sourceLink.style.display = voice.sourcePageUrl ? "inline" : "none";
  }
  if (metaEl) metaEl.style.display = (voice.license || voice.sourcePageUrl) ? "flex" : "none";

  const isWin       = voice.platform === "windows";
  const isInstalled = !!voice.installed;

  // status line (don't overwrite while DL is active)
  if (!dlBtn?.classList.contains("dl-active")) {
    if (statusEl) {
      if (isInstalled) {
        statusEl.innerHTML = `<span style="color:#77e2bd">${t("ui.web.piperDl.installed")}</span>`;
      } else if (isWin && !_isWindows()) {
        statusEl.innerHTML = `<span style="color:#8d9a9a">${t("ui.web.piperDl.windowsOnly")}</span>`;
      } else {
        statusEl.innerHTML = `<span style="color:#8d9a9a">${t("ui.web.piperDl.notInstalled")}` +
          (voice.sizeMb ? ` <span style="font-size:0.72rem">(~${voice.sizeMb} MB)</span>` : "") + `</span>`;
      }
    }
  }

  if (removeBtn) removeBtn.disabled = !isInstalled;
  if (dlBtn) {
    const canDl = !isWin || _isWindows();
    dlBtn.disabled = !canDl;
    if (!dlBtn.classList.contains("dl-active") && !dlBtn.classList.contains("dl-done") && !dlBtn.classList.contains("dl-err")) {
      dlBtn.textContent = t("ui.web.piperDl.download");
      dlBtn.style.removeProperty("--dlp");
    }
  }
}

function _isWindows() {
  return !navigator.platform?.toLowerCase().includes("linux");
}

async function _piperDlDownload() {
  const id = _piperDlSelectedId;
  if (!id) return;
  const voice = _piperVoiceCatalog.find(v => v.id === id);
  if (!voice) return;

  const confirmMsg =
    `${t("ui.web.piperDl.confirmDownload")}\n\n` +
    `${voice.name}\n` +
    `${t("ui.web.piperDl.licenseLabel")}: ${voice.license || "?"}\n` +
    `${voice.sourcePageUrl || ""}`;
  if (!confirm(confirmMsg)) return;

  const dlBtn    = document.getElementById("piper-dl-download-btn");
  const statusEl = document.getElementById("piper-dl-status");

  if (dlBtn) {
    dlBtn.classList.remove("dl-done", "dl-err");
    dlBtn.classList.add("dl-active");
    dlBtn.style.setProperty("--dlp", "0%");
    dlBtn.textContent = "0%";
    dlBtn.disabled = true;
  }
  if (statusEl) statusEl.innerHTML = `<span style="color:#6ed6cd">${t("ui.web.piperDl.downloading")}</span>`;

  try {
    const res = await fetch("/api/tts/voices/download", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id })
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      _piperDlSetError(err.error || err.message || "Download failed");
      return;
    }
    _piperDlStartPoll(id);
  } catch (e) {
    _piperDlSetError(String(e));
  }
}

function _piperDlStartPoll(id) {
  if (_piperDlPollTimer) clearInterval(_piperDlPollTimer);
  _piperDlPollTimer = setInterval(async () => {
    try {
      const res = await fetch(`/api/tts/voices/download/${encodeURIComponent(id)}/status`);
      if (!res.ok) return;
      const data = await res.json();
      const pct = Math.round((data.progress || 0) * 100);

      const dlBtn    = document.getElementById("piper-dl-download-btn");
      const statusEl = document.getElementById("piper-dl-status");

      if (data.status === "done") {
        clearInterval(_piperDlPollTimer); _piperDlPollTimer = null;
        if (dlBtn) {
          dlBtn.classList.remove("dl-active");
          dlBtn.classList.add("dl-done");
          dlBtn.style.setProperty("--dlp", "100%");
          dlBtn.textContent = "✓ Done";
          dlBtn.disabled = false;
        }
        if (statusEl) statusEl.innerHTML = `<span style="color:#77e2bd">${t("ui.web.piperDl.installed")}</span>`;
        // Refresh catalog
        await initPiperVoiceDl();
        // Refresh TTS model list and auto-select the new model
        await fetchTtsEngines();
        _piperDlAutoSelectInTts(id);
      } else if (data.status === "error") {
        clearInterval(_piperDlPollTimer); _piperDlPollTimer = null;
        _piperDlSetError(data.error || "unknown error");
      } else {
        // downloading
        if (dlBtn) {
          dlBtn.style.setProperty("--dlp", `${pct}%`);
          dlBtn.textContent = `${pct}%`;
        }
        if (statusEl) statusEl.innerHTML =
          `<span style="color:#6ed6cd">${t("ui.web.piperDl.downloading")} ${pct}%</span>`;
      }
    } catch (e) {
      console.warn("piperDlPoll", e);
    }
  }, 600);
}

function _piperDlSetError(msg) {
  const dlBtn    = document.getElementById("piper-dl-download-btn");
  const statusEl = document.getElementById("piper-dl-status");
  if (dlBtn) {
    dlBtn.classList.remove("dl-active", "dl-done");
    dlBtn.classList.add("dl-err");
    dlBtn.style.removeProperty("--dlp");
    dlBtn.textContent = t("ui.web.piperDl.download");
    dlBtn.disabled = false;
  }
  if (statusEl) statusEl.innerHTML = `<span style="color:#ff8a9a">${t("ui.web.piperDl.error")}: ${_esc(msg)}</span>`;
}

function _piperDlAutoSelectInTts(id) {
  // If piper_plus engine is selected, pick the newly installed model
  const engineSel = document.getElementById("tts-engine-select");
  const modelSel  = document.getElementById("tts-piper-model-select");
  if (!engineSel || !modelSel) return;
  if (engineSel.value !== TTS_ENGINE.PIPER_PLUS) return;
  // Find option matching the new voice's model id (from EnumeratePiperPlusModels id field)
  const opt = Array.from(modelSel.options).find(o => o.value === id);
  if (opt) {
    modelSel.value = id;
    localStorage.setItem("mobmate.piperPlusModel", id);
    setTtsEngineConfig();
  }
}

async function _piperDlRemove() {
  const id = _piperDlSelectedId;
  if (!id) return;
  if (!confirm(t("ui.web.piperDl.confirmRemove"))) return;
  try {
    await fetch(`/api/tts/voices/${encodeURIComponent(id)}`, { method: "DELETE" });
    await initPiperVoiceDl();
    await fetchTtsEngines();
  } catch (e) {
    console.warn("piperDlRemove", e);
  }
}

function _esc(s) {
  return String(s).replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;");
}

// ============================================================
// Kokoro Rich TTS UI  (Piper pdc-wrap スタイル)
// ============================================================

let _kokoroCatalog = [];
let _kokoroDlSelectedId = null;
let _kokoroDlActivePollers = {};

async function initKokoroUi() {
  try {
    const res = await fetch("/api/tts/rich/catalog");
    const all = (await res.json()).voices || [];
    _kokoroCatalog = all.filter(v => (v.engine || "kokoro") === "kokoro");
  } catch (e) { console.warn("initKokoroUi catalog fetch failed", e); }

  _kokoroDlRenderList();

  // Trigger button
  const trigger = document.getElementById("kokoro-dl-trigger");
  const panel   = document.getElementById("kokoro-dl-panel");
  if (trigger && panel) {
    trigger.addEventListener("click", (e) => {
      e.stopPropagation();
      const wrap = document.getElementById("kokoro-dl-wrap");
      const open = wrap?.classList.toggle("open");
      if (open) document.getElementById("kokoro-dl-search")?.focus();
    });
    document.addEventListener("click", e => {
      if (!document.getElementById("kokoro-dl-wrap")?.contains(e.target))
        document.getElementById("kokoro-dl-wrap")?.classList.remove("open");
    });
  }
  document.getElementById("kokoro-dl-search")?.addEventListener("input", e => _kokoroDlRenderList(e.target.value));

  // Download button
  document.getElementById("kokoro-dl-download-btn")?.addEventListener("click", async () => {
    if (!_kokoroDlSelectedId) return;
    await _kokoroDlStart(_kokoroDlSelectedId);
  });

  // Remove button
  document.getElementById("kokoro-dl-remove-btn")?.addEventListener("click", async () => {
    if (!_kokoroDlSelectedId) return;
    if (!confirm(`Delete Kokoro model '${_kokoroDlSelectedId}'?`)) return;
    await _kokoroDelete(_kokoroDlSelectedId);
  });
}

function _kokoroDlRenderList(query = "") {
  const listEl = document.getElementById("kokoro-dl-list");
  if (!listEl) return;
  const q = String(query || "").toLowerCase().trim();
  const list = q ? _kokoroCatalog.filter(v =>
    (v.id || "").toLowerCase().includes(q) ||
    (v.name || "").toLowerCase().includes(q) ||
    (v.language || "").toLowerCase().includes(q) ||
    (v.languageDisplay || "").toLowerCase().includes(q)
  ) : _kokoroCatalog;

  if (!list.length) {
    listEl.innerHTML = `<div class="pdc-empty" style="padding:0.5rem 0.7rem;font-size:0.82rem;color:var(--text-secondary);">—</div>`;
    return;
  }

  if (!_kokoroDlSelectedId || !list.some(v => v.id === _kokoroDlSelectedId))
    _kokoroDlSelectedId = list[0].id;

  listEl.innerHTML = list.map(v => {
    const sel = v.id === _kokoroDlSelectedId ? " pdc-selected" : "";
    const incomplete = v.installed && ttsEnginesCache && !(_kokoroReadyIds().has(v.id));
    const langTag = v.language === "multi" ? "🌐" : (v.language === "en" ? "🇬🇧" : v.language === "zh" ? "🇨🇳" : "🌐");
    return `<div class="pdc-item${sel}" data-id="${_esc(v.id)}">` +
      `<span class="pdc-iflag">${langTag}</span>` +
      `<span class="pdc-imain"><div class="pdc-iname">${_esc(v.name || v.id)}</div><div class="pdc-ilang">${_esc(v.languageDisplay || v.language)}${v.speakerCount ? ` · ${v.speakerCount} speakers` : ""}</div></span>` +
      `<span class="pdc-ibadges">` +
        `<span class="pdc-badge ${v.quality === "high" ? "pdc-badge-qh" : "pdc-badge-qm"}">${_esc(v.quality)}</span>` +
        (incomplete ? `<span class="pdc-badge">${_esc(t("tts.kokoro.unavailable"))}</span>` : v.installed ? `<span class="pdc-badge pdc-badge-ok">✓</span>` : "") +
      `</span>` +
    `</div>`;
  }).join("");

  listEl.querySelectorAll(".pdc-item").forEach(item => {
    item.addEventListener("click", () => {
      _kokoroDlSelectedId = item.dataset.id;
      document.getElementById("kokoro-dl-wrap")?.classList.remove("open");
      const search = document.getElementById("kokoro-dl-search");
      if (search) search.value = "";
      _kokoroDlRenderList("");
      _kokoroDlUpdateInfo();
    });
  });

  _kokoroDlUpdateInfo();
  _kokoroPopulateModelSelect();
}

function _kokoroDlUpdateInfo() {
  const v = _kokoroCatalog.find(x => x.id === _kokoroDlSelectedId);
  const flag   = document.getElementById("kokoro-dl-flag");
  const label  = document.getElementById("kokoro-dl-label");
  const meta   = document.getElementById("kokoro-dl-meta");
  const lic    = document.getElementById("kokoro-dl-license");
  const srcLnk = document.getElementById("kokoro-dl-source-link");
  const status = document.getElementById("kokoro-dl-status");
  const dlBtn  = document.getElementById("kokoro-dl-download-btn");
  const rmBtn  = document.getElementById("kokoro-dl-remove-btn");

  if (!v) {
    if (flag) flag.textContent = "🌐";
    if (label) label.textContent = "—";
    if (meta) meta.style.display = "none";
    return;
  }

  if (flag)  flag.textContent  = v.language === "en" ? "🇬🇧" : v.language === "zh" ? "🇨🇳" : "🌐";
  if (label) label.textContent = `${v.languageDisplay || v.language} — ${v.name}`;
  if (meta)  meta.style.display = "flex";
  if (lic) {
    lic.textContent = v.license || "";
    lic.title = v.licenseNote || v.license || "";
  }
  if (srcLnk) {
    srcLnk.style.display = v.sourcePageUrl ? "inline" : "none";
    srcLnk.href = v.sourcePageUrl || "#";
  }
  const active = _kokoroDlActivePollers[v.id];
  if (status) {
    if (!active) {
      const incomplete = v.installed && ttsEnginesCache && !_kokoroReadyIds().has(v.id);
      status.textContent = incomplete ? t("tts.kokoro.incomplete") : v.installed
        ? `✓ ${t("tts.kokoro.dl.done")} (~${v.sizeMb}MB)`
        : `~${v.sizeMb}MB`;
    }
  }
  if (dlBtn) {
    dlBtn.disabled = v.installed || !!active;
    dlBtn.classList.toggle("dl-done", v.installed);
  }
  if (rmBtn) rmBtn.disabled = !v.installed || !!active;
}

function _kokoroReadyIds() {
  return new Set((ttsEnginesCache?.kokoroModels || []).map(v => v.id));
}

function _kokoroPopulateModelSelect() {
  const sel = document.getElementById("tts-kokoro-model-select");
  if (!sel) return;
  const readyIds = _kokoroReadyIds();
  const installed = _kokoroCatalog.filter(v => v.installed && readyIds.has(v.id));
  const saved = localStorage.getItem("mobmate.kokoroModelSpeaker");
  sel.innerHTML = "";
  const savedAvailable = saved && installed.some(v => v.id === saved.split("|")[0] &&
    (!Array.isArray(v.speakers) || !v.speakers.length || v.speakers.some(s => `${v.id}|${s.id}` === saved)));
  if (saved && !savedAvailable) {
    const opt = document.createElement("option");
    opt.value = saved; opt.textContent = `${saved} — ${t("tts.kokoro.unavailable")}`; opt.disabled = true; opt.selected = true; sel.appendChild(opt);
  }
  if (!installed.length && !saved) {
    const opt = document.createElement("option");
    opt.value = ""; opt.textContent = t("tts.kokoro.none_installed"); opt.disabled = true; sel.appendChild(opt);
  }
  if (installed.length) {
    installed.forEach(v => {
      const speakers = Array.isArray(v.speakers) && v.speakers.length ? v.speakers : [{ id: v.defaultSpeakerId || 3, name: `speaker ${v.defaultSpeakerId || 3}` }];
      speakers.forEach(s => {
        const opt = document.createElement("option");
        opt.value = `${v.id}|${s.id}`;
        opt.textContent = `${v.name} — ${s.id}:${s.name}`;
        if (saved && opt.value === saved) opt.selected = true;
        sel.appendChild(opt);
      });
    });
  }
  sel.onchange = () => { localStorage.setItem("mobmate.kokoroModelSpeaker", sel.value); setTtsEngineConfig(); };
}

async function _kokoroDlStart(id) {
  const statusEl = document.getElementById("kokoro-dl-status");
  if (statusEl) statusEl.textContent = t("tts.kokoro.dl.downloading");
  try {
    const res = await fetch("/api/tts/rich/download", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id })
    });
    const data = await res.json();
    if (!data.ok) throw new Error(data.message || "error");
  } catch (e) {
    if (statusEl) statusEl.textContent = `${t("tts.kokoro.dl.error")}: ${e.message}`;
    return;
  }
  _kokoroDlActivePollers[id] = true;
  _kokoroDlUpdateInfo();
  _kokoroDlPoll(id);
}

async function _kokoroDlPoll(id) {
  const statusEl = document.getElementById("kokoro-dl-status");
  try {
    const res = await fetch(`/api/tts/rich/download/${encodeURIComponent(id)}/status`);
    const state = await res.json();
    if (statusEl && id === _kokoroDlSelectedId) {
      const pct = Math.round((state.progress || 0) * 100);
      if (state.status === "downloading")  statusEl.textContent = `${t("tts.kokoro.dl.downloading")} ${pct}%`;
      else if (state.status === "extracting") statusEl.textContent = t("tts.kokoro.dl.extracting");
      else if (state.status === "done")    statusEl.textContent = `✓ ${t("tts.kokoro.dl.done")}`;
      else if (state.status === "error")   statusEl.textContent = `${t("tts.kokoro.dl.error")}: ${state.error || ""}`;
    }
    if (state.status !== "done" && state.status !== "error" && state.status !== "idle") {
      setTimeout(() => _kokoroDlPoll(id), 1500);
    } else {
      delete _kokoroDlActivePollers[id];
      if (state.status === "done") {
        try { const r = await fetch("/api/tts/rich/catalog"); const all = (await r.json()).voices || []; _kokoroCatalog = all.filter(v => (v.engine || "kokoro") === "kokoro"); }
        catch { /* ignore */ }
        _kokoroDlRenderList();
        await fetchTtsEngines();
      }
    }
  } catch (e) {
    console.warn("_kokoroDlPoll", e);
    setTimeout(() => _kokoroDlPoll(id), 3000);
  }
}

async function _kokoroDelete(id) {
  try {
    await fetch(`/api/tts/rich/${encodeURIComponent(id)}`, { method: "DELETE" });
    try { const r = await fetch("/api/tts/rich/catalog"); const all = (await r.json()).voices || []; _kokoroCatalog = all.filter(v => (v.engine || "kokoro") === "kokoro"); }
    catch { /* ignore */ }
    _kokoroDlRenderList();
    await fetchTtsEngines();
  } catch (e) { console.warn("_kokoroDelete", e); }
}

function _kokoroSaveIfEngineSelected() {
  if (document.getElementById("tts-engine-select")?.value === TTS_ENGINE.KOKORO) setTtsEngineConfig();
}

// ─── MOSS Rich TTS UI (Kokoro pdc-wrap スタイル) ───────────────────────────────

let _mossCatalog = [];
let _mossDlSelectedId = null;
let _mossDlActivePollers = {};

async function initMossUi() {
  try {
    const res = await fetch("/api/tts/rich/catalog");
    const all = (await res.json()).voices || [];
    _mossCatalog = all.filter(v => v.engine === "moss");
  } catch (e) { console.warn("initMossUi catalog fetch failed", e); }

  _mossDlRenderList();

  const trigger = document.getElementById("moss-dl-trigger");
  const panel   = document.getElementById("moss-dl-panel");
  if (trigger && panel) {
    trigger.addEventListener("click", () => {
      const wrap = document.getElementById("moss-dl-wrap");
      const open = wrap?.classList.toggle("open");
    });
    document.addEventListener("click", (e) => {
      if (!document.getElementById("moss-dl-wrap")?.contains(e.target))
        document.getElementById("moss-dl-wrap")?.classList.remove("open");
    });
  }

  document.getElementById("moss-dl-download-btn")?.addEventListener("click", async () => {
    if (!_mossDlSelectedId) return;
    await _mossDlStart(_mossDlSelectedId);
  });

  document.getElementById("moss-dl-remove-btn")?.addEventListener("click", async () => {
    if (!_mossDlSelectedId) return;
    if (!confirm(`Delete MOSS model '${_mossDlSelectedId}'?`)) return;
    await _mossDelete(_mossDlSelectedId);
  });
}

function _mossDlRenderList() {
  const listEl = document.getElementById("moss-dl-list");
  if (!listEl) return;
  listEl.innerHTML = "";
  const list = _mossCatalog;
  if (!list.length) return;
  if (!_mossDlSelectedId || !list.some(v => v.id === _mossDlSelectedId))
    _mossDlSelectedId = list[0].id;

  list.forEach(v => {
    const sel = v.id === _mossDlSelectedId ? " pdc-selected" : "";
    const item = document.createElement("div");
    item.className = `pdc-item${sel}`;
    item.dataset.id = v.id;
    item.innerHTML = `<span class="pdc-item-name">${v.name}</span>`
      + `<span class="pdc-item-desc" style="font-size:0.75rem;color:var(--text-secondary);">`
      + `${v.languageDisplay || v.language} · ${v.sizeMb || "?"}MB</span>`;
    item.addEventListener("click", () => {
      _mossDlSelectedId = item.dataset.id;
      document.getElementById("moss-dl-wrap")?.classList.remove("open");
      _mossDlRenderList();
      _mossDlUpdateInfo();
    });
    listEl.appendChild(item);
  });

  _mossDlUpdateInfo();
  _mossPopulateModelSelect();
}

function _mossDlUpdateInfo() {
  const v = _mossCatalog.find(x => x.id === _mossDlSelectedId);
  const flag   = document.getElementById("moss-dl-flag");
  const label  = document.getElementById("moss-dl-label");
  const meta   = document.getElementById("moss-dl-meta");
  const lic    = document.getElementById("moss-dl-license");
  const srcLnk = document.getElementById("moss-dl-source-link");
  const status = document.getElementById("moss-dl-status");
  const dlBtn  = document.getElementById("moss-dl-download-btn");
  const rmBtn  = document.getElementById("moss-dl-remove-btn");
  if (!v) { if (label) label.textContent = "—"; return; }

  if (flag) flag.textContent = "🌐";
  if (label) label.textContent = `${v.languageDisplay || v.language} (${v.sizeMb || "?"}MB) — ${v.name}`;
  if (meta) meta.style.display = "block";
  if (lic) {
    lic.textContent = v.license || "";
    lic.title = v.licenseNote || v.license || "";
  }
  if (srcLnk && v.sourcePageUrl) { srcLnk.href = v.sourcePageUrl; srcLnk.style.display = ""; }

  const active = _mossDlActivePollers[v.id];
  if (active) {
    if (status) status.textContent = t("tts.moss.dl.downloading");
    if (dlBtn) dlBtn.disabled = true;
    if (rmBtn) rmBtn.disabled = true;
    return;
  }
  if (v.installed) {
    if (status) status.textContent = `✓ ${t("tts.moss.dl.done")} (~${v.sizeMb}MB)`;
    if (dlBtn) dlBtn.disabled = true;
    if (rmBtn) { rmBtn.disabled = false; rmBtn.style.display = ""; }
  } else {
    if (status) status.textContent = "";
    if (dlBtn) { dlBtn.disabled = false; }
    if (rmBtn) { rmBtn.disabled = true; }
  }
}

// The prompt's native language is a label, not an output-language restriction.
const MOSS_DEFAULT_VOICES = [
  { id: "Junhao",  language: "zh", gender: "male",   nativeLang: "zh" },
  { id: "Zhiming", language: "zh", gender: "male",   nativeLang: "zh" },
  { id: "Weiguo",  language: "zh", gender: "male",   nativeLang: "zh" },
  { id: "Xiaoyu",  language: "zh", gender: "female", nativeLang: "zh" },
  { id: "Yuewen",  language: "zh", gender: "female", nativeLang: "zh" },
  { id: "Lingyu",  language: "zh", gender: "female", nativeLang: "zh" },
  { id: "Trump",   language: "en", gender: "male",   nativeLang: "en" },
  { id: "Ava",     language: "en", gender: "female", nativeLang: "en" },
  { id: "Bella",   language: "en", gender: "female", nativeLang: "en" },
  { id: "Adam",    language: "en", gender: "male",   nativeLang: "en" },
  { id: "Nathan",  language: "en", gender: "male",   nativeLang: "en" },
  { id: "Soyo",    language: "ja", gender: "female", nativeLang: "ja" },
  { id: "Saki",    language: "ja", gender: "female", nativeLang: "ja" },
  { id: "Mortis",  language: "ja", gender: "female", nativeLang: "ja" },
  { id: "Umiri",   language: "ja", gender: "female", nativeLang: "ja" },
  { id: "Mei",     language: "ja", gender: "female", nativeLang: "ja" },
  { id: "Anon",    language: "ja", gender: "female", nativeLang: "ja" },
  { id: "Arisa",   language: "ja", gender: "female", nativeLang: "ja" }
];
function _mossPopulateModelSelect() {
  const sel = document.getElementById("tts-moss-model-select");
  if (!sel) return;
  const saved = uiSettings.isKnown("mobmate.mossModelVoice") ? localStorage.getItem("mobmate.mossModelVoice") : null;
  const selected = saved || sel.value;
  sel.innerHTML = "";
  const installed = _mossCatalog.filter(v => v.installed);
  const selectedAvailable = selected && installed.some(v =>
    (Array.isArray(v.voices) && v.voices.length ? v.voices : MOSS_DEFAULT_VOICES)
      .some(voice => `${v.id}|${typeof voice === "string" ? voice : voice.id}` === selected));
  if (selected && !selectedAvailable) {
    const opt = document.createElement("option");
    opt.value = selected; opt.textContent = `${selected} — ${t("tts.kokoro.unavailable")}`;
    opt.disabled = true; opt.selected = true; sel.appendChild(opt);
  }
  if (!installed.length) {
    if (!selected) {
      const opt = document.createElement("option");
      opt.value = ""; opt.textContent = t("tts.moss.none_installed"); sel.appendChild(opt);
    }
    return;
  }
  installed.forEach(v => {
    // Use voices from catalog entry (may include group/language from server), fallback to defaults
    const voiceList = (Array.isArray(v.voices) && v.voices.length) ? v.voices : MOSS_DEFAULT_VOICES;
    voiceList.forEach(voice => {
      const vid = typeof voice === "string" ? voice : voice.id;
      const vLang = voice.nativeLang || voice.language || "?";
      const vGender = voice.gender || "";
      const suffix = vGender ? ` (${vLang}, ${vGender})` : ` (${vLang})`;
      const opt = document.createElement("option");
      opt.value = `${v.id}|${vid}`;
      opt.textContent = `${v.name} — ${vid}${suffix}`;
      if (selected === opt.value) opt.selected = true;
      sel.appendChild(opt);
    });
  });
  if (!Array.from(sel.options).some(o => o.selected) && sel.options.length > 0)
    sel.options[0].selected = true;
  sel.onchange = () => { localStorage.setItem("mobmate.mossModelVoice", sel.value); setTtsEngineConfig(); };
}

async function _mossDlStart(id) {
  const statusEl = document.getElementById("moss-dl-status");
  if (statusEl) statusEl.textContent = t("tts.moss.dl.downloading");
  try {
    const res = await fetch("/api/tts/rich/download", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id })
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      if (statusEl) statusEl.textContent = `${t("tts.moss.dl.error")}: ${err.message || res.status}`;
      return;
    }
  } catch (e) {
    if (statusEl) statusEl.textContent = `${t("tts.moss.dl.error")}: ${e.message}`;
    return;
  }
  _mossDlActivePollers[id] = true;
  _mossDlUpdateInfo();
  _mossDlPoll(id);
}

async function _mossDlPoll(id) {
  const statusEl = document.getElementById("moss-dl-status");
  try {
    const res = await fetch(`/api/tts/rich/download/${encodeURIComponent(id)}/status`);
    const state = await res.json();
    const pct = Math.round((state.progress || 0) * 100);
    if (id === _mossDlSelectedId) {
      if (state.status === "downloading")  statusEl && (statusEl.textContent = `${t("tts.moss.dl.downloading")} ${pct}%`);
      else if (state.status === "extracting") statusEl && (statusEl.textContent = t("tts.kokoro.dl.extracting"));
      else if (state.status === "done")    statusEl && (statusEl.textContent = `✓ ${t("tts.moss.dl.done")}`);
      else if (state.status === "error")   statusEl && (statusEl.textContent = `${t("tts.moss.dl.error")}: ${state.error || ""}`);
    }
    if (state.status === "downloading" || state.status === "extracting" || state.status === "idle") {
      setTimeout(() => _mossDlPoll(id), 1500);
    } else {
      delete _mossDlActivePollers[id];
      if (state.status === "done") {
        try { const r = await fetch("/api/tts/rich/catalog"); const all = (await r.json()).voices || []; _mossCatalog = all.filter(v => v.engine === "moss"); } catch { /* ignore */ }
        _mossDlRenderList();
        await fetchTtsEngines();
      }
    }
  } catch (e) {
    console.warn("_mossDlPoll", e);
    setTimeout(() => _mossDlPoll(id), 3000);
  }
}

async function _mossDelete(id) {
  try {
    await fetch(`/api/tts/rich/${encodeURIComponent(id)}`, { method: "DELETE" });
    try { const r = await fetch("/api/tts/rich/catalog"); const all = (await r.json()).voices || []; _mossCatalog = all.filter(v => v.engine === "moss"); } catch { /* ignore */ }
    _mossDlRenderList();
    await fetchTtsEngines();
  } catch (e) { console.warn("_mossDelete", e); }
}

// ============================================================
// Voice Conversion UI
// ============================================================
let vcRunning = false;
let vcPendingStarts = 0;
let vcPollingTimer = null;
let vcCatalogModels = [];
let vcGateResolved = false;

async function startVoiceConversionRequest(options) {
  vcPendingStarts += 1;
  try {
    const fillerStart = typeof OnsetFillerUi !== "undefined" ? await OnsetFillerUi.mobfyStarting() : null;
    const response = await fetch("/api/voice-conversion/start", {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(options)
    });
    if (response.ok) {
      vcRunning = true;
      if (fillerStart) OnsetFillerUi.mobfyStarted(fillerStart);
    }
    return response;
  } finally { vcPendingStarts -= 1; }
}

function vcIsSupportedCatalogModel(m) {
  return m?.engine === "rvc_onnx";
}

function vcModelSetupStatus(m) {
  return m?.setupState?.status || (m?.installed ? "ready" : "missing");
}

function vcSelectedCatalogModel() {
  const id = document.getElementById("vc-model-select")?.value || "";
  return vcCatalogModels.find(m => m.id === id);
}

function vcShowVcclientGuide(model) {
  const statusEl = document.getElementById("vc-model-status");
  const pth = model?.setupState?.pthFile || "*.pth";
  const idx = model?.setupState?.indexFile || "*.index";
  const modelId = model?.id || "<model-id>";
  const modelDir = `models/voiceconv/${modelId}/`;
  const msg = `${t("vc.model.needsVcclient")}\n${model?.displayName || modelId}\n${t("vc.model.vcclientModelPlacement", { dir: modelDir })}\nPTH: ${pth}\nINDEX: ${idx}\n${t("vc.model.vcclientGuide")}`;
  if (statusEl) statusEl.textContent = msg;
  try { window.open("https://github.com/w-okada/voice-changer", "_blank", "noopener"); } catch { /* ignore */ }
}

function vcFriendlyVcclientError(message, model) {
  const raw = String(message || "").trim();
  const modelId = model?.id || "<model-id>";
  const modelDir = `models/voiceconv/${modelId}/`;
  const lower = raw.toLowerCase();
  const connectionLike = lower.includes("connection") || lower.includes("refused") || lower.includes("connect") || lower.includes("timed out") || lower.includes("actively refused") || lower.includes("到達") || lower.includes("接続");
  const prefix = connectionLike ? t("vc.model.vcclientNotReachable") : raw;
  return `${prefix}\n${t("vc.model.vcclientModelPlacement", { dir: modelDir })}\n${t("vc.model.vcclientGuide")}`;
}

async function vcExportWithVcclient(model) {
  if (!model) return;
  const statusEl = document.getElementById("vc-model-status");
  const btn = document.getElementById("vc-model-install-btn") || document.getElementById("vc-gate-download-btn");
  const defaultUrl = localStorage.getItem("mobmate.vc.vcclientBaseUrl") || "http://127.0.0.1:18000";
  const url = prompt(t("vc.model.vcclientUrlPrompt"), defaultUrl);
  if (!url) return;
  const ok = confirm(`${t("vc.model.untrustedPthWarning")}\n\n${t("vc.model.vcclientExportConfirm")}`);
  if (!ok) return;
  localStorage.setItem("mobmate.vc.vcclientBaseUrl", url);
  if (btn) btn.disabled = true;
  if (statusEl) statusEl.textContent = t("vc.model.convertingWithVcclient");
  let pollTimer = null;
  const pollStatus = async () => {
    try {
      const sr = await fetch(`/api/voice-conversion/models/status?id=${encodeURIComponent(model.id || "")}`);
      const sd = await sr.json();
      const st = sd.download || {};
      const pct = Math.round(Number(st.progress || 0) * 100);
      const phase = st.status || "";
      if (st.error) {
        if (statusEl) statusEl.textContent = `✗ ${vcFriendlyVcclientError(st.error, model)}`;
        return;
      }
      if (statusEl && phase) statusEl.textContent = `${t("vc.model.convertingWithVcclient")} ${pct}% (${phase})`;
    } catch { /* ignore */ }
  };
  try {
    pollTimer = setInterval(pollStatus, 1000);
    pollStatus();
    const r = await fetch("/api/voice-conversion/models/vcclient-export", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id: model.id, vcclientBaseUrl: url, slot: 0, acceptPthRisk: true }),
    });
    const data = await r.json().catch(() => ({}));
    if (!r.ok || data.ok === false) throw new Error(data.detail || data.message || r.statusText);
    if (statusEl) statusEl.textContent = "✓ " + t("vc.model.vcclientExportDone");
    vcGateResolved = true;
    await vcLoadModelCatalog();
    setTimeout(() => vcSetGateVisibility(false), 800);
  } catch (e) {
    if (statusEl) statusEl.textContent = "✗ " + vcFriendlyVcclientError(e.message, model);
    vcShowVcclientGuide(model);
  } finally {
    if (pollTimer) clearInterval(pollTimer);
    if (btn) btn.disabled = false;
    vcUpdateModelInstallUi();
  }
}

function vcUpdateGateModelUi(models) {
  const sel = document.getElementById("vc-gate-model-select");
  const btn = document.getElementById("vc-gate-download-btn");
  const lic = document.getElementById("vc-gate-model-license");
  const licLink = document.getElementById("vc-gate-license-link");
  const candidates = (Array.isArray(models) ? models : []).filter(vcIsSupportedCatalogModel);
  if (sel) {
    sel.innerHTML = "";
    candidates.forEach(m => sel.appendChild(new Option(m.displayName || m.id || "RVC model", m.id || "")));
    sel.disabled = candidates.length === 0;
  }
  const selectedId = sel?.value || candidates[0]?.id || "";
  const model = candidates.find(m => m.id === selectedId) || candidates[0];
  if (lic) {
    lic.textContent = model
      ? [
          model.displayName || model.id,
          `License: ${model.license || "User supplied / see source"}`,
          `Source: ${model.sourcePageUrl || model.downloadUrl || ""}`,
          model.riskNotice || "",
        ].filter(Boolean).join("\n")
      : t("vc.gate.noModel");
  }
  if (licLink) {
    licLink.href = model?.licenseUrl || model?.sourcePageUrl || model?.downloadUrl || "#";
    licLink.style.display = model ? "inline" : "none";
  }
  if (btn) {
    const sizeMB = model?.sizeBytes ? Math.round(model.sizeBytes / 1000000) : "?";
    const downloadable = !!(model?.downloadUrl);
    const setupStatus = vcModelSetupStatus(model);
    btn.textContent = model
      ? (setupStatus === "needs_vcclient_export" ? t("vc.model.convertWithVcclient") : (downloadable ? t("vc.gate.downloadBtn") + ` (~${sizeMB}MB)` : t("vc.gate.bundledMissing")))
      : t("vc.gate.downloadBtn");
    btn.disabled = !model || (setupStatus !== "needs_vcclient_export" && !downloadable);
    btn.onclick = model ? (setupStatus === "needs_vcclient_export" ? () => vcExportWithVcclient(model) : (downloadable ? () => vcGateDownload(model) : null)) : null;
  }
  if (sel) sel.onchange = () => vcUpdateGateModelUi(candidates);
}

// License gate: check if any supported VC model is installed
function vcSetGateVisibility(showGate) {
  const gate = document.getElementById("vc-license-gate");
  const mobfyContent = document.getElementById("mode-mobfy-content");
  if (gate) gate.style.display = showGate ? "block" : "none";
  // Hide/show all children of mobfy-content except the gate itself
  if (mobfyContent) {
    for (const child of mobfyContent.children) {
      if (child.id === "vc-license-gate") continue;
      child.style.display = showGate ? "none" : "";
    }
  }
}

async function vcCheckLicenseGate() {
  const gate = document.getElementById("vc-license-gate");
  if (!gate) return;
  // If already resolved this session and model was installed, skip
  if (vcGateResolved) { vcSetGateVisibility(false); return; }
  try {
    const r = await fetch("/api/voice-conversion/models/catalog");
    const data = await r.json();
    const models = Array.isArray(data.models) ? data.models : [];
    const hasInstalled = models.some(m => vcIsSupportedCatalogModel(m) && m.installed);
    if (hasInstalled) {
      vcGateResolved = true;
      vcSetGateVisibility(false);
      return;
    }
    // Show gate, hide main content behind it
    vcSetGateVisibility(true);
    vcUpdateGateModelUi(models);
  } catch (e) {
    console.warn("[VC] license gate check failed", e);
  }
}

async function vcGateDownload(model) {
  const btn = document.getElementById("vc-gate-download-btn");
  const status = document.getElementById("vc-gate-status");
  if (btn) btn.disabled = true;
  if (status) status.textContent = t("vc.gate.downloading");
  try {
    const r = await fetch("/api/voice-conversion/models/download", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id: model.id, acceptLicense: true }),
    });
    const data = await r.json().catch(() => ({}));
    if (!r.ok || data.ok === false) throw new Error(data.detail || data.message || r.statusText);
    if (status) status.textContent = "✓ " + t("vc.gate.downloadComplete");
    vcGateResolved = true;
    // Hide gate after short delay
    setTimeout(() => vcSetGateVisibility(false), 1200);
    // Refresh catalog
    await vcLoadModelCatalog();
  } catch (e) {
    if (status) status.textContent = "✗ " + e.message;
    if (btn) btn.disabled = false;
  }
}

async function initVoiceConvUi() {
  // Restore saved settings from localStorage
  const rawMode = document.getElementById("vc-raw-mode");
  if (rawMode) rawMode.checked = localStorage.getItem("mobmate.vc.rawMode") === "true";
  const savedF0 = localStorage.getItem("mobmate.vc.f0Shift");
  if (savedF0) { const el = document.getElementById("vc-f0-shift"); if (el) el.value = savedF0; }
  const savedSpk = localStorage.getItem("mobmate.vc.speakerId");
  if (savedSpk) { const el = document.getElementById("vc-speaker-id"); if (el) el.value = savedSpk; }
  const savedDsp = localStorage.getItem("mobmate.vc.dspEnabled");
  if (savedDsp) { const el = document.getElementById("vc-dsp-enabled"); if (el) el.value = savedDsp; }
  const savedNeutralizerPreset = localStorage.getItem("mobmate.vc.neutralizerPreset");
  if (savedNeutralizerPreset) { const el = document.getElementById("vc-neutralizer-preset"); if (el) el.value = savedNeutralizerPreset; }
  const savedNeutralizerStrength = localStorage.getItem("mobmate.vc.neutralizerStrength");
  if (savedNeutralizerStrength) { const el = document.getElementById("vc-neutralizer-strength"); if (el) el.value = savedNeutralizerStrength; }
  const savedNoiseCleanup = localStorage.getItem("mobmate.vc.noiseCleanup");
  if (savedNoiseCleanup) { const el = document.getElementById("vc-noise-cleanup"); if (el) el.value = savedNoiseCleanup; }
  const savedWorldWet = localStorage.getItem("mobmate.vc.worldWet");
  if (savedWorldWet) { const el = document.getElementById("vc-world-wet"); if (el) el.value = savedWorldWet; }

  // Save settings on change
  rawMode?.addEventListener("change", () => {
    localStorage.setItem("mobmate.vc.rawMode", String(rawMode.checked));
    void OnsetFillerUi.prepare();
  });
  document.getElementById("vc-f0-shift")?.addEventListener("change", () => {
    localStorage.setItem("mobmate.vc.f0Shift", document.getElementById("vc-f0-shift")?.value || "0");
  });
  document.getElementById("vc-speaker-id")?.addEventListener("change", () => {
    localStorage.setItem("mobmate.vc.speakerId", document.getElementById("vc-speaker-id")?.value || "0");
  });
  document.getElementById("vc-dsp-enabled")?.addEventListener("change", () => {
    localStorage.setItem("mobmate.vc.dspEnabled", document.getElementById("vc-dsp-enabled")?.value || "true");
  });
  document.getElementById("vc-neutralizer-preset")?.addEventListener("change", () => {
    localStorage.setItem("mobmate.vc.neutralizerPreset", document.getElementById("vc-neutralizer-preset")?.value || "neutral");
  });
  document.getElementById("vc-neutralizer-strength")?.addEventListener("input", () => {
    localStorage.setItem("mobmate.vc.neutralizerStrength", document.getElementById("vc-neutralizer-strength")?.value || "60");
  });
  document.getElementById("vc-noise-cleanup")?.addEventListener("change", () => {
    localStorage.setItem("mobmate.vc.noiseCleanup", document.getElementById("vc-noise-cleanup")?.value || "dtln");
  });
  document.getElementById("vc-world-wet")?.addEventListener("input", () => {
    localStorage.setItem("mobmate.vc.worldWet", document.getElementById("vc-world-wet")?.value || "0");
  });
  document.getElementById("vc-model-select")?.addEventListener("change", () => {
    localStorage.setItem("mobmate.vc.modelId", document.getElementById("vc-model-select")?.value || "");
    vcUpdateModelInstallUi();
    void OnsetFillerUi.prepare();
  });
  document.getElementById("vc-model-install-btn")?.addEventListener("click", vcInstallSelectedModel);
  document.getElementById("vcclient-install-btn")?.addEventListener("click", installVcclient);

  await vcLoadModelCatalog();

  // License gate check on init
  if (isMobfyMode()) vcCheckLicenseGate();

  // Check initial status
  vcFetchStatus();
}

async function vcLoadModelCatalog() {
  const sel = document.getElementById("vc-model-select");
  const hint = document.getElementById("vc-no-model-hint");
  const statusEl = document.getElementById("vc-model-status");
  if (!sel) return;
  sel.disabled = true;
  sel.innerHTML = "";
  sel.appendChild(new Option(t("vc.model.none"), ""));
  try {
    const r = await fetch("/api/voice-conversion/models/catalog");
    const data = await r.json();
    vcCatalogModels = Array.isArray(data.models) ? data.models.filter(vcIsSupportedCatalogModel) : [];
    vcCatalogModels.forEach(m => {
      const status = vcModelSetupStatus(m);
      const suffix = status === "ready" ? " ✓" : (status === "needs_vcclient_export" ? ` (${t("vc.model.needsVcclientShort")})` : "");
      const label = `${m.displayName || m.id}${suffix}`;
      const opt = new Option(label, m.id || "");
      opt.title = [m.license, m.sourcePageUrl, m.licenseUrl].filter(Boolean).join(" | ");
      sel.appendChild(opt);
    });
    const saved = localStorage.getItem("mobmate.vc.modelId") || "";
    if (saved && vcCatalogModels.some(m => m.id === saved)) {
      sel.value = saved;
    } else if (vcCatalogModels.length > 0) {
      sel.value = (vcCatalogModels.find(m => m.installed)?.id || vcCatalogModels[0].id || "");
      localStorage.setItem("mobmate.vc.modelId", sel.value || "");
    }
    sel.disabled = vcCatalogModels.length === 0;
    if (hint) hint.style.display = vcCatalogModels.length === 0 ? "" : "none";
    if (statusEl && vcCatalogModels.length === 0) statusEl.textContent = t("vc.model.installHint");
  } catch (e) {
    vcCatalogModels = [];
    if (statusEl) statusEl.textContent = "✗ " + e.message;
  }
  vcUpdateModelInstallUi();
  if (isMobfyMode()) void OnsetFillerUi.prepare();
}

function vcUpdateModelInstallUi() {
  const sel = document.getElementById("vc-model-select");
  const btn = document.getElementById("vc-model-install-btn");
  const vcclientBtn = document.getElementById("vcclient-install-btn");
  const risk = document.getElementById("vc-model-risk");
  const source = document.getElementById("vc-model-source-link");
  const license = document.getElementById("vc-model-license-link");
  const summary = document.getElementById("vc-model-license-summary");
  if (!btn) return;
  const id = sel?.value || "";
  const model = vcCatalogModels.find(m => m.id === id);
  const setupStatus = vcModelSetupStatus(model);
  const downloadable = !!(model?.downloadUrl);
  btn.disabled = !model || (setupStatus !== "needs_vcclient_export" && (!!model.installed || !downloadable));
  btn.textContent = setupStatus === "ready"
    ? "✓ " + t("vc.model.loaded")
    : (setupStatus === "needs_vcclient_export" ? t("vc.model.convertWithVcclient") : (downloadable ? t("vc.model.installBtn") : t("vc.model.bundledMissing")));
  if (vcclientBtn) {
    vcclientBtn.style.display = setupStatus === "needs_vcclient_export" ? "" : "none";
    vcclientBtn.disabled = false;
    if (setupStatus === "needs_vcclient_export") updateVcclientInstallUi();
  }
  if (risk) risk.style.display = model ? "block" : "none";
  if (summary) {
    summary.textContent = model
      ? [
          model.displayName || model.id,
          `License: ${model.license || "User supplied / see source"}`,
          `Source: ${model.sourcePageUrl || model.downloadUrl || ""}`,
          setupStatus === "needs_vcclient_export" ? t("vc.model.untrustedPthWarning") : "",
          model.riskNotice || "",
        ].filter(Boolean).join("\n")
      : "";
  }
  if (source) {
    source.href = model?.sourcePageUrl || model?.downloadUrl || "#";
    source.style.display = model?.sourcePageUrl || model?.downloadUrl ? "inline" : "none";
  }
  if (license) {
    license.href = model?.licenseUrl || model?.sourcePageUrl || "#";
    license.style.display = model?.licenseUrl || model?.sourcePageUrl ? "inline" : "none";
  }
}

function vcHasSelectedModel() {
  return !!(document.getElementById("vc-model-select")?.value || "");
}

async function vcInstallSelectedModel() {
  const sel = document.getElementById("vc-model-select");
  const btn = document.getElementById("vc-model-install-btn");
  const statusEl = document.getElementById("vc-model-status");
  const id = sel?.value || "";
  const model = vcCatalogModels.find(m => m.id === id);
  if (!model) return;
  if (vcModelSetupStatus(model) === "needs_vcclient_export") {
    await vcExportWithVcclient(model);
    return;
  }
  if (model.requiresUserAgreement) {
    const ok = confirm(
      `${model.displayName || model.id}\n\n` +
      `${t("vc.model.riskTitle")}\n` +
      `${t("vc.model.riskBody")}\n\n` +
      `${model.sourcePageUrl || ""}\n${model.licenseUrl || ""}`);
    if (!ok) return;
  }
  if (btn) btn.disabled = true;
  if (statusEl) statusEl.textContent = t("vc.model.loading");
  try {
    const r = await fetch("/api/voice-conversion/models/download", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ id, acceptLicense: true }),
    });
    const data = await r.json().catch(() => ({}));
    if (!r.ok || data.ok === false) throw new Error(data.detail || data.message || r.statusText);
    if (statusEl) statusEl.textContent = "✓ " + t("vc.model.loaded");
    await vcLoadModelCatalog();
  } catch (e) {
    if (statusEl) statusEl.textContent = "✗ " + e.message;
  }
  vcUpdateModelInstallUi();
}

async function vcLoadDevices() {
  try {
    const data = await RealtimeSessionClient.listAudioInputDevices();
    const devices = Array.isArray(data.devices) ? data.devices : [];
    const inputSel = document.getElementById("vc-input-device");
    const outputSel = document.getElementById("vc-output-device");
    devices.forEach(d => {
      const id = d.id || d.name || "";
      if (!id) return;
      const label = d.name && d.name !== id ? `${d.name} (${id})` : id;
      if (inputSel) inputSel.appendChild(new Option(label, id));
      if (outputSel) outputSel.appendChild(new Option(label, id));
    });
    // Also try TTS output devices for output
    try {
      const tr = await fetch("/api/tts/devices");
      const td = await tr.json();
      const tDevices = Array.isArray(td.devices) ? td.devices : [];
      tDevices.forEach(d => {
        const id = d.id || d.name || "";
        if (!id) return;
        const existing = Array.from(outputSel?.options || []).some(o => o.value === id);
        if (!existing && outputSel) {
          const label = d.name && d.name !== id ? `${d.name} (${id})` : id;
          outputSel.appendChild(new Option(label, id));
        }
      });
    } catch { /* ignore */ }
    // Restore saved device selections
    const savedInput = localStorage.getItem("mobmate.vc.inputDevice");
    if (savedInput && inputSel) inputSel.value = savedInput;
    const savedOutput = localStorage.getItem("mobmate.vc.outputDevice");
    if (savedOutput && outputSel) outputSel.value = savedOutput;
    // Save on change
    inputSel?.addEventListener("change", () => localStorage.setItem("mobmate.vc.inputDevice", inputSel.value));
    outputSel?.addEventListener("change", () => localStorage.setItem("mobmate.vc.outputDevice", outputSel.value));
  } catch (e) { console.warn("[VC] device load failed", e); }
}

function vcGetOptions() {
  const monitorVolume = Number(document.getElementById("tts-monitor-volume-select")?.value || localStorage.getItem("mobmate.ttsMonitorVolume") || 0);
  const options = {
    modelId: document.getElementById("vc-model-select")?.value || "",
    inputDeviceId: selectedDeviceIdOrName("rt-device-select"),
    outputDeviceId: selectedDeviceIdOrName("tts-output-device-select"),
    f0ShiftSemitones: 1,
    speakerId: 0,
    prosodyNeutralizerEnabled: true,
    neutralizerPreset: "flat",
    neutralizerStrength: 1.0,
    pressureSuppressorStrength: 0.8,
    noiseCleanup: "dtln",
    f0Flatten: 0.5,
    energyCompression: 0.95,
    sharpnessSuppression: 0.75,
    worldWet: 2,
    pressureEstimatorEnabled: true,
    adaptiveConversionStrength: true,
    outputVolumePercent: 140,
    monitorVolumePercent: monitorVolume,
    vadStartThreshold: 0.54,
    vadContinueThreshold: 0.36,
    wavOutputEnabled: document.getElementById("wav-output-enabled")?.checked === true,
  };
  if (document.getElementById("vc-raw-mode")?.checked) options.mode = "rvc_raw";
  return options;
}

async function startMobfyVoice() {
  if (setupMicBlocksOtherCapture()) return;
  const el = document.getElementById("rt-status");
  const captureSelect = document.getElementById("rt-device-select");
  if (rejectUnavailableCaptureInput(captureSelect, el)) return;
  try {
    const selectedModel = vcSelectedCatalogModel();
    if (selectedModel && vcModelSetupStatus(selectedModel) === "needs_vcclient_export") {
      vcShowVcclientGuide(selectedModel);
      if (el) { el.textContent = t("vc.model.needsVcclient"); el.className = "status err"; }
      return;
    }
    await stopRealtimeDirectForModeSwitch();
    if (setupMicBlocksOtherCapture()) return;
    if (rejectUnavailableCaptureInput(captureSelect, el)) return;
    setRecordingButtons(true);
    updateMainActionLabels();
    if (el) { el.textContent = t("vc.status.starting"); el.className = "status"; }
    const guide = document.getElementById("rt-guide");
    if (guide) guide.textContent = vcHasSelectedModel() ? t("vc.status.starting") : t("vc.guide.startingPassthrough");
    const r = await startVoiceConversionRequest(vcGetOptions());
    if (!r.ok) {
      const body = await r.json().catch(async () => ({ detail: await r.text().catch(() => "") }));
      if (el) { el.textContent = t("ui.web.status.failed", { message: apiMessage(body, r.status) }); el.className = "status err"; }
      const guide = document.getElementById("rt-guide");
      if (guide) guide.textContent = apiMessage(body, r.status);
      setRecordingButtons(false);
      setMobfyWasRunning(false);
      updateMainActionLabels();
      clearNoiseWarmupUi();
      return;
    }
    vcRunning = true;
    setMobfyWasRunning(true);
    meterStart();
    startNoiseWarmupUi(8, "ui.web.guide.noiseWarmup");
    vcFetchStatus();
  } catch (e) {
    if (el) { el.textContent = t("ui.web.status.error", { message: e.message }); el.className = "status err"; }
    clearNoiseWarmupUi();
    setRecordingButtons(false);
    setMobfyWasRunning(false);
    updateMainActionLabels();
  }
}

async function stopMobfyVoice() {
  OnsetFillerUi.sessionStopping();
  const el = document.getElementById("rt-status");
  try {
    const stopBtn = document.getElementById("rt-stop-btn");
    if (stopBtn) stopBtn.disabled = true;
    if (el) { el.textContent = t("ui.web.status.stopping"); el.className = "status"; }
    await fetch("/api/voice-conversion/stop", { method: "POST" });
  } catch { /* ignore */ }
  vcRunning = false;
  OnsetFillerUi.mobfyStopped();
  setMobfyWasRunning(false);
  clearNoiseWarmupUi();
  if (vcPollingTimer) { clearInterval(vcPollingTimer); vcPollingTimer = null; }
  meterStop();
  spkStop();
  waveClear();
  setRecordingButtons(false);
  updateMainActionLabels();
  const guide = document.getElementById("rt-guide");
  if (guide) guide.textContent = t("vc.guide.stopped");
  if (el) { el.textContent = t("vc.status.off"); el.className = "status ok"; }
  vcFetchStatus();
}

async function vcToggle() {
  if (setupMicBlocksOtherCapture()) return;
  if (vcRunning) {
    OnsetFillerUi.sessionStopping();
    clearNoiseWarmupUi();
    setMobfyWasRunning(false);
    try {
      await fetch("/api/voice-conversion/stop", { method: "POST" });
    } catch { /* ignore */ }
    OnsetFillerUi.mobfyStopped();
    vcFetchStatus();
  } else {
    const opts = vcGetOptions();
    const selectedModel = vcSelectedCatalogModel();
    if (selectedModel && vcModelSetupStatus(selectedModel) === "needs_vcclient_export") {
      vcShowVcclientGuide(selectedModel);
      return;
    }
    try {
      const r = await startVoiceConversionRequest(opts);
      if (!r.ok) {
        const text = await r.text();
        alert("VC start failed: " + text);
        setMobfyWasRunning(false);
      } else {
        setMobfyWasRunning(true);
        startNoiseWarmupUi(8, "ui.web.guide.noiseWarmup");
      }
    } catch (e) { alert("VC start error: " + e.message); }
    vcFetchStatus();
  }
}

async function vcLoadModels() {
  const statusEl = document.getElementById("vc-model-status");
  if (statusEl) statusEl.textContent = t("vc.model.loading");
  try {
    const r = await fetch("/api/voice-conversion/load-models", { method: "POST" });
    const data = await r.json();
    if (data.ok) {
      if (statusEl) statusEl.textContent = "✓ " + t("vc.model.loaded");
    } else {
      if (statusEl) statusEl.textContent = "✗ " + (data.detail || t("vc.model.error"));
    }
  } catch (e) {
    if (statusEl) statusEl.textContent = "✗ " + e.message;
  }
  vcFetchStatus();
}

async function vcUnloadModels() {
  try {
    await fetch("/api/voice-conversion/unload-models", { method: "POST" });
    const statusEl = document.getElementById("vc-model-status");
    if (statusEl) statusEl.textContent = t("vc.model.unloaded");
  } catch { /* ignore */ }
  vcFetchStatus();
}

async function vcProcessFile() {
  const statusEl = document.getElementById("vc-process-status");
  const inputFile = document.getElementById("vc-input-file")?.value || "";
  const outputFile = document.getElementById("vc-output-file")?.value || "";
  if (!inputFile || !outputFile) {
    if (statusEl) statusEl.textContent = t("vc.processFile.noPath");
    return;
  }
  if (statusEl) statusEl.textContent = t("vc.processFile.processing");
  try {
    const opts = vcGetOptions();
    const r = await fetch("/api/voice-conversion/process-file", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ inputFile, outputFile, ...opts }),
    });
    const data = await r.json();
    if (data.ok) {
      const ps = data.pressureStats || {};
      if (statusEl) statusEl.textContent = `✓ ${t("vc.processFile.done")} | RMS: ${(ps.rmsReduction * 100).toFixed(0) || "-"}% | Peak: ${(ps.peakReduction * 100).toFixed(0) || "-"}%`;
    } else {
      if (statusEl) statusEl.textContent = "✗ " + (data.detail || data.message || "error");
    }
  } catch (e) {
    if (statusEl) statusEl.textContent = "✗ " + e.message;
  }
}

function vcFetchStatus() {
  fetch("/api/voice-conversion/status").then(r => r.json()).then(st => {
    const status = st.status || st.nativeStatus || st;
    vcRunning = !!(status.running ?? st.running);
    if (isMobfyMode() && typeof OnsetFillerUi !== "undefined") void OnsetFillerUi.refresh();
    const btn = document.getElementById("vc-toggle");
    const badge = document.getElementById("vc-status-badge");
    const detail = document.getElementById("vc-status-detail");

    if (vcRunning) {
      setMobfyWasRunning(true);
      if (btn) { btn.textContent = t("vc.btn.stop"); btn.classList.add("btn-active"); }
      if (badge) { badge.textContent = t("vc.status.on"); badge.className = "status-badge status-on"; }
      if (detail) detail.style.display = "";
      if (isMobfyMode()) { setRecordingButtons(true); updateMainActionLabels(); }
      if (!vcPollingTimer) vcPollingTimer = setInterval(vcFetchStatus, MOBFY_STATUS_POLL_MS);
    } else {
      if (btn) { btn.textContent = t("vc.btn.start"); btn.classList.remove("btn-active"); }
      if (badge) { badge.textContent = t("vc.status.off"); badge.className = "status-badge status-off"; }
      if (detail) detail.style.display = "";
      if (isMobfyMode()) { setRecordingButtons(false); updateMainActionLabels(); }
      if (vcPollingTimer) { clearInterval(vcPollingTimer); vcPollingTimer = null; }
    }

    // Update stats
    const rmsRaw = Number(status.inputRmsLevel ?? status.dsp?.rmsLevel ?? 0);
    const outRmsRaw = Number(status.outputRmsLevel ?? 0);
    const vad = status.vad || {};
    if (document.getElementById("vc-stat-vad")) {
      const vadEl = document.getElementById("vc-stat-vad");
      const vadOn = vad.enabled === true;
      const vadState = vadOn ? (vad.speechActive ? "speech" : "silence") : t("vc.status.unused");
      const vadProb = Number(vad.lastProb ?? NaN);
      const vadText = vadOn
        ? `${vadState}${vad.speechActive ? " (1)" : " (0)"}${Number.isFinite(vadProb) ? ` / ${vadProb.toFixed(3)}` : ""}`
        : vadState;
      if (vad.laughDetectedRecent === true) {
        vadEl.innerHTML = `${vadText} <span style="color:#ff9800;font-weight:700;">lol</span>`;
      } else {
        vadEl.textContent = vadText;
      }
    }
    const noiseReady = vad.noiseProfileReady === true;
    const noiseFloor = Number(vad.noiseFloorRms ?? NaN);
    const noiseStd = Number(vad.noiseRmsStddev ?? NaN);
    const noiseCount = Number(vad.noiseSampleCount ?? NaN);
    const noiseState = noiseReady ? t("vc.status.on") : `${t("ui.web.waiting")}${Number.isFinite(noiseCount) ? ` (${Math.min(10, Math.max(0, noiseCount))}/10)` : ""}`;
    document.getElementById("vc-stat-noise-floor") && (document.getElementById("vc-stat-noise-floor").textContent = Number.isFinite(noiseFloor)
      ? `${noiseState} / rms ${(noiseFloor * 32768).toFixed(1)}${Number.isFinite(noiseStd) ? ` ±${(noiseStd * 32768).toFixed(1)}` : ""}`
      : noiseState);
    const dsp = status.dsp || {};
    const neutralizerEl = document.getElementById("vc-stat-neutralizer");
    if (neutralizerEl) {
      const asrText = String(vad.laughAsrText || "").trim();
      const asrDecision = String(vad.laughAsrDecision || "").trim();
      const gate = status.rtRvc || {};
      if (asrText) {
        neutralizerEl.textContent = asrText;
      } else if (vad.laughAsrPending === true || gate.asrGateActive === true) {
        neutralizerEl.textContent = "ASR ...";
      } else if (asrDecision) {
        neutralizerEl.textContent = `ASR: ${asrDecision}`;
      } else {
        neutralizerEl.textContent = "ASR: -";
      }
    }
    const onnx = status.onnx || {};
    if (isMobfyMode() && vcRunning) {
      const rms = rmsRaw * 32768;
      const outRms = outRmsRaw * 32768;
      meterSetRms(rms, rms > 200 ? "speech" : "");
      waveUpdate(rms);
      waveDraw();
      spkSetLevel(outRms / 327.68);
      const outLabel = document.getElementById("rt-output-meter-label");
      if (outLabel) outLabel.textContent = "RVC";
      const guide = document.getElementById("rt-guide");
      if (guide) guide.textContent = (!status.conversionRuntimeAvailable && !vcHasSelectedModel()) ? t("vc.guide.runningPassthrough") : t("vc.guide.running");
    }
    // Model status
    const msEl = document.getElementById("vc-model-status");
    if (msEl && status.conversionRuntimeAvailable != null) {
      if (onnx.loaded) msEl.textContent = "✓ " + t("vc.model.loaded");
      else if (onnx.error) msEl.textContent = "✗ " + onnx.error;
    }
  }).catch(() => {});
}
