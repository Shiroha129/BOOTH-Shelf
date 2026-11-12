import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const names = ["index.html", "locales.json", "tests/bridge.js", "i18n.js", "parser.js", "library-filters.js", "app.js", "features.js", "filter-presets.js", "notifications.js", "app-updates.js"];
const [html, catalogText, bridge, ...scripts] = await Promise.all(names.map(name => fs.readFile(path.join(root, name), "utf8")));
const catalog = JSON.parse(catalogText);
const checks = {};
const check = (name, condition) => { assert.ok(condition, name); checks[name] = true; };
const wait = () => new Promise(resolve => setTimeout(resolve, 40));

async function createHarness(options = {}) {
  const dom = new JSDOM(html, { url: "https://shelf.test/", runScripts: "outside-only", pretendToBeVisual: true });
  const { window } = dom;
  window.fetch = async () => ({ ok: true, json: async () => catalog });
  window.structuredClone = structuredClone;
  window.scrollTo = () => {};
  window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new window.Event("close")); };
  window.eval([bridge, `
    const startupFixture = ${JSON.stringify(options)};
    testFixture.testing = startupFixture.testing === true;
    testFixture.boothAccess.paused = startupFixture.paused === true;
    if (startupFixture.firstUse) testFixture.snapshot = null;
    if (startupFixture.empty) testFixture.snapshot.items = [];
    if (startupFixture.pendingMode) testFixture.pendingMode = startupFixture.pendingMode;
    testFixture.settings.startWithWindows = startupFixture.enabled === true;
    const fixturePostMessage = chrome.webview.postMessage;
    chrome.webview.postMessage = message => {
      const auth = message.action === "auth-check";
      const failure = (message.action === "sync" && startupFixture.syncError)
        || (message.action === "settings-save" && startupFixture.settingsError);
      if (!auth && !failure) { fixturePostMessage(message); return; }
      testMessages.push(structuredClone(message));
      setTimeout(() => {
        const response = auth ? { id: message.id, result: { authenticated: startupFixture.auth !== false } }
          : { id: message.id, error: "Fixture request failed" };
        for (const listener of testListeners) listener({ data: response });
      }, 0);
    };
  `, ...scripts, `
    window.testHarness = {
      messages: testMessages,
      emit: message => { for (const listener of testListeners) listener({ data: message }); }
    };
  `].join("\n;\n"));
  await window.I18n.ready;
  await wait();
  await wait();
  return { window, element: id => window.document.getElementById(id), messages: window.testHarness.messages,
    syncs: () => window.testHarness.messages.filter(message => message.action === "sync"), close: () => window.close() };
}

for (const [name, options, expected] of [
  ["existingLibraryUpdatesOnce", {}, 1],
  ["firstUseDoesNotSynchronize", { firstUse: true }, 0],
  ["savedEmptyLibraryUpdates", { empty: true }, 1],
  ["unfinishedFullSyncIsPreserved", { pendingMode: "full" }, 0],
  ["unfinishedQuickSyncResumes", { pendingMode: "quick" }, 1],
  ["pausedAccessDoesNotSynchronize", { paused: true }, 0],
  ["smokeModeDoesNotSynchronize", { testing: true }, 0],
  ["failedStartupSyncDoesNotLoop", { syncError: true }, 1]
]) {
  const harness = await createHarness(options);
  try {
    check(name, harness.syncs().length === expected);
    if (expected) check(name + "UsesQuickMode", harness.syncs()[0].payload.mode === "quick");
    harness.window.testHarness.emit({ type: "auth", authenticated: true });
    await wait();
    check(name + "IgnoresRepeatedAuth", harness.syncs().length === expected);
  } finally { harness.close(); }
}

for (const manual of [false, true]) {
  const harness = await createHarness({ auth: false });
  try {
    check("waitsForLogin_" + manual, harness.syncs().length === 0);
    if (manual) harness.element("sync").click();
    else harness.window.testHarness.emit({ type: "auth", authenticated: true });
    await wait();
    harness.window.testHarness.emit({ type: "auth", authenticated: true });
    await wait();
    check(manual ? "manualSyncConsumesStartupRequest" : "loginStartsDeferredQuickSync", harness.syncs().length === 1 && harness.syncs()[0].payload.mode === "quick");
  } finally { harness.close(); }
}

const harness = await createHarness({ firstUse: true });
try {
  harness.element("settings-view").click();
  const checkbox = harness.element("start-with-windows");
  check("startupOptionIsInSettings", checkbox?.closest("#settings-panel") && !checkbox.checked);
  checkbox.click();
  await wait();
  check("enablingStartupSendsSetting", checkbox.checked && harness.messages.some(message => message.action === "settings-save" && message.payload.startWithWindows === true));
  checkbox.click();
  await wait();
  check("disablingStartupSendsSetting", !checkbox.checked && harness.messages.some(message => message.action === "settings-save" && message.payload.startWithWindows === false));
  for (const language of ["zh-CN", "zh-TW", "ja", "en", "ko"]) {
    harness.window.I18n.set(language, language);
    check("translatedStartupOption_" + language, checkbox.closest("label").textContent.trim() === harness.window.I18n.t("开机自启"));
  }
} finally { harness.close(); }

for (const options of [{ firstUse: true, enabled: true }, { firstUse: true, settingsError: true }]) {
  const harness = await createHarness(options);
  try {
    const checkbox = harness.element("start-with-windows");
    if (options.enabled) check("startupSettingSurvivesReload", checkbox.checked);
    else { checkbox.click(); await wait(); check("startupFailureRestoresCheckbox", !checkbox.checked && !checkbox.disabled && harness.element("notice").textContent.includes("Fixture request failed")); }
  } finally { harness.close(); }
}

await fs.writeFile(path.join(root, "tests/startup-dom-result.json"), JSON.stringify({ passed: true, checks: Object.keys(checks).length, results: checks }, null, 2));
console.log(JSON.stringify({ passed: true, checks: Object.keys(checks).length }));
