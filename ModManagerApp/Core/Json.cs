using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SMM
{
    /// <summary>SMAPI 的清单解析允许尾逗号 / 注释，System.Text.Json 不允许，这里做兼容。</summary>
    internal static class JsonUtil
    {
        private static readonly Regex TrailingComma = new(@",\s*([}\]])", RegexOptions.Compiled);
        private static readonly Regex LineComment = new(@"^\s*//.*$", RegexOptions.Compiled | RegexOptions.Multiline);

        public static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            text = LineComment.Replace(text, "");
            return TrailingComma.Replace(text, "$1");
        }

        public static JsonDocument Parse(string text) => JsonDocument.Parse(Clean(text));
    }

    internal static class JsonEx
    {
        public static string Str(this JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static string StrOrNum(this JsonElement e, string name)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
                return null;
            if (v.ValueKind == JsonValueKind.String)
                return v.GetString();
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetRawText();
            return null;
        }

        public static bool Flag(this JsonElement e, string name)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        public static List<string> StrArray(this JsonElement e, string name)
        {
            var list = new List<string>();
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString());
            return list;
        }
    }
}
