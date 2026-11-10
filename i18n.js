(function () {
  const languages = ["zh-CN", "zh-TW", "ja", "en", "ko"];
  const locales = { "zh-CN": "zh-CN", "zh-TW": "zh-TW", ja: "ja-JP", en: "en-US", ko: "ko-KR" };
  const names = { "zh-CN": "简体中文", "zh-TW": "繁體中文", ja: "日本語", en: "English", ko: "한국어" };
  let language = "zh-CN";
  let catalog = {};
  const bindings = [];
  let patterns = [];
  const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  function resolve(systemLanguage) {
    const code = String(systemLanguage || "en").replaceAll("_", "-").toLowerCase();
    if (/^zh(?:-|$)/.test(code)) return code.includes("-hans") ? "zh-CN" : /(?:-hant|-tw|-hk|-mo)(?:-|$)/.test(code) ? "zh-TW" : "zh-CN";
    if (/^ja(?:-|$)/.test(code)) return "ja";
    if (/^ko(?:-|$)/.test(code)) return "ko";
    return "en";
  }
  function t(source, values = {}) {
    const translated = language === "zh-CN" ? source : catalog[source]?.[languages.indexOf(language) - 1] ?? source;
    return translated.replace(/\{(\w+)\}/g, (match, key) => values[key] == null ? match : String(values[key]));
  }
  function message(value, depth = 0) {
    if (value && typeof value === "object") return t(value.key, value.values);
    const source = String(value || "");
    if (Object.hasOwn(catalog, source)) return t(source);
    if (depth > 4) return source;
    for (const pattern of patterns) {
      const match = source.match(pattern.regex);
      if (match) return t(pattern.source, Object.fromEntries(pattern.keys.map((key, index) => [key, message(match[index + 1], depth + 1)])));
    }
    return source.includes(" · ") ? source.split(" · ").map(part => message(part, depth + 1)).join(" · ") : source;
  }
  function bind(root) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node = walker.currentNode;
      const source = node.textContent.trim();
      if (Object.hasOwn(catalog, source)) {
        const leading = node.textContent.match(/^\s*/)[0];
        const trailing = node.textContent.match(/\s*$/)[0];
        bindings.push(() => { node.textContent = leading + t(source) + trailing; });
      }
    }
    for (const node of root.querySelectorAll("*"))
      for (const attribute of ["placeholder", "aria-label", "title"])
        if (Object.hasOwn(catalog, node.getAttribute(attribute))) {
          const source = node.getAttribute(attribute);
          bindings.push(() => node.setAttribute(attribute, t(source)));
        }
    for (const template of root.querySelectorAll("template")) bind(template.content);
  }
  const ready = fetch("locales.json").then(response => {
    if (!response.ok) throw new Error("Language resources could not be loaded.");
    return response.json();
  }).then(data => {
    catalog = data;
    patterns = Object.keys(catalog).filter(source => /\{\w+\}/.test(source)).map(source => {
      const keys = [...source.matchAll(/\{(\w+)\}/g)].map(match => match[1]);
      const regex = new RegExp("^" + source.split(/\{\w+\}/).map(escapeRegex).join("([\\s\\S]+?)") + "$");
      return { source, keys, regex };
    });
    bind(document);
  });
  globalThis.I18n = {
    languages, names, ready, resolve, t, message,
    get locale() { return locales[language]; },
    get language() { return language; },
    set(preference, systemLanguage) {
      language = languages.includes(preference) ? preference : resolve(systemLanguage || navigator.language);
      document.documentElement.lang = locales[language];
      for (const apply of bindings) apply();
    }
  };
})();
