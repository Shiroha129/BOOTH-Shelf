import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const catalogText = await fs.readFile(path.join(root, "locales.json"), "utf8");
const catalogKeys = [...catalogText.matchAll(/^\s*("(?:[^"\\]|\\.)*"):/gm)].map(match => JSON.parse(match[1]));
assert.equal(new Set(catalogKeys).size, catalogKeys.length, "Duplicate translation keys");
const catalog = JSON.parse(catalogText);
const placeholders = value => [...value.matchAll(/\{\w+\}/g)].map(match => match[0]).sort();
const escape = value => value.replace(/[.*+?^{}$()|[\]\\]/g, "\\$&");
const patterns = Object.keys(catalog).filter(key => /\{\w+\}/.test(key))
  .map(key => new RegExp("^" + key.split(/\{\w+\}/).map(escape).join("[\\s\\S]+?") + "$"));
const known = value => Object.hasOwn(catalog, value) || patterns.some(pattern => pattern.test(value))
  || (value.includes(" · ") && value.split(" · ").every(part => known(part)));
const names = new Set(["简体中文", "繁體中文", "日本語"]);
const smokeErrors = new Set(["桌面界面冒烟检查失败：", "本地存储检查失败", "大库界面检查失败："]);
let checked = 0;
function checkSource(value, location) {
  if (!/\p{Script=Han}/u.test(value) || names.has(value)) return;
  assert.ok(known(value), "Missing translation at " + location + ": " + value);
  checked++;
}
for (const [source, variants] of Object.entries(catalog)) {
  assert.equal(variants.length, 4, "Missing language: " + source);
  for (const variant of variants) {
    assert.ok(typeof variant === "string" && variant.trim(), "Empty translation: " + source);
    assert.deepEqual(placeholders(variant), placeholders(source), "Wrong placeholders: " + source);
  }
}
const dom = new JSDOM(await fs.readFile(path.join(root, "index.html"), "utf8"));
function walk(node) {
  if (node.nodeType === 3) checkSource(node.textContent.trim(), "index.html");
  for (const child of node.childNodes) walk(child);
  if (node.localName === "template") walk(node.content);
  if (node.nodeType === 1) for (const name of ["title", "placeholder", "aria-label"])
    checkSource(node.getAttribute(name) || "", "index.html " + name);
}
walk(dom.window.document.body);
dom.window.close();
for (const directory of [root, path.join(root, "desktop")]) {
  for (const file of await fs.readdir(directory)) {
    if (!/\.(?:js|cs)$/.test(file) || /SelfTest|Diagnostics/.test(file)) continue;
    const source = await fs.readFile(path.join(directory, file), "utf8");
    const literals = /(?:\bt|\bnotify|new Error|NativeLanguage\.(?:Text|Message)|SetHint|throw new \w+)\(\s*\$?("(?:[^"\\]|\\.)*")|(?:\bText|\bDescription|\bFilter|\bconst string \w*Error)\s*=\s*\$?("(?:[^"\\]|\\.)*")/g;
    for (const match of source.matchAll(literals)) {
      let value = JSON.parse(match[1] || match[2]);
      if (file === "Program.cs" && smokeErrors.has(value)) continue;
      if (source.slice(match.index + match[0].length).trimStart().startsWith("+")) value += "{value}";
      checkSource(value, file + ":" + source.slice(0, match.index).split("\n").length);
    }
  }
}
console.log(JSON.stringify({ passed: true, catalogEntries: Object.keys(catalog).length, textSources: checked }));
