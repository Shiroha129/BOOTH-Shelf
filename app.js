const element = id => document.getElementById(id);
const t = (source, values) => I18n.t(source, values);
const desktopMode = Boolean(globalThis.chrome?.webview);
const desktopRequests = new Map();
let snapshot = { items: [] };
let settings = {};
let downloads = [];
let displayPage = 1;
let currentView = "library";
let syncing = false;
let selectedTags = new Set();
let anyTags = new Set();
let excludedTags = new Set();
let filterPresets = [];
let tagCounts = [];
const pendingTagPins = new Set();
let expandedTags = false;
let noticeTimer;
let itemIndex = new Map();
let fileSelection = null;
let fileRequest = 0;
let localFiles = [];
let exchangeRates = {};
let activeSection = "bought";
let selectedShop = "";
let shopPage = 1;
let shopScrollTop = 0;
let authenticationState = null;
let authenticationPending = false;
let authenticationTimer;
let sessionBackup = {};
let systemLanguage = navigator.language;
let lastNotice = "";
let lastProgress = null;
const pageSize = 80;

function desktopRequest(action, payload = null) {
  if (!desktopMode) return Promise.reject(new Error("请使用桌面 EXE 打开程序。"));
  return new Promise((resolve, reject) => {
    const id = crypto.randomUUID();
    desktopRequests.set(id, { resolve, reject });
    chrome.webview.postMessage({ id, action, payload });
  });
}

function notify(message, error = false, completed = false) {
  clearTimeout(noticeTimer);
  element("sync-status").hidden = false;
  lastNotice = message;
  element("notice").textContent = I18n.message(message);
  element("notice").classList.toggle("error", error);
  if (completed) noticeTimer = setTimeout(() => {
    element("sync-status").hidden = true;
    element("sync-progress").hidden = true;
  }, 10000);
}

function setAuth(authenticated) {
  authenticationState = authenticated === true ? true : authenticated === false ? false : null;
  element("login").hidden = authenticated !== false;
  element("auth-state").hidden = authenticated === false;
  element("auth-state").textContent = t(authenticated === true ? "● 已登录" : "登录状态待确认");
  element("auth-state").classList.toggle("pending", authenticated == null);
  element("retry-auth").hidden = authenticated != null;
  element("settings-auth").textContent = t(authenticated === true ? "已登录" : authenticated === false ? "未登录" : "暂时无法确认登录状态");
  element("login-privacy").hidden = authenticationState !== false || currentView !== "library";
}

async function refreshAuthentication(retry = true) {
  if (authenticationPending || syncing) return;
  clearTimeout(authenticationTimer);
  authenticationPending = true;
  element("retry-auth").disabled = true;
  if (authenticationState == null) element("auth-state").textContent = t("正在检查登录…");
  try { const result = await desktopRequest("auth-check"); setAuth(result.authenticated); }
  catch { setAuth(null); }
  finally { authenticationPending = false; element("retry-auth").disabled = false; }
  if (retry && authenticationState == null) authenticationTimer = setTimeout(() => refreshAuthentication(false), 8000);
}

function setSnapshot(value) {
  snapshot = value || { items: [] };
  snapshot.items ||= [];
  if (typeof renderLocalCount === "function") renderLocalCount();
  itemIndex = new Map(snapshot.items.map(item => [item.id, item]));
  displayPage = 1;
  renderShops();
  render();
}

function renderSyncProgress(message) {
  const eta = message.remainingSeconds > 0 ? t(" · 剩余约 {minutes} 分钟", { minutes: Math.max(1, Math.ceil(message.remainingSeconds / 60)) }) : "";
  element("sync-detail").textContent = t("{page} / {total} 页 · {count} 件", message) + eta;
}

if (desktopMode) {
  document.body.dataset.desktop = "true";
  chrome.webview.addEventListener("message", event => {
    const message = event.data;
    if (message.type === "booth-progress") {
      element("sync-progress").hidden = false;
      element("sync-meter").max = message.total || 1;
      element("sync-meter").value = message.page;
      lastProgress = message;
      renderSyncProgress(message);
      notify(message.status || "正在同步…");
      return;
    }
    if (message.type === "auth") { setAuth(message.authenticated); return; }
    if (message.type === "session") { sessionBackup = message.status || {}; renderSessionBackup(); return; }
    if (message.type === "downloads") { downloads = message.items || []; renderDownloads(); return; }
    if (message.type === "local-files") { localFiles = message.items || []; renderLocalCount(); if (currentView === "local") render(); return; }
    if (message.type === "currency") { exchangeRates = message.rates || {}; renderCurrency(); render(); return; }
    if (message.type === "close-request") { showCloseChoice(); return; }
    if (message.type === "updates") { notificationState = message.state; renderNotifications(); return; }
    if (message.type === "updates-open") { showView("notifications"); return; }
    const request = desktopRequests.get(message.id);
    if (!request) return;
    desktopRequests.delete(message.id);
    if (message.error) request.reject(new Error(message.error));
    else request.resolve(message.result);
  });
}

function safeURL(value, library = false) {
  try {
    const url = new URL(value);
    if (url.protocol !== "https:" || !(url.hostname === "booth.pm" || url.hostname.endsWith(".booth.pm"))) return "";
    if (library && /^(\/[a-z-]+)?\/library\/?$/.test(url.pathname) && url.hostname === "booth.pm") return "https://accounts.booth.pm/library";
    return url.href;
  } catch { return ""; }
}

function imageURL(value) {
  try { const url = new URL(value); return url.protocol === "https:" ? url.href : ""; } catch { return ""; }
}

function getFilters() {
  return {
    all: element("search").value, any: element("any-keywords").value, exclude: element("exclude-keywords").value,
    shop: element("shop-filter").value, category: element("category-filter").value, subcategory: element("subcategory-filter").value,
    event: element("event-filter").value, form: element("form-filter").value, age: element("age-filter").value,
    tags: [...selectedTags], anyTags: [...anyTags], excludedTags: [...excludedTags]
  };
}

function fillOptions(id, values, emptyLabel, labels = {}) {
  const select = element(id);
  const previous = select.value;
  const options = [...new Set(values.filter(Boolean))].sort((left, right) => left.localeCompare(right));
  select.replaceChildren(new Option(t(emptyLabel), ""), ...options.map(value => new Option(labels[value] ? t(labels[value]) : value, value)));
  if (previous && !options.includes(previous)) select.add(new Option(previous, previous));
  select.value = previous;
}

function renderShops() {
  const items = sourceItems();
  fillOptions("shop-filter", items.map(item => item.shop), "全部店铺");
  fillOptions("category-filter", items.map(item => item.category), "全部类别");
  renderSubcategories();
  fillOptions("event-filter", items.flatMap(item => item.events || []), "全部活动");
  element("event-filter").disabled = !items.some(item => item.events?.length);
  element("event-filter").title = element("event-filter").disabled ? t("当前商品数据中没有活动信息") : "";
  fillOptions("form-filter", items.flatMap(item => item.forms || []), "全部形态", { digital: "下载商品", physical: "实体商品", factory: "受注制作" });
  tagCounts = LibraryFilters.tagCounts(items);
  renderTags();
}

function renderSubcategories() {
  const category = element("category-filter").value;
  fillOptions("subcategory-filter", sourceItems().filter(item => !category || item.category === category).map(item => item.subcategory), "全部详细分类");
}

function renderTags() {
  const query = LibraryFilters.normalize(element("tag-search").value).trim();
  const pinned = new Set(settings.pinnedTags || []);
  const activeTags = new Set([...selectedTags, ...anyTags, ...excludedTags]);
  const counts = new Map(tagCounts);
  for (const tag of activeTags) if (!counts.has(tag)) counts.set(tag, 0);
  const compareTags = ([left, leftCount], [right, rightCount]) => Number(pinned.has(right)) - Number(pinned.has(left)) || rightCount - leftCount || left.localeCompare(right);
  const matching = tagCounts.filter(([tag]) => !query || LibraryFilters.normalize(tag).includes(query)).sort(compareTags);
  const visible = expandedTags || query ? matching : matching.filter(([tag]) => pinned.has(tag));
  const remainingSelected = [...counts].filter(([tag]) => activeTags.has(tag) && !visible.some(([shown]) => shown === tag));
  const fragment = document.createDocumentFragment();
  for (const [tag, count] of [...remainingSelected, ...visible].sort(compareTags)) {
    const chip = document.createElement("div");
    chip.className = "tag-chip" + (selectedTags.has(tag) ? " selected" : anyTags.has(tag) ? " any-selected" : excludedTags.has(tag) ? " excluded" : "");
    chip.dataset.tag = tag;
    const pin = document.createElement("button");
    pin.className = "tag-pin";
    pin.type = "button";
    pin.textContent = pinned.has(tag) ? "★" : "☆";
    pin.title = t(pinned.has(tag) ? "取消收藏置顶：{tag}" : "收藏并置顶：{tag}", { tag });
    pin.setAttribute("aria-label", pin.title);
    pin.setAttribute("aria-pressed", String(pinned.has(tag)));
    pin.disabled = pendingTagPins.has(tag);
    pin.onclick = async () => {
      if (pendingTagPins.has(tag)) return;
      pendingTagPins.add(tag);
      renderTags();
      try { settings.pinnedTags = await desktopRequest("tag-pin", { tag, pinned: !pinned.has(tag) }); }
      catch (error) { notify(error.message, true); }
      finally { pendingTagPins.delete(tag); renderTags(); }
    };
    const button = document.createElement("button");
    button.className = "tag-button";
    button.title = t("包含标签：{tag}", { tag });
    button.setAttribute("aria-pressed", String(selectedTags.has(tag)));
    const label = document.createElement("span");
    label.textContent = tag;
    const number = document.createElement("span");
    number.className = "tag-count";
    number.textContent = count;
    button.append(label, number);
    const toggle = group => {
      const selected = group.has(tag);
      for (const tags of [selectedTags, anyTags, excludedTags]) tags.delete(tag);
      if (!selected) group.add(tag);
      displayPage = 1;
      renderTags();
      render();
    };
    button.onclick = () => toggle(selectedTags);
    chip.append(pin, button);
    for (const [name, group, className, title] of [["任一", anyTags, "tag-any", "任一标签：{tag}"], ["排除", excludedTags, "tag-exclude", "排除标签：{tag}"]]) {
      const mode = document.createElement("button");
      mode.type = "button";
      mode.className = "tag-mode " + className;
      mode.textContent = t(name);
      mode.title = t(title, { tag });
      mode.setAttribute("aria-label", mode.title);
      mode.setAttribute("aria-pressed", String(group.has(tag)));
      mode.onclick = () => toggle(group);
      chip.append(mode);
    }
    fragment.append(chip);
  }
  element("tag-buttons").replaceChildren(fragment);
  element("tag-buttons").hidden = visible.length + remainingSelected.length === 0;
  element("tag-selected").textContent = [["包含", selectedTags], ["任一", anyTags], ["排除", excludedTags]]
    .filter(([, tags]) => tags.size).map(([name, tags]) => t(name) + " " + tags.size).join(" · ");
  element("tag-empty").hidden = query ? matching.length > 0 : tagCounts.length > 0;
  element("tag-empty").textContent = t(query ? "未找到标签" : "同步时收集标签");
  element("more-tags").hidden = tagCounts.length === 0;
  element("more-tags").setAttribute("aria-expanded", String(expandedTags));
  element("more-tags").textContent = t(expandedTags ? "收起标签" : "展开全部标签");
}

function render() {
  closeAssetMenu();
  renderPresets();
  const items = sourceItems();
  const filters = getFilters();
  const filtered = items.filter(item => LibraryFilters.matches(item, filters));
  const sort = element("sort").value;
  if (sort === "name") filtered.sort((left, right) => left.title.localeCompare(right.title, I18n.locale));
  if (sort === "shop") filtered.sort((left, right) => (left.shop || "").localeCompare(right.shop || "", I18n.locale));
  if (sort === "default" && currentView !== "local") filtered.sort((left, right) => rankOf(left) - rankOf(right));
  const pages = Math.max(1, Math.ceil(filtered.length / pageSize));
  displayPage = Math.min(displayPage, pages);
  element("total").textContent = String(snapshot.items.length);
  element("nav-count").textContent = snapshot.items.length.toLocaleString(I18n.locale);
  renderFinancials();
  for (const section of ["bought", "gift", "free"]) {
    const button = element("section-" + section);
    button.setAttribute("aria-selected", String(activeSection === section));
    button.querySelector("span").textContent = snapshot.items.filter(item => itemSections(item).includes(section) && (!selectedShop || shopKey(item) === selectedShop)).length.toLocaleString(I18n.locale);
  }
  element("library-tabs").hidden = currentView === "local";
  element("shop-selection").hidden = !selectedShop;
  element("last-sync").textContent = snapshot.syncedAt ? new Date(snapshot.syncedAt).toLocaleString(I18n.locale, { hour12: false }) : t("尚未同步");
  element("results").textContent = t("{count} 件商品", { count: filtered.length.toLocaleString(I18n.locale) });
  element("partial-state").textContent = snapshot.complete === false ? t("部分同步结果") : "";
  const fragment = document.createDocumentFragment();
  for (const item of filtered.slice((displayPage - 1) * pageSize, displayPage * pageSize)) {
    const card = element("card-template").content.cloneNode(true);
    card.querySelector(".cover").href = safeURL(item.url);
    const image = card.querySelector("img");
    const source = imageURL(item.image);
    if (source) {
      image.src = source;
      image.alt = item.title;
      image.onload = () => { image.nextElementSibling.hidden = true; };
      image.onerror = () => { image.hidden = true; };
    } else image.hidden = true;
    card.querySelector(".card-shop").textContent = item.shop || "BOOTH";
    const link = card.querySelector(".item-link");
    link.textContent = item.title;
    link.title = item.title;
    link.href = safeURL(item.url);
    card.querySelector(".item-id").textContent = "#" + item.id;
    card.querySelector(".card-price").textContent = priceText(item);
    card.querySelector(".card-price").title = t("订单中的商品金额（含 BOOST）；折算金额为参考值，非当时实际扣款币种金额");
    card.querySelector(".download-button").onclick = () => openFiles(item);
    if (currentView !== "local" && (activeSection === "free" || !canDownload(item))) card.querySelector(".download-button").remove();
    if (currentView === "local") {
      card.querySelector(".download-button").remove();
      const localCard = card.querySelector(".card");
      localCard.tabIndex = 0;
      localCard.setAttribute("aria-haspopup", "menu");
      localCard.oncontextmenu = event => openAssetMenu(event, item, localCard);
      localCard.onkeydown = event => {
        if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) openAssetMenu(event, item, localCard);
      };
      for (const link of card.querySelectorAll(".cover, .item-link")) {
        link.removeAttribute("target");
        link.href = "#";
        link.onclick = event => { event.preventDefault(); openLocal(item); };
      }
      const count = document.createElement("span");
      count.className = "local-file-count";
      count.textContent = t("{count} 个文件", { count: localFiles.filter(file => file.itemId === item.id).length });
      card.querySelector(".card-bottom").append(count);
    }
    fragment.append(card);
  }
  element("grid").replaceChildren(fragment);
  element("empty").hidden = filtered.length > 0;
  element("empty").querySelector("h2").textContent = t(items.length ? "没有符合条件的商品" : "资料库为空");
  element("empty").querySelector("p").textContent = t(items.length ? "调整关键词或标签筛选" : "登录后同步商品");
  element("login-privacy").hidden = authenticationState !== false || currentView !== "library";
  element("empty-sync").hidden = items.length > 0 || currentView === "local";
  if (currentView === "local" && !items.length) {
    element("empty").querySelector("h2").textContent = t("本地库为空");
    element("empty").querySelector("p").textContent = t("下载完成的资产会显示在这里");
  }
  element("pagination").hidden = filtered.length <= pageSize;
  element("display-page").textContent = displayPage + " / " + pages;
  element("previous-page").disabled = displayPage === 1;
  element("next-page").disabled = displayPage === pages;
}

function showView(view) {
  closeAssetMenu();
  const fromShops = currentView === "shops";
  if (fromShops && view !== "shops") shopScrollTop = window.scrollY;
  currentView = view;
  for (const name of ["library", "downloads", "settings", "shops", "notifications"]) element(name + "-panel").hidden = view !== name && !(name === "library" && view === "local");
  for (const [id, name] of [["all-view", "library"], ["local-view", "local"], ["shops-view", "shops"], ["downloads-view", "downloads"], ["settings-view", "settings"], ["notifications-view", "notifications"]]) element(id).classList.toggle("active", view === name);
  element("view-title").textContent = t({ library: "资料库", local: "本地库", shops: "店铺", downloads: "下载", settings: "设置", notifications: "通知" }[view]);
  if (view === "notifications") run(async () => { notificationState = await desktopRequest("updates-list"); renderNotifications(); });
  if (view === "library" || view === "local") { displayPage = 1; renderShops(); render(); }
  if (view === "shops") renderShopPage();
  if (view === "shops") window.scrollTo({ top: shopScrollTop, left: 0, behavior: "instant" });
  else if (fromShops && view === "library" && selectedShop) window.scrollTo({ top: 0, left: 0, behavior: "instant" });
}

function bytes(value) {
  if (!value) return "0 B";
  const power = Math.min(3, Math.floor(Math.log(value) / Math.log(1024)));
  return (value / 1024 ** power).toFixed(power ? 1 : 0) + " " + ["B", "KB", "MB", "GB"][power];
}

function renderDownloads() {
  element("download-count").textContent = downloads.filter(item => ["queued", "downloading"].includes(item.state)).length || "";
  element("download-empty").hidden = downloads.length > 0;
  const fragment = document.createDocumentFragment();
  const states = { queued: "排队中", downloading: "下载中", completed: "已完成", failed: "失败", canceled: "已取消", interrupted: "已中断" };
  for (const record of downloads) {
    const row = document.createElement("article");
    row.className = "download-row";
    const info = document.createElement("div");
    const name = document.createElement("strong");
    name.textContent = record.name;
    const title = document.createElement("p");
    title.textContent = record.title;
    const status = document.createElement("p");
    status.className = "download-status";
    status.textContent = t(states[record.state] || record.state) + " · " + bytes(record.bytes) + (record.total > 0 ? " / " + bytes(record.total) : "") + (record.error ? " · " + I18n.message(record.error) : "");
    info.append(name, title, status);
    if (record.state === "downloading") {
      const progress = document.createElement("progress");
      if (record.total > 0) { progress.max = record.total; progress.value = record.bytes || 0; }
      info.append(progress);
    }
    if (record.path && record.state === "completed") {
      const path = document.createElement("p");
      path.className = "path";
      path.textContent = record.path;
      info.append(path);
    }
    row.append(info);
    const action = document.createElement("button");
    action.className = "secondary";
    if (["queued", "downloading"].includes(record.state)) {
      action.textContent = t("取消");
      action.onclick = () => run(() => desktopRequest("downloads-cancel", { id: record.id }));
    } else {
      action.textContent = t("选择文件");
      action.onclick = () => {
        const item = snapshot.items.find(item => item.id === record.itemId);
        if (item) openFiles(item); else notify("请先同步该商品，再选择下载文件。", true);
      };
    }
    row.append(action);
    fragment.append(row);
  }
  element("download-list").replaceChildren(fragment);
}

function updateFileSelection() {
  const inputs = [...element("file-list").querySelectorAll("input[type=checkbox]")];
  const chosen = inputs.filter(input => input.checked).length;
  element("file-selection-count").textContent = t("已选 {count} / {total}", { count: chosen, total: inputs.length });
  element("select-all-files").checked = inputs.length > 0 && chosen === inputs.length;
  element("select-all-files").indeterminate = chosen > 0 && chosen < inputs.length;
  element("download-selected").disabled = !fileSelection || chosen === 0;
}

async function openFiles(item) {
  if (!canDownload(item)) return;
  const request = ++fileRequest;
  fileSelection = null;
  element("file-product").textContent = item.title;
  element("file-list").textContent = t("正在读取可下载文件…");
  element("file-error").hidden = true;
  element("file-directory").textContent = settings.downloadDirectory || "";
  updateFileSelection();
  if (!element("file-dialog").open) element("file-dialog").showModal();
  try {
    const response = await desktopRequest("files", { id: item.id });
    if (request !== fileRequest) return;
    fileSelection = response;
    element("file-directory").textContent = response.directory;
    const fragment = document.createDocumentFragment();
    for (const file of response.files) {
      const label = document.createElement("label");
      label.className = "file-option";
      const input = document.createElement("input");
      input.type = "checkbox";
      input.value = file.id;
      input.onchange = updateFileSelection;
      const name = document.createElement("span");
      name.textContent = file.name;
      label.append(input, name);
      if (file.material) {
        const badge = document.createElement("span");
        badge.className = "material-badge";
        badge.textContent = t("材质文件");
        badge.title = t("根据文件名识别");
        label.append(badge);
        label.classList.add("material-file");
      }
      fragment.append(label);
    }
    element("file-list").replaceChildren(fragment);
    updateFileSelection();
  } catch (error) {
    if (request !== fileRequest) return;
    element("file-list").replaceChildren();
    element("file-error").textContent = I18n.message(error.message);
    element("file-error").hidden = false;
  }
}

function renderSettings() {
  element("language-select").value = settings.language || "auto";
  element("system-language").textContent = t("系统语言：{language}", { language: I18n.names[I18n.resolve(systemLanguage)] });
  element("download-directory").textContent = settings.downloadDirectory || "";
  element("currency-select").value = settings.currency || "CNY";
  element("close-behavior").value = settings.closeBehavior || "ask";
  element("update-notifications").checked = settings.updateNotifications !== false;
  renderCurrency();
  renderSessionBackup();
}

function renderSessionBackup() {
  element("remember-session").checked = settings.rememberSession !== false;
  const date = sessionBackup.savedAt ? " · " + new Date(sessionBackup.savedAt).toLocaleString(I18n.locale, { hour12: false }) : "";
  element("session-state").textContent = I18n.message(sessionBackup.message || "登录成功后自动加密保存") + date;
  element("save-session").disabled = settings.rememberSession === false;
}

async function updateSessionBackup(action, payload) {
  element("save-session").disabled = true;
  element("remember-session").disabled = true;
  try {
    const result = await desktopRequest(action, payload);
    settings = result.settings;
    sessionBackup = result.session;
  } catch (error) { notify(error.message, true); }
  finally {
    element("remember-session").disabled = false;
    renderSessionBackup();
  }
}

async function run(action) {
  try { return await action(); } catch (error) { notify(error.message, true); return null; }
}

async function synchronize() {
  if (syncing) return;
  syncing = true;
  element("sync").disabled = true;
  element("sync-mode").disabled = true;
  element("login").disabled = true;
  element("pause-sync").hidden = false;
  element("pause-sync").disabled = false;
  element("sync-progress").hidden = true;
  notify("正在连接 BOOTH…");
  try {
    const result = await desktopRequest("sync", { mode: element("sync-mode").value });
    setSnapshot(result);
    setAuth(true);
    element("sync-mode").value = "quick";
    notify({ key: "同步完成 · {count} 件商品", values: { count: snapshot.items.length.toLocaleString(I18n.locale) } }, false, true);
  } catch (error) {
    notify(error.message, true);
    const stored = await run(() => desktopRequest("load"));
    if (stored) setSnapshot(stored.pendingSnapshot || stored.snapshot);
  } finally {
    syncing = false;
    element("sync").disabled = false;
    element("sync-mode").disabled = false;
    element("login").disabled = false;
    element("pause-sync").hidden = true;
  }
}

element("all-view").onclick = () => { selectedShop = ""; showView("library"); };
element("downloads-view").onclick = () => showView("downloads");
element("settings-view").onclick = () => showView("settings");
element("sync").onclick = synchronize;
element("empty-sync").onclick = synchronize;
element("pause-sync").onclick = () => {
  element("pause-sync").disabled = true;
  run(() => desktopRequest("pause"));
};
for (const id of ["login", "relogin"]) element(id).onclick = () => run(() => desktopRequest("login"));
element("retry-auth").onclick = () => refreshAuthentication(false);
element("export").onclick = () => run(() => desktopRequest("export"));
element("filter-toggle").onclick = () => {
  element("filter-panel").hidden = !element("filter-panel").hidden;
  element("filter-toggle").setAttribute("aria-expanded", String(!element("filter-panel").hidden));
};
for (const id of ["search", "any-keywords", "exclude-keywords"]) element(id).oninput = () => { displayPage = 1; render(); };
for (const id of ["category-filter", "subcategory-filter", "shop-filter", "event-filter", "form-filter", "age-filter", "sort"]) element(id).onchange = () => {
  if (id === "category-filter") { element("subcategory-filter").value = ""; renderSubcategories(); }
  displayPage = 1;
  render();
};
element("clear-filters").onclick = () => {
  for (const id of ["search", "any-keywords", "exclude-keywords", "category-filter", "subcategory-filter", "shop-filter", "event-filter", "form-filter", "age-filter", "tag-search"]) element(id).value = "";
  selectedTags.clear();
  anyTags.clear();
  excludedTags.clear();
  selectedShop = "";
  displayPage = 1;
  renderShops();
  render();
};
element("tag-search").oninput = renderTags;
element("more-tags").onclick = () => { expandedTags = !expandedTags; element("tag-search").value = ""; renderTags(); };
element("previous-page").onclick = () => { displayPage--; render(); };
element("next-page").onclick = () => { displayPage++; render(); };
element("close-files").onclick = () => element("file-dialog").close();
element("file-dialog").addEventListener("close", () => { fileRequest++; fileSelection = null; });
element("select-all-files").onchange = () => {
  for (const input of element("file-list").querySelectorAll("input")) input.checked = element("select-all-files").checked;
  updateFileSelection();
};
element("download-selected").onclick = async () => {
  if (!fileSelection) return;
  element("download-selected").disabled = true;
  const ids = [...element("file-list").querySelectorAll("input:checked")].map(input => input.value);
  try {
    await desktopRequest("downloads-start", { ticket: fileSelection.ticket, ids });
    element("file-dialog").close();
    showView("downloads");
  } catch (error) {
    element("file-error").textContent = I18n.message(error.message);
    element("file-error").hidden = false;
    updateFileSelection();
  }
};
element("choose-directory").onclick = () => run(async () => {
  settings = await desktopRequest("choose-directory");
  renderSettings();
});
element("open-download-folder").onclick = () => run(() => desktopRequest("open-download-folder"));
element("save-session").onclick = () => updateSessionBackup("session-save");
element("remember-session").onchange = () => updateSessionBackup("session-remember", { enabled: element("remember-session").checked });

document.addEventListener("DOMContentLoaded", () => run(async () => {
  await I18n.ready;
  const stored = await desktopRequest("load");
  settings = stored.settings || {};
  filterPresets = stored.filterPresets || [];
  notificationState = stored.notifications || { items: [] };
  systemLanguage = stored.systemLanguage || navigator.language;
  I18n.set(settings.language || "auto", systemLanguage);
  downloads = stored.downloads || [];
  localFiles = stored.localFiles || [];
  exchangeRates = stored.rates || {};
  sessionBackup = stored.session || {};
  setSnapshot(stored.pendingSnapshot || stored.snapshot);
  renderSettings();
  renderDownloads();
  renderLocalCount();
  renderNotifications();
  element("sync-mode").value = stored.pendingMode || (snapshot.items.length ? "quick" : "full");
  await desktopRequest("window", { action: "ready" });
  if (stored.testing) { setAuth(false); return; }
  refreshAuthentication();
}));
