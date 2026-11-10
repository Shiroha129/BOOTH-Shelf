let notificationState = { items: [] };
let notificationPage = 1;
function renderNotifications() {
  const all = notificationState.items || [];
  const unread = all.filter(item => !item.read).length;
  element("notification-count").textContent = String(unread);
  element("notification-count").hidden = unread === 0;
  const status = { disabled: "已关闭更新检测", login: "登录后检查更新", syncing: "同步中，稍后检查", paused: "更新检测已暂停", error: "检查未完成，稍后重试", active: notificationState.busy ? "正在检查更新…" : "" }[notificationState.status || "login"] || "";
  element("notification-status").textContent = status ? t(status) : "";
  element("notification-status").hidden = !status;
  element("check-updates").disabled = notificationState.detectionEnabled === false || ["disabled", "login", "paused", "syncing"].includes(notificationState.status) || notificationState.busy || notificationState.total === 0;
  element("read-all-notifications").disabled = unread === 0;
  const items = element("unread-notifications").checked ? all.filter(item => !item.read) : all;
  const fragment = document.createDocumentFragment();
  for (const notice of items.slice(0, notificationPage * 40)) {
    const card = document.createElement("article");
    card.className = "notification-card" + (notice.read ? " read" : "");
    const title = document.createElement("h2");
    const link = document.createElement("a");
    link.textContent = notice.title || notice.itemId;
    link.href = safeURL("https://booth.pm/ja/items/" + notice.itemId);
    link.target = "_blank";
    link.rel = "noreferrer";
    title.append(link);
    const meta = document.createElement("p");
    meta.className = "muted";
    meta.textContent = t("文件更新") + " · " + new Date(notice.createdAt).toLocaleString(I18n.locale);
    const files = document.createElement("ul");
    for (const name of notice.files || []) { const file = document.createElement("li"); file.textContent = name; files.append(file); }
    const read = document.createElement("button");
    read.className = "secondary";
    read.textContent = t(notice.read ? "已读" : "标记已读");
    read.disabled = notice.read;
    read.onclick = () => run(async () => { notificationState = await desktopRequest("updates-read", { id: notice.id }); renderNotifications(); });
    card.append(title, meta, files, read);
    fragment.append(card);
  }
  element("notification-list").replaceChildren(fragment);
  element("notifications-empty").hidden = items.length > 0;
  element("more-notifications").hidden = items.length <= notificationPage * 40;
}
element("notifications-view").onclick = () => showView("notifications");
element("check-updates").onclick = () => run(async () => { notificationState = await desktopRequest("updates-check"); renderNotifications(); });
element("read-all-notifications").onclick = () => run(async () => { notificationState = await desktopRequest("updates-read"); renderNotifications(); });
element("unread-notifications").onchange = () => { notificationPage = 1; renderNotifications(); };
element("more-notifications").onclick = () => { notificationPage++; renderNotifications(); };
element("update-notifications").onchange = async () => {
  element("update-notifications").disabled = true;
  try {
    settings = await desktopRequest("settings-save", { updateNotifications: element("update-notifications").checked });
    notificationState = await desktopRequest("updates-list");
    renderNotifications();
  } catch (error) { notify(error.message, true); }
  finally { element("update-notifications").disabled = false; renderSettings(); }
};

element("notification-interval").onchange = async () => {
  element("notification-interval").disabled = true;
  try {
    settings = await desktopRequest("settings-save", { notificationIntervalMinutes: Number(element("notification-interval").value) });
    notificationState = await desktopRequest("updates-list");
    renderNotifications();
  } catch (error) { notify(error.message, true); }
  finally { renderSettings(); }
};

let boothAccessTimer;
function renderBoothAccess() {
  clearTimeout(boothAccessTimer);
  const paused = boothAccessState.paused === true;
  const remaining = Math.max(0, new Date(boothAccessState.retryAt || 0).getTime() - Date.now());
  let status = "";
  if (paused) {
    status = t(boothAccessState.reason === "rate-limit" ? "网站限制了请求频率，已暂停自动访问。" : "网站拒绝访问或要求验证，已暂停自动访问。请先打开 BOOTH 确认。");
    status += " " + (remaining > 0 ? t("最早可在 {time} 手动继续。", { time: new Date(boothAccessState.retryAt).toLocaleString(I18n.locale) }) : t("确认后点击继续访问；同步可从已保存的进度继续。"));
    if (remaining > 0) boothAccessTimer = setTimeout(renderBoothAccess, Math.min(remaining + 50, 2147483647));
  }
  element("booth-access-banner-text").textContent = status;
  element("booth-access-banner").hidden = !paused;
  element("booth-access-login").hidden = !paused;
  element("booth-access-resume").hidden = !paused;
  element("booth-access-resume").disabled = remaining > 0;
}
element("booth-access-login").onclick = () => run(() => desktopRequest("login"));
element("booth-access-resume").onclick = () => run(async () => {
  boothAccessState = await desktopRequest("booth-access-resume");
  notificationState = await desktopRequest("updates-list");
  renderBoothAccess();
  renderNotifications();
});
