(function () {
  const normalize = value => String(value || "").replace(/\s+/g, " ").trim();
  const digits = String.raw`(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d{1,2})?`;
  const yen = `(?:(?:¥|JPY)\\s*${digits}(?:\\s*(?:円|JPY))?|${digits}\\s*(?:円|JPY))`;
  const tax = String.raw`(?:\s*\((?:税込み?|tax included)\))?`;
  const exactYen = new RegExp(`^(${yen})${tax}$`, "i");
  const amount = value => {
    const match = normalize(value).normalize("NFKC").match(exactYen);
    return match ? Number(match[1].replace(/[^\d.]/g, "")) : null;
  };
  const paymentLabel = "(?:お?支払(?:い)?(?:合計金額|合計|総額|金額|額)|(?:ご)?請求金額|決済金額|支付金[额額]|Payment Amount|Amount Paid|Total Amount|Total payment)";
  const paymentTotal = new RegExp(`${paymentLabel}${tax}\\s*:?\\s*(${yen})(?![\\d,.])`, "gi");
  const totalLabel = new RegExp(`^(?:(?:ご?注文)?合計(?:金額)?|Grand total|Total)${tax}\\s*:?$`, "i");
  function orderTotal(root, text) {
    const values = new Set([...text.matchAll(paymentTotal)].map(match => amount(match[1])));
    if (!values.size) {
      // A generic total must be a separate, exact label. Never substitute the item subtotal.
      for (const label of root.querySelectorAll("dt,th,td,div,p,span,strong")) {
        if (!totalLabel.test(normalize(label.textContent).normalize("NFKC"))) continue;
        const value = amount(label.nextElementSibling?.textContent);
        if (value !== null) values.add(value);
      }
    }
    return values.size === 1 ? [...values][0] : null;
  }
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
    const text = normalize(root.textContent).normalize("NFKC");
    const id = new URL(sourceURL).pathname.match(/^\/orders\/(\d+)$/)?.[1];
    if (!id || doc.querySelector('input[type="password"]')) throw new Error("订单详情未识别，请重新登录。");
    const heading = text.slice(0, text.indexOf("Contact this Shop") > 0 ? text.indexOf("Contact this Shop") : 450);
    const canceled = /cancelled|canceled|キャンセル|已取消|取消済/i.test(heading);
    const paid = !canceled && /completed|paid|完了|已付款|已完成|(?:支払い?|決済|入金)済|支付完成/i.test(heading) && !/unpaid|not paid|未払|支払い?待ち|入金待ち|未付款|未支付/i.test(heading);
    const total = orderTotal(root, text);
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
        if (row.querySelector("article,tr")) break;
        const boostNode = [...row.querySelectorAll(".particulars-heading")].find(node => /BOOST/i.test(node.textContent));
        const priceNode = [...row.querySelectorAll("div,p,span,td,dd,strong,b")].find(node => !boostNode?.parentElement.contains(node) && amount(node.textContent) !== null);
        if (priceNode) {
          const price = amount(priceNode.textContent);
          const boost = boostNode ? amount(normalize(boostNode.parentElement.textContent).replace(/^.*?BOOST\s*:?\s*/i, "")) ?? 0 : 0;
          const title = normalize(anchor.textContent);
          if (!seen.has(row)) { lines.push({ itemId, title, price, boost }); seen.add(row); }
          break;
        }
        row = row.parentElement;
      }
    }
    return { id, paid, canceled, paymentUnconfirmed: paid && (total === null || !lines.length),
      total, currency: "JPY", lines,
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
          if (!response.ok) {
            const header = response.headers.get("Retry-After");
            const wait = header && /^\d+$/.test(header) ? Number(header) * 1000 : Math.max(0, Date.parse(header) - Date.now()) || 0;
            const body = await response.text();
            const challenge = /<title[^>]*>\s*(?:just a moment|attention required|security verification|checking your browser)|(?:id=["'](?:challenge-form|cf-challenge-running)["'])|<script[^>]+\/cdn-cgi\/challenge-platform\//i.test(body);
            job.result = { error: "BOOTH 返回 HTTP " + response.status, status: response.status, challenge, retryable: response.status === 429 || response.status >= 500, retryAfterMs: Math.min(2147480000, wait) };
            return;
          }
          const doc = new DOMParser().parseFromString(await response.text(), "text/html");
          if (/just a moment|attention required|security verification|checking your browser/i.test(doc.title) || doc.querySelector('#challenge-form, #cf-challenge-running, script[src*="/cdn-cgi/challenge-platform/"]')) {
            job.result = { error: "BOOTH 自动访问已暂停，请到设置中查看并手动继续。", status: response.status, challenge: true, retryable: false };
            return;
          }
          if (new URL(response.url).pathname !== url.pathname) throw new Error("会话已跳转，请重新登录。");
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
