import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const read = name => fs.readFile(path.join(root, name), "utf8");
const [html, parser, reader, notifications, catalogText, boughtFixture, freeFixture] = await Promise.all(
  ["index.html", "parser.js", "account-reader.js", "notifications.js", "locales.json", "tests/fixtures/library-bought.html", "tests/fixtures/library-free.html"].map(read));
const dom = new JSDOM(html, { url: "https://accounts.booth.pm/library", runScripts: "outside-only" });
const { window } = dom;
const checks = {};
const check = (name, condition) => { assert.ok(condition, name); checks[name] = true; };
window.eval(parser);
const parse = value => new window.DOMParser().parseFromString(value, "text/html");
const card = '<article><a href="https://shop.booth.pm/items/1">Asset</a><div>Materials.zip<div data-href="https://booth.pm/downloadables/100"><button>DL</button></div><div data-dropdown-items=\'[{"deeplinkDownloadableUrl":"https://booth.pm/downloadables/100/deeplink?variation_id=11"}]\'></div></div></article>';
const sorted = '<div id="js-library-search-data" data-search-params=\'{"sort":"file_updated_at_desc","page":1,"adult":"include"}\'></div><main>' + card + '</main><a href="?sort=file_updated_at_desc&page=2">2</a><a href="?page=99">99</a>';
for (const source of ["library", "library/gifts", "library/free_downloads"]) {
  const result = window.BoothParser.parse(parse(sorted), "https://accounts.booth.pm/" + source + "?sort=file_updated_at_desc&page=1");
  check("sortAndPagination_" + source, result.sort === "file_updated_at_desc" && result.searchPage === 1 && result.maxPage === 2 && result.unfiltered && result.next.includes("sort=file_updated_at_desc"));
  check("ownership_" + source, result.items[0].ownedVariationIds.join() === "11");
}
check("sortMustBeServerConfirmed", window.BoothParser.parse(parse('<main>No gifts</main>'), "https://accounts.booth.pm/library?sort=file_updated_at_desc").sort === null);
check("filteredPagesDetected", !window.BoothParser.parse(parse(sorted.replace('"adult":"include"', '"adult":"only"')), "https://accounts.booth.pm/library?sort=file_updated_at_desc").unfiltered);
check("malformedSearchMetadataRejected", !window.BoothParser.parse(parse(sorted.replace('"sort":"file_updated_at_desc"', 'invalid')), "https://accounts.booth.pm/library?sort=file_updated_at_desc").unfiltered);
const actual = window.BoothParser.parse(parse(boughtFixture), "https://accounts.booth.pm/library?sort=file_updated_at_desc");
check("libraryHtmlOwnership", actual.items.find(item => item.id === "101")?.ownedVariationIds.includes("201"));
const freeParsed = window.BoothParser.parse(parse(freeFixture), "https://accounts.booth.pm/library/free_downloads");
check("freePageWithoutDownloadMenu", freeParsed.items[0].ownedVariationIds.length === 0 && freeParsed.items[0].files.length === 0 && freeParsed.items[0].libraryLabels.includes("Example Glasses (無料)"));
const repeatedFree = window.BoothParser.parse(parse('<main><article><a href="https://booth.pm/items/1">Asset (A)</a></article><article><a href="https://booth.pm/items/1">Asset (B)</a></article></main>'), "https://accounts.booth.pm/library/free_downloads");
check("freeLabelsPreservedAcrossCards", repeatedFree.items[0].libraryLabels.includes("Asset (A)") && repeatedFree.items[0].libraryLabels.includes("Asset (B)"));
let requests = 0;
window.fetch = async (url, options) => {
  requests++;
  check("accountReadOnlyLibrary", url === "https://accounts.booth.pm/library?sort=file_updated_at_desc&page=1" && options.credentials === "same-origin");
  return { ok: true, url, headers: { get: name => name === "Date" ? "Tue, 10 Nov 2026 01:00:00 GMT" : null }, text: async () => sorted };
};
window.eval(reader);
window.AccountReader.start("fixture", "https://accounts.booth.pm/library?sort=file_updated_at_desc&page=1", "library");
await new Promise(resolve => setTimeout(resolve, 5));
const response = window.AccountReader.take("fixture");
check("serverClockPreserved", response.data.serverDate === "Tue, 10 Nov 2026 01:00:00 GMT" && response.data.sort === "file_updated_at_desc" && requests === 1);
for (const [name, status, body, challenge] of [["rateLimit", 429, "Slow down", false], ["forbidden", 403, "Forbidden", false], ["challenge", 200, "<title>Just a moment...</title>", true], ["challenge503", 503, "<form id='challenge-form'></form>", true]]) {
  window.fetch = async url => ({ ok: status === 200, status, url, headers: { get: key => key === "Retry-After" ? "1200" : null }, text: async () => body });
  window.AccountReader.start(name, "https://accounts.booth.pm/library", "library");
  await new Promise(resolve => setTimeout(resolve, 5));
  const blocked = window.AccountReader.take(name);
  check(name + "ReportedToHost", blocked.status === status && blocked.challenge === challenge && Boolean(blocked.error));
  if (status === 429) check("retryAfterPreserved", blocked.retryAfterMs === 1200000);
}
const catalog = JSON.parse(catalogText);
window.element = id => window.document.getElementById(id);
window.safeURL = value => value;
window.run = action => action();
window.showView = () => {};
window.settings = {};
window.boothAccessState = {};
window.renderSettings = () => {};
window.notify = () => {};
window.desktopRequest = async () => ({});
window.checkFixture = check;
for (const [index, language] of ["zh-CN", "zh-TW", "ja", "en", "ko"].entries()) {
  window.I18n = { locale: language };
  window.t = (key, values = {}) => {
    assert.ok(key in catalog, "Missing translations: " + key);
    const text = index ? catalog[key][index - 1] : key;
    return text.replace(/\{(\w+)\}/g, (match, name) => values[name] ?? match);
  };
  window.eval(notifications + `
    notificationState = { enabled: true, status: "active", sourcesReady: 3, pages: 3, checked: 18, total: 7067, unavailable: 0, lastCheckedAt: "2026-11-10T01:00:00Z", items: [{ id: "notice1", itemId: "1", title: "Asset", files: ["Materials.zip"], createdAt: "2026-11-10T01:00:00Z", read: false }] };
    renderNotifications();
    checkFixture("idleFrequency_${language}", element("notification-status").textContent.includes("60") && !element("check-updates").disabled);
    checkFixture("incrementalCoverage_${language}", element("notification-coverage").textContent.includes("3 / 3") && element("notification-coverage").textContent.includes("18") && !element("notification-coverage").textContent.includes("7067"));
    checkFixture("notifications_${language}", document.querySelector(".notification-card li").textContent === "Materials.zip" && element("notification-count").textContent === "1");
    notificationState.busy = true;
    renderNotifications();
    checkFixture("busyButton_${language}", element("check-updates").disabled);
    notificationState.busy = false;
    notificationState.status = "error";
    renderNotifications();
    checkFixture("retryStatus_${language}", element("notification-status").textContent === t("部分检查未完成，保留进度并稍后重试"));
    notificationState.sources = [{ id: "bought", error: false, ownership: 0 }, { id: "free", error: true, ownership: 10 }];
    renderNotifications();
    checkFixture("sourceErrorDetails_${language}", element("notification-status").textContent.includes(t("免费")) && element("notification-status").textContent.includes("10"));
    notificationState.status = "paused";
    renderNotifications();
    checkFixture("pausedCannotCheck_${language}", element("check-updates").disabled);
    notificationState.status = "active";
    notificationState.total = 0;
    renderNotifications();
    checkFixture("noDownloadsIdle_${language}", element("check-updates").disabled && element("notification-status").textContent === t("本地库暂无已下载资产，不会自动检查更新。"));
    boothAccessState = { paused: true, reason: "rate-limit", retryAt: new Date(Date.now() + 1200000).toISOString() };
    renderBoothAccess();
    checkFixture("cooldownBlocksResume_${language}", element("booth-access-resume").disabled && !element("booth-access-banner").hidden);
    boothAccessState.retryAt = new Date(Date.now() - 1000).toISOString();
    renderBoothAccess();
    checkFixture("cooldownNeedsManualResume_${language}", !element("booth-access-resume").disabled && !element("booth-access-banner").hidden);
    boothAccessState = { paused: false };
    renderBoothAccess();
    checkFixture("resumeHidesWarning_${language}", element("booth-access-banner").hidden);
  `);
}
let savedInterval;
window.desktopRequest = async (action, payload) => {
  if (action === "settings-save") { savedInterval = payload.notificationIntervalMinutes; return { notificationIntervalMinutes: savedInterval }; }
  return { status: "active", enabled: true, total: 1, intervalMinutes: savedInterval, items: [] };
};
window.element("notification-interval").value = "180";
await window.element("notification-interval").onchange();
check("intervalSavedThroughBridge", savedInterval === 180 && window.element("notification-status").textContent.includes("180"));
dom.window.close();
const result = { passed: true, checks: Object.keys(checks).length, results: checks, environment: "jsdom; no real browser layout or live account" };
await fs.writeFile(path.join(root, "tests/notifications-dom-result.json"), JSON.stringify(result, null, 2));
console.log(JSON.stringify({ passed: result.passed, checks: result.checks }));
