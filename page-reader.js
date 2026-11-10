(function () {
  if (globalThis.BoothPageReader) return;
  const jobs = new Map();
  const valid = value => {
    const url = new URL(value);
    return url.origin === "https://accounts.booth.pm" && /^\/library(?:\/gifts|\/free_downloads)?\/?$/.test(url.pathname);
  };
  globalThis.BoothPageReader = {
    start(key, page) {
      if (!valid(location.href) || !Number.isSafeInteger(page) || page < 1) throw new Error("请先打开 BOOTH Library。");
      const controller = new AbortController();
      const job = { controller, result: null };
      jobs.set(key, job);
      const timeout = setTimeout(() => controller.abort(), 30000);
      const url = new URL(location.href);
      url.searchParams.set("page", String(page));
      url.hash = "";
      (async () => {
        try {
          const response = await fetch(url.href, { credentials: "same-origin", cache: "no-cache", signal: controller.signal });
          if (!valid(response.url)) throw new Error("会话已跳转到登录或验证页，请重新登录后继续。");
          if (!response.ok) {
            const retryHeader = response.headers.get("Retry-After");
            const retrySeconds = Number(retryHeader);
            const retryAfterMs = retryHeader ? (Number.isFinite(retrySeconds) ? retrySeconds * 1000 : Math.max(0, Date.parse(retryHeader) - Date.now())) : 0;
            job.result = { error: `BOOTH 返回 HTTP ${response.status}`, retryable: response.status === 429 || response.status >= 500, retryAfterMs: Math.min(2147480000, Math.max(0, retryAfterMs || 0)) };
            return;
          }
          const html = await response.text();
          const doc = new DOMParser().parseFromString(html, "text/html");
          const title = doc.title || "";
          if (/just a moment|attention required|アクセス制限/i.test(title)) throw new Error("BOOTH 要求网页验证，请在登录窗口完成后继续。");
          const data = BoothParser.parse(doc, response.url);
          if (data.login) throw new Error("登录已失效，请重新登录后继续。");
          job.result = { data };
        } catch (error) {
          job.result = { error: error.name === "AbortError" ? "页面请求超时或已暂停" : error.message, retryable: error.name === "AbortError" || error instanceof TypeError };
        } finally { clearTimeout(timeout); }
      })();
      return true;
    },
    take(key) {
      const job = jobs.get(key);
      if (!job) return { error: "同步页面已重新加载，请继续同步。", retryable: false };
      if (!job.result) return null;
      jobs.delete(key);
      return job.result;
    },
    cancel(key) {
      jobs.get(key)?.controller.abort();
      jobs.delete(key);
    }
  };
})();
