using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SMM
{
    /// <summary>
    /// SMAPI 的清单解析允许注释（// 行注释 和 /* */ 块注释）以及尾逗号，System.Text.Json 默认不允许。
    /// 这里统一用一套宽松规则：Generic Mod Config Menu 这类用 ModManifestBuilder 生成的 manifest
    /// 开头就带一块 /* ... */，不放开的话连读都读不了。
    /// </summary>
    internal static class JsonUtil
    {
        public static readonly JsonDocumentOptions Options = new()
        {
            CommentHandling = JsonCommentHandling.Skip,   // 支持 // 和 /* */
            AllowTrailingCommas = true
        };

        public static JsonDocument Parse(string text) => JsonDocument.Parse(text, Options);

        /// <summary>需要改字段（例如改中文名）时用这个，规则与 Parse 一致。</summary>
        public static JsonNode ParseNode(string text) => JsonNode.Parse(text, null, Options);
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
