// Run with: node tests/MobMate.Core.Tests/whisper_turbo_ui_regression.js
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const source = fs.readFileSync("web/app.js", "utf8");
const begin = source.indexOf("async function fetchWhisperModels()");
const end = source.indexOf("/* --- Realtime ASR --- */", begin);
assert.ok(begin >= 0 && end > begin);
class Element {
  constructor() { this.options = []; this.value = ""; this.disabled = false; this.hidden = true; }
  set innerHTML(value) { assert.equal(value, ""); this.options = []; }
  appendChild(option) { this.options.push(option); }
}
const elements = Object.fromEntries(["cfg-defaultModelPath", "whisper-turbo-download", "whisper-turbo-cancel", "whisper-model-status"].map(id => [id, new Element()]));
const small = "models/whisper/ggml-small-q8_0.bin";
const turbo = "/user/models/ggml-large-v3-turbo-q8_0.bin";
let selected = small, mode = "success", posts = 0, pending;
const context = vm.createContext({
  document: { getElementById: id => elements[id], createElement: () => new Element() },
  AbortController, t: (key, vars) => key + (vars?.message || ""), apiMessage: data => data.code,
  fetchConfig: async () => {},
  fetch: async (url, options) => {
    if (url === "/api/whisper/models") return { ok: true, json: async () => ({ selected,
      models: [{ path: small, name: "Small" }, ...(selected === turbo ? [{ path: turbo, name: "Turbo" }] : [])] }) };
    assert.equal(url, "/api/whisper/model");
    assert.equal(options.method, "POST");
    assert.deepEqual(JSON.parse(options.body), { path: "models/whisper/ggml-large-v3-turbo-q8_0.bin" });
    posts++;
    if (mode === "cancel") return new Promise((resolve, reject) => {
      pending = true;
      options.signal.addEventListener("abort", () => reject(Object.assign(new Error("aborted"), { name: "AbortError" })));
    });
    if (mode === "fail") return { ok: false, json: async () => ({ code: "whisper.download_failed" }) };
    selected = turbo;
    return { ok: true, json: async () => ({ selected }) };
  }
});
vm.runInContext(source.slice(begin, end), context);
(async () => {
  await context.fetchWhisperModels();
  assert.equal(posts, 0, "first-run/list refresh must never download");
  assert.equal(elements["cfg-defaultModelPath"].value, small);
  await context.downloadWhisperTurbo();
  assert.equal(elements["cfg-defaultModelPath"].value, turbo);
  assert.equal(posts, 1);
  selected = small; mode = "fail";
  await context.downloadWhisperTurbo();
  assert.equal(elements["cfg-defaultModelPath"].value, small);
  assert.match(elements["whisper-model-status"].textContent, /download_failed/);
  mode = "cancel";
  const request = context.downloadWhisperTurbo();
  assert.ok(pending);
  assert.equal(elements["cfg-defaultModelPath"].disabled, true);
  assert.equal(elements["whisper-turbo-cancel"].hidden, false);
  await context.downloadWhisperTurbo();
  assert.equal(posts, 3, "double click must not start a second request");
  context.cancelWhisperModelRequest();
  await request;
  assert.equal(elements["cfg-defaultModelPath"].value, small);
  assert.equal(elements["cfg-defaultModelPath"].disabled, false);
  assert.equal(elements["whisper-turbo-cancel"].hidden, true);
  assert.match(elements["whisper-model-status"].textContent, /download_cancelled/);
  const keys = [...source.slice(begin, end).matchAll(/t\("(ui\.web\.(?:whisper|api\.whisper)\.[^"]+)"/g)].map(match => match[1]);
  keys.push("ui.web.whisper.downloadTurbo", "ui.web.whisper.cancel", "ui.web.whisper.optional", "ui.web.api.whisper.download_failed");
  for (const language of ["en", "ja", "ko", "zh_cn", "zh_tw"]) {
    const lines = fs.readFileSync(`libs/preset/_ui_${language}.txt`, "utf8").split(/\r?\n/);
    for (const key of keys) assert.equal(lines.filter(line => line.startsWith(`${key}=`)).length, 1, `${language}: ${key}`);
  }
  console.log("PASS Whisper UI: explicit request, success, failure, cancellation, duplicate guard, five locales");
})().catch(error => { console.error(error); process.exitCode = 1; });
