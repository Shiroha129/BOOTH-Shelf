let productItem = null;
let productData = null;
let productImageIndex = 0;
let productRequest = 0;

function productCompatibility(data) {
  const text = value => String(value || "").normalize("NFKC").trim();
  const bundle = /\b(?:full|all|complete)[\s_-]*(?:set|pack|avatars?|models?|edition|version|bundle|collection)(?:\b|_)|\ball[\s_-]*in[\s_-]*one\b|\bbundle\b|^(?:full|all|complete|フル)$|フル(?:セット|パック|バージョン|版)|コンプリート|全(?:\d+\s*)?(?:種類?|種|体|人|モデル|アバター|対応|部|て|套(?:装|裝)?)|まとめ(?:買い|セット)|一括|お得(?:な)?セット/i;
  const support = /\b(?:boost|support(?:er|ing)?|sponsor(?:ship)?|donation|tip)\b|応援|支援|お布施|投げ銭|寄付|赞助|贊助|후원/i;
  const generic = /^(?:(?:通常|普通|標準|标准|有料|無料|無償|有償|下载|下載|ダウンロード)(?:版)?|(?:normal|standard|regular|download)(?:\s+(?:edition|version))?|商品|本体|データ|data|衣装|衣裝|outfit|texture|テクスチャ|unitypackage|fbx|vrchat|\d+\s*(?:アバター|モデル|avatars?|models?|人|体|種)|[\d.,]+\s*(?:円|元|yen)|s|m|l|xl|xxl|black|white|red|blue|pink|黒|白|赤|青)$/i;
  const excluded = value => bundle.test(text(value)) || support.test(text(value)) || generic.test(text(value));
  const split = value => text(value).replace(/\s*[-–—]\s*\([^)]*\)\s*$/, "")
    .replace(/^(?:for\s+|対応アバター\s*[:：]\s*)/i, "")
    .split(/[\/|,、&+]/).map(name => name.trim().replace(/^[・●〇○■\-—\s]+/, "")
      .replace(/\s*(?:向け|専用|対応(?:版)?|用(?:版)?|版)$/, "").trim())
    .filter(name => name && !excluded(name) && text(name) !== text(data.title));
  const unique = values => {
    const seen = new Set();
    return values.filter(name => !seen.has(name.toLowerCase()) && seen.add(name.toLowerCase()));
  };
  const variants = (data.variations || []).filter(variant => !excluded(variant.name)).flatMap(variant => split(variant.name));
  if (variants.length) return unique(variants);

  const names = [];
  const lines = String(data.description || "").normalize("NFKC").split(/\r?\n/);
  let listing = false;
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index].trim();
    const heading = line.match(/^[\s■●〇◆◇★☆【\[]*(?:対応(?:アバター|モデル)(?:一覧)?|適配(?:型號|模型)|适配(?:型号|模型)|Supported\s+(?:avatars|models)|Compatible\s+(?:avatars|models|with))[\s】\]]*[:：]?\s*(.*)$/i);
    if (heading) { listing = true; names.push(...split(heading[1])); }
    else if (listing && line) {
      if (/^[【\[■◆#]|[。!?：:]/.test(line) && !/^https?:\/\//i.test(line)) listing = false;
      else if (!/^https?:\/\//i.test(line) && line.length < 80) names.push(...split(line));
    }
    const reference = line.match(/^[-—─]\s*(.+?)\s*[-—─]$/);
    if (reference && /3D|モデル|アバター|様/i.test(lines[index + 1] || "")) names.push(...split(reference[1]));
  }
  for (const match of text(data.title).matchAll(/[【\[]([^【】\[\]\n]+?)(?:専用|対応|用)[】\]]/g)) names.push(...split(match[1]));
  return unique(names);
}

async function openProduct(item) {
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
    button.onclick = () => { productImageIndex = index; renderProductImage(); };
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
element("product-dialog").addEventListener("close", () => { productRequest++; productData = null; });
element("product-retry").onclick = () => openProduct(productItem);
element("product-previous").onclick = () => { productImageIndex--; renderProductImage(); };
element("product-next").onclick = () => { productImageIndex++; renderProductImage(); };
