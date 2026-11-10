(function () {
  const normalize = value => String(value || "").replace(/\s+/g, " ").trim();
  const amount = value => {
    const match = normalize(value).match(/^([\d,]+(?:\.\d+)?)\s*(?:JPY|円)$/i);
    return match ? Number(match[1].replace(/,/g, "")) : null;
  };
  function orders(doc, sourceURL) {
    const source = new URL(sourceURL);
    const links = [...doc.querySelectorAll('a[href]')].map(node => ({ node, url: new URL(node.getAttribute("href"), sourceURL) }));
    const ids = [...new Set(links.filter(({url}) => url.origin === source.origin && /^\/orders\/\d+$/.test(url.pathname)).map(({url}) => url.pathname.split("/").pop()))];
    const pages = links.filter(({url}) => url.origin === source.origin && url.pathname === source.pathname).map(({url}) => Number(url.searchParams.get("page"))).filter(Number.isSafeInteger);
    const root = doc.querySelector("main") || doc.body;
    const text = normalize(root.textContent);
    if (!ids.length && !/no orders|no purchase|ありません|没有|尚无|購入履歴がありません/i.test(text)) throw new Error("订单列表未识别，登录可能已失效。");
    return { ids, page: Number(source.searchParams.get("page") || 1), maxPage: Math.max(1, ...pages) };
  }
  function receipt(doc, sourceURL) {
    const root = doc.querySelector("main") || doc.body;
    const text = normalize(root.textContent);
    const id = new URL(sourceURL).pathname.match(/^\/orders\/(\d+)$/)?.[1];
    if (!id || doc.querySelector('input[type="password"]')) throw new Error("订单详情未识别，请重新登录。");
    const heading = text.slice(0, text.indexOf("Contact this Shop") > 0 ? text.indexOf("Contact this Shop") : 450);
    const canceled = /cancelled|canceled|キャンセル|已取消|取消済/i.test(heading);
    const paid = !canceled && /completed|paid|完了|已付款|已完成|支払済|支付完成/i.test(heading) && !/unpaid|not paid|未払|未付款|未支付/i.test(heading);
    const totalMatch = text.match(/(?:支付金额|支付金額|お支払い金額|支払金額|お支払い総額|Payment Amount|Amount Paid|Total Amount|Total payment)\s*([\d,]+(?:\.\d+)?)\s*(?:JPY|円)/i);
    const createdMatch = text.match(/(?:Created At|注文日時|注文日|创建时间|建立時間)\s*(\d{4}\/\d{2}\/\d{2} \d{2}:\d{2}(?::\d{2})?)/i);
    const lines = [];
    const seen = new Set();
    for (const anchor of root.querySelectorAll('a[href*="/items/"]')) {
      if (!normalize(anchor.textContent)) continue;
      const itemId = BoothParser.itemID(anchor.getAttribute("href"), sourceURL);
      if (!itemId) continue;
      let row = anchor.parentElement;
      while (row && row !== root) {
        const ids = new Set([...row.querySelectorAll('a[href*="/items/"]')].map(node => BoothParser.itemID(node.getAttribute("href"), sourceURL)).filter(Boolean));
        if (ids.size > 1) break;
        const prices = [...row.querySelectorAll("div,p,span")].filter(node => amount(node.textContent) !== null);
        if (prices.length) {
          const price = amount(prices[0].textContent);
          const boostNode = [...row.querySelectorAll(".particulars-heading")].find(node => /BOOST/i.test(node.textContent));
          const boost = boostNode ? Number(normalize(boostNode.parentElement.textContent).match(/BOOST\s*([\d,]+)/i)?.[1]?.replace(/,/g, "") || 0) : 0;
          const title = normalize(anchor.textContent);
          if (!seen.has(row)) { lines.push({ itemId, title, price, boost }); seen.add(row); }
          break;
        }
        row = row.parentElement;
      }
    }
    if (paid && (totalMatch === null || !lines.length)) throw new Error("订单 " + id + " 的实际支付金额无法确认，已保留已有价格。");
    return { id, paid, canceled, total: totalMatch ? Number(totalMatch[1].replace(/,/g, "")) : null, currency: "JPY", lines,
      createdAt: createdMatch ? createdMatch[1].replace(/\//g, "-").replace(" ", "T") + "+09:00" : null };
  }
  const jobs = new Map();
  globalThis.AccountReader = {
    orders, receipt,
    start(key, target, kind) {
      const url = new URL(target, location.href);
      if (url.origin !== "https://accounts.booth.pm" || !/^\/(?:orders(?:\/\d+)?|library(?:\/gifts|\/free_downloads)?)\/?$/.test(url.pathname)) throw new Error("无效账号页面");
      const controller = new AbortController();
      const job = { controller, result: null };
      jobs.set(key, job);
      const timeout = setTimeout(() => controller.abort(), 30000);
      (async () => {
        try {
          const response = await fetch(url.href, { credentials: "same-origin", cache: "no-cache", signal: controller.signal });
          if (new URL(response.url).pathname !== url.pathname) throw new Error("会话已跳转，请重新登录。");
          if (!response.ok) {
            const header = response.headers.get("Retry-After");
            const wait = header && /^\d+$/.test(header) ? Number(header) * 1000 : Math.max(0, Date.parse(header) - Date.now()) || 0;
            job.result = { error: "BOOTH 返回 HTTP " + response.status, retryable: response.status === 429 || response.status >= 500, retryAfterMs: Math.min(2147480000, wait) };
            return;
          }
          const doc = new DOMParser().parseFromString(await response.text(), "text/html");
          if (doc.querySelector('input[type="password"]') || /just a moment|attention required/i.test(doc.title)) throw new Error("BOOTH 要求登录或网页验证，请完成后继续。");
          const data = kind === "orders" ? orders(doc, response.url) : kind === "receipt" ? receipt(doc, response.url) : BoothParser.parse(doc, response.url);
          data.serverDate = response.headers.get("Date");
          job.result = { data };
        } catch (error) { job.result = { error: error.message, retryable: error.name === "AbortError" || error instanceof TypeError }; }
        finally { clearTimeout(timeout); }
      })();
      return true;
    },
    take(key) { const job = jobs.get(key); if (!job) return { error: "读取任务已丢失" }; if (!job.result) return null; jobs.delete(key); return job.result; },
    cancel(key) { jobs.get(key)?.controller.abort(); jobs.delete(key); }
  };
})();
