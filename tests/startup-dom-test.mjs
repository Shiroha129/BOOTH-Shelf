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
    if (startupFixture.language) testFixture.settings.language = startupFixture.language;
    if (startupFixture.currency) testFixture.settings.currency = startupFixture.currency;
    if (startupFixture.unconfirmedPayments) {
      testFixture.snapshot.spend.unconfirmedOrders = 1;
      testFixture.snapshot.spend.complete = false;
    }
    if (startupFixture.spend) Object.assign(testFixture.snapshot.spend, startupFixture.spend);
    testFixture.pendingOrders = startupFixture.pendingOrders || [{ id: "123", reason: "missing-total" }];
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

for (const language of ["zh-CN", "zh-TW", "ja", "en", "ko"]) {
  const harness = await createHarness({ language, unconfirmedPayments: true });
  try {
    const notice = harness.element("notice");
    check("unconfirmedPaymentKeepsItems_" + language, harness.syncs().length === 1 && harness.window.document.querySelectorAll(".card").length > 0);
    check("unconfirmedPaymentIsWarning_" + language, notice.textContent === harness.window.I18n.t("同步完成 · {count} 件商品；部分订单金额待核对 [{code}]", { count: "5", code: "BS-SYNC-ORDERS-PRICE" }) && !notice.classList.contains("error"));
    check("unconfirmedPaymentFinancials_" + language, harness.element("spend-detail").textContent.includes(harness.window.I18n.t("部分订单金额待核对")));
    harness.element("settings-view").click();
    const button = harness.element("open-diagnostics");
    check("diagnosticsInSettings_" + language, button.closest("#settings-panel") && button.textContent === harness.window.I18n.t("打开日志目录"));
    button.click();
    await wait();
    check("diagnosticsUsesNativeFolder_" + language, harness.messages.some(message => message.action === "open-diagnostics" && message.payload === null));
    const source = "[BS-SYNC-ORDERS-DETAIL-NETWORK] 核对订单金额：BOOTH 请求超时 · 请检查网络后重试；若仍失败，可在设置中打开诊断日志。";
    const translated = harness.window.I18n.message(source);
    check("diagnosticErrorTranslated_" + language, translated.includes("BS-SYNC-ORDERS-DETAIL-NETWORK") && translated.includes(harness.window.I18n.t("核对订单金额")) && translated.includes(harness.window.I18n.t("BOOTH 请求超时")));
  } finally { harness.close(); }
}

for (const language of ["zh-CN", "zh-TW", "ja", "en", "ko"]) {
  const harness = await createHarness({ language, testing: true,
    spend: { totalJpy: 0, orders: 0, complete: false, unconfirmedOrders: 402 },
    pendingOrders: [{ id: "123", reason: "unknown" }, { id: "122", reason: "missing-total-and-lines" }]
  });
  try {
    check("unknownAmountDoesNotShowZero_" + language, harness.element("total-spend").textContent === harness.window.I18n.t("订单金额待核对")
      && !harness.element("spend-detail").textContent.includes("0 JPY"));
    const button = harness.element("show-pending-orders");
    check("legacyPendingOrdersVisible_" + language, !button.hidden && button.textContent.includes("402"));
    button.click();
    await wait();
    const dialog = harness.element("pending-orders-dialog");
    const link = dialog.querySelector("a");
    check("pendingOrdersOpenOfficialReceipt_" + language, dialog.open && link?.href === "https://accounts.booth.pm/orders/123"
      && link.target === "_blank" && link.textContent === harness.window.I18n.t("订单 {id}", { id: "123" }));
    check("legacyReasonIsHonest_" + language, dialog.textContent.includes(harness.window.I18n.t("原因尚未记录，请全局更新后再核对。")));
    check("missingAmountAndLinesExplained_" + language, dialog.textContent.includes(harness.window.I18n.t("未识别支付总额和商品明细")));
    harness.element("close-pending-orders").click();
    check("pendingOrdersClose_" + language, !dialog.open);
    harness.window.notify("链接已复制");
    harness.element("language-select").value = "en";
    harness.element("language-select").dispatchEvent(new harness.window.Event("change"));
    await wait();
    check("noticeFollowsLanguageChange_" + language, harness.element("notice").textContent === "Link copied");
  } finally { harness.close(); }
}

const rowsHarness = await createHarness({ language: "ja", currency: "JPY", testing: true,
  spend: { totalJpy: 1300, orders: 1, complete: false, unconfirmedOrders: 1, unconfirmedTotals: 0 },
  pendingOrders: [{ id: "123", reason: "missing-lines" }]
});
try {
  check("confirmedTotalShownWithoutRows", rowsHarness.element("total-spend").textContent.includes("1,300")
    && rowsHarness.element("spend-detail").textContent.includes(rowsHarness.window.I18n.t("部分订单商品明细待核对")));
  rowsHarness.element("show-pending-orders").click();
  await wait();
  rowsHarness.element("language-select").value = "ko";
  rowsHarness.element("language-select").dispatchEvent(new rowsHarness.window.Event("change"));
  await wait();
  check("openPendingDialogFollowsLanguage", rowsHarness.element("pending-orders-list").textContent.includes(rowsHarness.window.I18n.t("已确认支付总额，未识别商品明细")));
  rowsHarness.element("close-pending-orders").click();
  rowsHarness.element("form-filter").value = "digital";
  rowsHarness.element("age-filter").value = "adult";
  rowsHarness.element("save-preset").click();
  check("presetValuesUseTranslatedLabels", rowsHarness.element("preset-summary").textContent.includes(rowsHarness.window.I18n.t("下载商品"))
    && rowsHarness.element("preset-summary").textContent.includes(rowsHarness.window.I18n.t("仅 R-18")));
} finally { rowsHarness.close(); }

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
