(function () {
  const normalize = value => String(value || "").normalize("NFKC").toLocaleLowerCase();
  const words = value => normalize(value).trim().split(/\s+/).filter(Boolean);
  function matches(item, filters) {
    const tags = item.tags || [];
    const haystack = normalize([item.title, item.shop, item.id, ...tags].join(" "));
    const all = words(filters.all);
    const any = words(filters.any);
    const exclude = words(filters.exclude);
    return all.every(word => haystack.includes(word))
      && (!any.length || any.some(word => haystack.includes(word)))
      && !exclude.some(word => haystack.includes(word))
      && (!filters.shop || item.shop === filters.shop)
      && (!filters.category || item.category === filters.category)
      && (!filters.subcategory || item.subcategory === filters.subcategory)
      && (!filters.event || (item.events || []).includes(filters.event))
      && (!filters.form || (item.forms || []).includes(filters.form))
      && (!filters.age || (filters.age === "adult" ? item.adult === true : item.adult === false))
      && (!filters.tags?.length || filters.tags.every(tag => tags.includes(tag)))
      && (!filters.anyTags?.length || filters.anyTags.some(tag => tags.includes(tag)))
      && !filters.excludedTags?.some(tag => tags.includes(tag));
  }
  function tagCounts(items) {
    const counts = new Map();
    for (const item of items) for (const tag of new Set(item.tags || [])) counts.set(tag, (counts.get(tag) || 0) + 1);
    return [...counts].sort((left, right) => right[1] - left[1] || left[0].localeCompare(right[0]));
  }
  globalThis.LibraryFilters = { matches, tagCounts, normalize };
})();
