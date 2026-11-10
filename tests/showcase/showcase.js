const showcaseLanguage = new URLSearchParams(location.search).get("lang") || "zh-CN";
Object.assign(testFixture.settings, { language: showcaseLanguage, pinnedTags: ["VRChat", "衣装", "アクセサリー"], currency: showcaseLanguage === "ja" ? "JPY" : showcaseLanguage === "en" ? "USD" : "CNY" });
testFixture.testing = false;
testFixture.snapshot.items = [
  ["1", "Linen Jacket / リネンジャケット", "Paper Atelier", "outfit", ["VRChat", "衣装", "カジュアル"], 1800, ["bought"]],
  ["2", "Soft Bob / ショートヘア", "Sunday Studio", "hair", ["VRChat", "ヘアスタイル"], 1200, ["bought"]],
  ["3", "Round Glasses / 丸メガネ", "Paper Atelier", "glasses", ["VRChat", "アクセサリー"], 500, ["bought", "gift"]],
  ["4", "Pastel Material Pack", "Studio Palette", "material", ["VRChat", "マテリアル"], 800, ["bought"]],
  ["5", "Weekend Outfit / おでかけ衣装", "Paper Atelier", "outfit", ["VRChat", "衣装"], 2200, ["bought"]],
  ["6", "Ribbon Hair / リボンヘア", "Sunday Studio", "hair", ["VRChat", "ヘアスタイル"], 1500, ["bought"]],
  ["7", "Gift Accessory Set", "Studio Palette", "glasses", ["VRChat", "アクセサリー"], null, ["gift"]],
  ["8", "Free Material Sample", "Studio Palette", "material", ["VRChat", "マテリアル"], null, ["free"]]
].map(([id, title, shop, art, tags, purchaseJpy, sections], index) => ({ id, title, shop, tags, purchaseJpy, sections,
  shopUrl: "https://demo-" + shop.split(" ")[0].toLowerCase() + ".booth.pm", url: "https://booth.pm/ja/items/" + id,
  image: location.origin + "/tests/showcase/" + art + ".svg", acquiredRanks: { bought: index, gift: index, free: index } }));
testFixture.snapshot.spend = { totalJpy: 8000, orders: 6, complete: true };
testFixture.snapshot.syncedAt = "2026-11-10T02:30:00Z";
testFixture.localFiles = ["Materials.zip", "LinenJacket_Milfy.unitypackage", "LinenJacket_Eku.unitypackage"].map((name, index) => ({
  id: "local" + index, itemId: "1", title: testFixture.snapshot.items[0].title, name, size: (index + 1) * 10485760,
  path: "D:\\BOOTH\\1_LinenJacket\\" + name, createdAt: "2026-11-10T02:40:00Z"
}));
testFixture.notifications = { ...testFixture.notifications, total: 8, checked: 8, lastCheckedAt: "2026-11-10T02:45:00Z",
  items: [{ id: "demo-update", itemId: "1", title: testFixture.snapshot.items[0].title, files: ["Materials.zip", "LinenJacket_Milfy.unitypackage"], createdAt: "2026-11-10T02:45:00Z", read: false }] };
const originalImageURL = imageURL;
imageURL = value => String(value).startsWith(location.origin + "/tests/showcase/") ? value : originalImageURL(value);
