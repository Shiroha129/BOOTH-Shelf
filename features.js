function itemSections(item) { return item.sections || [item.section || "bought"]; }
function canDownload(item) { return itemSections(item).some(section => section === "bought" || section === "gift"); }
function shopKey(item) {
  const url = safeURL(item.shopUrl);
  return url ? new URL(url).origin : item.shop || "BOOTH";
}
function sourceItems() {
  let items = snapshot.items || [];
  if (currentView === "local") {
    const byId = new Map(items.map(item => [item.id, item]));
    const seen = new Set();
    items = localFiles.filter(file => !seen.has(file.itemId) && seen.add(file.itemId)).map(file =>
      byId.get(file.itemId) || { id: file.itemId, title: file.title, shop: file.shop || "", image: file.image || "", tags: [], url: "https://booth.pm/ja/items/" + file.itemId });
  } else items = items.filter(item => itemSections(item).includes(activeSection));
  return selectedShop ? items.filter(item => shopKey(item) === selectedShop) : items;
}
function rankOf(item) { return item.acquiredRanks?.[activeSection] ?? 0; }
function money(value, code = "JPY") {
  return new Intl.NumberFormat(I18n.locale, { maximumFractionDigits: code === "JPY" || code === "KRW" ? 0 : 2 }).format(value) + " " + code;
}
function converted(value) {
  const code = settings.currency || "CNY";
  const rate = code === "JPY" ? 1 : exchangeRates.rates?.[code];
  return Number.isFinite(rate) ? money(value * rate, code) : null;
}
function priceText(item) {
  if (currentView !== "local" && activeSection === "gift") return t("礼物");
  if (currentView !== "local" && activeSection === "free") return t("免费");
  if (item.purchaseJpy == null) {
    if (!itemSections(item).includes("bought")) return t(itemSections(item).includes("gift") ? "礼物" : "免费");
    return t("成交价待同步");
  }
  const original = money(item.purchaseJpy);
  const value = converted(item.purchaseJpy);
  return original + (settings.currency !== "JPY" && value ? "（≈ " + value + "）" : "");
}
function renderFinancials() {
  const spend = snapshot.spend;
  element("total-spend").textContent = spend && (spend.complete !== false || spend.orders > 0) ? converted(spend.totalJpy) || money(spend.totalJpy) : t("待全局更新");
  element("spend-detail").textContent = spend ? money(spend.totalJpy) + " · " + t("{count} 笔已核对订单", { count: spend.orders.toLocaleString(I18n.locale) }) + (spend.complete === false ? " · " + t("历史待全局更新") : "") : t("全局更新后统计历史成交金额");
  element("total-spend").title = t("已核对订单的实际支付金额，包含 BOOST 和订单费用；非日元金额按最新可用汇率估算。");
}
function renderCurrency() {
  element("currency-state").textContent = exchangeRates.error ? I18n.message(exchangeRates.error) : exchangeRates.date
    ? t("汇率日期 {date} · {source} · 折算金额仅供参考", { date: exchangeRates.date, source: exchangeRates.source })
    : t("尚无汇率缓存，点击更新汇率");
}
function renderLocalCount() {
  element("local-count").textContent = new Set(localFiles.map(file => file.itemId)).size.toLocaleString(I18n.locale);
  element("shop-count").textContent = new Set((snapshot.items || []).map(shopKey)).size.toLocaleString(I18n.locale);
}
function applyLanguage() {
  I18n.set(settings.language || "auto", systemLanguage);
  element("view-title").textContent = t({ library: "资料库", local: "本地库", shops: "店铺", downloads: "下载", settings: "设置", notifications: "通知" }[currentView]);
  renderSettings();
  renderShops();
  render();
  renderDownloads();
  renderLocalCount();
  renderNotifications();
  setAuth(authenticationState);
  if (authenticationPending && authenticationState == null) element("auth-state").textContent = t("正在检查登录…");
  if (currentView === "shops") renderShopPage();
  if (lastNotice) element("notice").textContent = I18n.message(lastNotice);
  if (lastProgress) renderSyncProgress(lastProgress);
  updateFileSelection();
  for (const badge of document.querySelectorAll(".material-badge")) { badge.textContent = t("材质文件"); badge.title = t("根据文件名识别"); }
  if (localItem) renderLocalFiles();
  if (localSelected) selectLocalFile(localSelected); else element("local-details").textContent = t("选择文件查看详情");
}
function renderShopPage() {
  const groups = new Map();
  for (const item of snapshot.items) {
    const key = shopKey(item);
    if (!groups.has(key)) groups.set(key, { key, name: item.shop || "BOOTH", image: item.shopImage || "", items: [] });
    const group = groups.get(key);
    if (!group.image && item.shopImage) group.image = item.shopImage;
    group.items.push(item);
  }
  const query = LibraryFilters.normalize(element("shop-search").value).trim();
  const matching = [...groups.values()].filter(group => !query || LibraryFilters.normalize(group.name).includes(query))
    .sort((left, right) => right.items.length - left.items.length || left.name.localeCompare(right.name));
  element("shops-result").textContent = t("{count} 家店铺", { count: matching.length.toLocaleString(I18n.locale) });
  const fragment = document.createDocumentFragment();
  for (const group of matching.slice(0, shopPage * 40)) {
    const row = document.createElement("article");
    row.className = "shop-row";
    const button = document.createElement("button");
    button.className = "shop-identity";
    const avatar = document.createElement("img");
    if (imageURL(group.image)) avatar.src = imageURL(group.image); else avatar.hidden = true;
    avatar.onerror = () => { avatar.hidden = true; };
    avatar.alt = group.name;
    avatar.loading = "lazy";
    const label = document.createElement("span");
    const name = document.createElement("strong");
    name.textContent = group.name;
    const count = document.createElement("small");
    count.textContent = t("{count} 件资产", { count: group.items.length.toLocaleString(I18n.locale) });
    label.append(name, count);
    button.append(avatar, label);
    button.onclick = () => {
      element("clear-filters").click();
      selectedShop = group.key;
      activeSection = ["bought", "gift", "free"].find(section => group.items.some(item => itemSections(item).includes(section)));
      element("selected-shop").textContent = group.name;
      showView("library");
    };
    const previews = document.createElement("div");
    previews.className = "shop-previews";
    for (const item of group.items.slice(0, 6)) {
      const preview = document.createElement("a");
      preview.title = item.title;
      preview.href = safeURL(item.url);
      preview.target = "_blank";
      preview.rel = "noreferrer";
      preview.setAttribute("aria-label", item.title);
      const image = document.createElement("img");
      if (imageURL(item.image)) image.src = imageURL(item.image); else image.hidden = true;
      image.onerror = () => { image.hidden = true; };
      image.alt = item.title;
      image.loading = "lazy";
      const placeholder = document.createElement("span");
      placeholder.className = "shop-preview-label";
      placeholder.textContent = item.title;
      preview.append(placeholder, image);
      if (canDownload(item)) preview.onclick = event => { event.preventDefault(); openFiles(item); };
      previews.append(preview);
    }
    row.append(button, previews);
    fragment.append(row);
  }
  element("shop-list").replaceChildren(fragment);
  element("more-shops").hidden = matching.length <= shopPage * 40;
}

let assetMenuItem = null;
let assetMenuCard = null;
let assetDeletion = null;
function closeAssetMenu(restoreFocus = false) {
  element("asset-menu").hidden = true;
  if (restoreFocus && assetMenuCard?.isConnected) assetMenuCard.focus({ preventScroll: true });
  assetMenuItem = null;
  assetMenuCard = null;
}
function openAssetMenu(event, item, card) {
  event.preventDefault();
  if (currentView !== "local" || !/^\d{1,20}$/.test(item.id)) return;
  assetMenuItem = item;
  assetMenuCard = card;
  element("asset-web").href = "https://booth.pm/ja/items/" + item.id;
  const menu = element("asset-menu");
  menu.hidden = false;
  const bounds = card.getBoundingClientRect();
  const keyboard = event.type === "keydown" || (!event.clientX && !event.clientY);
  menu.style.left = Math.max(8, Math.min(keyboard ? bounds.left + 16 : event.clientX, innerWidth - menu.offsetWidth - 8)) + "px";
  menu.style.top = Math.max(33, Math.min(keyboard ? bounds.top + 16 : event.clientY, innerHeight - menu.offsetHeight - 8)) + "px";
  element("asset-web").focus({ preventScroll: true });
}
element("asset-web").onclick = () => closeAssetMenu();
element("asset-delete").onclick = () => run(async () => {
  const item = assetMenuItem;
  closeAssetMenu();
  if (!item) return;
  const directories = await desktopRequest("local-asset-folders", { itemId: item.id });
  assetDeletion = { itemId: item.id, directories };
  element("asset-delete-name").textContent = item.title;
  element("asset-delete-paths").replaceChildren(...directories.map(directory => {
    const entry = document.createElement("li");
    entry.textContent = directory;
    return entry;
  }));
  element("asset-delete-error").hidden = true;
  element("asset-delete-dialog").showModal();
});
for (const id of ["dismiss-asset-delete", "cancel-asset-delete"]) element(id).onclick = () => element("asset-delete-dialog").close();
element("asset-delete-dialog").addEventListener("close", () => { assetDeletion = null; });
element("asset-delete-dialog").addEventListener("cancel", event => { if (element("confirm-asset-delete").disabled) event.preventDefault(); });
element("confirm-asset-delete").onclick = async () => {
  if (!assetDeletion) return;
  const controls = ["confirm-asset-delete", "dismiss-asset-delete", "cancel-asset-delete"].map(element);
  controls.forEach(control => { control.disabled = true; });
  try {
    localFiles = await desktopRequest("local-asset-delete", assetDeletion);
    element("asset-delete-dialog").close();
    notify(t("资产文件夹已移到回收站"));
  } catch (error) {
    element("asset-delete-error").textContent = I18n.message(error.message);
    element("asset-delete-error").hidden = false;
    try { localFiles = await desktopRequest("local-list"); } catch {}
  } finally {
    controls.forEach(control => { control.disabled = false; });
    renderLocalCount();
    if (currentView === "local") render();
  }
};
document.addEventListener("pointerdown", event => { if (!event.target.closest("#asset-menu")) closeAssetMenu(); });
document.addEventListener("keydown", event => {
  if (element("asset-menu").hidden) return;
  if (event.key === "Escape") { event.preventDefault(); closeAssetMenu(true); }
  if (event.key === "Tab") closeAssetMenu();
  if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
    event.preventDefault();
    const controls = [...element("asset-menu").querySelectorAll("[role=menuitem]")];
    const index = controls.indexOf(document.activeElement);
    controls[event.key === "Home" ? 0 : event.key === "End" ? controls.length - 1 : (index + (event.key === "ArrowUp" ? -1 : 1) + controls.length) % controls.length].focus();
  }
});
window.addEventListener("scroll", () => closeAssetMenu(), true);
window.addEventListener("resize", () => closeAssetMenu());
window.addEventListener("blur", () => closeAssetMenu());
element("window-chrome").addEventListener("wheel", event => event.preventDefault(), { passive: false });

let localItem = null;
let localSelected = null;
function openLocal(item) {
  localItem = item;
  localSelected = null;
  element("local-title").textContent = item.title;
  element("local-shop").textContent = item.shop || "";
  element("local-cover").hidden = !imageURL(item.image);
  if (imageURL(item.image)) element("local-cover").src = imageURL(item.image);
  element("local-details").textContent = t("选择文件查看详情");
  element("local-error").hidden = true;
  element("file-menu").hidden = true;
  renderLocalFiles();
  if (!element("local-dialog").open) element("local-dialog").showModal();
}
function selectLocalFile(file) {
  localSelected = file;
  const details = element("local-details");
  const name = document.createElement("strong");
  name.textContent = file.name;
  const size = document.createElement("p");
  size.textContent = bytes(file.size ?? file.bytes);
  const date = document.createElement("p");
  date.textContent = t("下载于 {date}", { date: new Date(file.createdAt).toLocaleString(I18n.locale, { hour12: false }) });
  const path = document.createElement("p");
  path.className = "path";
  path.textContent = file.path;
  details.replaceChildren(name, size, date, path);
}
function renderLocalFiles() {
  const files = localFiles.filter(file => file.itemId === localItem?.id);
  const fragment = document.createDocumentFragment();
  for (const file of files) {
    const button = document.createElement("button");
    button.className = "local-file";
    const icon = document.createElement("span");
    icon.className = "file-symbol";
    icon.textContent = /\.(zip|rar|7z)$/i.test(file.name) ? "▣" : "▤";
    const name = document.createElement("span");
    name.textContent = file.name;
    button.title = file.name;
    button.append(icon, name);
    button.onfocus = () => selectLocalFile(file);
    button.onclick = async () => { selectLocalFile(file); await localAction(file, "open"); };
    button.oncontextmenu = event => {
      event.preventDefault();
      selectLocalFile(file);
      const menu = element("file-menu");
      menu.hidden = false;
      menu.style.left = Math.min(event.clientX, innerWidth - 190) + "px";
      menu.style.top = Math.min(event.clientY, innerHeight - 110) + "px";
    };
    fragment.append(button);
  }
  element("local-file-grid").replaceChildren(fragment);
  if (!files.length) element("local-file-grid").textContent = t("该资产没有可用的本地文件");
}
async function localAction(file, action) {
  try {
    localFiles = await desktopRequest("local-action", { id: file.id, action });
    renderLocalCount();
    renderLocalFiles();
    if (currentView === "local") render();
  } catch (error) { element("local-error").textContent = I18n.message(error.message); element("local-error").hidden = false; }
}
for (const section of ["bought", "gift", "free"]) element("section-" + section).onclick = () => {
  activeSection = section;
  selectedTags.clear();
  anyTags.clear();
  excludedTags.clear();
  displayPage = 1;
  renderShops();
  render();
};
element("local-view").onclick = () => run(async () => {
  localFiles = await desktopRequest("local-list");
  element("clear-filters").click();
  showView("local");
});
element("shops-view").onclick = () => showView("shops");
element("browse-shops").onclick = element("shops-view").onclick;
element("back-shops").onclick = () => showView("shops");
element("clear-shop").onclick = () => { selectedShop = ""; renderShops(); render(); };
element("shop-search").oninput = () => { shopPage = 1; renderShopPage(); };
element("more-shops").onclick = () => { shopPage++; renderShopPage(); };
element("file-choose-directory").onclick = () => run(async () => {
  settings = await desktopRequest("choose-directory");
  element("file-directory").textContent = settings.downloadDirectory;
  renderSettings();
});
element("currency-select").onchange = () => run(async () => {
  settings = await desktopRequest("settings-save", { currency: element("currency-select").value });
  renderSettings();
  render();
});
element("language-select").onchange = () => run(async () => {
  element("language-select").disabled = true;
  try {
    settings = await desktopRequest("settings-save", { language: element("language-select").value });
    applyLanguage();
  } finally { element("language-select").disabled = false; renderSettings(); }
});
element("refresh-currency").onclick = () => run(async () => {
  element("refresh-currency").disabled = true;
  try { exchangeRates = await desktopRequest("currency-refresh", { force: true }); renderCurrency(); render(); }
  finally { element("refresh-currency").disabled = false; }
});
element("close-local").onclick = () => element("local-dialog").close();
for (const id of ["file-dialog", "local-dialog"]) {
  const dialog = element(id);
  let pressedBackdrop = false;
  const isBackdrop = event => {
    const bounds = dialog.getBoundingClientRect();
    return event.target === dialog && (event.clientX < bounds.left || event.clientX > bounds.right
      || event.clientY < bounds.top || event.clientY > bounds.bottom);
  };
  dialog.addEventListener("pointerdown", event => { pressedBackdrop = event.button === 0 && isBackdrop(event); });
  dialog.addEventListener("pointercancel", () => { pressedBackdrop = false; });
  dialog.addEventListener("click", event => {
    const dismiss = pressedBackdrop && event.button === 0 && isBackdrop(event);
    pressedBackdrop = false;
    if (dismiss) dialog.close();
  });
  dialog.addEventListener("close", () => { pressedBackdrop = false; });
}
element("local-dialog").addEventListener("close", () => { element("file-menu").hidden = true; });
element("local-refresh").onclick = () => run(async () => { localFiles = await desktopRequest("local-list"); renderLocalFiles(); renderLocalCount(); if (currentView === "local") render(); });
element("local-folder").onclick = () => { element("file-menu").hidden = true; if (localSelected) localAction(localSelected, "folder"); };
element("local-delete").onclick = () => {
  element("file-menu").hidden = true;
  if (localSelected && confirm(t("将「{name}」移到回收站？", { name: localSelected.name }))) localAction(localSelected, "delete");
};
element("local-dialog").addEventListener("click", event => { if (!event.target.closest(".context-menu")) element("file-menu").hidden = true; });
element("local-dialog").addEventListener("keydown", event => { if (event.key === "Escape") element("file-menu").hidden = true; });
function showCloseChoice() {
  if (!element("close-dialog").open) {
    element("close-remember").checked = false;
    element("close-error").hidden = true;
    element("close-dialog").showModal();
  }
}
for (const action of ["minimize", "maximize"]) element("window-" + action).onclick = () => run(() => desktopRequest("window", { action }));
element("window-close").onclick = () => run(() => desktopRequest("window", { action: "close" }));
for (const id of ["close-cancel", "dismiss-close"]) element(id).onclick = () => element("close-dialog").close();
for (const action of ["tray", "exit"]) element("close-" + action).onclick = async () => {
  const controls = ["close-tray", "close-exit", "close-cancel", "dismiss-close", "close-remember"].map(element);
  controls.forEach(control => { control.disabled = true; });
  try {
    if (element("close-remember").checked) {
      settings = await desktopRequest("settings-save", { closeBehavior: action });
      renderSettings();
    }
    await desktopRequest("window", { action });
    element("close-dialog").close();
  } catch (error) {
    element("close-error").textContent = I18n.message(error.message);
    element("close-error").hidden = false;
  } finally { controls.forEach(control => { control.disabled = false; }); }
};
element("close-behavior").onchange = async () => {
  element("close-behavior").disabled = true;
  try { settings = await desktopRequest("settings-save", { closeBehavior: element("close-behavior").value }); }
  catch (error) { notify(error.message, true); }
  finally { element("close-behavior").disabled = false; renderSettings(); }
};
element("window-drag").onpointerdown = event => { if (event.button === 0 && event.detail < 2) desktopRequest("window", { action: "drag" }); };
element("window-drag").ondblclick = () => desktopRequest("window", { action: "maximize" });
for (const edge of ["left", "right", "top", "bottom", "top-left", "top-right", "bottom-left", "bottom-right"]) {
  const handle = document.createElement("div");
  handle.className = "resize-handle " + edge;
  handle.onpointerdown = event => { if (event.button === 0) desktopRequest("window", { action: edge }); };
  document.body.append(handle);
}
