using System.Text.Json.Nodes;

namespace BoothShelf;

internal static class FilterPresetSelfTest
{
    internal static JsonObject Run(string root)
    {
        var store = new FilterPresetStore(root);
        var payload = JsonNode.Parse("""{"name":"衣装","sort":"name","selectedShop":"","filters":{"all":"VRChat","tags":["A"],"anyTags":["B","C"],"excludedTags":["D"]}}""")!;
        var saved = store.Save(payload);
        var id = saved[0]!["id"]!.ToString();
        var restarted = new FilterPresetStore(root);
        if (!JsonNode.DeepEquals(saved, restarted.List())) throw new InvalidOperationException("预设重启后丢失");
        payload["id"] = id;
        payload["name"] = "衣装更新";
        if (restarted.Save(payload).Count != 1) throw new InvalidOperationException("更新预设产生重复");
        var previous = File.ReadAllText(Path.Combine(root, "filter-presets.json"));
        payload["filters"]!["excludedTags"]!.AsArray().Add("A");
        var rejected = false;
        try { restarted.Save(payload); } catch (InvalidDataException) { rejected = true; }
        if (!rejected || File.ReadAllText(Path.Combine(root, "filter-presets.json")) != previous) throw new InvalidOperationException("非法组合修改了原预设");
        restarted.Delete(id);
        if (new FilterPresetStore(root).List().Count != 0) throw new InvalidOperationException("删除预设未保存");
        return new JsonObject { ["persisted"] = true, ["updatedWithoutDuplicates"] = true, ["invalidRulePreservesData"] = true, ["deletePersisted"] = true };
    }
}
