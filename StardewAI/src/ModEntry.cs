using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewAI.Game;
using StardewAI.Ui;
using StardewAI.Voice;
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
        private static ChatSession _companionSession;

        /// <summary>进存档后延迟几拍自动打开「她和我的对话」（0 = 不打开）。</summary>
        private static int _companionAutoOpen;

        /// <summary>进存档后延迟几拍让她先开口打招呼（0 = 不打招呼）。</summary>
        private static int _companionGreetIn;

        /// <summary>正在打招呼（用来把她这次的开场白记下来，下次换着说）。</summary>
        private static bool _greetPending;

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
            helper.Events.Display.RenderedHud += (_, e) => SubtitleOverlay.Draw(e.SpriteBatch);
            helper.Events.Display.RenderedWorld += (_, e) => BubbleOverlay.Draw(e.SpriteBatch);
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += (_, _) =>
            {
                RefreshLocalGateway(false);
                ProactiveDirector.Reset();
                EdgeWorker.Warmup();
                VoiceAssistant.WarmUpTts();

                // 进存档就把她的对话功能准备好：会话、人设、记忆、朗读队列都就绪，
                // 这样你第一次按 V 说话 / 她主动搭话时不用现建。
                // 注意：这里【只加载，不打开面板】—— 面板会打断你正在做的事。
                CompanionSession();

                if (Config.VoiceEnabled)
                    Monitor.Log("她已就绪：按住 " + Config.PttKey + " 说话，或按 " + Config.CompanionKey +
                                " 打开对话面板（不会自动弹出）。", LogLevel.Info);

                // 只有玩家显式打开「进存档就自动打开伴侣对话」时才弹（默认关）
                _companionAutoOpen = Config.CompanionAutoOpen ? 90 : 0;

                // 让她先开口（约 6 秒后，避开开场淡入；每次都由她想一句新的）
                _companionGreetIn = Config.CompanionGreet ? 180 : 0;
            };
            helper.Events.GameLoop.DayStarted += (_, _) => _todayFriendship.Clear();
            helper.Events.GameLoop.Saving += (_, _) => SaveMemories();
            helper.Events.GameLoop.DayEnding += (_, _) => SaveMemories();
            helper.Events.GameLoop.ReturnedToTitle += (_, _) => { VoiceAssistant.Shutdown(); ProactiveDirector.Reset(); };

            VoiceAssistant.Init(helper.DirectoryPath);
            VoiceAssistant.SendTranscript = SendVoiceTranscript;
            EdgeWorker.ScriptPath = Path.Combine(helper.DirectoryPath, "tts_worker.py");
            ActivityTracker.Hook(helper);

            helper.ConsoleCommands.Add("ai", "显示 StardewAI 的用法", (_, args) => PrintUsage());
            helper.ConsoleCommands.Add("aiask", "打开 AI 问答面板，aiask <问题>", (_, args) => AskByCommand(args));
            helper.ConsoleCommands.Add("aitalk", "和 NPC 自由对话，aitalk [NPC 名字]", (_, args) => TalkByCommand(args));
            helper.ConsoleCommands.Add("ainpc", "打开 NPC 选择面板（全地图村民）", (_, _) => OpenNpcPicker());
            helper.ConsoleCommands.Add("aiclear", "清空所有 AI 对话记录", (_, _) => ClearSessions());
            helper.ConsoleCommands.Add("aikey", "临时设置 API Key，aikey <密钥>", (_, args) => SetApiKey(args));
            helper.ConsoleCommands.Add("aisettings", "打开游戏内 AI 设置界面", (_, _) => OpenSettings(null));
            helper.ConsoleCommands.Add("aivoice", "语音开关：aivoice on / off / status", (_, args) => VoiceCommand(args));
            helper.ConsoleCommands.Add("aitts", "试听语音：aitts <要念的文字>", (_, args) => TtsCommand(args));
            helper.ConsoleCommands.Add("aicompanion", "打开语音伴侣对话面板", (_, _) => OpenCompanionChat());
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

            EdgeWorker.Warmup();
            VoiceAssistant.WarmUpTts();

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
                        "  " + Config.CompanionKey + "  打开「她和我」的对话面板（打字/说话都在这）\n" +
                        "  " + Config.NpcChatKey + "  与最近的 NPC 自由对话\n" +
                        (Config.VoiceEnabled && Config.SttEnabled ? "  按住 " + Config.PttKey + "  说话（松开自动转写并发给她）\n" : "") +
                        "  aiask <问题>   在控制台提问\n" +
                        "  aitalk [名字]  在控制台发起 NPC 对话\n" +
                        "  aicompanion    打开语音伴侣面板\n" +
                        "  aivoice on|off 语音开关（听你说话 / 她说话）\n" +
                        "  aitts <文字>   试听语音合成\n" +
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

            if (Config.TryCompanionKey(out SButton companion) && e.Pressed.Contains(companion))
            {
                Hlp.Input.Suppress(companion);
                OpenCompanionChat();
                return;
            }

            if (Config.TryNpcKey(out SButton talk) && e.Pressed.Contains(talk))
            {
                Hlp.Input.Suppress(talk);
                OpenNearby();
            }

            // 按住说话：这里【不能】Suppress —— SMAPI 会把被 Suppress 的键从 IsDown() 里一起屏蔽，
            // 结果就是按住的下一帧查不到"还按着"，被当成立刻松开（提示「录得太短（0.0 秒）」）。
            // 原版游戏没有把 V 绑给任何功能，所以不 Suppress 也没有副作用。
            // 开始/停止统一交给 OnUpdateTicked 里每帧轮询按键状态；这里只顺手记一份按键事件的按下状态，
            // 万一 IsDown 在某个环境下不好使，还能拿它兜底。
            if (Config.VoiceEnabled && Config.TryPttKey(out SButton key))
            {
                if (e.Pressed.Contains(key) || e.Held.Contains(key))
                    _pttEventDown = true;

                if (e.Released.Contains(key))
                    _pttEventDown = false;
            }
        }

        private static bool _pttWasDown;
        private static bool _pttEventDown;     // 按键事件记的按下状态（给 IsDown 兜底）
        private static int _pttMissTicks;      // IsDown 连续说"没按"的帧数（防松开事件丢失后一直卡住）

        /// <summary>
        /// 按住说话：每帧轮询按键状态。按下就开始录、松开就停止并转写。
        /// 用轮询而不是事件，丢焦点/卡帧/批处理都不会漏。
        /// </summary>
        private static void UpdatePushToTalk()
        {
            var cfg = Config;

            if (cfg == null || !cfg.VoiceEnabled || !cfg.TryPttKey(out SButton ptt))
            {
                _pttWasDown = false;
                return;
            }

            // 聊天输入框开着时不吃这个键（玩家自己开了 PttInChat 就照吃）。
            // 注意：这里只拦「开始录音」，绝不能在录音中途把它掐断 ——
            // 否则面板/输入框恰好这时候拿到焦点，就会变成"刚按下就提示录得太短（0.0 秒）"。
            bool blocked = Game1.keyboardDispatcher?.Subscriber is TextBox && !cfg.PttInChat;

            bool isDown = Hlp.Input.IsDown(ptt);

            if (isDown)
            {
                _pttMissTicks = 0;
            }
            else if (_pttEventDown && ++_pttMissTicks > 12)
            {
                _pttEventDown = false;      // 松开的那个事件大概丢了，别再以为按着
            }

            bool down = isDown || _pttEventDown;

            if (down && !_pttWasDown && !blocked)
            {
                Log?.Log("按住说话：开始录音（键 " + cfg.PttKey + "，IsDown=" + isDown + "，事件=" + _pttEventDown + "）", LogLevel.Info);
                VoiceAssistant.StartRecording();
            }
            else if (!down && _pttWasDown)
            {
                Log?.Log("按住说话：松开，停止录音（已录 " + VoiceAssistant.Mic.Seconds.ToString("0.0") + " 秒）", LogLevel.Info);
                VoiceAssistant.StopRecordingAndSend();
            }

            _pttWasDown = down;
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
            string name = assistantName;

            var session = new ChatSession
            {
                AssistantName = assistantName,
                UserName = Game1.player?.displayName ?? "玩家"
            };

            // 语音朗读：新回答打断上一段；流式边出边念；结束把剩下的念完
            session.OnAnswerStarted = () =>
            {
                VoiceAssistant.OnAnswerStarted(name);
                ProactiveDirector.OnTalk();          // 对话有活动了：定时闲聊重新计时
            };
            session.OnAnswerDelta = line =>
            {
                VoiceAssistant.OnAnswerDelta(line, name);
                ProactiveDirector.OnAnswerDelta(line);   // 主动搭话时顺手弹头顶气泡
            };
            session.OnAnswerDone = line =>
            {
                VoiceAssistant.OnAnswerDone(line, name);
                ProactiveDirector.OnAnswerDone();

                // 把这次的开场白记下来，下次进游戏她就不会再说一样的了
                if (_greetPending)
                {
                    _greetPending = false;
                    GreetingStore.Add(line?.Text);
                }
            };
            session.OnAnswerCancelled = () =>
            {
                VoiceAssistant.OnAnswerCancelled();
                ProactiveDirector.OnAnswerCancelled();
                _greetPending = false;      // 这次打招呼没成，别把下一条回答当成开场白记下来
            };

            return session;
        }

        /// <summary>
        /// 读档后过几拍自动打开她的窗口。
        /// 如果那会儿正好在剧情里或别的面板开着，就往后挪一下再试。
        /// </summary>
        private static void AutoOpenCompanion()
        {
            if (_companionAutoOpen <= 0)
                return;

            _companionAutoOpen--;

            if (_companionAutoOpen != 0 || Config?.CompanionAutoOpen != true)
                return;

            if (Game1.activeClickableMenu != null || Game1.eventUp)
            {
                _companionAutoOpen = 30;
                return;
            }

            OpenCompanionChat();
        }

        /// <summary>
        /// 进游戏几秒后让她先开口打招呼。
        /// 关键是"不套模板"：把当下的时间/天气/地点/你在干什么，以及她以前说过的开场白一起给它，
        /// 明确要求她说点只有现在才成立的话、别重复以前的说法。
        /// </summary>
        private static void GreetCompanion()
        {
            if (_companionGreetIn <= 0)
                return;

            _companionGreetIn--;

            if (_companionGreetIn != 0)
                return;

            var cfg = Config;

            if (cfg == null || !cfg.CompanionGreet || Game1.player == null || Game1.eventUp)
                return;

            var session = CompanionSession();

            if (session == null || session.Busy)
            {
                _companionGreetIn = 60;      // 她正忙着，过一会儿再试
                return;
            }

            string call = PlayerCallName();
            var sb = new StringBuilder();

            sb.Append("【刚进游戏】你和").Append(call).Append("又要一起过这一天了，现在主动跟他打个招呼。\n");
            sb.Append(GameContext.Build());
            sb.Append(ActivityTracker.Describe());

            var past = GreetingStore.Recent();

            if (past.Count > 0)
            {
                sb.Append("\n你以前进游戏时说过这些话（这次别再这么说了，换个角度、换个说法）：\n");

                foreach (string s in past)
                    sb.Append("· ").Append(s).Append("\n");
            }

            sb.Append("\n要求：一到两句，口语，像熟人随口开口那种；")
              .Append("必须说点只有此刻才成立的话（时间、天气、季节、你俩在哪儿、手里拿着什么）；")
              .Append("禁止「你好呀」「我们又见面了」「新的一天开始了」这类套话，也别自我介绍、别一口气问一堆问题。");

            VoiceAssistant.MuteNextAnswer = false;
            _greetPending = true;
            session.Trigger(sb.ToString());

            Log?.Log("她开口打招呼了（会把她这次说的记下来，下次换着说）：" + call, LogLevel.Trace);
        }

        /// <summary>她怎么称呼你：配置里填了就用它，留空 = 你的游戏名字。</summary>
        internal static string PlayerCallName()
        {
            string title = Config?.PlayerTitle?.Trim();

            if (!string.IsNullOrWhiteSpace(title))
                return title;

            return Game1.player?.displayName ?? Game1.player?.Name ?? "你";
        }

        /// <summary>伴侣会话：她主动搭话或「按住说话」时用的那个（没有就建一个，不打开面板）。</summary>
        internal static ChatSession CompanionSession()
            => _companionSession ??= CreateSession(
                string.IsNullOrWhiteSpace(Config?.CompanionName) ? "伙伴" : Config.CompanionName.Trim());

        /// <summary>
        /// 伴侣面板没开着的时候，主动搭话的回答也得有人 Poll，否则流式内容和朗读不会推进。
        /// 面板开着时它自己会 Poll，这里就跳过，避免同一个 tick 里 Poll 两次。
        /// </summary>
        private static void PollCompanion()
        {
            var session = _companionSession;
            if (session == null || !session.Busy)
                return;

            if (Game1.activeClickableMenu is BaseChatMenu menu && ReferenceEquals(menu.Chat, session))
                return;

            session.PollUI();
        }

        /// <summary>
        /// 把「按住说话」识别出来的文字送进当前对话。
        /// 关键：**没有面板就直接用她的会话，不要把面板弹出来** ——
        /// 你要的是边玩边聊。回答照样会念出来 + 屏幕下方字幕，你刚说的话用头顶气泡飘一下。
        /// （想看她之前说过什么，随时按 CompanionKey 打开面板，历史都在。）
        /// </summary>
        private static bool SendVoiceTranscript(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            bool panelOpen = Game1.activeClickableMenu is BaseChatMenu;
            var session = panelOpen ? ((BaseChatMenu)Game1.activeClickableMenu).Chat : CompanionSession();

            if (session == null)
                return false;

            // 面板没开时，让你看到"我刚才说的是这句"（否则被识别成什么你完全不知道）
            if (!panelOpen)
            {
                string shown = text.Trim();
                if (shown.Length > 30)
                    shown = shown.Substring(0, 30) + "…";

                BubbleOverlay.Show("我说：" + shown, 4);
            }

            session.Ask(text);
            return true;
        }

        /// <summary>打开语音伴侣面板（不填 NPC 时，「按住说话」的默认对话对象）。</summary>
        internal static void OpenCompanionChat()
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            if (Game1.activeClickableMenu is CompanionChatMenu)
            {
                Game1.activeClickableMenu = null;
                Game1.playSound("bigDeSelect");
                return;
            }

            var session = CompanionSession();

            try { Game1.keyboardDispatcher.Subscriber = null; } catch { /* ignore */ }

            Game1.activeClickableMenu = new CompanionChatMenu(session);
            Game1.playSound("bigSelect");
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
            // 按住说话：每帧看按键状态（比按下/松开事件可靠）
            if (e.IsMultipleOf(2))
                UpdatePushToTalk();

            // 语音：推进朗读播放、收尾录音、把识别结果发出去
            // 注意：这里不能加 Context.IsWorldReady —— 主菜单里用 aitts 试听也要能出声，
            // 之前加了它，结果在主菜单试听永远是"合成好了但没播"。
            if (e.IsMultipleOf(2))
            {
                VoiceAssistant.Tick();

                if (Context.IsWorldReady)
                {
                    ActivityTracker.Tick();
                    ProactiveDirector.Tick();
                    PollCompanion();
                    AutoOpenCompanion();
                    GreetCompanion();
                }
            }

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
            _companionSession?.Clear();
            _companionSession = null;
            VoiceAssistant.Interrupt();
            Game1.addHUDMessage(new HUDMessage("StardewAI：对话历史已清空", 2));
        }

        /// <summary>控制台：aivoice on / off / status</summary>
        private void VoiceCommand(string[] args)
        {
            string arg = args != null && args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "status";

            switch (arg)
            {
                case "on":
                case "1":
                case "true":
                    Config.VoiceEnabled = true;
                    Hlp.WriteConfig(Config);
                    break;

                case "off":
                case "0":
                case "false":
                    Config.VoiceEnabled = false;
                    Hlp.WriteConfig(Config);
                    VoiceAssistant.Shutdown();
                    break;

                case "status":
                    break;

                default:
                    Monitor.Log("用法：aivoice on | off | status（也可用 aitts <文字> 试听）", LogLevel.Info);
                    return;
            }

            Monitor.Log(VoiceAssistant.Describe(), LogLevel.Info);

            if (Config.VoiceEnabled && string.Equals(Config.TtsBackend?.Trim(), "openai", StringComparison.OrdinalIgnoreCase))
                Monitor.Log("提示：语音合成为 openai 后端，用的是 " + Config.EffectiveTtsUrl + " 的 /audio/speech。" +
                            "如果你的中转站不支持，就在 config.json 里把 TtsBackend 改成 edge（需 pip install edge-tts，免费免密钥）。", LogLevel.Info);
        }

        /// <summary>控制台：aitts &lt;文字&gt;，直接念一段话验证语音链路。</summary>
        private void TtsCommand(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                Monitor.Log("用法：aitts <要念的文字>", LogLevel.Info);
                return;
            }

            VoiceAssistant.SpeakTest(string.Join(" ", args));
            Monitor.Log("已送去合成，注意听声音（" + VoiceAssistant.Describe() + "）", LogLevel.Info);
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
