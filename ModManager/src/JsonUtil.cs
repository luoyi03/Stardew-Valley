using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModManager
{
    /// <summary>
    /// SMAPI 的清单解析器允许注释（// 和 /* */）和尾逗号，System.Text.Json 默认不允许，这里统一放开。
    /// </summary>
    internal static class JsonUtil
    {
        public static readonly JsonDocumentOptions Options = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static JsonDocument Parse(string text) => JsonDocument.Parse(text, Options);

        public static JsonNode ParseNode(string text) => JsonNode.Parse(text, null, Options);
    }
}
