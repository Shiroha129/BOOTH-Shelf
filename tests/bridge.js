const testFixture = {
  testing: true,
  appUpdate: { status: "idle", currentVersion: "0.5.13" },
  systemLanguage: "zh-CN",
  settings: { currency: "CNY", downloadDirectory: "C:\\TestDownloads", rememberSession: true, pinnedTags: [], language: "auto", closeBehavior: "ask", updateNotifications: true },
  filterPresets: [],
  notifications: { items: [], total: 5, mode: "incremental", sourcesReady: 3, pages: 3, checked: 5, unavailable: 0, enabled: true, status: "active", lastCheckedAt: "2026-11-10T01:00:00Z" },
  session: { state: "waiting", message: "登录成功后自动加密保存" },
  rates: { date: "2026-11-06", source: "测试汇率", rates: { JPY: 1, CNY: .042137, USD: .0064 } },
  localFiles: [
    { id: "local1", itemId: "1", title: "测试衣装", name: "Materials.zip", size: 1048576, path: "C:\\TestDownloads\\Materials.zip", createdAt: "2026-11-09T10:00:00Z" },
    { id: "local2", itemId: "1", title: "测试衣装", name: "Outfit.zip", size: 2097152, path: "C:\\TestDownloads\\Outfit.zip", createdAt: "2026-11-09T10:00:00Z" }
  ],
  downloads: [],
  snapshot: {
    schemaVersion: 2, complete: true, syncedAt: "2026-11-09T10:00:00Z", spend: { totalJpy: 2700, orders: 2, complete: true },
    items: [
      { id: "1", title: "测试衣装", shop: "Test Atelier", shopUrl: "https://atelier.booth.pm/", sections: ["bought", "gift"], tags: ["VRChat", "衣装"], purchaseJpy: 790, acquiredRanks: { bought: 0, gift: 0 }, url: "https://booth.pm/ja/items/1", category: "3Dモデル", subcategory: "3D衣装", forms: ["digital"], adult: false },
      { id: "2", title: "发型 Hair", shop: "Test Atelier", shopUrl: "https://atelier.booth.pm", sections: ["bought"], tags: ["VRChat", "Hair"], purchaseJpy: 1330, acquiredRanks: { bought: 1 }, url: "https://booth.pm/ja/items/2" },
      { id: "3", title: "礼物 Gift", shop: "Other Shop", shopUrl: "https://other.booth.pm/", sections: ["gift"], tags: ["VRChat", "礼物"], url: "https://booth.pm/ja/items/3" },
      { id: "4", title: "免费 Glasses", shop: "Free Studio", shopUrl: "https://free.booth.pm/", sections: ["free"], tags: ["VRChat", "Glasses"], url: "https://booth.pm/ja/items/4" },
      { id: "5", title: "未核对金额", shop: "Other Shop", shopUrl: "https://other.booth.pm/", sections: ["bought"], tags: ["VRChat"], acquiredRanks: { bought: 2 }, url: "https://booth.pm/ja/items/5" }
    ]
  }
};
const scrollFixture = new URLSearchParams(location.search).has("scroll");
if (scrollFixture) {
  const item = testFixture.snapshot.items[0];
  testFixture.snapshot.items = Array.from({ length: 80 }, (_, index) => ({ ...structuredClone(item), id: String(index + 1), title: "滚动验证资产 " + (index + 1) }));
  testFixture.settings.pinnedTags = ["衣装"];
  const local = testFixture.localFiles[0];
  testFixture.localFiles = Array.from({ length: 80 }, (_, index) => ({ ...structuredClone(local), id: "local" + (index + 1), itemId: String(index + 1) }))
    .concat(Array.from({ length: 30 }, (_, index) => ({ ...structuredClone(local), id: "file" + index, name: "Outfit_" + (index + 1) + ".zip" })));
}
const testMessages = [];
const testWindowActions = [];
const testListeners = [];
window.chrome ||= {};
window.chrome.webview = {
  addEventListener(type, handler) { if (type === "message") testListeners.push(handler); },
  postMessage(message) {
    testMessages.push(structuredClone(message));
    setTimeout(() => {
      let result;
      const payload = message.payload;
      switch (message.action) {
        case "load": result = testFixture; break;
        case "app-update-check":
          testFixture.appUpdate = { ...testFixture.appUpdate, status: "checking", error: null };
          result = testFixture.appUpdate; break;
        case "app-update-start":
          testFixture.appUpdate = { ...testFixture.appUpdate, status: "downloading", received: 0, total: 41943040, error: null };
          result = testFixture.appUpdate; break;
        case "app-update-cancel":
          testFixture.appUpdate = { ...testFixture.appUpdate, status: "available", received: 0, total: 0, error: null };
          result = testFixture.appUpdate; break;
        case "app-update-apply":
          testFixture.appUpdate = { ...testFixture.appUpdate, status: "installing", error: null };
          result = testFixture.appUpdate; break;
        case "app-update-release": result = true; break;
        case "settings-save": Object.assign(testFixture.settings, payload); result = testFixture.settings; break;
        case "window":
          result = true;
          if (payload.action === "close" && testFixture.settings.closeBehavior === "ask") {
            for (const listener of testListeners) listener({ data: { type: "close-request" } });
          } else if (payload.action === "close") testWindowActions.push(testFixture.settings.closeBehavior);
          else testWindowActions.push(payload.action);
          break;
        case "preset-save": {
          const saved = { ...payload, id: payload.id || crypto.randomUUID() };
          const index = testFixture.filterPresets.findIndex(preset => preset.id === saved.id);
          if (index >= 0) testFixture.filterPresets[index] = saved; else testFixture.filterPresets.push(saved);
          result = testFixture.filterPresets;
          break;
        }
        case "preset-delete": testFixture.filterPresets = testFixture.filterPresets.filter(preset => preset.id !== payload.id); result = testFixture.filterPresets; break;
        case "updates-read":
          for (const notice of testFixture.notifications.items) if (!payload?.id || payload.id === notice.id) notice.read = true;
          result = testFixture.notifications; break;
        case "updates-list": case "updates-check":
          testFixture.notifications.enabled = testFixture.settings.updateNotifications;
          testFixture.notifications.status = testFixture.settings.updateNotifications ? "active" : "disabled";
          result = testFixture.notifications; break;
        case "tag-pin": {
          const tags = new Set(testFixture.settings.pinnedTags);
          if (payload.pinned) tags.add(payload.tag); else tags.delete(payload.tag);
          testFixture.settings.pinnedTags = [...tags];
          result = testFixture.settings.pinnedTags; break;
        }
        case "choose-directory": testFixture.settings.downloadDirectory = "D:\\ChangedFolder"; result = testFixture.settings; break;
        case "files": result = { ticket: "fixture-ticket", directory: testFixture.settings.downloadDirectory, files: [
          { id: "101", name: "Materials.zip", material: true }, { id: "103", name: "Outfit_Milfy.zip", material: false },
          { id: "104", name: "Outfit_エク.zip", material: false }, { id: "102", name: "Outfit.zip", material: false }
        ].concat(scrollFixture ? Array.from({ length: 30 }, (_, index) => ({ id: String(index + 105), name: "Outfit_" + (index + 1) + ".zip", material: false })) : []) }; break;
        case "downloads-start": result = payload.ids.length; break;
        case "local-list": case "local-action": result = testFixture.localFiles; break;
        case "local-asset-folders": result = ["C:\\TestDownloads\\1_测试衣装"]; break;
        case "local-asset-delete":
          testFixture.localFiles = testFixture.localFiles.filter(file => file.itemId !== payload.itemId);
          result = testFixture.localFiles; break;
        case "currency-refresh": result = testFixture.rates; break;
        case "sync": result = testFixture.snapshot; break;
        case "auth-check": result = { authenticated: true }; break;
        case "session-save":
          testFixture.session = { state: "saved", message: "登录会话已加密保存", savedAt: "2026-11-09T10:00:00Z" };
          result = { settings: testFixture.settings, session: testFixture.session }; break;
        case "session-remember":
          testFixture.settings.rememberSession = payload.enabled;
          testFixture.session = { state: payload.enabled ? "waiting" : "disabled", message: payload.enabled ? "登录成功后自动加密保存" : "已关闭并删除会话备份，当前网页登录保留" };
          result = { settings: testFixture.settings, session: testFixture.session }; break;
        default: result = true;
      }
      for (const listener of testListeners) listener({ data: { id: message.id, result: structuredClone(result) } });
    }, 0);
  }
};
