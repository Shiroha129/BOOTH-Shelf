import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const read = name => fs.readFile(path.join(root, name), "utf8");
const dom = new JSDOM(await read("index.html"), { url: "https://shelf.test/", runScripts: "outside-only", pretendToBeVisual: true });
const { window } = dom;
const checks = {};
const check = (name, condition) => { assert.ok(condition, name); checks[name] = true; };
const wait = async condition => {
  for (let attempt = 0; attempt < 100 && !condition(); attempt++) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(condition(), "UI update timed out");
};
const catalog = JSON.parse(await read("locales.json"));
window.fetch = async () => ({ ok: true, json: async () => catalog });
window.structuredClone = structuredClone;
window.scrollTo = () => {};
window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new window.Event("close")); };
const style = window.document.createElement("style");
style.textContent = await read("styles.css");
window.document.head.append(style);
const scripts = await Promise.all(["tests/bridge.js", "i18n.js", "parser.js", "library-filters.js", "app.js", "features.js", "product-details.js", "filter-presets.js", "notifications.js", "app-updates.js"].map(read));
window.eval(scripts.join("\n;\n") + "\nwindow.testHarness = { messages: testMessages, fixture: testFixture, emit: data => testListeners.forEach(listener => listener({ data })), size: value => downloadFileSize(value) };");
window.document.dispatchEvent(new window.Event("DOMContentLoaded"));
await window.I18n.ready;
await wait(() => window.document.querySelector("#grid .download-button"));
const element = id => window.document.getElementById(id);
const harness = window.testHarness;
harness.fixture.localFiles = [
  { id: "saved-material", itemId: "1", fileId: "101", name: "original-material-name.zip" },
  { id: "legacy-record", itemId: "1", name: "Outfit_Milfy.zip" },
  { id: "other-product", itemId: "2", fileId: "104", name: "Outfit_エク.zip" },
  { id: "replaced-file", itemId: "1", fileId: "999", name: "Outfit_エク.zip" }
];

window.document.querySelector("#grid .download-button").click();
await wait(() => window.document.querySelectorAll(".file-option").length === 4);
const options = [...window.document.querySelectorAll(".file-option")];
check("everyFileHasSizePreview", options.every(option => option.querySelector(".file-size")));
check("sizeNextToFilename", options[0].querySelector(".file-name").nextElementSibling.classList.contains("file-size"));
check("megabytePreview", options[0].querySelector(".file-size").textContent === "8.0 MB");
check("gigabytePreview", options[1].querySelector(".file-size").textContent === "1.2 GB");
check("smallFilesStillUseMegabytes", options[2].querySelector(".file-size").textContent === "0.5 MB");
check("unavailableSizeIsExplicit", options[3].querySelector(".file-size").textContent === "大小未知");
check("sizeUsesRedText", window.getComputedStyle(options[0].querySelector(".file-size")).color === "rgb(200, 50, 50)");
const downloaded = index => options[index].querySelector(".file-downloaded-badge");
check("downloadWindowRefreshesLocalFileState", harness.messages.some(message => message.action === "local-list"));
check("downloadedFileMatchesItsBoothId", !downloaded(0).hidden && downloaded(0).textContent === "已下载");
check("downloadedMarkerFollowsFileSize", options[0].querySelector(".file-size").nextElementSibling === downloaded(0));
check("legacyRecordMatchesExactNameInSameProduct", !downloaded(1).hidden);
check("otherProductAndReplacedFilesAreNotMarked", downloaded(2).hidden && downloaded(3).hidden);
check("materialAndDownloadedLabelsCanCoexist", options[0].querySelector(".material-badge") && !downloaded(0).hidden);
options[0].querySelector(".file-size").click();
check("sizeLabelStillSelectsFile", options[0].querySelector("input").checked);
const completedFile = { id: "new-file", itemId: "1", fileId: "104", name: "Outfit_エク.zip" };
harness.emit({ type: "local-files", items: [...harness.fixture.localFiles, completedFile] });
check("finishedDownloadUpdatesOpenDialog", !downloaded(2).hidden);
check("downloadStateUpdatePreservesSelection", options[0].querySelector("input").checked && element("file-selection-count").textContent === "已选 1 / 4");
harness.emit({ type: "local-files", items: [completedFile] });
check("removedLocalFilesLoseTheirMarkers", downloaded(0).hidden && downloaded(1).hidden && !downloaded(2).hidden);
downloaded(2).click();
check("downloadedFileCanStillBeSelected", options[2].querySelector("input").checked);
for (const [name, value, expected] of [
  ["belowGigabyte", 1024 ** 3 - 1, "1024.0 MB"], ["exactGigabyte", 1024 ** 3, "1.0 GB"],
  ["aboveGigabyte", 1024 ** 3 + 1, "1.0 GB"], ["zeroBytes", 0, "0.0 MB"],
  ["formattedMegabytes", "2.12 MB", "2.1 MB"], ["formattedKilobytes", "512 KB", "0.5 MB"],
  ["invalidSize", "invalid", "大小未知"], ["negativeSize", -1, "大小未知"]
]) check(name, harness.size(value) === expected);
element("close-files").click();

harness.emit({ type: "downloads", items: [
  { id: "older-download", itemId: "999", name: "old.zip", title: "Older asset", state: "completed", path: "D:\\OldDownloads\\older-asset\\old.zip", bytes: 1024 },
  { id: "unsaved", itemId: "1", name: "canceled.zip", state: "canceled" },
  { id: "active", itemId: "1", name: "active.zip", state: "downloading" }
] });
element("downloads-view").click();
let buttons = [...window.document.querySelectorAll(".download-row > button")];
check("downloadRecordOpensDirectory", buttons[0].textContent === "打开目录" && !buttons[0].disabled);
check("unsavedRecordCannotOpenDirectory", buttons[1].textContent === "打开目录" && buttons[1].disabled);
check("activeDownloadKeepsCancel", buttons[2].textContent === "取消");
const fileRequests = harness.messages.filter(message => message.action === "files").length;
buttons[0].click();
await wait(() => harness.messages.some(message => message.action === "open-download-folder" && message.payload?.id === "older-download"));
check("directoryResolvedByRecordId", !harness.messages.findLast(message => message.action === "open-download-folder").payload.path);
check("directoryActionDoesNotReselectFiles", harness.messages.filter(message => message.action === "files").length === fileRequests && !element("file-dialog").open);
buttons[2].click();
check("cancelStillUsesDownloadId", harness.messages.some(message => message.action === "downloads-cancel" && message.payload.id === "active"));

element("local-view").click();
await wait(() => element("local-view").classList.contains("active") && window.document.querySelector("#grid .item-link"));
window.document.querySelector("#grid .item-link").click();
check("localDownloadButtonAvailable", element("local-dialog").open && !element("local-download").disabled);
const started = harness.messages.filter(message => message.action === "downloads-start").length;
element("local-download").click();
await wait(() => element("file-dialog").open && window.document.querySelectorAll(".file-option").length === 4);
check("localDownloadOpensSameAsset", harness.messages.findLast(message => message.action === "files").payload.id === "1" && element("file-product").textContent === "测试衣装");
check("localDownloadClosesLocalDialog", !element("local-dialog").open);
check("additionalFilesRequireUserSelection", harness.messages.filter(message => message.action === "downloads-start").length === started);
for (const language of ["zh-CN", "zh-TW", "ja", "en", "ko"]) {
  element("language-select").value = language;
  element("language-select").dispatchEvent(new window.Event("change"));
  await wait(() => window.I18n.language === language);
  check("translatedDownloadActions_" + language, element("local-download").textContent === window.I18n.t("下载其他文件")
    && window.document.querySelector(".download-row > button").textContent === window.I18n.t("打开目录"));
  check("translatedUnknownSize_" + language, [...window.document.querySelectorAll(".file-size")].at(-1).textContent === window.I18n.t("大小未知"));
  check("translatedDownloadedMarker_" + language, window.document.querySelector(".file-downloaded-badge:not([hidden])").textContent === window.I18n.t("已下载"));
}
dom.window.close();
const result = { passed: true, checks: Object.keys(checks).length, results: checks, environment: "jsdom and test bridge; no real downloads or Explorer" };
await fs.writeFile(path.join(root, "tests/downloads-dom-result.json"), JSON.stringify(result, null, 2));
console.log(JSON.stringify({ passed: result.passed, checks: result.checks }));
