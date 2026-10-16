using System;
using StardewAI.Ui;
using StardewAI.Voice;
using StardewModdingAPI;
using StardewValley;

namespace StardewAI.Game
{
    /// <summary>
    /// 让她主动开口：玩家钓到鱼、下到新矿层、技能升级、进新地点、体力见底、站着发呆时，
    /// 按冷却和概率挑一件事，让伴侣用一句话主动搭话（同时弹头顶气泡，按配置还会念出来）。
    /// </summary>
    internal static class ProactiveDirector
    {
        private static DateTime _lastSay = DateTime.MinValue;
        private static bool _waiting;     // 上一次主动搭话还在等回答
        private static string _bubble = "";

        /// <summary>上一次「你俩说过话」（她主动开口，或你找她说话）——定时闲聊用它计时。</summary>
        private static DateTime _lastTalk = DateTime.UtcNow;

        /// <summary>对话有活动了（她开口 / 你找她说话）时调用，把定时闲聊的计时归零。</summary>
        public static void OnTalk() => _lastTalk = DateTime.UtcNow;

        /// <summary>上一次主动开口是什么时候（诊断用）。</summary>
        private static DateTime _waitingSince = DateTime.MinValue;

        /// <summary>"没发生什么事，单纯隔了一阵子没说话"用的那件事。</summary>
        private static GameEvent ChatEvent()
        {
            int gap = (int)(DateTime.UtcNow - _lastTalk).TotalSeconds;

            return new GameEvent
            {
                Key = "chat",
                Description = "你们已经大约 " + gap + " 秒没说过话了，你就在他旁边陪着他干活"
                              + "（他正在忙什么，系统提示里有）",
                Priority = 20
            };
        }

        /// <summary>每 tick 调用（ModEntry 里每 2 tick 一次）。</summary>
        public static void Tick()
        {
            var cfg = ModEntry.Config;

            if (cfg == null || !cfg.ProactiveChat || !Context.IsWorldReady)
                return;

            if (Game1.player == null || Game1.eventUp || Game1.isFestival())
                return;

            // 有面板开着就先别插嘴……但「她和我」的对话框开着时要说：
            // 那是她的主窗口，一直开着就永远等不到她开口的话，陪伴感就没了。
            if (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not CompanionChatMenu)
                return;

            // 上一次搭话的"回答"如果早就结束了，就自己把状态收回来。
            // 面板里的「清空」、回答被中断这些路径不会回调我们，不这么做就会永远卡在
            // "等她说完" → 聊过一次之后她再也不主动开口（表现为砍树种地时一片安静）。
            if (_waiting)
            {
                var live = ModEntry.CompanionSession();

                if ((live == null || !live.Busy) || (DateTime.UtcNow - _waitingSince).TotalSeconds > 120)
                {
                    ModEntry.Log?.Log("主动搭话：上一条回答其实早就结束了（状态自愈，" +
                                      (int)(DateTime.UtcNow - _waitingSince).TotalSeconds + " 秒前）", LogLevel.Trace);
                    _waiting = false;
                }
                else
                {
                    return;
                }
            }

            int cooldown = Math.Max(20, cfg.ProactiveCooldown);
            if ((DateTime.UtcNow - _lastSay).TotalSeconds < cooldown)
                return;

            int every = cfg.ProactiveEverySeconds;
            bool timerDue = every > 0 && (DateTime.UtcNow - _lastTalk).TotalSeconds >= every;

            var ev = ActivityTracker.TakeBest();
            bool fromTimer = false;

            if (ev == null)
            {
                // 定时闲聊：没发生任何事，也隔一段时间主动找你说一句（0 = 关闭）
                if (!timerDue)
                    return;

                ev = ChatEvent();
                fromTimer = true;

                ModEntry.Log?.Log("主动搭话：定时到点（已经 " + (int)(DateTime.UtcNow - _lastTalk).TotalSeconds + " 秒没说话了）", LogLevel.Trace);
            }
            else if (timerDue)
            {
                // 到点了，就别让队列里那些小事占掉这次开口——直接按"闲聊"说，保证节奏
                string queued = ev.Key;
                ev = ChatEvent();
                fromTimer = true;

                ModEntry.Log?.Log("主动搭话：定时到点，按闲聊说（本来攒到 " + queued + "）", LogLevel.Trace);
            }
            else
            {
                ModEntry.Log?.Log("主动搭话：攒到事情 → " + ev.Description, LogLevel.Trace);
            }

            // 定时闲聊是按时间约好的，不再掷骰子（否则"设了 5 分钟"却随机不说话，很难理解）
            if (!fromTimer)
            {
                int chance = Math.Clamp(cfg.ProactiveChance, 0, 100);
                if (chance < 100 && Random.Shared.Next(100) >= chance)
                    return;
            }

            Say(cfg, ev);
        }

        private static void Say(ModConfig cfg, GameEvent ev)
        {
            var session = ModEntry.CompanionSession();
            if (session == null || session.Busy)
                return;

            _lastSay = DateTime.UtcNow;
            _lastTalk = DateTime.UtcNow;
            _waiting = true;
            _waitingSince = DateTime.UtcNow;
            _bubble = "";

            // 不念的话就静音这一条（气泡和文字还是有的）
            VoiceAssistant.MuteNextAnswer = !cfg.ProactiveSpeak;

            string instruction =
                "【刚刚游戏里发生的事】" + ev.Description + "\n" +
                "现在主动开口跟他说句话：\n" +
                "- 就一句到两句，口语，像朋友在旁边随口冒出来的那种；\n" +
                "- 顺着这件事说（他刚钓到鱼就替他高兴、他累了就让他歇会儿……），但用你自己的口气和性格说；\n" +
                "- 别用「你好呀」「我来陪你啦」这类套话，也别重复你之前说过的句子，每次换个说法；\n" +
                "- 别喊他名字或称呼（每次都喊很生分），直接说「你」就行；\n" +
                "- 句子别太工整，可以有语气词、省略、说半句；\n" +
                "- 别问一堆问题，别说教，别用列表，别自我介绍，别像客服，别复述他刚才干了什么（他自己知道）。";

            session.Trigger(instruction);
        }

        /// <summary>回答流式增长时更新头顶气泡（只显示第一句）。</summary>
        public static void OnAnswerDelta(ChatLine line)
        {
            if (!_waiting || line == null)
                return;

            string text = (line.Text ?? "").Trim().Replace("\n", " ");
            if (text.Length == 0)
                return;

            int end = SpeechText.LastSentenceEnd(text);
            ShowBubble(end > 0 ? text.Substring(0, end) : text);
        }

        public static void OnAnswerDone()
        {
            _waiting = false;
            _bubble = "";
        }

        public static void OnAnswerCancelled()
        {
            _waiting = false;
            _bubble = "";
            VoiceAssistant.MuteNextAnswer = false;
            BubbleOverlay.Clear();
        }

        /// <summary>存读档 / 回标题时清掉状态。</summary>
        public static void Reset()
        {
            _waiting = false;
            _bubble = "";
            _lastSay = DateTime.MinValue;
            _lastTalk = DateTime.UtcNow;
            VoiceAssistant.MuteNextAnswer = false;
            BubbleOverlay.Clear();
        }

        private static void ShowBubble(string text)
        {
            if (text.Length == 0 || text == _bubble)
                return;

            if (text.Length > 34)
                text = text.Substring(0, 34) + "…";

            _bubble = text;
            BubbleOverlay.Show(text, 6);
        }
    }
}
