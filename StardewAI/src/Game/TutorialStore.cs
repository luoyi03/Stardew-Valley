using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;

namespace StardewAI.Game
{
    /// <summary>把本地教程（InGameGuide 的 guide.json + items.json）做成简易检索库。</summary>
    public static class TutorialStore
    {
        public sealed class Chunk
        {
            public string Title;
            public string Text;
        }

        public static List<Chunk> Chunks { get; private set; } = new();
        public static string Status { get; private set; } = "未加载本地教程";

        /// <summary>物品名（中文/英文）→ 游戏内限定 id，用于「AI 直接给物品」。</summary>
        private static readonly Dictionary<string, string> ItemIndex = new(StringComparer.OrdinalIgnoreCase);
        public static int ItemCount => ItemIndex.Count;

        /// <summary>按名字找一个物品的游戏 id。</summary>
        public static bool TryResolveItem(string name, out string itemId)
        {
            itemId = null;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            string key = Normalize(name);
            if (ItemIndex.TryGetValue(key, out itemId))
                return true;

            // 退一步：包含匹配（玩家可能只说了简称，比如「抚摸机」）
            foreach (var kv in ItemIndex)
            {
                if (kv.Key.Length >= 2 && (kv.Key.Contains(key) || key.Contains(kv.Key)))
                {
                    itemId = kv.Value;
                    return true;
                }
            }

            return false;
        }

        private static string Normalize(string s)
            => new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).Trim();

        public static bool Load(IMonitor log, string guidePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(guidePath) || !File.Exists(guidePath))
                {
                    Status = "未找到本地教程文件";
                    return false;
                }

                var chunks = new List<Chunk>();
                ItemIndex.Clear();
                using var doc = JsonDocument.Parse(File.ReadAllText(guidePath));

                if (Prop(doc.RootElement, "Chapters", out JsonElement chapters))
                {
                    foreach (var ch in chapters.EnumerateArray())
                    {
                        string title = Get(ch, "Title") ?? "";
                        string sub = Get(ch, "Sub") ?? "";

                        var sb = new StringBuilder();
                        if (sub.Length > 0)
                            sb.Append(sub).Append("\n");

                        if (Prop(ch, "Blocks", out JsonElement blocks))
                        {
                            foreach (var b in blocks.EnumerateArray())
                            {
                                string t = Get(b, "T") ?? "";
                                string piece;

                                switch (t)
                                {
                                    case "h3":
                                    case "h4":
                                        piece = "\n【" + (Get(b, "X") ?? "") + "】\n";
                                        break;
                                    case "p":
                                    case "cap":
                                        piece = (Get(b, "X") ?? "") + "\n";
                                        break;
                                    case "li":
                                        piece = "- " + (Get(b, "X") ?? "") + "\n";
                                        break;
                                    case "note":
                                        piece = (Get(b, "X") ?? "") + "\n";
                                        break;
                                    case "flow":
                                        piece = string.Join(" → ", ListProperty(b, "Xs")) + "\n";
                                        break;
                                    case "box":
                                    {
                                        string bt = Get(b, "Title") ?? "";
                                        string body = string.Join("；", ListProperty(b, "Xs"));
                                        piece = "\n" + bt + "：" + body + "\n";
                                        break;
                                    }
                                    case "tbl":
                                    {
                                        var rows = new List<string>();
                                        if (Prop(b, "Rows", out JsonElement rs))
                                        {
                                            foreach (var row in rs.EnumerateArray())
                                            {
                                                var cells = row.EnumerateArray()
                                                    .Select(c => c.GetString() ?? "")
                                                    .ToList();
                                                rows.Add(string.Join(" | ", cells));
                                            }
                                        }
                                        piece = string.Join("\n", rows) + "\n";
                                        break;
                                    }
                                    default:
                                        continue;
                                }

                                sb.Append(piece);

                                if (sb.Length >= 420)
                                {
                                    chunks.Add(new Chunk { Title = title, Text = sb.ToString() });
                                    sb.Clear();
                                }
                            }
                        }

                        if (sb.Length > 0)
                            chunks.Add(new Chunk { Title = title, Text = sb.ToString() });
                    }
                }

                // 物品代码表
                string dir = Path.GetDirectoryName(guidePath) ?? "";
                string itemsPath = Path.Combine(dir, "items.json");
                if (File.Exists(itemsPath))
                {
                    using var idoc = JsonDocument.Parse(File.ReadAllText(itemsPath));
                    if (Prop(idoc.RootElement, "Items", out JsonElement items))
                    {
                        const int perChunk = 60;
                        int batch = 0;
                        var sb = new StringBuilder();

                        foreach (var it in items.EnumerateArray())
                        {
                            if (it.ValueKind != JsonValueKind.Array || it.GetArrayLength() < 4)
                                continue;

                            string type = it[0].GetString() ?? "";
                            string zh = it[1].GetString() ?? "";
                            string en = it[2].GetString() ?? "";
                            string code = it[3].GetString() ?? "";

                            // 物体类在表里存的是裸数字，其它类型已经带 (T)/(BC) 这类前缀
                            string gameId = code.StartsWith("(", StringComparison.Ordinal) ? code : "(O)" + code;

                            string k1 = Normalize(zh);
                            if (k1.Length > 0)
                                ItemIndex[k1] = gameId;

                            string k2 = Normalize(en);
                            if (k2.Length > 0)
                                ItemIndex[k2] = gameId;

                            sb.Append(zh).Append("（").Append(en).Append("）代码：").Append(code)
                              .Append("，类别：").Append(type).Append("\n");
                            batch++;

                            if (batch >= perChunk)
                            {
                                chunks.Add(new Chunk { Title = "物品代码表", Text = sb.ToString() });
                                sb.Clear();
                                batch = 0;
                            }
                        }

                        if (sb.Length > 0)
                            chunks.Add(new Chunk { Title = "物品代码表", Text = sb.ToString() });
                    }
                }

                Chunks = chunks;
                Status = "已加载 " + chunks.Count + " 段教程 + " + ItemIndex.Count + " 个物品名";
                return true;
            }
            catch (Exception ex)
            {
                Status = "本地教程加载失败：" + ex.Message;
                log?.Log("本地教程加载失败：" + ex.Message, LogLevel.Warn);
                return false;
            }
        }

        /// <summary>按问题检索若干教程片段，拼成一整段文本。</summary>
        public static string Retrieve(string query, int maxChars)
        {
            if (Chunks.Count == 0 || string.IsNullOrWhiteSpace(query))
                return "";

            var terms = Terms(query);
            if (terms.Count == 0)
                return "";

            var scored = new List<(float Score, Chunk Chunk)>();

            foreach (var c in Chunks)
            {
                string hay = c.Title + "\n" + c.Text;
                float score = 0f;
                int hits = 0;

                foreach (string term in terms)
                {
                    int hit = CountContains(hay, term);
                    if (hit > 0)
                    {
                        hits++;
                        score += Math.Min(hit, 3);
                    }
                }

                if (hits == 0)
                    continue;

                score += hits * 2f / Math.Max(1, terms.Count);
                scored.Add((score, c));
            }

            if (scored.Count == 0)
                return "";

            scored.Sort((a, b) => b.Score.CompareTo(a.Score));

            var sb = new StringBuilder();
            int used = 0;

            foreach (var s in scored)
            {
                string block = "[" + s.Chunk.Title + "]\n" + s.Chunk.Text.Trim();
                if (block.Length > maxChars)
                    block = block.Substring(0, maxChars);
                if (used + block.Length > maxChars)
                    break;

                sb.Append(block).Append("\n\n");
                used += block.Length;

                if (sb.Length >= maxChars)
                    break;
            }

            return sb.ToString().Trim();
        }

        private static List<string> Terms(string query)
        {
            var res = new List<string>();
            string clean = new string(query.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray());

            for (int i = 0; i < clean.Length; i++)
            {
                if (i + 2 <= clean.Length)
                    res.Add(clean.Substring(i, 2));
            }

            var en = query.Split(' ', '\t', ',', '，', '。')
                .Select(s => s.Trim())
                .Where(s => s.Length > 1 && s.All(char.IsLetterOrDigit))
                .Select(s => s.ToLowerInvariant());

            res.AddRange(en);

            return res.Distinct().Take(40).ToList();
        }

        private static int CountContains(string hay, string needle)
        {
            int count = 0;
            int idx = hay.IndexOf(needle, StringComparison.Ordinal);
            while (idx >= 0)
            {
                count++;
                idx = hay.IndexOf(needle, idx + needle.Length, StringComparison.Ordinal);
                if (count > 8)
                    break;
            }
            return count;
        }

        /// <summary>大小写不敏感地取属性（guide.json 用的是小写字段名）。</summary>
        private static bool Prop(JsonElement e, string name, out JsonElement value)
        {
            if (e.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            if (e.TryGetProperty(name, out value))
                return true;

            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string Get(JsonElement e, string name)
            => Prop(e, name, out JsonElement v) ? v.GetString() : null;

        private static List<string> ListProperty(JsonElement e, string name)
        {
            var res = new List<string>();
            if (Prop(e, name, out JsonElement v) && v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                {
                    string s = item.GetString();
                    if (!string.IsNullOrEmpty(s))
                        res.Add(s);
                }
            }
            return res;
        }
    }
}
