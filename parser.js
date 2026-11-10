(function () {
  const normalize = value => (value || "").replace(/\s+/g, " ").trim();

  const isBoothHost = hostname => hostname === "booth.pm"
    || hostname === "accounts.booth.pm"
    || hostname.endsWith(".booth.pm");

  function boothURL(value, base) {
    try {
      const url = new URL(value, base);
      return url.protocol === "https:" && isBoothHost(url.hostname) ? url : null;
    } catch {
      return null;
    }
  }

  function itemID(value, base) {
    return boothURL(value, base)?.pathname.match(/\/items\/(\d+)(?:\/|$)/)?.[1] || null;
  }

  function files(doc, sourceURL, id) {
    const result = new Map();
    const root = doc.querySelector("main, [role='main']") || doc.body;
    for (const anchor of root.querySelectorAll("a[href]")) {
      if (itemID(anchor.getAttribute("href"), sourceURL) !== id) continue;
      let card = anchor;
      for (let parent = anchor.parentElement; parent && parent !== root; parent = parent.parentElement) {
        const ids = new Set([...parent.querySelectorAll("a[href]")].map(link => itemID(link.getAttribute("href"), sourceURL)).filter(Boolean));
        if (ids.size !== 1 || !ids.has(id)) break;
        card = parent;
      }
      for (const button of card.querySelectorAll("[data-href], a[href]")) {
        const url = boothURL(button.getAttribute("data-href") || button.getAttribute("href"), sourceURL);
        const match = url?.hostname === "booth.pm" && url.pathname.match(/^\/downloadables\/(\d+)\/?$/);
        if (!match) continue;
        let name = "";
        for (let row = button.parentElement; row && row !== card; row = row.parentElement) {
          if (row.querySelectorAll('[data-href*="/downloadables/"],a[href*="/downloadables/"]').length > 1) break;
          const copy = row.cloneNode(true);
          for (const control of copy.querySelectorAll(".js-download-button,button,a,[data-href]")) control.remove();
          name = normalize(copy.textContent);
          if (name) break;
        }
        if (name && name.length <= 500) result.set(match[1], { ...result.get(match[1]), id: match[1], name, url: url.href, material: isMaterial(name) });
      }
      const inspect = (value, depth = 0) => {
        if (depth > 12 || value == null) return;
        if (typeof value === "string") {
          const url = boothURL(value, sourceURL);
          const fileId = url?.hostname === "booth.pm" && url.pathname.match(/^\/downloadables\/(\d+)\/deeplink\/?$/)?.[1];
          const variation = url?.searchParams.get("variation_id");
          if (fileId && result.has(fileId) && /^\d{1,20}$/.test(variation || "")) {
            const file = result.get(fileId);
            file.variationIds = [...new Set([...(file.variationIds || []), variation])];
          }
        } else if (typeof value === "object") for (const child of Object.values(value)) inspect(child, depth + 1);
      };
      for (const dropdown of card.querySelectorAll("[data-dropdown-items]")) {
        try { inspect(JSON.parse(dropdown.getAttribute("data-dropdown-items"))); } catch {}
      }
    }
    return [...result.values()].sort((left, right) => Number(right.material) - Number(left.material));
  }

  function isMaterial(name) {
    const normalized = String(name).normalize("NFKC");
    return /materials?|マテリアル|材質|材质|着色器|シェーダー|shaders?|liltoon|poiyomi/i.test(normalized)
      || /(?:^|[._\s()[\]{}-])mats?(?:$|[._\s()[\]{}-]|\d)/i.test(normalized);
  }

  function parse(doc, sourceURL) {
    const source = new URL(sourceURL);
    const root = doc.querySelector("main, [role='main'], #main") || doc.body;
    const items = new Map();
    for (const anchor of root.querySelectorAll("a[href]")) {
      const id = itemID(anchor.getAttribute("href"), sourceURL);
      if (!id) continue;
      let card = anchor;
      let parent = anchor.parentElement;
      for (let depth = 0; parent && parent !== root && depth < 6; depth++, parent = parent.parentElement) {
        const ids = new Set(Array.from(parent.querySelectorAll("a[href]"))
          .map(link => itemID(link.getAttribute("href"), sourceURL)).filter(Boolean));
        if (ids.size !== 1 || !ids.has(id)) break;
        card = parent;
        if (parent.matches("article, li, [data-product-id], [data-item-id], .library-item, .item-card")) break;
      }
      const image = card.querySelector("img");
      const titleNode = card.querySelector("[data-product-name], .item-name, .item-title, .item-card__title, h2, h3, h4");
      const titleLink = Array.from(card.querySelectorAll("a[href]")).find(link =>
        itemID(link.getAttribute("href"), sourceURL) === id && normalize(link.textContent));
      const title = normalize(titleNode?.textContent || titleLink?.textContent || image?.alt || anchor.getAttribute("title"));
      if (!title) continue;
      const shopLink = Array.from(card.querySelectorAll("a[href]")).find(link => {
        const url = boothURL(link.getAttribute("href"), sourceURL);
        return url && !["booth.pm", "accounts.booth.pm"].includes(url.hostname) && !itemID(url.href, sourceURL) && normalize(link.textContent);
      });
      const libraryLink = Array.from(card.querySelectorAll("a[href]")).map(link =>
        boothURL(link.getAttribute("href"), sourceURL)).find(url =>
        url?.hostname === "accounts.booth.pm" && /^\/library\/[^/]+/.test(url.pathname));
      let imageURL = "";
      try {
        const candidate = new URL(image?.getAttribute("data-src") || image?.getAttribute("src") || "", sourceURL);
        if (candidate.protocol === "https:" && image) imageURL = candidate.href;
      } catch {}
      const item = {
        id, title: title.slice(0, 500),
        libraryLabels: [...new Set(Array.from(card.querySelectorAll("a[href]")).filter(link => itemID(link.getAttribute("href"), sourceURL) === id)
          .map(link => normalize(link.textContent)).filter(Boolean))],
        shop: normalize(shopLink?.textContent).slice(0, 200),
        url: boothURL(anchor.getAttribute("href"), sourceURL).href,
        libraryUrl: libraryLink?.href || source.href,
        image: imageURL,
        shopUrl: shopLink ? boothURL(shopLink.getAttribute("href"), sourceURL)?.href : "",
        shopImage: shopLink?.querySelector("img")?.getAttribute("src") || "",
        section: source.pathname.includes("/gifts") ? "gift" : source.pathname.includes("/free_downloads") ? "free" : "bought"
      };
      const existing = items.get(id);
      item.libraryLabels = [...new Set([...(existing?.libraryLabels || []), ...item.libraryLabels])];
      if (!existing || (!existing.image && item.image) || item.title.length > existing.title.length) items.set(id, item);
      else existing.libraryLabels = item.libraryLabels;
    }
    const currentPage = Number(source.searchParams.get("page") || 1);
    const queryKey = value => {
      const params = new URL(value).searchParams;
      params.delete("page");
      params.sort();
      return params.toString();
    };
    const pagination = Array.from(doc.querySelectorAll("a[href]")).map(anchor => {
      const url = boothURL(anchor.getAttribute("href"), sourceURL);
      if (!url || url.origin !== source.origin || url.pathname.replace(/\/$/, "") !== source.pathname.replace(/\/$/, "")) return null;
      if (queryKey(url) !== queryKey(source)) return null;
      const page = Number(url.searchParams.get("page"));
      if (!Number.isSafeInteger(page) || page <= 0) return null;
      return { url: url.href, page };
    }).filter(Boolean).sort((left, right) => left.page - right.page);
    const text = normalize(root.textContent);
    const login = /\/(?:[a-z]{2}\/)?(?:users\/sign_in|signin|login)(?:\/|$)/.test(source.pathname)
      || Boolean(doc.querySelector("input[type='password']"));
    for (const item of items.values()) {
      item.files = files(doc, sourceURL, item.id).map(({url, ...file}) => file);
      item.ownedVariationIds = [...new Set(item.files.flatMap(file => file.variationIds || []))];
    }
    let searchParams = null;
    try { searchParams = JSON.parse(doc.querySelector("#js-library-search-data")?.getAttribute("data-search-params") || "null"); } catch {}
    return {
      items: [...items.values()], next: pagination.find(link => link.page > currentPage)?.url || null, login,
      sourceUrl: source.href, sort: searchParams?.sort || null,
      searchPage: searchParams ? Number(searchParams.page || 1) : null,
      unfiltered: Boolean(searchParams && !searchParams.q && (!searchParams.category || searchParams.category === "default")
        && (!searchParams.parent_category || searchParams.parent_category === "default") && (!searchParams.adult || searchParams.adult === "include")),
      title: doc.title, textLength: text.length, page: currentPage,
      maxPage: Math.max(currentPage, ...pagination.map(link => link.page)),
      empty: items.size === 0 && /no items|no products|no gifts|no downloads|ありません|沒有|没有|尚无/i.test(text),
      displayedPage: Number(normalize(doc.querySelector("[aria-current='page'], .pagination .current, .pagination .active")?.textContent)) || null
    };
  }

  function authentication(doc, sourceURL) {
    const source = new URL(sourceURL);
    const onBooth = isBoothHost(source.hostname);
    const onPixiv = source.hostname === "pixiv.net" || source.hostname.endsWith(".pixiv.net");
    if (source.protocol !== "https:" || !(onBooth || onPixiv)) return null;
    if (/just a moment|attention required|security check|access denied/i.test(doc.title)
        || doc.querySelector('#challenge-running, #challenge-form, iframe[src*="challenges.cloudflare.com"]')) return null;
    if (/\/(?:[a-z]{2}\/)?(?:users\/sign_in|signin|login)(?:\/|$)/.test(source.pathname)
        || doc.querySelector('input[type="password"]')) return false;
    if (source.hostname !== "accounts.booth.pm" || !/^\/library(?:\/gifts|\/free_downloads)?\/?$/.test(source.pathname)) return null;
    const parsed = parse(doc, sourceURL);
    if (parsed.items.length || doc.querySelector('a[href*="sign_out"], form[action*="sign_out"]')) return true;
    if (parsed.empty && [...doc.querySelectorAll("a[href]")].some(anchor => {
      const target = boothURL(anchor.getAttribute("href"), sourceURL);
      return target?.hostname === "accounts.booth.pm" && target.pathname === "/orders";
    })) return true;
    return null;
  }

  globalThis.BoothParser = { parse, boothURL, itemID, files, isMaterial, authentication };
})();
