let appUpdateState = { status: "idle", currentVersion: "1.0.1" };
let pendingAppUpdate = null;
let appUpdateReleasePending = false;
let appUpdateReleaseError = "";
let dismissedAppUpdateVersions = new Set();
try {
  const saved = JSON.parse(localStorage.getItem("booth-shelf-dismissed-app-update") || "[]");
  if (Array.isArray(saved)) dismissedAppUpdateVersions = new Set(saved.filter(version => typeof version === "string"));
} catch {}

function appUpdateStatus() {
  if (pendingAppUpdate?.state === appUpdateState) {
    const status = { "app-update-check": "checking", "app-update-start": "downloading", "app-update-apply": "installing" }[pendingAppUpdate.action];
    if (status) return status;
  }
  return appUpdateState.status || "idle";
}

function appUpdateBytes(value) {
  const power = value > 0 ? Math.max(0, Math.min(3, Math.floor(Math.log(value) / Math.log(1024)))) : 0;
  return new Intl.NumberFormat(I18n.locale, { maximumFractionDigits: power ? 1 : 0 }).format(value / 1024 ** power) + " " + ["B", "KiB", "MiB", "GiB"][power];
}

function renderAppUpdate() {
  const state = appUpdateState;
  const status = appUpdateStatus();
  const version = String(state.version || "");
  const busy = ["checking", "downloading", "installing"].includes(status);
  const canStart = Boolean(version) && ["available", "ready", "error"].includes(status);
  const source = {
    idle: "尚未检查软件更新", checking: "正在检查软件更新…", current: "已是最新版本",
    available: "发现新版本 {version}", downloading: "正在下载更新 {version}…",
    ready: "更新 {version} 已下载，可重启安装", installing: "正在重启以安装更新…", error: "软件更新失败，请重试"
  }[status] || "尚未检查软件更新";
  const statusText = t(source, { version });
  element("app-update-current-version").textContent = t("当前版本：{version}", { version: String(state.currentVersion || "1.0.1") });
  element("app-update-status").textContent = statusText;
  element("app-update-banner-title").textContent = statusText;
  const checkedAt = state.lastCheckedAt ? new Date(state.lastCheckedAt) : null;
  element("app-update-last-checked").hidden = !checkedAt || !Number.isFinite(checkedAt.getTime());
  element("app-update-last-checked").textContent = element("app-update-last-checked").hidden ? "" : t("上次检查：{time}", { time: checkedAt.toLocaleString(I18n.locale) });
  const error = state.error || appUpdateReleaseError;
  element("app-update-error").textContent = error ? I18n.message(error) : "";
  element("app-update-error").hidden = !error;
  element("app-update-check").textContent = t(status === "checking" ? "正在检查软件更新…" : status === "error" ? "重试检查更新" : "检查更新");
  element("app-update-check").disabled = busy || Boolean(pendingAppUpdate);
  element("app-update-banner").hidden = !version || !(["downloading", "installing"].includes(status)
    || (["available", "ready"].includes(status) && !dismissedAppUpdateVersions.has(version)));
  element("app-update-dismiss").hidden = !["available", "ready"].includes(status);
  const starting = pendingAppUpdate?.action === "app-update-start" && pendingAppUpdate.state === state;
  const received = !starting && Number.isFinite(state.received) ? Math.max(0, state.received) : 0;
  const total = !starting && Number.isFinite(state.total) && state.total > 0 ? state.total : 0;
  const fraction = total ? Math.min(1, received / total) : 0;
  const detail = total ? t("已下载 {received} / {total}（{percent}）", {
    received: appUpdateBytes(received), total: appUpdateBytes(total), percent: new Intl.NumberFormat(I18n.locale, { style: "percent", maximumFractionDigits: 0 }).format(fraction)
  }) : t("已下载 {received} · 总大小未知", { received: appUpdateBytes(received) });
  for (const prefix of ["app-update", "app-update-banner"]) {
    element(prefix + "-start").textContent = t(status === "ready" ? "重启并安装更新" : status === "error" ? "重试下载并重启" : "下载并重启");
    element(prefix + "-start").hidden = !canStart;
    element(prefix + "-start").disabled = busy || Boolean(pendingAppUpdate);
    element(prefix + "-cancel").hidden = status !== "downloading";
    element(prefix + "-cancel").disabled = Boolean(pendingAppUpdate && pendingAppUpdate.action !== "app-update-start");
    element(prefix + "-cancel").textContent = t(pendingAppUpdate?.action === "app-update-cancel" ? "正在取消更新下载…" : "取消更新下载");
    element(prefix + "-release").hidden = !state.releaseUrl;
    element(prefix + "-release").disabled = appUpdateReleasePending || status === "installing";
    element(prefix + "-progress").hidden = status !== "downloading";
    const meter = element(prefix + "-meter");
    meter.max = 1;
    if (total) meter.value = fraction; else meter.removeAttribute("value");
    meter.setAttribute("aria-valuetext", detail);
    element(prefix + "-detail").textContent = detail;
  }
}

async function requestAppUpdate(action) {
  const status = appUpdateStatus();
  if (action === "app-update-cancel") {
    if (status !== "downloading" || (pendingAppUpdate && pendingAppUpdate.action !== "app-update-start")) return;
  } else {
    if (pendingAppUpdate || ["checking", "downloading", "installing"].includes(status)) return;
    if (action === "app-update-start" && (!appUpdateState.version || !["available", "error"].includes(status))) return;
    if (action === "app-update-apply" && status !== "ready") return;
  }
  const request = { action, state: appUpdateState };
  pendingAppUpdate = request;
  appUpdateReleaseError = "";
  renderAppUpdate();
  try {
    const state = await desktopRequest(action);
    if (pendingAppUpdate === request && appUpdateState === request.state && state && typeof state.status === "string") appUpdateState = state;
  } catch (error) {
    if (pendingAppUpdate === request && appUpdateState === request.state) {
      const fallback = {
        "app-update-check": "无法检查软件更新，请检查网络后重试。",
        "app-update-start": "更新下载或校验失败，旧版未更改，请重试。",
        "app-update-cancel": "无法取消更新下载，请重试。",
        "app-update-apply": "更新安装失败，旧版已保留，请重试。"
      }[action];
      appUpdateState = { ...appUpdateState, status: action === "app-update-apply" || request.state.status === "ready" ? "ready"
        : action === "app-update-cancel" ? "downloading" : "error", error: error.message || fallback };
    }
  } finally {
    if (pendingAppUpdate === request) pendingAppUpdate = null;
    renderAppUpdate();
  }
}

async function openAppUpdateRelease() {
  if (appUpdateReleasePending || !appUpdateState.releaseUrl || appUpdateStatus() === "installing") return;
  appUpdateReleasePending = true;
  appUpdateReleaseError = "";
  renderAppUpdate();
  try { await desktopRequest("app-update-release"); }
  catch (error) { appUpdateReleaseError = error.message || "无法打开发行说明，请重试。"; }
  finally { appUpdateReleasePending = false; renderAppUpdate(); }
}

element("app-update-check").addEventListener("click", () => requestAppUpdate("app-update-check"));
for (const prefix of ["app-update", "app-update-banner"]) {
  element(prefix + "-start").addEventListener("click", () => requestAppUpdate(appUpdateState.status === "ready" ? "app-update-apply" : "app-update-start"));
  element(prefix + "-cancel").addEventListener("click", () => requestAppUpdate("app-update-cancel"));
  element(prefix + "-release").addEventListener("click", openAppUpdateRelease);
}
element("app-update-dismiss").addEventListener("click", () => {
  dismissedAppUpdateVersions.add(String(appUpdateState.version || ""));
  try { localStorage.setItem("booth-shelf-dismissed-app-update", JSON.stringify([...dismissedAppUpdateVersions])); } catch {}
  renderAppUpdate();
});
renderAppUpdate();
