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
const compatibility = (description, variations = [], title = "Outfit") => Array.from(harness.compatibility({ title, description, variations: variations.map(name => ({ name })) }));
const publicHair = JSON.parse(await read("tests/fixtures/product-compatibility.json"));
assert.deepEqual(Array.from(harness.compatibility(publicHair)), publicHair.expected);
assert.deepEqual(compatibility(publicHair.description.replace("【桔梗対応】撮影用衣装", "【13アバター対応】Sweet Rebellion")), publicHair.expected);
check("descriptionOnlyProductHasAllTwentyModels", true);
assert.deepEqual(compatibility(publicHair.description, ["Manuka [-25%OFF]", "ミルフィ/エク用", "Full Set"]),
  ["Manuka", "Milfy", "Eku", ...publicHair.expected.filter(name => !["Manuka", "Milfy", "Eku"].includes(name))]);
check("variantsAndDescriptionAreMergedWithoutAliasDuplicates", true);
assert.deepEqual(names(["ＭＡＮＵＫＡ", "マヌカちゃん用", "まぬか", "Manukaマヌカ", "しゅあん/Shuan", "シュアン", "海咲-Misaki-", "みさき用", "ミルフィ Milfy", "みるふぃ", "Vivian/ヴィヴィアン/비비안"]),
  ["Manuka", "Shuan", "Misaki", "Milfy", "Vivian"]);
check("mixedScriptsAndHonorificsReferToOneModel", true);
assert.deepEqual(names(["しお/Sio", "ソルト/Salt", "真央/mao", "Mayoまよ", "舞夜/Maya", "Milfy/ミルフィ", "Milltina/ミルティナ", "Lime/Chiffon/Chocolat"]),
  ["Sio", "Salt", "Mao", "Mayo", "Maya", "Milfy", "Milltina", "Lime", "Chiffon", "Chocolat"]);
check("SimilarNamesAndSharedBodiesRemainDistinct", true);
for (const heading of ["◈ 対応アバター ◈", "◆対応モデル一覧◆", "【対応アバター】", "■ 対応アバター：", "Supported avatars:", "Compatible with:", "适配模型：", "支援模型：", "대응 아바타"]) {
  assert.deepEqual(compatibility(`${heading}\n・Manuka（マヌカ）\n\n- しなの用\n\n【商品内容】\nFBX\nUnityPackage`), ["Manuka", "Shinano"], heading);
}
check("decoratedAndTranslatedCompatibilityHeadings", true);
assert.deepEqual(compatibility("対応アバター\nマヌカ\nしなの (未対応)\nMayo (not supported)\nEku (対応予定)\nミルフィ\n商品説明\nLime\n使用アバター\nKipfel\nhttps://mukumi.booth.pm/items/5813187\nクレジット\n〇オリジナル3Dモデル『愛莉』\nhttps://booth.pm/ja/items/6082686\n利用規約\nManuka"), ["Manuka", "Milfy"]);
check("unsupportedModelsAndCreditsAreNotCompatibility", true);
assert.deepEqual(compatibility("作者はManukaで撮影しています。\nMayaで制作しました。\nhttps://booth.pm/ja/items/5058077\n【撮影用アバター】\n〇オリジナル3Dモデル『愛莉』\nhttps://booth.pm/ja/items/6082686"), []);
check("proseAndUnrelatedProductLinksAreNotScannedForModelNames", true);
assert.deepEqual(compatibility("対応アバター\n【マヌカ】\nhttps://jingo1016.booth.pm/items/5058077\n〇JINGO CHANNEL様 「セレスティア」\nhttps://booth.pm/ja/items/4035411\n〇オリジナル3Dモデル『ミルティナ』\nhttps://booth.pm/ja/items/6538026\n【対応シェイプキー】\nBreast_big\n【利用規約】"), ["Manuka", "Selestia", "Milltina"]);
check("officialModelReferencesStripCreatorsAndProductLabels", true);
assert.deepEqual(compatibility("対応アバター\n別の表記\nhttps://jingo1016.booth.pm/items/5058077\nマヌカちゃん用\n〇オリジナル3Dモデル『しなの』は未対応です\nhttps://booth.pm/ja/items/6106863\n利用規約"), ["Manuka"]);
check("originalProductLinkResolvesAnUnlistedAliasWithoutInferringUnsupportedModels", true);
assert.deepEqual(compatibility("対応アバター：New Avatar 2.0 / R2-D2\nhttps://example.booth.pm/items/9999998\n商品内容\nFBX", ["new avatar 2.0"]), ["new avatar 2.0", "R2-D2"]);
check("unknownModelsKeepTheirNamesAndNumbers", true);
assert.deepEqual(compatibility("対応アバター\n1. Manuka\n2. Eku\n【作者】\nJohn Doe"), ["Manuka", "Eku"]);
check("numberedListsAndAuthorSectionAreClean", true);
assert.deepEqual(compatibility("対応アバター：エク", ["Manuka"], "【複数アバター対応】【MA対応】【PB対応】【Quest対応】Outfit"), ["Manuka", "Eku"]);
check("mergingTitleDoesNotAddTechnicalCompatibilityTags", true);
assert.deepEqual(compatibility("・禁止事項・\n購入していないユーザーへの配布は禁止。\n📌12 avatar / Sio, Kikyo, Selestia, Shinra, Moe, Manuka, Mizuki, Shinano, Milltina, Kumaly, Mayo, LUMINA\n\n- Headset / Messy-bun hair https://chocolaterice.booth.pm/items/4804565\n\n- Update -\n2023/10/30\nLayered hush cut Hair has been released\n\nContents\nUnity Package\n\n- Special thanks -\nShinraちゃんモデル / @Yoni090"),
  ["Sio", "Kikyo", "Selestia", "Shinra", "Moe", "Manuka", "Mizuki", "Shinano", "Milltina", "Kumaly", "Mayo", "Lumina"]);
check("countedAvatarListFromPublicHairDescription", true);
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
const discountedModels = ["Chocolat", "Eku", "kaguya", "manuka", "Miredo", "Mayo", "milfy", "Plum", "Ririka", "Rurune", "Salt", "Shinano"];
assert.deepEqual(names(["Full Set [-30%OFF]", "Chiffon&Chocolat [-25%OFF]", ...discountedModels.slice(1).map(name => name + " [-25%OFF]"), "Fullset30%OFF", "BOOST [-25%OFF]"]), ["Chiffon", "Chocolat", "Eku", "Kaguya", "Manuka", "Miredo", "Mayo", "Milfy", "Plum", "Ririka", "Rurune", "Salt", "Shinano"]);
check("discountedVariantsOnlyShowModelNames", true);
assert.deepEqual(names(["【２５％ＯＦＦ】Manuka", "Manuka (25% off)", "Eku【25%オフ】", "Shinano [25%割引]", "Milfy/Eku - (ミルフィ/エク用) [-25%OFF]", "[-25%OFF]"]), ["Manuka", "Eku", "Shinano", "Milfy"]);
check("discountMarkersNormalizeBeforeExclusionAndDeduplication", true);
assert.deepEqual(names(["【SALE】Manuka 専用版（¥1,500）", "Eku用 [PC/Quest対応]", "[NEW] Shinano ver.1.2", "For Milfy (ミルフィ用)", "Chocolat - 25% OFF - ¥1200", "マヌカ専用【期間限定セール】", "Manuka [マヌカ用]"]), ["Manuka", "Eku", "Shinano", "Milfy", "Chocolat"]);
check("commercialAndVersionAnnotationsAreNotModelNames", true);
assert.deepEqual(names(["【期間限定セール 11/20まで】マヌカ専用", "[50% OFF until 11/20] Manuka", "【NEW!!】Milfy", "Eku 2割引", "［発売記念価格］Chocolat", "【25%割引中】Shinano"]), ["Manuka", "Milfy", "Eku", "Chocolat", "Shinano"]);
check("promotionDatesAndCaptionsAreRemovedTogether", true);
assert.deepEqual(names(["Model 2.0", "R2-D2", "【Manuka】 / (Eku)", "Manuka (PC)", "Eku [Quest]"]), ["Model 2.0", "R2-D2", "Manuka", "Eku"]);
check("modelNumbersAndBracketedNamesArePreserved", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "Outfit", description: "対応アバター：マヌカ [-25%OFF] / ミルフィ【セール】", variations: [{ name: "Full Set [-30%OFF]" }] })), ["Manuka", "Milfy"]);
check("descriptionModelNamesUseTheSameCleanup", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "Outfit", description: "対応アバター：マヌカ / ミルフィ\n\n■利用規約\n再配布禁止", variations: [{ name: "通常版" }, { name: "支援版" }] })), ["Manuka", "Milfy"]);
check("singleGenericEditionUsesDescription", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "【しなの対応】Outfit", description: "", variations: [{ name: "商品" }] })), ["Shinano"]);
check("singleGenericEditionUsesTitle", true);
assert.deepEqual(Array.from(harness.compatibility({ title: "Outfit", description: "-Milfy-\n〇オリジナル3Dモデル「ミルフィ」\nhttps://booth.pm/ja/items/1\n\n【対応シェイプキー】\n-Milfy-\nBreast_big", variations: [{ name: "Full Set" }] })), ["Milfy"]);
check("modelReferencesDoNotIncludeShapeKeys", true);

for (const variation of harness.fixture.product.variations) variation.name += " [-25%OFF]";
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
harness.respond({ type: "local-files", items: Array.from({ length: 181 }, (_, index) => ({ id: "file" + index, itemId: String(index + 1), name: "Asset.zip" })) });
element("search").value = "Located asset";
element("search").dispatchEvent(new window.Event("input"));
element("download-state-filter").value = "downloaded";
element("download-state-filter").dispatchEvent(new window.Event("change"));
const badge = window.document.querySelector('.card[data-item-id="181"] .downloaded-badge');
check("downloadedBadgeIsKeyboardAccessible", badge?.tagName === "BUTTON" && badge.title === "在本地库中定位");
badge.click();
const localCard = window.document.querySelector('.card[data-item-id="181"]');
check("downloadedBadgeLocatesLocalAsset", harness.state().view === "local" && harness.state().page === 3 && !element("local-dialog").open && !element("product-dialog").open);
check("localLocationClearsFiltersAndHighlights", harness.state().filters.all === "" && element("download-state-filter").value === "" && localCard?.classList.contains("located-asset") && window.document.activeElement === localCard && localCard.dataset.scrolled === "true");
check("noUnhandledInterfaceErrors", errors.length === 0);
dom.window.close();
await fs.writeFile(path.join(root, "tests/product-details-dom-result.json"), JSON.stringify({ passed: true, checks: Object.keys(checks).length, results: checks }, null, 2));
console.log(JSON.stringify({ passed: true, checks: Object.keys(checks).length, environment: "jsdom with synthetic product data" }));
