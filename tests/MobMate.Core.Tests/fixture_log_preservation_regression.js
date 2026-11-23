// Exercise the real fixture's log writes, without compiling C# or writing to disk.
const assert = require("node:assert/strict");
const fs = require("node:fs"), path = require("node:path"), vm = require("node:vm");
const filename = path.resolve(__dirname, "fixtures/addon_start_state_contract.js");
const files = new Map(), roots = new Set(), logs = [];
let attempts = 0, launches = 0;
const oldLog = "previous observation: retain verbatim\n";
const fakeFs = {
  readFileSync(file, encoding) {
    return files.has(file) ? files.get(file) : fs.readFileSync(file, encoding);
  },
  existsSync: file => files.has(file),
  mkdirSync(root) {
    roots.add(root);
    if (!files.has(path.join(root, "run.log"))) files.set(path.join(root, "run.log"), oldLog);
  },
  mkdtempSync(prefix) { return `${prefix}${++attempts}`; },
  writeFileSync(file, text, options) {
    if (path.basename(file) === "run.log") {
      assert.equal(options?.flag, "wx", "attempt logs require exclusive creation");
      assert.equal(files.has(file), false, "a previous log must never be overwritten");
      logs.push(file);
    }
    files.set(file, text);
  }
};
const moduleObject = { exports: {} };
const context = vm.createContext({ module: moduleObject, __filename: filename,
  require(name) {
    if (name === "node:fs") return fakeFs;
    if (name === "node:child_process") return { spawnSync() {
      launches++;
      return launches === 3 ? { status: null, error: new Error("fixture spawn unavailable") }
        : { status: 0, stdout: `attempt ${launches}`, stderr: "" };
    } };
    return require(name);
  }
});
vm.runInContext(fs.readFileSync(filename, "utf8"), context, { filename });
moduleObject.exports();
moduleObject.exports();
assert.throws(() => moduleObject.exports(), /public start state regression failed/);
assert.equal(roots.size, 1, "the same content-bound inputs must exercise the same root");
assert.equal(launches, 3);
assert.equal(logs.length, 3);
assert.equal(new Set(logs).size, 3, "each invocation needs a distinct log");
for (const root of roots) assert.equal(files.get(path.join(root, "run.log")), oldLog);
assert.match(files.get(logs[0]), /attempt 1/);
assert.match(files.get(logs[1]), /attempt 2/);
assert.match(files.get(logs[2]), /fixture spawn unavailable/);
console.log("PASS fixture logs: repeated success/failure preserve the old log and create exclusive attempt logs; no C# or disk writes");
