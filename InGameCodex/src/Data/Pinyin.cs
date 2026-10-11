using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;

namespace InGameCodex.Data
{
    /// <summary>汉字 -> 拼音首字母 的查表（assets/pinyin.json），用于拼音首字母检索。</summary>
    public static class Pinyin
    {
        private static Dictionary<char, string> _map;
        private static readonly HashSet<char> Missing = new();

        /// <summary>表里没有的汉字（补表用，日志里打一次）。</summary>
        public static IEnumerable<char> MissingChars => Missing;

        public static void Load(string path, IMonitor log)
        {
            try
            {
                if (!File.Exists(path))
                {
                    log?.Log("没找到拼音表：" + path + "（拼音首字母检索将不可用）", LogLevel.Warn);
                    _map = new Dictionary<char, string>();
                    return;
                }

                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                var map = new Dictionary<char, string>();
                if (raw != null)
                {
                    foreach (var kv in raw)
                    {
                        if (!string.IsNullOrEmpty(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                            map[kv.Key[0]] = kv.Value;
                    }
                }

                _map = map;
                log?.Log("拼音表载入完成：" + map.Count + " 个汉字", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                log?.Log("拼音表载入失败：" + ex.Message, LogLevel.Warn);
                _map = new Dictionary<char, string>();
            }
        }

        public static bool IsReady => _map != null && _map.Count > 0;

        /// <summary>取字符串的拼音首字母（字母数字原样保留，其它字符跳过）。</summary>
        public static string ToInitials(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c >= 'a' && c <= 'z') { sb.Append(c); continue; }
                if (c >= 'A' && c <= 'Z') { sb.Append(char.ToLowerInvariant(c)); continue; }
                if (c >= '0' && c <= '9') { sb.Append(c); continue; }

                if (IsCjk(c))
                {
                    if (_map != null && _map.TryGetValue(c, out string ini) && !string.IsNullOrEmpty(ini))
                        sb.Append(ini);
                    else
                        Missing.Add(c);
                }
            }
            return sb.ToString();
        }

        private static bool IsCjk(char c) => c >= 0x4E00 && c <= 0x9FFF;
    }
}
