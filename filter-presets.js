const filterFields = { all: "search", any: "any-keywords", exclude: "exclude-keywords", shop: "shop-filter", category: "category-filter", subcategory: "subcategory-filter", event: "event-filter", form: "form-filter", age: "age-filter" };
let lastPresetName = "";

function currentPreset() { return { filters: getFilters(), sort: element("sort").value, selectedShop }; }
function presetSignature(preset) {
  const filters = preset.filters || {};
  return JSON.stringify([Object.keys(filterFields).map(key => filters[key] || ""),
    ["tags", "anyTags", "excludedTags"].map(key => [...(filters[key] || [])].sort()), preset.sort || "default", preset.selectedShop || ""]);
}
function presetSummary(preset) {
  const filters = preset.filters || {};
  return [["包含", filters.tags], ["任一", filters.anyTags], ["排除", filters.excludedTags]]
    .filter(([, values]) => values?.length).map(([label, values]) => t(label) + ": " + values.join("、"))
    .concat(Object.entries(filterFields).filter(([key]) => filters[key]).map(([key, id]) => (element(id).getAttribute("aria-label") || t({ all: "全部关键词", any: "任一关键词", exclude: "排除关键词", shop: "店铺", category: "类别", subcategory: "详细分类", event: "活动", form: "商品形态", age: "年龄限制" }[key])) + ": " + filters[key]))
    .concat(preset.selectedShop ? [t("店铺") + ": " + preset.selectedShop] : []).join(" · ") || t("不限");
}
function renderPresets() {
  const fragment = document.createDocumentFragment();
  const signature = presetSignature(currentPreset());
  for (const preset of filterPresets) {
    const chip = document.createElement("div");
    chip.className = "preset-chip";
    const apply = document.createElement("button");
    apply.className = "preset-apply";
    apply.textContent = preset.name;
    apply.title = presetSummary(preset);
    apply.setAttribute("aria-pressed", String(presetSignature(preset) === signature));
    apply.onclick = () => applyPreset(preset);
    const remove = document.createElement("button");
    remove.className = "preset-remove";
    remove.textContent = "×";
    remove.setAttribute("aria-label", t("删除预设：{name}", { name: preset.name }));
    remove.onclick = () => {
      if (!confirm(t("删除预设「{name}」？", { name: preset.name }))) return;
      run(async () => { filterPresets = await desktopRequest("preset-delete", { id: preset.id }); renderPresets(); });
    };
    chip.append(apply, remove);
    fragment.append(chip);
  }
  element("preset-list").replaceChildren(fragment);
  element("preset-list").hidden = filterPresets.length === 0;
}
function setPresetField(id, value) {
  const field = element(id);
  if (value && field.tagName === "SELECT" && ![...field.options].some(option => option.value === value)) field.add(new Option(value, value));
  field.value = value || "";
}
function applyPreset(preset) {
  lastPresetName = preset.name;
  selectedShop = preset.selectedShop || "";
  selectedTags = new Set(preset.filters.tags || []);
  anyTags = new Set(preset.filters.anyTags || []);
  excludedTags = new Set(preset.filters.excludedTags || []);
  renderShops();
  for (const [key, id] of Object.entries(filterFields)) {
    setPresetField(id, preset.filters[key]);
    if (key === "category") renderSubcategories();
  }
  element("sort").value = preset.sort || "default";
  element("tag-search").value = "";
  element("selected-shop").textContent = snapshot.items.find(item => shopKey(item) === selectedShop)?.shop || selectedShop;
  element("filter-panel").hidden = !Object.keys(filterFields).some(key => !["all", "shop"].includes(key) && preset.filters[key]);
  element("filter-toggle").setAttribute("aria-expanded", String(!element("filter-panel").hidden));
  displayPage = 1;
  renderTags();
  render();
}
function updatePresetSaveLabel() {
  const name = element("preset-name").value.trim();
  element("preset-submit").textContent = t(filterPresets.some(preset => preset.name === name) ? "更新预设" : "保存预设");
}
element("save-preset").onclick = () => {
  element("preset-name").value = lastPresetName;
  element("preset-summary").textContent = presetSummary(currentPreset());
  element("preset-error").hidden = true;
  updatePresetSaveLabel();
  element("preset-dialog").showModal();
  element("preset-name").focus();
};
element("preset-name").oninput = updatePresetSaveLabel;
for (const id of ["preset-cancel", "dismiss-preset"]) element(id).onclick = () => element("preset-dialog").close();
element("preset-form").onsubmit = async event => {
  event.preventDefault();
  const name = element("preset-name").value.trim();
  if (!name) { element("preset-name").focus(); return; }
  element("preset-submit").disabled = true;
  try {
    filterPresets = await desktopRequest("preset-save", { ...currentPreset(), name, id: filterPresets.find(preset => preset.name === name)?.id || "" });
    lastPresetName = name;
    renderPresets();
    element("preset-dialog").close();
  } catch (error) { element("preset-error").textContent = I18n.message(error.message); element("preset-error").hidden = false; }
  finally { element("preset-submit").disabled = false; }
};
