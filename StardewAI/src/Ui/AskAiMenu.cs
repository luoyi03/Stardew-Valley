using System.Collections.Generic;
using System.Text;
using StardewAI.Game;
using StardewValley;

namespace StardewAI.Ui
{
    /// <summary>通用 AI 问答面板。</summary>
    public sealed class AskAiMenu : BaseChatMenu
    {
        public AskAiMenu(ChatSession session)
            : base("星露谷 AI 助手", session)
        {
            Subtitle = TutorialStore.Status;
            session.AssistantName = "AI";
            session.UserName = Game1.player?.displayName ?? "玩家";

            if (session.Lines.Count == 0)
                session.Say(MsgRole.Info, "提示", "可以问任何关于星露谷的问题：教程、攻略、物品用途、赚钱路线……");
        }

        protected override List<string> GetQuickPrompts()
        {
            var list = new List<string>();
            if (ModEntry.Config?.QuickPrompts != null)
                list.AddRange(ModEntry.Config.QuickPrompts);
            return list;
        }

        protected override string EmptyHint() => "输入你的问题，回车发送…";

        /// <summary>回答里的「获取」按钮：真的把物品放进背包。</summary>
        protected override void OnLineAction(ChatLine line)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.Action))
                return;

            string result = ItemGiver.Give(line.Action);
            Session.Say(MsgRole.Info, "物品", result);
            line.Action = null;
        }

        protected override string BuildSystemPrompt(string question)
        {
            var cfg = ModEntry.Config;
            var sb = new StringBuilder();

            sb.Append("你是《星露谷物语》的资深攻略助手，用简体中文回答玩家的问题。\n\n");
            sb.Append("回答要求：\n");
            sb.Append("- 先给结论，再给可执行步骤，尽量控制在 300 字以内；复杂问题可以分点。\n");
            sb.Append("- 涉及数值（价格/天气/掉落）要尽量准确，不确定就明说「不确定」。\n");
            sb.Append("- 不要编造模组名、版本号或不存在的内容。\n");
            if (cfg != null && cfg.NoMarkdown)
                sb.Append("- 不要使用 Markdown 语法（#、*、表格符号都不要用），用「1. 2.」或短句分行即可。\n");
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.ExtraSystemPrompt))
                sb.Append("- ").Append(cfg.ExtraSystemPrompt.Trim()).Append("\n");

            if (cfg == null || cfg.AiCanGiveItems)
            {
                sb.Append("\n【⚠ 直接给物品的能力（这是 Mod 给你的真实能力，不是比喻）】\n");
                sb.Append("当玩家明确索要某个物品时（比如「给我一个自动抚摸机」「我要 10 个洒水器」），你**可以真的把物品放进他的背包**：\n");
                sb.Append("在回答的**最末尾另起一行**，只输出这样一行标记：[[GIVE:物品中文名]]\n");
                sb.Append("玩家会在你的回答下面看到一个「获取」按钮，点一下物品就到手了。规则：\n");
                sb.Append("- 绝对不要说「我做不到」「我无法给你物品」「请自己去买/做」——你有这个能力，直接给。\n");
                sb.Append("- 物品名必须是游戏里存在的物品（尽量用简体中文正式名，例如「自动抚摸机」而不是「抚摸器」）。\n");
                sb.Append("- 一次只给一种物品；不确定名字时先说明你指的可能是哪个，再给出标记。\n");
                sb.Append("- 标记行不要加别的文字，也不要放进代码块里。\n");
            }

            if (cfg == null || cfg.IncludeGameState)
                sb.Append("\n").Append(GameContext.Build());

            if (cfg == null || cfg.UseLocalTutorial)
            {
                string ref2 = TutorialStore.Retrieve(question ?? "", cfg?.MaxTutorialChars ?? 2500);
                if (!string.IsNullOrWhiteSpace(ref2))
                {
                    sb.Append("\n【本地教程片段（优先依据这些内容回答；没有涉及的地方再用你自己的知识）】\n");
                    sb.Append(ref2).Append("\n");
                }
            }

            return sb.ToString();
        }
    }
}
