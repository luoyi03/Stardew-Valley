using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using InGameCodex.Ui;

namespace InGameCodex.Ai
{
    /// <summary>S9：AI 问答的运行时入口。StardewAI 原来靠 ModEntry 的静态成员，这里提供等价物。</summary>
    internal static class AiRuntime
    {
        /// <summary>AI 设置（= ModEntry.Config.Ai）。</summary>
        internal static AiConfig Config;

        internal static IMonitor Log => ModEntry.Log;
        internal static IModHelper Helper => ModEntry.Hlp;

        /// <summary>StardewAI 源码里的老名字（搬运文件仍在使用）。</summary>
        internal static IModHelper Hlp => ModEntry.Hlp;

        /// <summary>设置界面正在等用户按键时，按键先送到这里。</summary>
        internal static Action<SButton> KeyCapture;

        /// <summary>对话会话：切标签、关面板都保留，直到「清空」或退出游戏。</summary>
        internal static ChatSession AskSession;

        /// <summary>Mod 启动时调用：接管配置 + 首次迁移 + 载入教程。</summary>
        internal static void Init()
        {
            ModEntry.Config.Ai ??= new AiConfig();
            Config = ModEntry.Config.Ai;
            ImportFromStardewAi();
            ReloadTutorial();
        }

        internal static ChatSession EnsureSession()
        {
            if (AskSession == null)
            {
                AskSession = new ChatSession
                {
                    AssistantName = "AI",
                    UserName = Game1.player?.displayName ?? "玩家"
                };
            }

            return AskSession;
        }

        internal static void OpenSettings()
        {
            IClickableMenu back = Game1.activeClickableMenu;
            Game1.activeClickableMenu = new AiSettingsMenu(back);
        }

        internal static void SaveConfig()
        {
            try { Helper?.WriteConfig(ModEntry.Config); }
            catch (Exception ex) { Log?.Log("保存配置失败：" + ex.Message, LogLevel.Warn); }
        }

        /// <summary>本机网关方案（模型方案 = trio）时，从 ~/.dsh/.trio-shim.json 刷新地址与密钥。</summary>
        internal static void RefreshLocalGateway(bool verbose)
        {
            var cfg = Config;
            if (cfg == null || !string.Equals(cfg.Preset, "trio", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                var info = ShimStore.Read();
                if (info == null)
                {
                    if (verbose)
                        Log?.Log("本机网关方案：没找到 " + ShimStore.PrimaryPath + "（要先启动 DSH）", LogLevel.Warn);
                    return;
                }

                // 端口和密钥只有变了才写盘，免得每次读档都刷一次 config.json
                if (string.Equals(cfg.BaseUrl, info.BaseUrl, StringComparison.Ordinal) &&
                    string.Equals(cfg.ApiKey, info.Token, StringComparison.Ordinal))
                    return;

                cfg.BaseUrl = info.BaseUrl;
                cfg.ApiKey = info.Token;
                SaveConfig();
                Log?.Log("已连上本机 DSH 网关：" + info.BaseUrl, LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log?.Log("读取本机网关信息失败：" + ex.Message, LogLevel.Warn);
            }
        }

        /// <summary>教程 + 物品表：优先用 InGameCodex 自己的 assets，缺失时回退 InGameGuide。</summary>
        internal static void ReloadTutorial()
        {
            string own = Path.Combine(Helper.DirectoryPath, "assets", "guide.json");
            string other = Path.Combine(Helper.DirectoryPath, "..", "InGameGuide", "assets", "guide.json");
            TutorialStore.Load(Log, File.Exists(own) ? own : other);
        }

        // ---------------- 首次启动迁移（需求文档 §23.3.5 / §23.3.9） ----------------

        private static void ImportFromStardewAi()
        {
            try
            {
                string dir = Path.Combine(Helper.DirectoryPath, "..", "StardewAI");
                string file = Path.Combine(dir, "config.json");
                if (!File.Exists(file))
                    return;

                JsonNode root = JsonNode.Parse(File.ReadAllText(file));
                if (root == null)
                    return;

                if (string.IsNullOrWhiteSpace(Config.ApiKey))
                {
                    Config.Preset = Str(root, "Preset", Config.Preset);
                    Config.BaseUrl = Str(root, "BaseUrl", Config.BaseUrl);
                    Config.ApiKey = Str(root, "ApiKey", Config.ApiKey);
                    Config.Model = Str(root, "Model", Config.Model);
                    Config.Temperature = FNum(root, "Temperature", Config.Temperature);
                    Config.MaxTokens = Num(root, "MaxTokens", Config.MaxTokens);
                    Config.TimeoutSeconds = Num(root, "TimeoutSeconds", Config.TimeoutSeconds);
                    Config.Stream = Bool(root, "Stream", Config.Stream);
                    Config.MaxHistory = Num(root, "MaxHistory", Config.MaxHistory);
                    Config.IncludeGameState = Bool(root, "IncludeGameState", Config.IncludeGameState);
                    Config.UseLocalTutorial = Bool(root, "UseLocalTutorial", Config.UseLocalTutorial);
                    Config.MaxTutorialChars = Num(root, "MaxTutorialChars", Config.MaxTutorialChars);
                    Config.ExtraSystemPrompt = Str(root, "ExtraSystemPrompt", Config.ExtraSystemPrompt);
                    Config.AiCanGiveItems = Bool(root, "AiCanGiveItems", Config.AiCanGiveItems);
                    Config.NoMarkdown = Bool(root, "NoMarkdown", Config.NoMarkdown);
                    Config.SaveMode = Bool(root, "SaveMode", Config.SaveMode);
                    Config.InvertScroll = Bool(root, "InvertScroll", Config.InvertScroll);
                    CopyKeys(root);
                    SaveConfig();
                    Log?.Log("已从 StardewAI 的 config.json 复制 AI 设置（之后由 InGameCodex 独立保存）", LogLevel.Info);
                }

                string ask = Str(root, "AskKey", "");
                if (ask.Length > 0)
                {
                    string bak = Path.Combine(dir, "config.json.bak-inGameCodex");
                    if (!File.Exists(bak))
                        File.Copy(file, bak);
                    ((JsonObject)root)["AskKey"] = "";
                    File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    Log?.Log("已把 StardewAI 的 AskKey 置空并备份 config.json.bak-inGameCodex：F5 交给 InGameCodex", LogLevel.Info);
                }
            }
            catch (Exception ex)
            {
                Log?.Log("读取 StardewAI 配置失败（忽略，用默认值）：" + ex.Message, LogLevel.Warn);
            }
        }

        private static void CopyKeys(JsonNode root)
        {
            if (root["PresetKeys"] is JsonObject keys && keys.Count > 0 && Config.PresetKeys.Count == 0)
            {
                foreach (KeyValuePair<string, JsonNode> kv in keys)
                {
                    if (kv.Value != null)
                        Config.PresetKeys[kv.Key] = kv.Value.GetValue<string>();
                }
            }

            if (root["QuickPrompts"] is JsonArray arr && arr.Count > 0 && Config.QuickPrompts.Count == 0)
            {
                foreach (JsonNode n in arr)
                {
                    if (n != null)
                        Config.QuickPrompts.Add(n.GetValue<string>());
                }
            }
        }

        private static string Str(JsonNode o, string name, string fallback)
        {
            try { return o?[name]?.GetValue<string>() ?? fallback; }
            catch { return fallback; }
        }

        private static int Num(JsonNode o, string name, int fallback)
        {
            try { return o?[name]?.GetValue<int>() ?? fallback; }
            catch { return fallback; }
        }

        private static float FNum(JsonNode o, string name, float fallback)
        {
            try { return o?[name]?.GetValue<float>() ?? fallback; }
            catch { return fallback; }
        }

        private static bool Bool(JsonNode o, string name, bool fallback)
        {
            try { return o?[name]?.GetValue<bool>() ?? fallback; }
            catch { return fallback; }
        }

        /// <summary>设置界面按下「保存」前先落盘（同时刷新快捷提问等内存状态）。</summary>
        internal static void ReloadTutorialAndSave()
        {
            ReloadTutorial();
            SaveConfig();
        }
    }
}