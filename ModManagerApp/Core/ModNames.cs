using System;
using System.Collections.Generic;
using System.IO;

namespace SMM
{
    /// <summary>
    /// Mod 名字的显示格式：统一成「中文名（English Name）」，一眼能认出是哪个 Mod。
    ///
    /// 中文名来源，优先级从高到低：
    ///   1. 自己配过的中文名（AppConfig.ChineseNames，ModScanner 扫描时已经写进 LocalMod.Name）
    ///   2. 内置对照表（常见 Mod，见下面 BuiltIn）
    /// 英文名：manifest 里的原名 —— 改过中文名时存在 LocalMod.OriginalName / AppConfig.OriginalNames。
    ///
    /// 注意：这里只负责「显示」。要把中文名写进 manifest.json 的是 ModNamer（改名对话框），
    /// 千万不能把「中文（English）」这种组合写进文件。
    /// 内置表与游戏内 ModManager 的 src/ModNames.cs 保持同步（两边显示一致）。
    /// </summary>
    internal static class ModNames
    {
        /// <summary>内置对照表（UniqueID → 中文名）。只当兜底用，认不出来就显示英文原名。</summary>
        private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
        {
            // 框架类
            ["Pathoschild.ContentPatcher"] = "内容补丁",
            ["Pathoschild.SpaceCore"] = "太空核心",
            ["Pathoschild.DynamicGameAssets"] = "动态游戏资源",
            ["spacechase0.GenericModConfigMenu"] = "通用 Mod 配置菜单",
            ["focustense.StardewUI"] = "StardewUI 界面框架",
            ["SMAPI.ConsoleCommands"] = "控制台命令",
            ["SMAPI.SaveBackup"] = "存档备份",
            ["SMAPI"] = "SMAPI 本体",

            // 常用功能类
            ["Pathoschild.ChestsAnywhere"] = "随处开箱",
            ["Pathoschild.LookupAnything"] = "查询一切",
            ["Pathoschild.Automate"] = "自动化",
            ["Pathoschild.TractorMod"] = "拖拉机",
            ["Pathoschild.TrainStation"] = "传送站",
            ["Pathoschild.Predictor"] = "预测器",
            ["Esca.FarmTypeManager"] = "农场类型管理器",
            ["Pathoschild.FarmTypeManager"] = "农场类型管理器",
            ["Bouhm.NPCMapLocations"] = "NPC 地图位置",
            ["Omegasis.SaveAnywhere"] = "随处存档",
            ["ZeroXPatch.CraftAnywhere"] = "随处制作",
            ["ReMapping.Vlgor"] = "按键重映射",
            ["Annosz.UiInfoSuite2"] = "界面信息套件 2",
            ["chiccen.MapTeleport"] = "地图传送",
            ["ccr.TeleportNPCLocation"] = "NPC 位置传送",
            ["ChibiKyu.FishingAssistant2"] = "钓鱼助手",
            ["Borealis.MatrixFishingUI"] = "钓鱼界面",
            ["shekurika.WaterFish"] = "可见鱼类",
            ["Digus.AnimalHusbandryMod"] = "畜牧扩展",
            ["Digus.PFMAutomate"] = "PFM 自动化",

            // 大型内容包
            ["FlashShifter.StardewValleyExpandedCP"] = "星露谷扩展",
            ["FlashShifter.SVECode"] = "星露谷扩展 · 核心",
            ["FlashShifter.SVE-FTM"] = "星露谷扩展 · 农场管理器",
            ["FlashShifter.SVE"] = "星露谷扩展",

            // 本仓库自己的 Mod
            ["ly.InGameGuide"] = "游戏内教程",
            ["ly.InGameCodex"] = "游戏内图鉴",
            ["ly.StardewAI"] = "游戏内 AI 助手",
            ["ly.ModManager"] = "Mod 管理器（本 Mod）"
        };

        /// <summary>「中文名（English Name）」；缺哪边就只显示另一边。</summary>
        public static string Display(LocalMod mod)
            => mod == null ? "" : Display(mod.Id, mod.Name, mod.OriginalName);

        /// <summary>按 Id / 现在的名字 / 原名拼显示名（备份记录这类只有名字的地方用）。</summary>
        public static string Display(string id, string name, string originalName)
        {
            string raw = name ?? "";
            if (raw.Length == 0 && !string.IsNullOrWhiteSpace(id))
                raw = id;

            string cn = Chinese(id, raw);
            string en = English(id, raw, originalName);

            if (!string.IsNullOrEmpty(cn) && !string.IsNullOrEmpty(en) && !string.Equals(cn, en, StringComparison.Ordinal))
                return cn + "（" + en + "）";
            return !string.IsNullOrEmpty(cn) ? cn : (en ?? raw);
        }

        /// <summary>中文名候选（改中文名对话框拿它预填）：配过的 → 内置表 → 名字本身（如果已经是中文）。</summary>
        public static string Chinese(LocalMod mod)
            => mod == null ? null : Chinese(mod.Id, mod.Name);

        private static string Chinese(string id, string name)
        {
            if (HasChinese(name))
                return name;                      // manifest / 配置里已经是中文名
            if (!string.IsNullOrWhiteSpace(id) && BuiltIn.TryGetValue(id, out string cn))
                return cn;
            return null;
        }

        /// <summary>英文名：改名前的原名，或者本来就是英文的 manifest 名。</summary>
        private static string English(string id, string name, string originalName)
        {
            if (!string.IsNullOrWhiteSpace(originalName) && !HasChinese(originalName))
                return originalName;
            if (!HasChinese(name))
                return name;
            return null;
        }

        /// <summary>有没有中文（用来判断 manifest 里的名字是中文还是英文）。</summary>
        public static bool HasChinese(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            foreach (char c in s)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)     // CJK 基本区
                    return true;
            }
            return false;
        }
    }
}
