let productItem = null;
let productData = null;
let productImageIndex = 0;
let productRequest = 0;
let productImageMenuTarget = null;
let productImageMenuSource = null;

function closeProductImageMenu(restoreFocus = false) {
  element("product-image-menu").hidden = true;
  if (restoreFocus && productImageMenuSource?.isConnected) productImageMenuSource.focus({ preventScroll: true });
  productImageMenuTarget = null;
  productImageMenuSource = null;
}

function openProductImageMenu(event, url, number) {
  event.preventDefault();
  const source = imageURL(url);
  if (!source || !productData) return;
  closeAssetMenu();
  productImageMenuTarget = { id: productItem.id, url: source, number };
  productImageMenuSource = event.currentTarget;
  const menu = element("product-image-menu");
  menu.hidden = false;
  const bounds = event.currentTarget.getBoundingClientRect();
  const keyboard = event.type === "keydown" || (!event.clientX && !event.clientY);
  menu.style.left = Math.max(8, Math.min(keyboard ? bounds.left + 16 : event.clientX, innerWidth - menu.offsetWidth - 8)) + "px";
  menu.style.top = Math.max(33, Math.min(keyboard ? bounds.top + 16 : event.clientY, innerHeight - menu.offsetHeight - 8)) + "px";
  element("product-save-image").focus({ preventScroll: true });
}

// BOOTH model identities and observed aliases; sources are recorded in docs/AVATAR-ALIASES.md.
const productAvatarAliases = [
  ["8355117", "Linne", "リンネ", "りんね"],
  ["6669962", "Shuan", "しゅあん"],
  ["5058077", "Manuka", "マヌカ"],
  ["5650156", "Sio", "しお"],
  ["6106863", "Shinano", "しなの"],
  ["6571299", "Milfy", "ミルフィ", "みるふぃ"],
  ["7328764", "Eku", "エク"],
  ["5354471", "Chiffon", "シフォン"],
  ["6405390", "Chocolat", "ショコラ", "しょこら"],
  ["6846646", "Mao", "真央", "まお"],
  ["5957830", "Rurune", "ルルネ"],
  ["8122803", "Mayo", "まよ"],
  ["5813187", "Kipfel", "キプフェル"],
  ["8352244", "Powari", "ぽわり"],
  ["6082686", "Airi", "愛莉"],
  ["8916626", "Michella", "ミシェラ"],
  ["8325804", "Misaki", "海咲", "みさき"],
  ["6305948", "Marycia", "マリシア"],
  ["7502898", "Lumina", "ルミナ"],
  ["8910263", "Vivian", "ヴィヴィアン", "비비안"],
  ["4035411", "Selestia", "セレスティア"],
  ["5132797", "Mizuki", "瑞希"],
  ["8562330", "Kaguya", "輝夜", "かぐや"],
  ["4876459", "Lime", "ライム"],
  ["7770415", "Plum", "プラム"],
  ["6538026", "Milltina", "ミルティナ"],
  ["7328789", "Ichigo", "イチゴ"],
  ["7699667", "Ramune", "ラムネ", "ラムネ(えも研)"],
  ["7928592", "Kumaly", "ひきくまりのクマリ", "クマリ"],
  ["3681787", "Kikyo", "Kikyou", "桔梗"],
  ["4667400", "Moe", "萌"],
  ["3390957", "Maya", "舞夜"],
  ["4707634", "Shinra", "森羅"],
  ["3443188", "Rindo", "Rindou", "竜胆"],
  ["8962830", "Salt", "ソルト"],
  ["3659436", "Leefa", "リーファ"],
  ["4340548", "Mamehinata", "まめひなた"],
  ["2559783", "Rusk", "ラスク"],
  ["3190100", "Grus"],
  ["8906276", "Miredo", "ミレド"],
  ["6373683", "Ririka", "りりか"]
];
const productAvatarKey = value => value.normalize("NFKC").toLowerCase()
  .replace(/[ァ-ヶ]/g, letter => String.fromCharCode(letter.charCodeAt(0) - 0x60));
const productAvatarNames = new Map(productAvatarAliases.flatMap(([, name, ...aliases]) =>
  [name, ...aliases].map(alias => [productAvatarKey(alias), name])));
const productAvatarById = new Map(productAvatarAliases.map(([id, name]) => [id, name]));
const productAvatarPattern = new RegExp([...productAvatarNames.keys()]
  .sort((a, b) => b.length - a.length).map(alias => (/^[a-z]/i.test(alias) ? "(?<![a-z0-9])" : "")
    + alias.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + (/[a-z]$/i.test(alias) ? "(?![a-z0-9])" : "")).join("|"), "gi");

function productCompatibility(data) {
  const text = value => String(value || "").normalize("NFKC")
    .replace(/\[([^\]]*)\]|\(([^)]*)\)|【([^】]*)】/g, (label, square, round, wide) =>
      /^(?:[-−–—]?\s*\d+(?:\.\d+)?\s*(?:%\s*(?:OFF\b|オフ|割引)|割引)|(?:SALE|NEW|LIMITED|PC|Quest)\b|期間限定|セール|特価|発売記念|新作|新発売|限时|限時|[¥$€£]\s*\d)/i.test((square ?? round ?? wide).trim()) ? "" : label)
    .replace(/[-−–—]?\s*\d+(?:\.\d+)?\s*(?:%\s*(?:OFF\b|オフ|割引)|割引)(?:中)?/gi, "")
    .replace(/(?:[¥$€£]\s*\d[\d,]*(?:\.\d+)?|\d[\d,]*(?:\.\d+)?\s*(?:円|元|JPY\b|USD\b))/gi, "")
    .replace(/\b(?:ver(?:sion)?\.?|v)\s*\d+(?:\.\d+)*\b/gi, "")
    .replace(/\b(?:on\s+sale|sale)\b|期間限定(?:セール)?|セール|限时(?:优惠|折扣)|限時(?:優惠|折扣)/gi, "")
    .replace(/\[\s*\]|\(\s*\)|【\s*】/g, "").trim();
  const bundle = /\b(?:full|all|complete)[\s_-]*(?:set|pack|avatars?|models?|edition|version|bundle|collection)(?:\b|_)|\ball[\s_-]*in[\s_-]*one\b|\bbundle\b|^(?:full|all|complete|フル)$|フル(?:セット|パック|バージョン|版)|コンプリート|全(?:\d+\s*)?(?:種類?|種|体|人|モデル|アバター|対応|部|て|套(?:装|裝)?)|まとめ(?:買い|セット)|一括|お得(?:な)?セット/i;
  const support = /\b(?:boost|support(?:er|ing)?|sponsor(?:ship)?|donation|tip)\b|応援|支援|お布施|投げ銭|寄付|赞助|贊助|후원/i;
  const generic = /^(?:(?:通常|普通|標準|标准|有料|無料|無償|有償|下载|下載|ダウンロード)(?:版)?|(?:normal|standard|regular|download)(?:\s+(?:edition|version))?|商品|本体|データ|data|衣装|衣裝|outfit|texture|テクスチャ|unitypackage|fbx|vrchat|liltoon|modular avatar|MA|PB|PhysBones?|VRCFT|VRM|PC|Quest|(?:\d+|複数|複數|多数|多数種|multiple)\s*(?:アバター|モデル|avatars?|models?|人|体|種)|[\d.,]+\s*(?:円|元|yen)|s|m|l|xl|xxl|black|white|red|blue|pink|黒|白|赤|青)$/i;
  const excluded = value => bundle.test(text(value)) || support.test(text(value)) || generic.test(text(value));
  const trim = value => value.replace(/^[\p{P}\p{S}\s]+|[\p{P}\p{S}\s]+$/gu, "");
  const unsupported = /未対応|非対応|対応(?:予定|検討)|対応希望|未适配|未適配|不支持|不支援|\b(?:not\s+(?:supported|compatible)|unsupported|planned|coming\s+soon)\b|미지원/i;
  const split = value => {
    let label = text(value).replace(/https?:\/\/\S+/gi, "");
    if (/オリジナル|original\s+3d|様/i.test(label)) {
      if (unsupported.test(label)) return [];
      const quoted = [...label.matchAll(/[「『]([^」』]+)[」』]/g)];
      if (quoted.length) label = quoted.map(match => match[1]).join("/");
    }
    return label.split(/[\/|,、&+]/).flatMap(part => {
      if (unsupported.test(part)) return [];
      const name = trim(part
        .replace(/^\s*\d+[.)]\s+/, "")
        .replace(/([^\s\[【(])\s*[-–—]?\s*(?:\([^)]*\)|\[[^\]]*\]|【[^】]*】)/g, "$1")
        .replace(/[\[\]【】()「」『』]/g, "")
        .replace(/^(?:for\s+|対応アバター\s*[:：]\s*)/i, "")
        .replace(/(?:ちゃん|さん|様)?(?:向け|専用(?:版)?|対応(?:版)?|用(?:版)?|版)(?=$|[\s\-–—])/g, "")
        .replace(/(?:ちゃん|さん|様)(?=$|[\s\-–—])/g, ""));
      if (!name || excluded(name) || name === text(data.title) || name.length > 80
          || /[。!?:：]|ください|します|しています|について|付属|含まれ|使用|シェイプキー|shape\s*keys?|Breast_|Waist_/i.test(name)) return [];
      const key = productAvatarKey(name);
      const matches = [...key.matchAll(productAvatarPattern)];
      if (matches.length && !trim(key.replace(productAvatarPattern, "")))
        return matches.map(match => productAvatarNames.get(match[0]));
      return [name];
    });
  };
  const names = (data.variations || []).filter(variant => !excluded(variant.name)).flatMap(variant => split(variant.name));
  const lines = String(data.description || "").normalize("NFKC").split(/\r?\n/);
  let listing = false;
  let otherSection = false;
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index].trim().replace(/^[\p{P}\p{S}\s]+/gu, "");
    if (!line || /^https?:\/\//i.test(line)) continue;
    const heading = line.match(/^(?:対応(?:アバター|モデル|素体)(?:一覧|リスト)?|(?:適配|支援)(?:型號|模型)|适配(?:型号|模型)|Supported\s+(?:avatars?|models?)|Compatible\s+(?:avatars?|models?|with)|\d+\s*(?:アバター|モデル|avatars?|models?)(?:対応)?(?=\s*(?:[:/]|$))|(?:대응|지원)\s*(?:아바타|모델))(?=$|[\s\p{P}\p{S}])[\s\p{P}\p{S}]*(.*)$/iu);
    if (heading) {
      listing = !/^\d/.test(line) || !heading[1]; otherSection = false; names.push(...split(heading[1])); continue;
    }
    if (/^(?:商品(?:説明|内容|仕様|紹介)|内容物|利用規約|規約|注意|ご使用|導入|使い方|使用方法|動作環境|禁止事項|更新履歴|対応シェイプキー|シェイプキー|撮影|使用(?:アバター|モデル|衣装)|衣装クレジット|クレジット|作者|製作者|制作者|非対応|未対応|対応希望|Terms\b|Credits?\b|Author\b|License\b|Installation\b|Updates?\b|Changelog\b|Contents\b|Special\s+thanks\b|How\s+to\b|Product\s+(?:description|contents)|商品简介|商品簡介|使用说明|使用說明|상품설명|내용물|업데이트|이용약관)/i.test(line)) {
      listing = false; otherSection = true; continue;
    }
    if (listing) {
      const found = split(line);
      let next = index + 1;
      while (next < lines.length && !lines[next].trim()) next++;
      const reference = (line + " " + (lines[next] || "")).match(/https:\/\/(?:[a-z0-9-]+\.)?booth\.pm\/(?:[a-z]{2}(?:-[a-z]{2})?\/)?items\/(\d+)(?=[/?#\s\\]|$)/i);
      const linked = productAvatarById.get(reference?.[1]);
      names.push(...(linked && found.length === 1 && !productAvatarNames.has(productAvatarKey(found[0])) ? [linked] : found));
    }
    else if (!otherSection && /^\s*[-—─].+[-—─]\s*$/.test(lines[index])
      && /3D|モデル|アバター|様/i.test(lines[index + 1] || "")) names.push(...split(line));
  }
  for (const match of text(data.title).matchAll(/[【\[]([^【】\[\]\n]+?)(?:専用|対応|用)[】\]]/g)) names.push(...split(match[1]));
  const seen = new Set();
  return names.filter(name => !seen.has(name.toLowerCase()) && seen.add(name.toLowerCase()));
}

async function openProduct(item) {
  closeProductImageMenu();
  const request = ++productRequest;
  productItem = item;
  productData = null;
  productImageIndex = 0;
  element("product-title").textContent = item.title;
  element("product-loading").hidden = false;
  element("product-failure").hidden = true;
  element("product-layout").hidden = true;
  if (!element("product-dialog").open) element("product-dialog").showModal();
  try {
    const data = await desktopRequest("product", { id: item.id });
    if (request !== productRequest) return;
    productData = data;
    renderProduct();
  } catch (error) {
    if (request !== productRequest) return;
    element("product-error").textContent = I18n.message(error.message);
    element("product-failure").hidden = false;
  } finally {
    if (request === productRequest) element("product-loading").hidden = true;
  }
}

function renderProduct() {
  if (!productData) return;
  element("product-title").textContent = productData.title || productItem.title;
  element("product-description").textContent = productData.description || t("暂无商品简介");
  const names = productCompatibility(productData);
  element("product-compatibility").replaceChildren(...names.map(name => {
    const badge = document.createElement("span");
    badge.className = "compatibility-tag";
    badge.textContent = name;
    return badge;
  }));
  if (!names.length) element("product-compatibility").textContent = t("未明确列出适配型号");
  const images = (productData.images || []).map(imageURL).filter(Boolean);
  element("product-thumbnails").replaceChildren(...images.map((url, index) => {
    const button = document.createElement("button");
    button.className = "product-thumbnail";
    button.setAttribute("aria-label", t("第 {number} 张图片", { number: index + 1 }));
    button.setAttribute("aria-haspopup", "menu");
    button.onclick = () => { productImageIndex = index; renderProductImage(); };
    button.oncontextmenu = event => openProductImageMenu(event, url, index + 1);
    button.onkeydown = event => {
      if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) openProductImageMenu(event, url, index + 1);
    };
    const image = document.createElement("img");
    image.src = url;
    image.alt = "";
    image.loading = "lazy";
    button.append(image);
    return button;
  }));
  renderProductImage();
  element("product-failure").hidden = true;
  element("product-layout").hidden = false;
}

function renderProductImage() {
  closeProductImageMenu();
  const images = (productData.images || []).map(imageURL).filter(Boolean);
  const image = element("product-main-image");
  image.hidden = images.length === 0;
  element("product-no-images").hidden = images.length > 0;
  element("product-no-images").textContent = t("暂无商品图片");
  element("product-image-controls").hidden = images.length < 2;
  if (images.length) {
    productImageIndex = (productImageIndex + images.length) % images.length;
    image.src = images[productImageIndex];
    image.alt = productData.title || productItem.title;
    image.onerror = () => { image.hidden = true; element("product-no-images").textContent = t("图片加载失败"); element("product-no-images").hidden = false; };
  } else image.removeAttribute("src");
  element("product-image-count").textContent = images.length ? `${productImageIndex + 1} / ${images.length}` : "";
  [...element("product-thumbnails").children].forEach((button, index) => button.setAttribute("aria-current", String(index === productImageIndex)));
}

element("close-product").onclick = () => element("product-dialog").close();
element("product-dialog").addEventListener("close", () => { productRequest++; productData = null; closeProductImageMenu(); });
element("product-retry").onclick = () => openProduct(productItem);
element("product-previous").onclick = () => { productImageIndex--; renderProductImage(); };
element("product-next").onclick = () => { productImageIndex++; renderProductImage(); };
element("product-main-image").oncontextmenu = event => openProductImageMenu(event, element("product-main-image").getAttribute("src"), productImageIndex + 1);
element("product-main-image").onkeydown = event => {
  if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) openProductImageMenu(event, element("product-main-image").getAttribute("src"), productImageIndex + 1);
};
element("product-save-image").onclick = () => run(async () => {
  const target = productImageMenuTarget;
  closeProductImageMenu();
  if (target && await desktopRequest("save-product-image", target)) notify(t("图片已保存"), false, true);
});
element("product-copy-image").onclick = () => run(async () => {
  const target = productImageMenuTarget;
  closeProductImageMenu();
  if (target && await desktopRequest("copy-product-image", target)) notify(t("图片已复制"), false, true);
});
document.addEventListener("pointerdown", event => { if (!event.target.closest("#product-image-menu")) closeProductImageMenu(); });
document.addEventListener("keydown", event => {
  if (element("product-image-menu").hidden) return;
  if (event.key === "Escape") { event.preventDefault(); closeProductImageMenu(true); }
  if (event.key === "Tab") closeProductImageMenu();
  if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
    event.preventDefault();
    const controls = [...element("product-image-menu").querySelectorAll("[role=menuitem]")];
    const index = controls.indexOf(document.activeElement);
    controls[event.key === "Home" ? 0 : event.key === "End" ? controls.length - 1 : (index + (event.key === "ArrowUp" ? -1 : 1) + controls.length) % controls.length].focus();
  }
});
window.addEventListener("scroll", () => closeProductImageMenu(), true);
window.addEventListener("resize", () => closeProductImageMenu());
window.addEventListener("blur", () => closeProductImageMenu());
