// Run from the repository root: node tests/MobMate.Core.Tests/kokoro_model_selection_regression.js
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const source = fs.readFileSync("web/app.js", "utf8");
const start = source.indexOf("function _kokoroReadyIds()");
const end = source.indexOf("async function _kokoroDlStart(", start);
assert.ok(start >= 0 && end > start);

const saved = new Map([["mobmate.kokoroModelSpeaker", "broken|3"]]);
const select = {
  options: [], set innerHTML(_) { this.options = []; },
  appendChild(option) { this.options.push(option); },
};
const context = vm.createContext({
  document: { getElementById: id => id === "tts-kokoro-model-select" ? select : null,
    createElement: () => ({ value: "", textContent: "", selected: false, disabled: false }) },
  localStorage: { getItem: k => saved.get(k) ?? null, setItem: (k, v) => saved.set(k, v) },
  t: key => key, setTtsEngineConfig() {},
});
vm.runInContext(`let _kokoroCatalog = []; let ttsEnginesCache = null;\n${source.slice(start, end)}`, context);
vm.runInContext(`_kokoroCatalog = [
  { id: "broken", name: "Broken", installed: true, speakers: [{ id: 3, name: "a" }] },
  { id: "good", name: "Good", installed: true, speakers: [{ id: 1, name: "b" }] }
]; ttsEnginesCache = { kokoroModels: [{ id: "good" }] }; _kokoroPopulateModelSelect();`, context);
assert.equal(select.options[0].value, "broken|3");
assert.equal(select.options[0].selected, true);
assert.equal(select.options[0].disabled, true);
assert.match(select.options[0].textContent, /tts\.kokoro\.unavailable/);
assert.equal(select.options[1].value, "good|1");
assert.equal(select.options[1].disabled, false);
assert.equal(saved.get("mobmate.kokoroModelSpeaker"), "broken|3");

vm.runInContext(`ttsEnginesCache.kokoroModels.push({ id: "broken" }); _kokoroPopulateModelSelect();`, context);
assert.equal(select.options.find(o => o.value === "broken|3").disabled, false);
assert.equal(select.options.find(o => o.value === "broken|3").selected, true);
assert.equal(saved.get("mobmate.kokoroModelSpeaker"), "broken|3");
console.log("Kokoro selector: unavailable saved model preserved; healthy model offered; recovery restores selection.");
