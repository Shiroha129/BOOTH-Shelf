using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BoothShelf;

internal static class NativeLanguage
{
    internal static readonly string SystemLanguage = CultureInfo.CurrentUICulture.Name;
    internal static string Preference { get; set; } = "auto";
    private static readonly Lazy<JsonObject> Catalog = new(() => JsonNode.Parse(Program.ReadAsset("locales.json"))!.AsObject());

    internal static string Resolve(string code)
    {
        code = code.Replace('_', '-').ToLowerInvariant();
        if (Regex.IsMatch(code, "^zh(?:-|$)")) return code.Contains("-hans") ? "zh-CN" : Regex.IsMatch(code, "(?:-hant|-tw|-hk|-mo)(?:-|$)") ? "zh-TW" : "zh-CN";
        if (Regex.IsMatch(code, "^ja(?:-|$)")) return "ja";
        if (Regex.IsMatch(code, "^ko(?:-|$)")) return "ko";
        return "en";
    }

    internal static void Initialize(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "settings.json");
        try { if (File.Exists(path)) Preference = JsonNode.Parse(File.ReadAllText(path))?["language"]?.GetValue<string>() ?? "auto"; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
    }

    internal static string Text(string source)
    {
        var language = Preference == "auto" ? Resolve(SystemLanguage) : Preference;
        var index = Array.IndexOf(new[] { "zh-TW", "ja", "en", "ko" }, language);
        return index >= 0 ? Catalog.Value[source]?[index]?.GetValue<string>() ?? source : source;
    }

    internal static string Message(string source, int depth = 0)
    {
        if (Catalog.Value.ContainsKey(source)) return Text(source);
        if (depth > 4) return source;
        foreach (var entry in Catalog.Value)
        {
            var keys = Regex.Matches(entry.Key, @"\{(\w+)\}").Select(match => match.Groups[1].Value).ToArray();
            if (keys.Length == 0) continue;
            var pattern = "^" + string.Join("([\\s\\S]+?)", Regex.Split(entry.Key, @"\{\w+\}").Select(Regex.Escape)) + "$";
            var match = Regex.Match(source, pattern);
            if (!match.Success) continue;
            var translated = Text(entry.Key);
            for (var index = 0; index < keys.Length; index++) translated = translated.Replace("{" + keys[index] + "}", Message(match.Groups[index + 1].Value, depth + 1));
            return translated;
        }
        return source;
    }
}
