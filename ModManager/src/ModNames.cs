using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;

namespace ModManager
{
    /// <summary>
    /// 中英文名对照：面板里显示成「中文名（English Name）」，一眼就能认出是哪个 Mod。
    ///
    /// 中文名来源，优先级从高到低：
    ///   1. 本 Mod 目录下的 names.json（首次运行自动生成，自己改 / 加都行，改完 `modmgr names` 立刻生效）
    ///   2. 桌面版（星露谷 Mod 管理器）配置里的 ChineseNames —— 在桌面版里配过的中文名游戏里也认
    ///   3. 内置的常见 Mod 对照表
    ///
    /// 英文名：优先用 manifest 里的 Name；如果 manifest 已经被桌面版改成中文了，
    /// 就回退到桌面版记下的原名（OriginalNames）。
    /// 查不到也不会出错，最差就是只显示一边的名字。
    /// </summary>
    internal static class ModNames
    {
        private static Dictionary<string, string> _user;
        private static Dictionary<string, string> _desktopCn;
        private static Dictionary<string, string> _desktopEn;
        private static bool _loaded;
        private static string _file;

        /// <summary>names.json 的位置（Mods\ModManager\names.json）。</summary>
        public static string FilePath
            => _file ??= Path.Combine(ModEntry.Hlp?.DirectoryPath ?? ".", "names.json");

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

        /// <summary>载入对照表（reload = true 时重新读文件，控制台 `modmgr names` 用）。</summary>
        public static void Load(bool reload = false)
        {
            if (_loaded && !reload)
                return;
            _loaded = true;

            _user = ReadUserFile();
            ReadDesktopConfig(out _desktopCn, out _desktopEn);
        }

        /// <summary>「中文名（English Name）」；缺哪边就只显示另一边。</summary>
        public static string Display(LocalMod mod)
        {
            if (mod == null)
                return "";

            Load();

            string raw = mod.Name ?? "";
            bool rawIsChinese = HasChinese(raw);
            string id = mod.Id ?? "";

            string cn = null;
            if (_user != null && _user.TryGetValue(id, out string u) && !string.IsNullOrWhiteSpace(u))
                cn = u;
            if (cn == null && _desktopCn != null && _desktopCn.TryGetValue(id, out string d) && !string.IsNullOrWhiteSpace(d))
                cn = d;
            if (cn == null && !rawIsChinese && BuiltIn.TryGetValue(id, out string b))
                cn = b;
            if (cn == null && rawIsChinese)
                cn = raw;                       // manifest 被改成了中文名，那就当中文名用

            string en = null;
            if (_desktopEn != null && _desktopEn.TryGetValue(id, out string de) && !string.IsNullOrWhiteSpace(de))
                en = de;
            if (en == null && !rawIsChinese)
                en = raw;

            if (cn != null && en != null && !string.Equals(cn, en, StringComparison.Ordinal))
                return cn + "（" + en + "）";
            return cn ?? en ?? raw;
        }

        /// <summary>日志用的一句话：各来源各有几条。</summary>
        public static string Summary()
            => $"自定义 {(_user?.Count ?? 0)} 条 / 内置 {BuiltIn.Count} 条 / 桌面版 {(_desktopCn?.Count ?? 0)} 条";

        // ------------------------------------------------------------------
        private static Dictionary<string, string> ReadUserFile()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                    WriteTemplate(path);

                if (!File.Exists(path))
                    return map;

                // JsonUtil 允许 // 与 /* */ 注释、尾逗号，所以文件里可以写说明
                using var doc = JsonUtil.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return map;

                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                        map[p.Name] = p.Value.GetString();
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log("读取 names.json 失败（将只用内置对照表）：" + ex.Message, LogLevel.Warn);
            }
            return map;
        }

        /// <summary>首次运行时生成一份带说明的模板（空表，内置表已经兜底了）。</summary>
        private static void WriteTemplate(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  // Mod 中英文名对照：UniqueID → 中文名（只写中文名就行，英文名会自动用 manifest 里的原名）");
                sb.AppendLine("  // 改完存盘，在游戏里输入 modmgr names 立刻生效，不用重启。");
                sb.AppendLine("  // 例：\"Pathoschild.LookupAnything\": \"查询一切\",");
                sb.AppendLine("  // 内置已经覆盖了常见 Mod（查不到才需要在这里加），这里是空表：");
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log("生成 names.json 失败：" + ex.Message, LogLevel.Debug);
            }
        }

        /// <summary>读桌面版管理器（星露谷 Mod 管理器）的配置，取其中已配好的中英文名。</summary>
        private static void ReadDesktopConfig(out Dictionary<string, string> cn, out Dictionary<string, string> en)
        {
            cn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            en = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "StardewModManager", "config.json");
                if (!File.Exists(path))
                    return;

                using var doc = JsonUtil.Parse(File.ReadAllText(path));
                Fill(doc.RootElement, "ChineseNames", cn);
                Fill(doc.RootElement, "OriginalNames", en);
            }
            catch
            {
                // 桌面版没装 / 配置读不了，不算错
            }
        }

        private static void Fill(JsonElement root, string prop, Dictionary<string, string> map)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(prop, out var obj) || obj.ValueKind != JsonValueKind.Object)
                return;

            foreach (var p in obj.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString();
            }
        }

        private static bool HasChinese(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            foreach (char c in s)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)   // CJK 基本区
                    return true;
            }
            return false;
        }
    }
}
