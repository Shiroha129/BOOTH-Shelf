import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const [html, bridge, i18n, updates, catalogText] = await Promise.all(
  ["index.html", "tests/bridge.js", "i18n.js", "app-updates.js", "locales.json"].map(name => fs.readFile(path.join(root, name), "utf8")));
const catalog = JSON.parse(catalogText);
const checks = {};
const check = (name, condition) => { assert.ok(condition, name); checks[name] = true; };
const candidate = { currentVersion: "0.5.13", version: "0.5.14", releaseUrl: "https://example.test/releases/0.5.14" };
const backendErrors = [
  "无法检查软件更新，请检查网络后重试。", "请先检查软件更新。", "请先暂停同步并等待资产下载完成，再更新软件。",
  "更新下载或校验失败，旧版未更改，请重试。", "程序目录不可写，请将软件移到可写目录后更新。",
  "更新安装失败，旧版已保留，请重试。", "新版未能启动，已恢复旧版。", "自动恢复未完成，旧版备份仍保留在程序目录。",
  "正在重启以安装更新…"
];
const strings = new Set([...updates.matchAll(/"([^"\n]*[\u3400-\u9fff][^"\n]*)"/g)].map(match => match[1]).concat(backendErrors));
const markup = new JSDOM(html);
for (const section of markup.window.document.querySelectorAll("#app-update-settings, #app-update-banner")) {
  const walker = markup.window.document.createTreeWalker(section, markup.window.NodeFilter.SHOW_TEXT);
  while (walker.nextNode()) {
    const source = walker.currentNode.textContent.trim();
    if (/[\u3400-\u9fff]/.test(source)) strings.add(source);
  }
  for (const element of section.querySelectorAll("*"))
    for (const attribute of ["title", "aria-label", "placeholder"])
      if (element.hasAttribute(attribute)) strings.add(element.getAttribute(attribute));
}
markup.window.close();
const placeholders = value => [...value.matchAll(/\{\w+\}/g)].map(match => match[0]).sort();
for (const source of strings) {
  assert.ok(Array.isArray(catalog[source]) && catalog[source].length === 4, "Four translations required: " + source);
  for (const translation of catalog[source]) {
    assert.ok(typeof translation === "string" && translation.trim(), "Empty translation: " + source);
    assert.deepEqual(placeholders(translation), placeholders(source), "Translation placeholders: " + source);
  }
}
check("allUpdateStringsHaveFiveLanguages", strings.has("软件更新") && strings.has("下载并重启"));

async function createHarness({ dismissed = "", storageBlocked = false } = {}) {
  const dom = new JSDOM(html, { url: "https://shelf.test/", runScripts: "outside-only" });
  const { window } = dom;
  let fetchCount = 0;
  window.fetch = async resource => {
    assert.equal(resource, "locales.json", "Only the in-memory translation fixture may be fetched");
    fetchCount++;
    return { ok: true, json: async () => catalog };
  };
  window.structuredClone = structuredClone;
  if (dismissed) window.localStorage.setItem("booth-shelf-dismissed-app-update", dismissed);
  if (storageBlocked) {
    window.Storage.prototype.getItem = () => { throw new Error("Storage unavailable"); };
    window.Storage.prototype.setItem = () => { throw new Error("Storage unavailable"); };
  }
  window.eval([bridge, i18n, `
    const element = id => document.getElementById(id);
    const t = (source, values) => I18n.t(source, values);
    const requests = new Map();
    function desktopRequest(action, payload = null) {
      return new Promise((resolve, reject) => {
        const id = crypto.randomUUID();
        requests.set(id, { resolve, reject });
        chrome.webview.postMessage({ id, action, payload });
      });
    }
    chrome.webview.addEventListener("message", event => {
      const message = event.data;
      if (message.type === "app-update") { appUpdateState = message.state; renderAppUpdate(); return; }
      const request = requests.get(message.id);
      if (!request) return;
      requests.delete(message.id);
      if (message.error) request.reject(new Error(message.error)); else request.resolve(message.result);
    });
  `, updates, `
    window.testHarness = {
      messages: testMessages,
      get state() { return appUpdateState; },
      emit(state) {
        testFixture.appUpdate = structuredClone(state);
        for (const listener of testListeners) listener({ data: { type: "app-update", state: structuredClone(state) } });
      },
      respond(message) { for (const listener of testListeners) listener({ data: message }); }
    };
  `].join("\n;\n"));
  await window.I18n.ready;
  window.I18n.set("zh-CN", "zh-CN");
  window.renderAppUpdate();
  return { dom, window, harness: window.testHarness, get fetchCount() { return fetchCount; } };
}

const main = await createHarness();
const { dom, window, harness } = main;
const element = id => window.document.getElementById(id);
const translate = (source, values) => window.I18n.t(source, values);
const count = action => harness.messages.filter(message => message.action === action).length;
const settle = () => new Promise(resolve => window.setTimeout(resolve, 0));
const state = value => harness.emit({ ...candidate, ...value });
function hold(action) {
  const original = window.chrome.webview.postMessage;
  const messages = [];
  window.chrome.webview.postMessage = message => {
    if (message.action !== action) return original(message);
    messages.push(message);
    harness.messages.push(structuredClone(message));
  };
  return {
    messages,
    finish(result, error) {
      window.chrome.webview.postMessage = original;
      for (const message of messages) harness.respond({ id: message.id, result, error });
    }
  };
}

check("scriptFollowsNotifications", window.document.querySelector('script[src="notifications.js"]').nextElementSibling?.getAttribute("src") === "app-updates.js");
check("bannerFollowsHeader", window.document.querySelector(".heading").nextElementSibling === element("app-update-banner"));
check("idleDoesNotCheckOrDownload", harness.messages.length === 0 && element("app-update-banner").hidden && element("app-update-start").hidden);
check("initialCurrentVersion", element("app-update-current-version").textContent.includes(JSON.parse(await fs.readFile(path.join(root, "package.json"), "utf8")).version));
element("app-update-check").click();
element("app-update-check").click();
check("checkLocksImmediately", count("app-update-check") === 1 && element("app-update-check").disabled && element("app-update-status").textContent === translate("正在检查软件更新…"));
await settle();
check("asyncCheckStaysBusy", harness.state.status === "checking" && element("app-update-check").disabled);
harness.emit({ status: "current", currentVersion: "0.5.13", lastCheckedAt: "2026-11-10T01:00:00Z" });
check("currentStateUnlocksCheck", !element("app-update-check").disabled && element("app-update-status").textContent === translate("已是最新版本") && !element("app-update-last-checked").hidden);
state({ status: "available" });
check("availableOffersExplicitRestart", !element("app-update-banner").hidden && !element("app-update-start").hidden && element("app-update-banner-start").textContent === translate("下载并重启") && count("app-update-start") === 0);
element("app-update-banner-release").click();
element("app-update-release").click();
check("releaseUsesPinnedBackendOnce", count("app-update-release") === 1 && harness.messages.at(-1).payload === null);
await settle();
element("app-update-dismiss").click();
check("dismissKeepsSettingsAction", element("app-update-banner").hidden && !element("app-update-start").hidden && !element("app-update-start").disabled);
state({ status: "available" });
check("sameVersionRemainsDismissed", element("app-update-banner").hidden);
const dismissed = window.localStorage.getItem("booth-shelf-dismissed-app-update");
state({ status: "available", version: "0.5.15" });
check("nextVersionShowsBanner", !element("app-update-banner").hidden);
element("app-update-banner-start").click();
element("app-update-start").click();
check("downloadLocksAcrossBothButtons", count("app-update-start") === 1 && element("app-update-check").disabled && element("app-update-start").disabled && !element("app-update-banner-progress").hidden);
await settle();
state({ status: "downloading", received: 10485760, total: 41943040 });
for (const prefix of ["app-update", "app-update-banner"]) {
  check("knownProgress_" + prefix, !element(prefix + "-progress").hidden && element(prefix + "-meter").value === 0.25 && element(prefix + "-detail").textContent.includes("25%"));
  check("accessibleProgress_" + prefix, element(prefix + "-meter").getAttribute("aria-valuetext") === element(prefix + "-detail").textContent && element(prefix + "-meter").getAttribute("aria-describedby") === prefix + "-detail");
}
state({ status: "downloading", received: 1048576, total: 0 });
check("unknownTotalIsIndeterminate", !element("app-update-meter").hasAttribute("value") && element("app-update-detail").textContent === translate("已下载 {received} · 总大小未知", { received: "1 MiB" }));
state({ status: "downloading", received: Infinity, total: -5 });
check("invalidProgressIsSafe", !element("app-update-meter").hasAttribute("value") && !/NaN|Infinity|undefined/.test(element("app-update-detail").textContent));
state({ status: "downloading", received: 200, total: 100 });
check("progressClampsToComplete", element("app-update-meter").value === 1);
element("app-update-banner-cancel").click();
element("app-update-cancel").click();
check("cancelLocksAcrossBothButtons", count("app-update-cancel") === 1 && element("app-update-cancel").disabled);
await settle();
check("cancelReturnsToOffer", harness.state.status === "available" && element("app-update-progress").hidden && !element("app-update-start").disabled);

harness.emit({ status: "error", currentVersion: "0.5.13", error: backendErrors[0] });
check("backgroundErrorOnlyInSettings", element("app-update-banner").hidden && element("sync-status").hidden && !element("app-update-error").hidden && element("app-update-start").hidden && !window.document.querySelector("dialog[open]"));
check("checkRetryLabel", element("app-update-check").textContent === translate("重试检查更新"));
element("app-update-check").click();
await settle();
check("checkCanRetry", count("app-update-check") === 2 && harness.state.status === "checking");
state({ status: "error", received: 1024, total: 2048, error: backendErrors[3] });
check("downloadFailureOffersRetry", !element("app-update-start").hidden && element("app-update-start").textContent === translate("重试下载并重启") && element("app-update-error").textContent === translate(backendErrors[3]));
element("app-update-start").click();
check("retryResetsVisibleProgress", !element("app-update-meter").hasAttribute("value") && element("app-update-detail").textContent.includes("0 B"));
await settle();
check("downloadRetriesAfterClick", count("app-update-start") === 2 && harness.state.status === "downloading");
state({ status: "ready", error: backendErrors[2] });
check("readyKeepsPreparedUpdateAndError", element("app-update-start").textContent === translate("重启并安装更新") && !element("app-update-start").disabled && element("app-update-error").textContent === translate(backendErrors[2]) && count("app-update-apply") === 0);
element("app-update-start").click();
element("app-update-banner-start").click();
check("readyAppliesWithoutRedownload", count("app-update-apply") === 1 && count("app-update-start") === 2 && element("app-update-check").disabled);
await settle();
check("installingDisablesActions", harness.state.status === "installing" && element("app-update-start").hidden && element("app-update-cancel").hidden && element("app-update-release").disabled && element("app-update-status").textContent === translate(backendErrors[8]));

state({ status: "ready" });
const failedApply = hold("app-update-apply");
element("app-update-start").click();
failedApply.finish(null, backendErrors[5]);
await settle();
check("applyFailurePreservesReadyForRetry", harness.state.status === "ready" && !element("app-update-start").disabled && element("app-update-error").textContent === translate(backendErrors[5]));
const failedRelease = hold("app-update-release");
element("app-update-release").click();
failedRelease.finish(null, "无法打开发行说明，请重试。");
await settle();
check("releaseFailureDoesNotDiscardPreparedUpdate", harness.state.status === "ready" && !element("app-update-release").disabled);
state({ status: "ready", error: null });
check("releaseFailureIsReadable", element("app-update-error").textContent === translate("无法打开发行说明，请重试。"));
element("app-update-release").click();
await settle();
check("releaseRetryClearsLocalError", element("app-update-error").hidden);

state({ status: "available" });
const delayedStart = hold("app-update-start");
element("app-update-start").click();
check("cancelAvailableWhileStartReplyPending", !element("app-update-cancel").hidden && !element("app-update-cancel").disabled);
element("app-update-cancel").click();
await settle();
delayedStart.finish({ ...candidate, status: "downloading" });
await settle();
check("lateStartReplyCannotUndoCancel", harness.state.status === "available" && element("app-update-progress").hidden);
const delayedCheck = hold("app-update-check");
element("app-update-check").click();
state({ status: "available", version: "0.5.16" });
delayedCheck.finish({ status: "checking", currentVersion: "0.5.13" });
await settle();
check("newerPushWinsOverCheckReply", harness.state.status === "available" && harness.state.version === "0.5.16" && !element("app-update-check").disabled);
const failedStart = hold("app-update-start");
element("app-update-start").click();
failedStart.finish(null, backendErrors[3]);
await settle();
check("bridgeDownloadFailureIsRetryable", harness.state.status === "error" && !element("app-update-start").disabled && element("app-update-banner").hidden);
state({ status: "downloading", received: 1, total: 2 });
const failedCancel = hold("app-update-cancel");
element("app-update-cancel").click();
failedCancel.finish(null, "无法取消更新下载，请重试。");
await settle();
check("cancelFailureKeepsProgressAndRetry", harness.state.status === "downloading" && !element("app-update-cancel").disabled && element("app-update-meter").value === 0.5 && !element("app-update-error").hidden);

for (const [index, language] of ["zh-CN", "zh-TW", "ja", "en", "ko"].entries()) {
  window.I18n.set(language, language);
  state({ status: "available", version: "0.5.17", lastCheckedAt: "2026-11-10T01:00:00Z" });
  check("staticLanguage_" + language, element("app-update-title").textContent === translate("软件更新") && element("app-update-release").textContent === translate("查看发行说明 ↗") && element("app-update-dismiss").getAttribute("aria-label") === translate("忽略此版本的更新提醒"));
  check("dynamicLanguage_" + language, element("app-update-status").textContent === translate("发现新版本 {version}", { version: "0.5.17" }) && element("app-update-start").textContent === translate("下载并重启") && !element("app-update-note") && !element("app-update-banner-note"));
  check("localizedLastChecked_" + language, element("app-update-last-checked").textContent === translate("上次检查：{time}", { time: new Date("2026-11-10T01:00:00Z").toLocaleString(window.I18n.locale) }));
  state({ status: "downloading", received: 1024, total: 2048 });
  check("progressLanguage_" + language, element("app-update-meter").getAttribute("aria-label") === translate("软件更新下载进度") && element("app-update-detail").textContent === translate("已下载 {received} / {total}（{percent}）", { received: "1 KiB", total: "2 KiB", percent: new Intl.NumberFormat(window.I18n.locale, { style: "percent" }).format(0.5) }));
  for (const error of backendErrors) {
    state({ status: "ready", error });
    assert.equal(element("app-update-error").textContent, index ? catalog[error][index - 1] : error);
  }
  check("backendErrorsAndReadyLanguage_" + language, element("app-update-start").textContent === translate("重启并安装更新"));
}
window.I18n.set("en", "en");
const unsafe = '<img src=x onerror="window.injected=true">';
state({ status: "ready", currentVersion: unsafe, version: unsafe, releaseUrl: "javascript:window.injected=true", error: unsafe, lastCheckedAt: "invalid" });
check("remoteFieldsStayPlainText", element("app-update-error").textContent === unsafe && element("app-update-status").textContent.includes(unsafe) && element("app-update-current-version").textContent.includes(unsafe) && !window.document.querySelector("#app-update-settings img, #app-update-banner img") && !window.injected);
check("remoteUrlNeverBecomesLink", !element("app-update-release").hasAttribute("href") && !element("app-update-banner-release").hasAttribute("href"));
check("invalidDateHidden", element("app-update-last-checked").hidden);
check("noAutomaticModalOrExternalFetch", !window.document.querySelector("dialog[open]") && main.fetchCount === 1);
dom.window.close();

const reloaded = await createHarness({ dismissed });
reloaded.harness.emit({ ...candidate, status: "available" });
check("dismissSurvivesReload", reloaded.window.document.getElementById("app-update-banner").hidden);
reloaded.harness.emit({ ...candidate, status: "available", version: "0.5.15" });
reloaded.window.document.getElementById("app-update-dismiss").click();
reloaded.harness.emit({ ...candidate, status: "available" });
check("dismissRemembersEachVersion", reloaded.window.document.getElementById("app-update-banner").hidden);
reloaded.dom.window.close();
const blockedStorage = await createHarness({ storageBlocked: true });
blockedStorage.harness.emit({ ...candidate, status: "available" });
blockedStorage.window.document.getElementById("app-update-dismiss").click();
check("storageFailureStillAllowsSessionDismiss", blockedStorage.window.document.getElementById("app-update-banner").hidden && blockedStorage.harness.messages.length === 0);
blockedStorage.dom.window.close();

const appScripts = await Promise.all(["parser.js", "library-filters.js", "app.js", "features.js", "filter-presets.js", "notifications.js"].map(name => fs.readFile(path.join(root, name), "utf8")));
const integrated = new JSDOM(html, { url: "https://shelf.test/", runScripts: "outside-only", pretendToBeVisual: true });
integrated.window.structuredClone = structuredClone;
integrated.window.scrollTo = () => {};
integrated.window.fetch = async resource => {
  assert.equal(resource, "locales.json", "Integration only reads the translation fixture");
  return { ok: true, json: async () => catalog };
};
integrated.window.eval([bridge, i18n, ...appScripts, updates, `
  testFixture.appUpdate = { status: "available", currentVersion: "0.5.13", version: "0.5.18", releaseUrl: "https://example.test/releases/0.5.18" };
  window.integrationHarness = {
    messages: testMessages,
    emit(state) { for (const listener of testListeners) listener({ data: { type: "app-update", state } }); }
  };
`].join("\n;\n"));
integrated.window.document.dispatchEvent(new integrated.window.Event("DOMContentLoaded"));
await integrated.window.I18n.ready;
const integrationSettled = () => new Promise(resolve => integrated.window.setTimeout(resolve, 0));
const integratedElement = id => integrated.window.document.getElementById(id);
for (let attempt = 0; attempt < 50 && !integrated.window.integrationHarness.messages.some(message => message.action === "window" && message.payload?.action === "ready") && !integratedElement("notice").textContent; attempt++) await integrationSettled();
assert.equal(integratedElement("notice").textContent, "", "Parent initialization should not report an error");
check("parentInitialLoadRendersStoredUpdate", integratedElement("app-update-status").textContent.includes("0.5.18") && !integratedElement("app-update-banner").hidden);
integratedElement("settings-view").click();
check("parentSettingsContainsUpdateControls", !integratedElement("settings-panel").hidden && !integratedElement("app-update-start").hidden);
for (const language of ["zh-CN", "zh-TW", "ja", "en", "ko"]) {
  integratedElement("language-select").value = language;
  integratedElement("language-select").dispatchEvent(new integrated.window.Event("change"));
  await integrationSettled();
  check("parentLanguageChangeRerenders_" + language, integratedElement("app-update-status").textContent === integrated.window.I18n.t("发现新版本 {version}", { version: "0.5.18" }) && integratedElement("app-update-start").textContent === integrated.window.I18n.t("下载并重启"));
  check("conciseSettingsLanguage_" + language,
    integratedElement("update-notifications").closest(".setting-row").querySelector("h2").textContent === integrated.window.I18n.t("已下载内容更新检测")
    && integratedElement("update-notifications").closest("label").textContent === integrated.window.I18n.t("开启已下载资产的更新通知提醒")
    && integratedElement("remember-session").closest("label").textContent === integrated.window.I18n.t("记住登录状态")
    && integratedElement("notification-interval").options[0].textContent === integrated.window.I18n.t("从不"));
}
integrated.window.integrationHarness.emit({ ...candidate, status: "ready", error: backendErrors[2] });
check("parentPushRendersPreparedRetry", integratedElement("app-update-start").textContent === integrated.window.I18n.t("重启并安装更新") && integratedElement("app-update-error").textContent === integrated.window.I18n.t(backendErrors[2]));
check("parentIntegrationDoesNotDownloadAutomatically", !integrated.window.integrationHarness.messages.some(message => ["app-update-start", "app-update-apply"].includes(message.action)));
integrated.window.close();
console.log(JSON.stringify({ passed: true, checks: Object.keys(checks).length, environment: "jsdom and test bridge; no network, downloads, or installer" }));
