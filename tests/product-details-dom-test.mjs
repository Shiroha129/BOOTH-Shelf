import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { JSDOM, VirtualConsole } from "jsdom";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const read = name => fs.readFile(path.join(root, name), "utf8");
const errors = [];
const virtualConsole = new VirtualConsole();
virtualConsole.on("jsdomError", error => errors.push(error.message));
const dom = new JSDOM(await read("index.html"), { url: "https://shelf.test/", runScripts: "outside-only", pretendToBeVisual: true, virtualConsole });
const { window } = dom;
const element = id => window.document.getElementById(id);
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
window.HTMLElement.prototype.scrollIntoView = function () { this.dataset.scrolled = "true"; };
window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
window.HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new window.Event("close")); };
const scriptNames = [...window.document.querySelectorAll("script[src]")].map(script => script.getAttribute("src"));
const scripts = await Promise.all(["tests/bridge.js", ...scriptNames].map(read));
window.eval(scripts.join("\n;\n") + `
  window.testHarness = {
    messages: testMessages, fixture: testFixture, compatibility: productCompatibility,
    snapshot: value => setSnapshot(value),
    open: item => openProduct(item),
    state: () => ({ view: currentView, section: activeSection, page: displayPage, filters: getFilters(), shop: selectedShop }),
    respond: message => testListeners.forEach(listener => listener({ data: message }))
  };
`);
window.document.dispatchEvent(new window.Event("DOMContentLoaded"));
await window.I18n.ready;
await wait(() => window.document.querySelector("#grid .item-link"));
const harness = window.testHarness;
const names = variations => Array.from(harness.compatibility({ title: "Outfit", description: "", variations: variations.map(name => ({ name, price: 1000 })) }));
assert.deepEqual(names(["Full Set [20人]", "Milfy/Eku - (ミルフィ/エク用)", "Manuka - (マヌカ用)", "応援版"]), ["Milfy", "Eku", "Manuka"]);
check("groupedAvatarNamesAndAliases", true);
for (const bundle of ["FULLSET", "Fullset_ALL", "Full Pack", "Complete edition", "Complete", "ALL", "All-in-one", "All avatars", "Full Collection", "Bundle", "フル", "フルセット", "全種類セット", "全20種", "まとめ買いセット", "全套版", "全套裝"]) {
  assert.deepEqual(names([bundle, "Manuka", "支援版"]), ["Manuka"], bundle);
}
check("bundleAliasesAreExcluded", true);
for (const support of ["BOOST", "Supporter edition", "Sponsor version", "Donation", "お布施", "投げ銭", "赞助版", "贊助版"]) {
  assert.deepEqual(names(["Manuka", support]), ["Manuka"], support);
}
check("supportAliasesAreExcluded", true);
assert.deepEqual(names(["通常版", "応援版"]), []);
check("ordinaryAndSupportEditionsAreNotAvatars", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "【20アバター対応】Outfit", description: "", variations: [{ name: "Full Set" }] })), []);
check("avatarCountsAreNotModelNames", true);
assert.deepEqual(names(["Manuka", "manuka", "Ｍａｎｕｋａ用"]), ["Manuka"]);
check("namesAreNormalizedAndDeduplicated", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "Outfit", description: "対応アバター：マヌカ / ミルフィ\n\n■利用規約\n再配布禁止", variations: [{ name: "通常版" }, { name: "支援版" }] })), ["マヌカ", "ミルフィ"]);
check("singleGenericEditionUsesDescription", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "【しなの対応】Outfit", description: "", variations: [{ name: "商品" }] })), ["しなの"]);
check("singleGenericEditionUsesTitle", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "Outfit", description: "-Milfy-\n〇オリジナル3Dモデル「ミルフィ」\nhttps://booth.pm/ja/items/1\n\n【対応シェイプキー】\n-Milfy-\nBreast_big", variations: [{ name: "Full Set" }] })), ["Milfy"]);
check("modelReferencesDoNotIncludeShapeKeys", true);

window.document.querySelector("#grid .item-link").click();
await wait(() => element("product-dialog").open && !element("product-layout").hidden);
check("libraryTitleOpensEmbeddedProduct", harness.messages.some(message => message.action === "product" && message.payload.id === "1"));
check("productTitlePreservesOriginalText", element("product-title").textContent === "测试衣装");
check("originalDescriptionIsPreserved", element("product-description").textContent === harness.fixture.product.description);
check("descriptionCannotInjectMarkup", !element("product-description").querySelector("script, img") && !window.productInjected);
check("firstGalleryImage", element("product-main-image").src === harness.fixture.product.images[0]);
check("galleryThumbnails", element("product-thumbnails").children.length === 2);
element("product-next").click();
check("galleryNextImage", element("product-main-image").src === harness.fixture.product.images[1]);
element("product-next").click();
check("galleryWraps", element("product-main-image").src === harness.fixture.product.images[0]);
element("product-thumbnails").lastElementChild.click();
check("thumbnailSelectsImage", element("product-image-count").textContent === "2 / 2");
check("onlyAvatarCompatibilityIsDisplayed", element("product-compatibility").textContent === "MilfyEkuManuka");
for (const language of ["zh-CN", "zh-TW", "ja", "en", "ko"]) {
  element("language-select").value = language;
  element("language-select").dispatchEvent(new window.Event("change"));
  await wait(() => window.I18n.language === language);
  check("translatedProductLabels_" + language, element("product-dialog").querySelector(".product-kicker").textContent === window.I18n.t("商品详情")
    && element("product-next").getAttribute("aria-label") === window.I18n.t("下一张图片"));
  check("originalSellerTextDoesNotChange_" + language, element("product-description").textContent === harness.fixture.product.description);
}
element("close-product").click();
element("language-select").value = "zh-CN";
element("language-select").dispatchEvent(new window.Event("change"));
window.document.querySelector("#grid .cover").click();
await wait(() => element("product-dialog").open && !element("product-layout").hidden);
check("libraryCoverOpensEmbeddedProduct", true);
element("close-product").click();

const originalPost = window.chrome.webview.postMessage;
const requests = [];
window.chrome.webview.postMessage = message => { if (message.action === "product") requests.push(message); else originalPost(message); };
const item = harness.fixture.snapshot.items[0];
const firstOpen = harness.open(item);
check("productLoadingIsVisible", !element("product-loading").hidden && element("product-layout").hidden);
const style = window.document.createElement("style");
style.textContent = await read("styles.css");
window.document.head.append(style);
check("hiddenProductLayoutStaysHidden", window.getComputedStyle(element("product-layout")).display === "none");
const secondOpen = harness.open({ ...item, id: "2", title: "Newer item" });
harness.respond({ id: requests[1].id, result: { id: "2", title: "Newer item", description: "Second", images: ["javascript:alert(1)"], variations: [] } });
await secondOpen;
harness.respond({ id: requests[0].id, result: harness.fixture.product });
await firstOpen;
check("staleProductResponseIsIgnored", element("product-title").textContent === "Newer item" && element("product-description").textContent === "Second");
check("unsafeImageAddressIsRejected", element("product-main-image").hidden && !element("product-no-images").hidden);
check("unknownCompatibilityIsExplicit", element("product-compatibility").textContent === "未明确列出适配型号");
element("close-product").click();
const failedOpen = harness.open(item);
harness.respond({ id: requests[2].id, error: "商品页面暂时无法访问，请稍后重试。" });
await failedOpen;
check("productFailureOffersRetry", !element("product-failure").hidden && element("product-loading").hidden);
element("product-retry").click();
harness.respond({ id: requests[3].id, result: harness.fixture.product });
await wait(() => !element("product-layout").hidden);
check("productRetryWorks", element("product-failure").hidden);
element("close-product").click();
const closedOpen = harness.open(item);
element("close-product").click();
harness.respond({ id: requests[4].id, result: harness.fixture.product });
await closedOpen;
check("closedProductDoesNotReopen", !element("product-dialog").open);
window.chrome.webview.postMessage = originalPost;

const target = { ...item, id: "181", title: "Located asset", image: "https://example.test/avatar.jpg", sections: ["gift"], acquiredRanks: { gift: 180 } };
harness.snapshot({ complete: true, items: Array.from({ length: 181 }, (_, index) => ({ ...target, id: String(index + 1), title: index === 180 ? target.title : "Asset " + index, acquiredRanks: { gift: index } })) });
harness.fixture.localFiles = [{ ...harness.fixture.localFiles[0], itemId: "181", title: target.title }];
element("local-view").click();
await wait(() => element("local-view").classList.contains("active") && window.document.querySelector('#grid .card[data-item-id="181"]'));
window.document.querySelector("#grid .item-link").click();
check("localCoverNavigationAvailable", element("local-dialog").open && !element("local-show-library").hidden);
element("search").value = "hides everything";
element("search").dispatchEvent(new window.Event("input"));
element("local-show-library").click();
const state = harness.state();
const card = window.document.querySelector('#grid .card[data-item-id="181"]');
check("localCoverReturnsToLibrary", state.view === "library" && !element("local-dialog").open);
check("localCoverChoosesOwnedSection", state.section === "gift");
check("localCoverChoosesCorrectPage", state.page === 3 && element("display-page").textContent === "3 / 3");
check("blockingFiltersAreCleared", state.filters.all === "" && state.filters.tags.length === 0 && state.shop === "");
check("locatedAssetIsHighlightedAndFocused", card?.classList.contains("located-asset") && window.document.activeElement === card && card.dataset.scrolled === "true");
check("noUnhandledInterfaceErrors", errors.length === 0);
dom.window.close();
await fs.writeFile(path.join(root, "tests/product-details-dom-result.json"), JSON.stringify({ passed: true, checks: Object.keys(checks).length, results: checks }, null, 2));
console.log(JSON.stringify({ passed: true, checks: Object.keys(checks).length, environment: "jsdom with synthetic product data" }));
