using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using InGameCodex.Data;
using StardewValley;

namespace InGameCodex.Ai
{
    /// <summary>AI 回答下方的一个可点击跳转项（物品 → 图鉴详情 / NPC → 喜好页）。见需求文档 §23.3.6。</summary>
    public sealed class CodexAiLink
    {
        public bool IsNpc;
        public string Label;
        public CodexItem Item;
        public CodexNpc Npc;
    }

    /// <summary>一次提问的图鉴注入结果：给模型的文本片段 + 给界面的可点击链接。</summary>
    public sealed class CodexAiResult
    {
        public string Text = "";
        public readonly List<CodexAiLink> Links = new List<CodexAiLink>();
    }

    /// <summary>
    /// S9 §23.3.6：按提问关键词从本地图鉴索引里取片段注入系统提示。
    /// 匹配规则 = 提问文本里出现物品的显示名 / 英文名（长度 ≥ 2）或 NPC 的中文名 / 内部名；
    /// 物品取命中最长的前 8 个，NPC 取前 3 个；文本受 AiMaxCodexChars 限制。
    /// </summary>
    internal static class CodexAiData
    {
        private const int MaxItems = 8;
        private const int MaxNpcs = 3;
        private const int MaxObtain = 6;
        private const int MaxUse = 6;
        private const int MaxFav = 5;

        internal static CodexAiResult Build(string question, int maxChars)
        {
            var result = new CodexAiResult();
            if (string.IsNullOrWhiteSpace(question))
                return result;

            ItemIndex index = ModEntry.Index;
            if (index == null || !index.IsBuilt || index.Items.Count == 0)
                return result;

            string q = question.Trim();
            List<CodexItem> items = MatchItems(index, q);
            List<CodexNpc> npcs = MatchNpcs(q);
            if (items.Count == 0 && npcs.Count == 0)
                return result;

            int cap = maxChars > 0 ? maxChars : 2000;
            var sb = new StringBuilder();
            sb.Append("【图鉴数据（本工具的本地索引，优先于你的记忆）】");

            foreach (CodexItem e in items)
            {
                if (sb.Length >= cap)
                    break;
                AppendItem(sb, e, cap);
                result.Links.Add(new CodexAiLink { IsNpc = false, Label = e.NameOrId, Item = e });
            }

            foreach (CodexNpc n in npcs)
            {
                if (sb.Length >= cap)
                    break;
                AppendNpc(sb, n, cap);
                result.Links.Add(new CodexAiLink { IsNpc = true, Label = n.Name, Npc = n });
            }

            string text = sb.ToString();
            if (text.Length > cap)
                text = text.Substring(0, cap);
            result.Text = text;
            return result;
        }

        /// <summary>兼容旧调用点：只要文本。</summary>
        internal static string BuildContext(string question, int maxChars) => Build(question, maxChars).Text;

        // ---------------- 匹配 ----------------

        private static List<CodexItem> MatchItems(ItemIndex index, string q)
        {
            var hits = new List<KeyValuePair<int, CodexItem>>();
            foreach (CodexItem e in index.Items)
            {
                if (e == null)
                    continue;

                int best = 0;
                best = Math.Max(best, HitLen(q, e.Name, 2));
                best = Math.Max(best, HitLen(q, e.EnglishName, 3));
                if (best > 0)
                    hits.Add(new KeyValuePair<int, CodexItem>(best, e));
            }

            return hits
                .OrderByDescending(h => h.Key)
                .Select(h => h.Value)
                .GroupBy(e => e.Name + "|" + e.Id)
                .Select(g => g.First())
                .Take(MaxItems)
                .ToList();
        }

        private static List<CodexNpc> MatchNpcs(string q)
        {
            NpcIndex index = ModEntry.Npcs;
            if (index == null || !index.IsBuilt)
                return new List<CodexNpc>();

            var hits = new List<KeyValuePair<int, CodexNpc>>();
            foreach (CodexNpc n in index.Npcs)
            {
                if (n == null || n.Universal)
                    continue;

                int best = Math.Max(HitLen(q, n.Name, 2), HitLen(q, n.Key, 4));
                if (best > 0)
                    hits.Add(new KeyValuePair<int, CodexNpc>(best, n));
            }

            return hits
                .OrderByDescending(h => h.Key)
                .Select(h => h.Value)
                .GroupBy(n => n.Key)
                .Select(g => g.First())
                .Take(MaxNpcs)
                .ToList();
        }

        /// <summary>name 出现在 q 里时返回 name 的长度（用于「命中最长优先」），否则 0。</summary>
        private static int HitLen(string q, string name, int minLen)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < minLen)
                return 0;
            if (name.Contains("?"))
                return 0;   // 游戏里没有正式名字的物品（???）不参与匹配
            return q.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ? name.Length : 0;
        }

        // ---------------- 文本 ----------------

        private static void AppendItem(StringBuilder sb, CodexItem e, int cap)
        {
            sb.Append("\n· ").Append(e.NameOrId);
            sb.Append("（").Append(e.TypeName).Append(" · 代码 ").Append(e.Id).Append("）");
            sb.Append("售价：").Append(e.PriceText);

            if (!string.IsNullOrWhiteSpace(e.Description))
                sb.Append("。说明：").Append(e.Description);

            IReadOnlyList<SrcLine> obtain = ModEntry.Refs?.Obtain(e) ?? (IReadOnlyList<SrcLine>)Array.Empty<SrcLine>();
            if (obtain.Count > 0)
            {
                sb.Append("\n  怎么获得：");
                for (int i = 0; i < obtain.Count && i < MaxObtain; i++)
                {
                    if (i > 0)
                        sb.Append("；");
                    sb.Append(obtain[i].Text);
                }
                if (obtain.Count > MaxObtain)
                    sb.Append("；……共 ").Append(obtain.Count).Append(" 条");
            }

            IReadOnlyList<UseLine> use = ModEntry.Refs?.Use(e) ?? (IReadOnlyList<UseLine>)Array.Empty<UseLine>();
            if (use.Count > 0)
            {
                sb.Append("\n  有什么用：");
                for (int i = 0; i < use.Count && i < MaxUse; i++)
                {
                    if (i > 0)
                        sb.Append("；");
                    sb.Append(use[i].Text);
                }
                if (use.Count > MaxUse)
                    sb.Append("；……共 ").Append(use.Count).Append(" 条");
            }
        }

        private static void AppendNpc(StringBuilder sb, CodexNpc n, int cap)
        {
            sb.Append("\n· ").Append(n.Name);
            if (!string.IsNullOrWhiteSpace(n.EnglishName))
                sb.Append("（").Append(n.EnglishName).Append("）");
            if (n.HasBirthday)
                sb.Append(" 生日：").Append(n.BirthdayText).Append(BirthdaySuffix(n));
            sb.Append(n.CanMarry ? " 可结婚" : " 不可结婚");

            // 五档顺序：0 最爱 / 1 喜欢 / 2 一般 / 3 不喜欢 / 4 厌恶
            AppendTier(sb, n, 0);
            AppendTier(sb, n, 1);
            AppendTier(sb, n, 3);
            AppendTier(sb, n, 4);
        }

        private static void AppendTier(StringBuilder sb, CodexNpc n, int index)
        {
            if (n.Tiers == null || index < 0 || index >= n.Tiers.Count)
                return;

            CodexNpcTier tier = n.Tiers[index];
            if (tier == null || (tier.Items.Count == 0 && tier.Categories.Count == 0))
                return;

            sb.Append("\n  ").Append(tier.Label).Append("（").Append(tier.Count).Append(" 件）：");

            int written = 0;
            foreach (CodexItem e in tier.Items)
            {
                if (written >= MaxFav)
                    break;
                if (written > 0)
                    sb.Append("、");
                sb.Append(e.NameOrId);
                written++;
            }
            foreach (string c in tier.Categories)
            {
                if (written >= MaxFav)
                    break;
                if (written > 0)
                    sb.Append("、");
                sb.Append(c);
                written++;
            }

            if (tier.Count > written)
                sb.Append(" 等 ").Append(tier.Count).Append(" 件");
        }

        private static string BirthdaySuffix(CodexNpc n)
        {
            if (Game1.player == null || !n.HasBirthday)
                return "";
            int today = CurrentDayOfYear();
            int left = (n.DayOfYear - today + 112) % 112;
            return left == 0 ? "（今天生日）" : "（距今还有 " + left + " 天）";
        }

        private static int CurrentDayOfYear()
        {
            int season = 0;
            string s = (Game1.currentSeason ?? "").ToLowerInvariant();
            if (s.StartsWith("summer"))
                season = 1;
            else if (s.StartsWith("fall"))
                season = 2;
            else if (s.StartsWith("winter"))
                season = 3;
            int day = Math.Max(1, Game1.dayOfMonth);
            return season * 28 + (day - 1);
        }
    }
}
