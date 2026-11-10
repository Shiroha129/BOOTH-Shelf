import http from "node:http";
import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const allowed = new Set(["index.html", "styles.css", "app.js", "features.js", "parser.js", "library-filters.js", "account-reader.js", "i18n.js", "locales.json", "tests/bridge.js", "tests/browser-test.js"]);
allowed.add("filter-presets.js");
allowed.add("notifications.js");
for (const name of ["showcase.js", "outfit.svg", "hair.svg", "glasses.svg", "material.svg"]) allowed.add("tests/showcase/" + name);
const server = http.createServer(async (request, response) => {
  try {
    const url = new URL(request.url, "http://127.0.0.1:5185");
    if (url.pathname === "/test-result" && request.method === "POST") {
      let body = "";
      for await (const chunk of request) {
        body += chunk;
        if (body.length > 30000) throw new Error("Report too large");
      }
      const result = JSON.parse(body);
      await fs.writeFile(path.join(root, "tests/browser-result.json"), JSON.stringify(result, null, 2));
      response.writeHead(200, { "Content-Type": "application/json" }).end('{"saved":true}');
      return;
    }
    const name = url.pathname === "/" ? "index.html" : url.pathname.slice(1);
    if (!allowed.has(name)) { response.writeHead(404).end(); return; }
    let body = await fs.readFile(path.join(root, name), "utf8");
    if (name === "index.html") body = body.replace('<script src="parser.js"', '<script src="tests/bridge.js" defer></script><script src="account-reader.js" defer></script><script src="parser.js"')
      .replace("</head>", '<script src="tests/browser-test.js" defer></script></head>');
    if (name === "index.html" && url.searchParams.has("showcase")) body = body.replace("</head>", '<script src="tests/showcase/showcase.js" defer></script></head>');
    response.writeHead(200, { "Content-Type": name.endsWith(".svg") ? "image/svg+xml" : name.endsWith(".css") ? "text/css; charset=utf-8" : name.endsWith(".js") ? "text/javascript; charset=utf-8" : name.endsWith(".json") ? "application/json; charset=utf-8" : "text/html; charset=utf-8", "Cache-Control": "no-store" }).end(body);
  } catch (error) { response.writeHead(500).end(error.message); }
});
server.listen(5185, "127.0.0.1", () => console.log("UI fixture: http://127.0.0.1:5185/?tests=1"));
