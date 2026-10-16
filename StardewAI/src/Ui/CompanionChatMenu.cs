using System;
using System.Collections.Generic;
using System.Text;
using StardewAI.Game;
using StardewAI.Voice;
using StardewValley;

namespace StardewAI.Ui
{
    /// <summary>
    /// 语音伴侣面板：一个专门陪你聊天的伙伴（按 PttKey 说话时的默认对话对象）。
    /// 和 NPC 面板的区别是：她的人设是「陪着你玩的朋友」，而且回答会被念出来。
    /// </summary>
    public sealed class CompanionChatMenu : BaseChatMenu
    {
        public CompanionChatMenu(ChatSession session)
            : base(ModEntry.Config?.CompanionName ?? "伙伴", session)
        {
            var cfg = ModEntry.Config;
            string name = string.IsNullOrWhiteSpace(cfg?.CompanionName) ? "伙伴" : cfg.CompanionName.Trim();

            session.AssistantName = name;
            session.UserName = Game1.player?.displayName ?? "玩家";

            // 提示只放在标题栏下面，不再往对话里塞固定台词 —— 那样每次进游戏看着都一样
            Subtitle = cfg?.VoiceEnabled == true
                ? "打字回车发送，或点下方「说话」；面板关着时按住 " + (string.IsNullOrWhiteSpace(cfg.PttKey) ? "V" : cfg.PttKey) + " 也能说"
                : "直接打字聊天";

            if (session.Lines.Count == 0)
                Subtitle = "她说的、你打的、你说话转出来的字都在这里 · " + Subtitle;
        }

        /// <summary>说着话的时候按钮要变成「停止」，所以在绘制时现取。</summary>
        protected override string VoiceButtonLabel()
        {
            var cfg = ModEntry.Config;

            if (cfg == null || !cfg.VoiceEnabled || !cfg.SttEnabled)
                return null;

            return VoiceAssistant.Mic.Active ? "停止并发送" : "说话";
        }

        protected override void OnVoiceButton()
        {
            if (VoiceAssistant.Mic.Active)
            {
                VoiceAssistant.StopRecordingAndSend();
                return;
            }

            VoiceAssistant.StartRecording();

            if (!VoiceAssistant.Mic.Active && !string.IsNullOrWhiteSpace(VoiceAssistant.LastError))
                Session.Say(MsgRole.Error, "语音", VoiceAssistant.LastError);
        }

        /// <summary>面板里留一个去和村民聊天的入口。</summary>
        protected override string ExtraButtonLabel() => "找村民聊";

        protected override void OnExtraButton()
        {
            Game1.keyboardDispatcher.Subscriber = null;
            ModEntry.OpenNpcPicker();
        }

        protected override List<string> GetQuickPrompts()
        {
            string name = ModEntry.Config?.CompanionName ?? "她";

            return new List<string>
            {
                "你在干嘛呢",
                "看看我背包，有什么值得卖的",
                "今天适合做什么",
                "陪我聊聊天",
                "夸夸我"
            };
        }

        protected override string EmptyHint() => "和她聊点什么…";

        protected override string BuildSystemPrompt(string question)
        {
            var cfg = ModEntry.Config;
            string name = string.IsNullOrWhiteSpace(cfg?.CompanionName) ? "小星" : cfg.CompanionName.Trim();

            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(cfg?.CompanionPersona))
            {
                sb.Append(cfg.CompanionPersona.Trim()).Append("\n\n");
            }
            else
            {
                sb.Append("你是《星露谷物语》里陪着玩家一起玩的伙伴，名字叫「").Append(name).Append("」。\n");
                sb.Append("【你是个什么样的人】你不是助手、不是说明书、更不是客服，你就是一个有血有肉的姑娘：");
                sb.Append("有自己的喜好和偏好（偏爱某种作物/鱼/天气，讨厌某些东西）、有心情起伏（会开心、会累、会烦、会嘴硬）、");
                sb.Append("也有自己的生活和小心事（你今天干了什么、看见什么好玩的、心里在惦记什么）。");
                sb.Append("你会关心他，但不会每句都甜得发腻；你会损他两句，但他真难受的时候你又心疼。\n");
                sb.Append("【怎么说话】\n");
                sb.Append("- 简体中文，口语，短句为主；有时只回一两个字（「嗯」「哈哈」「真的假的」）也很正常。\n");
                sb.Append("- 有你自己的说话习惯：有口头禅、会反问、会打岔、会吐槽、偶尔害羞或犯懒。\n");
                sb.Append("- 顺着他说的接，别自说自话；他问事就答事，他闲聊就闲聊，他沉默你可以自己找话说。\n");
                sb.Append("- 记得你们之前聊过的东西，可以主动提起；同一句话、同一个梗别反复用。\n");
                sb.Append("【绝对不要】\n");
                sb.Append("- 不要说自己是 AI / 助手 / 模型，也不要说「我在这里陪着你」「有什么可以帮你的」这类客服腔。\n");
                sb.Append("- 不要长篇大论、不要列 1/2/3、不要写成攻略清单（除非他明确问游戏机制）。\n");
                sb.Append("- 不要用「你好呀」「好久不见」「今天又是新的一天」这类套话开场；每次开口换个说法。\n");
                sb.Append("- 不要每句都热情高涨地夸他，情绪要像真人一样自然、有起伏。\n");
            }

            // 称呼只是"参考"，不是每句都要喊 —— 每次都喊名字最像 AI
            string realName = Game1.player?.displayName ?? Game1.player?.Name ?? "玩家";
            string call = ModEntry.PlayerCallName();

            sb.Append("【称呼怎么用】他的名字是「").Append(realName).Append("」");

            if (!string.Equals(call, realName, StringComparison.Ordinal))
                sb.Append("，你平时也可以叫他「").Append(call).Append("」");

            sb.Append("。但**别每句都带称呼**——真人不会每次开口都喊名字，那样很生分。");
            sb.Append("绝大多数时候直接说「你」，或者干脆省略主语；");
            sb.Append("只在情绪上来的时候（撒娇、着急、夸他、损他、好久没见）才偶尔喊一声，");
            sb.Append("喊法也要换着来（名字、「喂」、「哎」、随口起的绰号都行），别老用同一个。\n");

            sb.Append("【说话要像真人，不要像 AI】\n");
            sb.Append("- 句子长短参差，允许省略、倒装、说半句、自言自语，别每句都完整又工整。\n");
            sb.Append("- 多用语气词和口头禅（诶、啊、嘛、呗、唉、哈……），该断的地方就断。\n");
            sb.Append("- 不要「我理解你的感受」「谢谢你愿意告诉我」这种共情模板，也别复述他的话（「所以你是说……」）。\n");
            sb.Append("- 不要每句都反问、不要每条都总结、不要排比、不要书面语。\n");
            sb.Append("- 情绪要跳一点、真一点：可以嫌烦、可以被逗笑、可以答非所问、可以只回一个「嗯」。\n");
            sb.Append("- 别解释自己为什么这么说，别像旁白一样描述自己的心情。\n");
            sb.Append("【口气示例】只学语气和长短，别照抄内容：\n");
            sb.Append("  他说「今天好累」→ 像人：「累就歇会儿呗，谁逼你了」／不像人：「我理解你的辛苦，请好好休息」\n");
            sb.Append("  他说「我钓到鱼了」→ 像人：「哟，总算开张了」／不像人：「恭喜你！这真是令人兴奋的收获！」\n");
            sb.Append("  你看见他刚种完地 → 像人：「行啊，动作挺快」／不像人：「我看到你刚才种下了作物，真是勤劳的一天呢」\n");
            sb.Append("  他半天没说话 → 像人：「喂，人呢」／不像人：「请问你现在还在吗？有什么我可以帮你的吗」\n");

            if (cfg == null || cfg.IncludeGameState)
                sb.Append("\n").Append(GameContext.Build());

            if (cfg == null || cfg.ActivityInPrompt)
                sb.Append(ActivityTracker.Describe());

            if (cfg == null || cfg.UseLocalTutorial)
            {
                string ref2 = TutorialStore.Retrieve(question ?? "", Math.Min(cfg?.MaxTutorialChars ?? 2500, 1200));
                if (!string.IsNullOrWhiteSpace(ref2))
                {
                    sb.Append("\n【本地教程片段（只在玩家问游戏知识时参考）】\n");
                    sb.Append(ref2).Append("\n");
                }
            }

            return sb.ToString();
        }
    }
}
