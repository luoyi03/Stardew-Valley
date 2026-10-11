using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModManager
{
    /// <summary>SMAPI 的清单解析器允许尾逗号，System.Text.Json 不允许，这里做兼容。</summary>
    internal static class JsonUtil
    {
        private static readonly Regex TrailingComma = new(",\\s*([}\\]])", RegexOptions.Compiled);

        public static string Clean(string text)
            => string.IsNullOrEmpty(text) ? text : TrailingComma.Replace(text, "$1");

        public static JsonDocument Parse(string text) => JsonDocument.Parse(Clean(text));
    }
}
