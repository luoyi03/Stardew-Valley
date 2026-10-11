using System;
using System.Collections.Generic;
using System.Text;
using StardewValley;

namespace StardewAI.Game
{
    /// <summary>把当前存档的关键状态整理成一段给模型看的文字。</summary>
    public static class GameContext
    {
        private static readonly Dictionary<string, string> SeasonZh = new()
        {
            ["spring"] = "春",
            ["summer"] = "夏",
            ["fall"] = "秋",
            ["winter"] = "冬"
        };

        public static string Build()
        {
            Farmer p = Game1.player;
            if (p == null)
                return "";

            var sb = new StringBuilder();
            sb.Append("【当前存档状态】\n");

            string season = SeasonZh.TryGetValue(Game1.currentSeason ?? "", out string zh) ? zh : Game1.currentSeason;
            sb.Append($"玩家：{p.displayName}；农场：{p.farmName}；");
            sb.Append($"时间：第 {Game1.year} 年 {season}季 {Game1.dayOfMonth} 日 {TimeLabel(Game1.timeOfDay)}；");
            sb.Append($"天气：{Weather()}；");
            sb.Append($"地点：{p.currentLocation?.Name ?? "未知"}。\n");

            sb.Append($"金币：{p.Money}；体力：{(int)p.stamina}；");
            sb.Append($"技能：耕作 {p.farmingLevel} / 钓鱼 {p.fishingLevel} / 采集 {p.foragingLevel} / 采矿 {p.miningLevel} / 战斗 {p.combatLevel}；");
            sb.Append($"矿洞最深：{p.deepestMineLevel} 层。\n");

            if (!string.IsNullOrEmpty(p.spouse))
                sb.Append($"配偶：{p.spouse}。\n");

            string bag = Bag();
            if (bag.Length > 0)
                sb.Append($"背包主要物品：{bag}\n");

            return sb.ToString();
        }

        private static string TimeLabel(int t)
        {
            int hour = t / 100;
            int min = t % 100;
            return hour + ":" + min.ToString("00");
        }

        private static string Weather()
        {
            if (Game1.isSnowing)
                return "下雪";
            if (Game1.isLightning)
                return "雷暴";
            if (Game1.isRaining)
                return "下雨";
            return "晴";
        }

        private static string Bag()
        {
            Farmer p = Game1.player;
            if (p?.Items == null)
                return "";

            var counts = new Dictionary<string, int>();

            foreach (Item item in p.Items)
            {
                if (item == null)
                    continue;

                string name = item.DisplayName ?? item.Name ?? "?";
                if (counts.TryGetValue(name, out int n))
                    counts[name] = n + item.Stack;
                else
                    counts[name] = item.Stack;
            }

            var sb = new StringBuilder();
            int shown = 0;

            foreach (var kv in counts)
            {
                if (shown >= 14)
                    break;
                if (shown > 0)
                    sb.Append("、");
                sb.Append(kv.Key).Append(" x").Append(kv.Value);
                shown++;
            }

            return sb.ToString();
        }
    }
}
