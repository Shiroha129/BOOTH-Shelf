using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BoothShelf;

internal static class LanguageSelfTest
{
    internal static JsonObject Run(string root)
    {
        var original = NativeLanguage.Preference;
        try
        {
            var cases = new Dictionary<string, string>
            {
                ["zh-CN"] = "zh-CN", ["zh-SG"] = "zh-CN", ["zh-Hans"] = "zh-CN", ["zh-Hans-HK"] = "zh-CN",
                ["zh-TW"] = "zh-TW", ["zh-HK"] = "zh-TW", ["zh-MO"] = "zh-TW", ["zh-Hant"] = "zh-TW",
                ["zh-Hant-CN"] = "zh-TW", ["ja-JP"] = "ja", ["en-GB"] = "en", ["ko-KR"] = "ko",
                ["fr-FR"] = "en", ["zh_HK"] = "zh-TW"
            };
            foreach (var entry in cases) Check(NativeLanguage.Resolve(entry.Key) == entry.Value, "系统语言映射错误：" + entry.Key);
            var catalog = JsonNode.Parse(Program.ReadAsset("locales.json"))!.AsObject();
            foreach (var entry in catalog)
            {
                var variants = entry.Value!.AsArray();
                Check(variants.Count == 4, "缺少翻译：" + entry.Key);
                var expected = Placeholders(entry.Key);
                foreach (var variant in variants)
                {
                    var translated = variant!.GetValue<string>();
                    Check(!string.IsNullOrWhiteSpace(translated) && Placeholders(translated) == expected, "翻译占位符错误：" + entry.Key);
                }
            }
            foreach (var entry in new Dictionary<string, string> { ["zh-CN"] = "设置", ["zh-TW"] = "設定", ["ja"] = "設定", ["en"] = "Settings", ["ko"] = "설정" })
            {
                NativeLanguage.Preference = entry.Key;
                Check(NativeLanguage.Text("设置") == entry.Value, "原生窗口语言错误：" + entry.Key);
            }
            NativeLanguage.Preference = "en";
            Check(NativeLanguage.Message("第 2 页：BOOTH 页面加载超时，请检查网络后重试。") == "Page 2: BOOTH page timed out. Check your connection and retry.", "嵌套提示未翻译");
            Check(NativeLanguage.Message("Materials.zip") == "Materials.zip", "原始文件名发生变化");
            File.WriteAllText(Path.Combine(root, "settings.json"), """{"language":"ja","currency":"JPY","pinnedTags":["衣装"]}""");
            NativeLanguage.Initialize(root);
            Check(NativeLanguage.Preference == "ja" && NativeLanguage.Text("设置") == "設定", "重启后语言选择未恢复");
            return new JsonObject { ["systemLanguageMapping"] = true, ["fiveLanguages"] = true, ["catalogEntries"] = catalog.Count,
                ["translationPlaceholders"] = true, ["nestedMessages"] = true, ["preferenceRestored"] = true };
        }
        finally { NativeLanguage.Preference = original; }
    }

    private static string Placeholders(string value) => string.Join(",", Regex.Matches(value, @"\{\w+\}").Select(match => match.Value).Order());
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
