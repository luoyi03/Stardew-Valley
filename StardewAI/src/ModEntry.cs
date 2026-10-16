using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewAI.Game;
using StardewAI.Ui;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI
{
    public class ModEntry : Mod
    {
        internal static IModHelper Hlp;
        internal static IMonitor Log;
        internal static ModConfig Config;

        private static readonly Dictionary<string, ChatSession> NpcSessions = new();
        private static ChatSession _askSession;

        private static bool _pendingTakeover;
        private static NPC _pendingNpc;
        private static IClickableMenu _pendingBox;
        private static string _tutorialPath;

        /// <summary>设置界面正在等待玩家按下一个键时的回调。</summary>
        internal static Action<SButton> KeyCapture;

        /// <summary>今天靠聊天涨过的好感度，按 NPC 记。</summary>
        private static readonly Dictionary<string, int> _todayFriendship = new();

        internal static int TodayFriendship(string npcName)
            => npcName != null && _todayFriendship.TryGetValue(npcName, out int v) ? v : 0;

        internal static void AddTodayFriendship(string npcName, int amount)
        {
            if (string.IsNullOrEmpty(npcName))
                return;
            _todayFriendship[npcName] = TodayFriendship(npcName) + amount;
        }

        private static void SaveMemories()
        {
            foreach (var kv in NpcSessions)
                NpcMemoryStore.Set(kv.Key, kv.Value.ExportHistory());

            NpcMemoryStore.Save(Log);
        }

        private static Texture2D _pixel;
        internal static Texture2D Pixel => _pixel ??= MakePixel();

        private static Texture2D MakePixel()
        {
            var t = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            t.SetData(new[] { Color.White });
            return t;
        }

        public override void Entry(IModHelper helper)
        {
            Hlp = helper;
            Log = Monitor;
            Config = helper.ReadConfig<ModConfig>();

            helper.Events.Input.ButtonsChanged += OnButtonsChanged;
            helper.Events.Display.MenuChanged += OnMenuChanged;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += (_, _) => RefreshLocalGateway(false);
            helper.Events.GameLoop.DayStarted += (_, _) => _todayFriendship.Clear();
            helper.Events.GameLoop.Saving += (_, _) => SaveMemories();
            helper.Events.GameLoop.DayEnding += (_, _) => SaveMemories();

            helper.ConsoleCommands.Add("ai", "显示 StardewAI 的用法", (_, args) => PrintUsage());
            helper.ConsoleCommands.Add("aiask", "打开 AI 问答面板，aiask <问题>", (_, args) => AskByCommand(args));
            helper.ConsoleCommands.Add("aitalk", "和 NPC 自由对话，aitalk [NPC 名字]", (_, args) => TalkByCommand(args));
            helper.ConsoleCommands.Add("ainpc", "打开 NPC 选择面板（全地图村民）", (_, _) => OpenNpcPicker());
            helper.ConsoleCommands.Add("aiclear", "清空所有 AI 对话记录", (_, _) => ClearSessions());
            helper.ConsoleCommands.Add("aikey", "临时设置 API Key，aikey <密钥>", (_, args) => SetApiKey(args));
            helper.ConsoleCommands.Add("aisettings", "打开游戏内 AI 设置界面", (_, _) => OpenSettings(null));
            helper.ConsoleCommands.Add("aishim", "查看本机 DSH 网关（dsh-connect-trio）连接信息",
                (_, _) =>
                {
                    RefreshLocalGateway(true);
                    Monitor.Log("本机 DSH 网关：" + ShimStore.Describe(Monitor), LogLevel.Info);
                });
        }

        // ---------------- 启动 ----------------

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            Config = Hlp.ReadConfig<ModConfig>();
            MigratePreset();
            Hlp.WriteConfig(Config);

            // 用了本机网关方案时，DSH 每次启动端口和密钥都会变，进游戏先自动刷一次
            RefreshLocalGateway(true);

            NpcMemoryStore.Load(Monitor);

            string guide = Path.Combine(Hlp.DirectoryPath, "..", "InGameGuide", "assets", "guide.json");
            if (File.Exists(guide))
            {
                _tutorialPath = guide;
                TutorialStore.Load(Monitor, guide);
            }
            else
                Monitor.Log("未找到 InGameGuide 的本地教程，仅使用模型自身知识回答。你可以在 Mods/StardewAI 放置 guide.json 并重载。", LogLevel.Trace);

            Monitor.Log("StardewAI 就绪。模型：" + Config.Model + "；" + TutorialStore.Status, LogLevel.Info);
        }

        /// <summary>老配置升级：按地址推断出用的是哪家接口，并把现有密钥存进「按方案分」的密钥表。</summary>
        private void MigratePreset()
        {
            var cfg = Config;
            string id = string.IsNullOrWhiteSpace(cfg.Preset) ? Preset.Detect(cfg.BaseUrl) : cfg.Preset;
            if (string.IsNullOrWhiteSpace(id))
                id = "custom";

            cfg.Preset = id;
            cfg.PresetKeys ??= new Dictionary<string, string>();

            if (id != "custom" && !cfg.PresetKeys.ContainsKey(id))
            {
                string key = (cfg.ApiKey ?? "").Trim();
                if (key.Length > 0)
                    cfg.PresetKeys[id] = key;
            }
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
                Hlp?.WriteConfig(cfg);
                Log?.Log("已连上本机 DSH 网关：" + info.BaseUrl, LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log?.Log("读取本机网关信息失败：" + ex.Message, LogLevel.Warn);
            }
        }

        private void PrintUsage()
        {
            string ask = Config.TryAskKey(out _)
                ? "  " + Config.AskKey + "  打开 AI 问答面板\n"
                : "  （AI 问答快捷键已禁用：问答面板统一用 InGameCodex 的 F5；想启用本 Mod 的就把 AskKey 改成 F5）\n";

            Monitor.Log("StardewAI 用法：\n" +
                        ask +
                        "  " + Config.NpcChatKey + "  与最近的 NPC 自由对话\n" +
                        "  aiask <问题>   在控制台提问\n" +
                        "  aitalk [名字]  在控制台发起 NPC 对话\n" +
                        "  aiclear        清空对话历史\n" +
                        "  aisettings     打开游戏内设置界面\n" +
                        "  ainpc          打开 NPC 选择面板（不用走到他身边）\n" +
                        "  aishim         查看本机 DSH 网关状态\n" +
                        "  aikey <密钥>   临时设置 API Key", LogLevel.Info);
        }

        // ---------------- 输入 ----------------

        private void OnButtonsChanged(object sender, ButtonsChangedEventArgs e)
        {
            if (KeyCapture != null)
            {
                foreach (SButton b in e.Pressed)
                {
                    Hlp.Input.Suppress(b);

                    var cb = KeyCapture;
                    KeyCapture = null;
                    cb(b);
                    return;
                }
            }

            if (!Context.IsWorldReady)
                return;

            if (Config.TryAskKey(out SButton ask) && e.Pressed.Contains(ask))
            {
                Hlp.Input.Suppress(ask);
                OpenAsk();
                return;
            }

            if (Config.TryNpcKey(out SButton talk) && e.Pressed.Contains(talk))
            {
                Hlp.Input.Suppress(talk);
                OpenNearby();
            }
        }

        internal static void OpenSettings(IClickableMenu returnMenu)
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            // 先把聊天面板的输入焦点交出来，避免键盘事件还往旧输入框里灌
            try { Game1.keyboardDispatcher.Subscriber = null; } catch { /* ignore */ }

            Game1.activeClickableMenu = new SettingsMenu(returnMenu);
            Game1.playSound("bigSelect");
        }

        internal static void ReloadTutorial()
        {
            if (!string.IsNullOrEmpty(_tutorialPath) && File.Exists(_tutorialPath))
                TutorialStore.Load(Log, _tutorialPath);
        }

        internal static void OpenAsk()
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            if (Game1.activeClickableMenu is AskAiMenu)
            {
                Game1.activeClickableMenu = null;
                Game1.playSound("bigDeSelect");
                return;
            }

            if (string.IsNullOrWhiteSpace(Config.ApiKey) && !IsLocalhost(Config.BaseUrl))
                Game1.addHUDMessage(new HUDMessage("StardewAI：还没填 ApiKey，点本面板右上角「设置」填一下", 3));

            _askSession ??= CreateSession("AI");
            Game1.activeClickableMenu = new AskAiMenu(_askSession);
            Game1.playSound("bigSelect");
        }

        internal static void OpenNearby()
        {
            NPC npc = FindNearestNpc();

            // 身边没人就直接打开选择面板，不用跑过去
            if (npc == null)
            {
                OpenNpcPicker();
                return;
            }

            OpenNpcChat(npc, null);
        }

        internal static void OpenNpcPicker()
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            try { Game1.keyboardDispatcher.Subscriber = null; } catch { /* ignore */ }

            Game1.activeClickableMenu = new NpcPickerMenu();
            Game1.playSound("bigSelect");
        }

        internal static void OpenNpcChat(NPC npc, IClickableMenu vanillaBox)
        {
            if (Game1.graphics?.GraphicsDevice == null || npc == null)
                return;

            string key = npc.Name ?? npc.displayName ?? Guid.NewGuid().ToString();
            if (!NpcSessions.TryGetValue(key, out ChatSession session))
            {
                session = CreateSession(npc.displayName ?? key);

                // 从文件里恢复上次的聊天记忆
                if (ModEntry.Config != null && ModEntry.Config.NpcMemoryEnabled)
                {
                    var memory = NpcMemoryStore.Get(key);
                    if (memory.Count > 0)
                        session.Restore(memory);
                }

                NpcSessions[key] = session;
            }

            Game1.activeClickableMenu = new NpcChatMenu(npc, session, vanillaBox);
            Game1.playSound("bigSelect");
        }

        private static ChatSession CreateSession(string assistantName)
        {
            return new ChatSession
            {
                AssistantName = assistantName,
                UserName = Game1.player?.displayName ?? "玩家"
            };
        }

        private static NPC FindNearestNpc()
        {
            var loc = Game1.currentLocation;
            Farmer who = Game1.player;
            if (loc == null || who == null)
                return null;

            NPC best = null;
            float bestD = float.MaxValue;

            foreach (NPC npc in loc.characters)
            {
                if (npc == null || !npc.IsVillager)
                    continue;

                float d = Vector2.Distance(who.Tile, npc.Tile);
                if (d <= 4.5f && d < bestD)
                {
                    bestD = d;
                    best = npc;
                }
            }

            return best;
        }

        // ---------------- NPC 说话接管 ----------------

        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            if (!Config.IsAlways())
                return;

            if (e.NewMenu is not DialogueBox box)
                return;

            var npc = Game1.currentSpeaker as NPC;
            if (npc == null || !npc.IsVillager)
                return;

            if (Game1.eventUp || Game1.farmEvent != null || Context.IsWorldReady == false)
                return;

            foreach (string blocked in Config.Blacklist())
            {
                if (string.Equals(blocked, npc.displayName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(blocked, npc.Name, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            _pendingNpc = npc;
            _pendingBox = box;
            _pendingTakeover = true;
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!_pendingTakeover)
                return;

            _pendingTakeover = false;
            NPC npc = _pendingNpc;
            IClickableMenu box = _pendingBox;
            _pendingNpc = null;
            _pendingBox = null;

            if (npc == null || Game1.activeClickableMenu is NpcChatMenu)
                return;

            OpenNpcChat(npc, box);
        }

        // ---------------- 控制台命令 ----------------

        private void AskByCommand(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                OpenAsk();
                return;
            }

            string text = string.Join(" ", args);
            _askSession ??= CreateSession("AI");

            if (!(Game1.activeClickableMenu is AskAiMenu menu))
            {
                Game1.activeClickableMenu = new AskAiMenu(_askSession);
                menu = (AskAiMenu)Game1.activeClickableMenu;
            }

            menu.SendText(text);
        }

        private void TalkByCommand(string[] args)
        {
            string want = args != null && args.Length > 0 ? string.Join(" ", args).Trim() : "";

            NPC target = null;
            if (want.Length > 0)
                target = FindNearestNpcByName(want);

            if (target == null)
                target = FindNearestNpc();

            if (target == null)
            {
                Monitor.Log("没有找到可对话的 NPC。", LogLevel.Info);
                return;
            }

            OpenNpcChat(target, null);
        }

        private static NPC FindNearestNpcByName(string want)
        {
            var loc = Game1.currentLocation;
            if (loc == null)
                return null;

            foreach (NPC npc in loc.characters)
            {
                if (npc == null || !npc.IsVillager)
                    continue;
                if (string.Equals(npc.displayName, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(npc.Name, want, StringComparison.OrdinalIgnoreCase))
                    return npc;
            }

            foreach (NPC npc in Utility.getAllCharacters())
            {
                if (npc == null || !npc.IsVillager)
                    continue;
                if (string.Equals(npc.displayName, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(npc.Name, want, StringComparison.OrdinalIgnoreCase))
                    return npc;
            }

            return null;
        }

        private static void ClearSessions()
        {
            foreach (var s in NpcSessions.Values)
                s.Clear();
            NpcSessions.Clear();
            _askSession?.Clear();
            _askSession = null;
            Game1.addHUDMessage(new HUDMessage("StardewAI：对话历史已清空", 2));
        }

        private void SetApiKey(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Monitor.Log("用法：aikey <密钥>", LogLevel.Info);
                return;
            }

            Config.ApiKey = string.Join("", args).Trim();
            Hlp.WriteConfig(Config);
            Monitor.Log("ApiKey 已写入 config.json（下次启动也生效）。", LogLevel.Info);
        }

        private static bool IsLocalhost(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            return url.Contains("localhost") || url.Contains("127.0.0.1") || url.Contains("0.0.0.0");
        }
    }
}
