// Real client/follow queue with deferred Start; no server, device, timer or browser required.
const assert = require("node:assert/strict"), fs = require("node:fs"), vm = require("node:vm");
const source = fs.readFileSync("web/app.js", "utf8");
const deferred = () => { let resolve; const promise = new Promise(r => { resolve = r; }); return { promise, resolve }; };
async function checkStopIntent(holdStart) {
  const held = deferred(), entered = deferred(), requests = [], elements = {};
  const element = () => ({ value: "en", replaceChildren() {}, appendChild() {}, setAttribute() {}, classList: { toggle() {} }, textContent: "" });
  const context = vm.createContext({ window: null, document: { getElementById: id => elements[id] ??= element(), createElement: element },
    console, t: key => key, formatBytes: () => "", localStorage: { getItem: () => null }, realtimePollToken: 0,
    setRecordingButtons() {}, meterStop() {}, spkStop() {}, waveClear() {}, isMobfyMode: () => false, updateAddonAsrDetails() {},
    apiMessage: () => "fixture error", getRealtimeSettings: () => ({ language: "en" }),
    fetch: async url => {
      requests.push(url);
      if (url === "/api/realtime/start" && requests.filter(r => r === url).length === 1) {
        entered.resolve(); if (holdStart) await held.promise;
      }
      return { ok: true, json: async () => url.startsWith("/api/addon-asr") ? {
        loading: false, operationRunning: false, profiles: {}, engines: [], state: {}, languageState: { language: "en" }
      } : { ok: true } };
    } });
  context.window = context;
  vm.runInContext(fs.readFileSync("web/addon-language.js", "utf8"), context);
  const begin = source.indexOf("const AsrAcceptanceCue ="), end = source.indexOf("async function fetchAudioInputDevices()", begin);
  assert.ok(begin >= 0 && end > begin);
  vm.runInContext(source.slice(begin, end) + ";window.RealtimeSessionClient=RealtimeSessionClient;", context);
  const client = context.RealtimeSessionClient;
  context.startRealtime = () => client.startRealtimeSession("main", { language: "en" });
  const first = client.startRealtimeSession("main", { language: "ja" });
  await entered.promise;
  if (!holdStart) await first;
  const stop = client.stopRealtimeSession("main");
  const follow = context.AddonLanguageUi.followLanguage();
  held.resolve();
  await Promise.all([first, stop, follow]);
  assert.equal(requests.filter(r => r === "/api/realtime/start").length, 1, "Stop intent must forbid language-triggered restart even while Start is pending");
  assert.equal(client.isMainActive(), false);
  assert.ok(requests.includes("/api/addon-asr/language"), "idle language selection remains usable after Stop");
  await client.startRealtimeSession("main", { language: "en" });
  assert.equal(client.isMainActive(), true, "a later explicit Start must still work");
  const before = requests.filter(r => r === "/api/realtime/start").length;
  await context.AddonLanguageUi.followLanguage();
  assert.equal(requests.filter(r => r === "/api/realtime/start").length, before + 1, "normal owned language follow must still resume");
  assert.equal(client.isMainActive(), true);

  // A wizard Stop failure is not an owned main session or a right to resume it.
  await client.startRealtimeSession("wizard", { language: "en", ttsEnabled: false });
  const originalFetch = context.fetch;
  context.fetch = async url => {
    if (url === "/api/realtime/stop") { requests.push(url); throw new Error("wizard Stop response lost"); }
    return originalFetch(url);
  };
  await assert.rejects(client.stopRealtimeSession("wizard"), /wizard Stop response lost/);
  const startsBeforeFollow = requests.filter(r => r === "/api/realtime/start").length;
  const stopsBeforeFollow = requests.filter(r => r === "/api/realtime/stop").length;
  await context.AddonLanguageUi.followLanguage();
  assert.equal(requests.filter(r => r === "/api/realtime/start").length, startsBeforeFollow, "language follow must not restart after an unconfirmed wizard Stop");
  assert.equal(requests.filter(r => r === "/api/realtime/stop").length, stopsBeforeFollow, "language follow must not retry wizard Stop implicitly");
}
(async () => {
  for (const held of [true, false]) { await checkStopIntent(held); console.log(`PASS Stop intent (held Start=${held})`); }
})().catch(error => { console.error(error); process.exitCode = 1; });
