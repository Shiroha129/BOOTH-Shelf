using System.Text.Json.Nodes;

namespace BoothShelf;

internal sealed class FilterPresetStore
{
    private readonly string path;
    private JsonArray presets;
    internal FilterPresetStore(string root)
    {
        path = Path.Combine(root, "filter-presets.json");
        presets = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : new JsonArray();
    }
    internal JsonArray List() => (JsonArray)presets.DeepClone();
    private static string Text(JsonNode? value, int maximum = 2000)
    {
        if (value == null) return "";
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var result) || result.Length > maximum)
            throw new InvalidDataException("预设内容无效或过长");
        return result;
    }
    internal JsonArray Save(JsonNode? payload)
    {
        var name = Text(payload?["name"], 80).Trim();
        var id = Text(payload?["id"], 32);
        if (name.Length == 0 || payload?["filters"] is not JsonObject source) throw new InvalidDataException("请输入预设名称");
        var updated = List();
        var existing = updated.OfType<JsonObject>().FirstOrDefault(preset => preset["id"]!.GetValue<string>() == id);
        if (id.Length > 0 && existing == null) throw new InvalidDataException("预设已不存在");
        if (updated.OfType<JsonObject>().Any(preset => preset != existing && preset["name"]!.GetValue<string>() == name)) throw new InvalidDataException("已存在同名预设");
        if (existing == null && updated.Count >= 100) throw new InvalidDataException("预设最多保存 100 个");
        var filters = new JsonObject();
        foreach (var key in new[] { "all", "any", "exclude", "shop", "category", "subcategory", "event", "form", "age" }) filters[key] = Text(source[key]);
        var used = new HashSet<string>();
        foreach (var key in new[] { "tags", "anyTags", "excludedTags" })
        {
            if (source[key] is not JsonArray tags || tags.Count > 500) throw new InvalidDataException("预设内容无效或过长");
            var values = tags.Select(tag => Text(tag, 300)).Distinct().ToArray();
            if (values.Any(tag => string.IsNullOrWhiteSpace(tag) || !used.Add(tag))) throw new InvalidDataException("同一标签不能同时使用多种规则");
            filters[key] = new JsonArray(values.Select(tag => (JsonNode)JsonValue.Create(tag)!).ToArray());
        }
        var sort = Text(payload?["sort"]);
        if (sort is not ("default" or "name" or "shop")) throw new InvalidDataException("预设排序无效");
        var saved = new JsonObject { ["id"] = existing?["id"]?.DeepClone() ?? JsonValue.Create(Guid.NewGuid().ToString("N")),
            ["name"] = name, ["filters"] = filters, ["sort"] = sort, ["selectedShop"] = Text(payload?["selectedShop"]) };
        if (existing == null) updated.Add(saved); else updated[updated.IndexOf(existing)] = saved;
        return Persist(updated);
    }
    internal JsonArray Delete(string id) => Persist(new JsonArray(presets.OfType<JsonObject>().Where(preset => preset["id"]!.GetValue<string>() != id).Select(preset => preset.DeepClone()).ToArray()));
    private JsonArray Persist(JsonArray updated)
    {
        SyncCache.AtomicWrite(path, updated.ToJsonString());
        presets = updated;
        return List();
    }
}
