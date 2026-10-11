using System;
using System.Collections.Generic;
using StardewAI.Game;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI.Ui
{
    /// <summary>与某个 NPC 的自由对话面板。</summary>
    public sealed class NpcChatMenu : BaseChatMenu
    {
        /// <summary>快捷话题。前两个是特殊指令，不会发给模型。</summary>
        private const string SwitchTag = "__换人__";
        private const string ResetTag = "__重新开始__";

        private readonly NPC _npc;
        private IClickableMenu _vanillaBox;
        private bool _wasBusy;
        private int _awardedRounds;

        public NPC Npc => _npc;

        public NpcChatMenu(NPC npc, ChatSession session, IClickableMenu vanillaBox)
            : base("与 " + (npc?.displayName ?? "某人") + " 聊天", session)
        {
            _npc = npc;
            _vanillaBox = vanillaBox;

            string name = npc?.displayName ?? "NPC";
            session.AssistantName = name;
            session.UserName = Game1.player?.displayName ?? "玩家";

            var cfg = ModEntry.Config;
            Subtitle = Relationship(npc);

            if (session.Lines.Count == 0)
            {
                session.Say(MsgRole.Info, "提示", "自由对话模式：说什么都行，对方会用他自己的方式回答你。");
                if (cfg == null || cfg.NpcAutoGreet)
                    session.Trigger(NpcPersona.GreetInstruction(name));
            }
        }

        public void SetVanillaBox(IClickableMenu box) => _vanillaBox = box;

        private static string Relationship(NPC npc)
        {
            if (npc == null || Game1.player?.friendshipData == null)
                return "";

            if (Game1.player.friendshipData.TryGetValue(npc.Name, out var fs))
            {
                int hearts = fs.Points / 250;
                string place = npc.currentLocation?.Name ?? "";
                string rel = Game1.player.spouse == npc.Name ? "配偶"
                    : fs.IsEngaged() ? "订婚"
                    : fs.IsDating() ? "交往中"
                    : "普通朋友";
                return $"好感 {hearts}/10  |  {rel}  |  {place}";
            }

            return npc.currentLocation?.Name ?? "";
        }

        // ---------------- 快捷话题 ----------------

        protected override List<string> GetQuickPrompts()
        {
            string name = _npc?.displayName ?? "你";

            return new List<string>
            {
                SwitchTag,
                ResetTag,
                "你最喜欢什么礼物",
                "讲讲你的故事",
                "今天镇上有什么新鲜事",
                "给我一句今天的建议"
            };
        }

        protected override void SubmitPreset(string text)
        {
            if (text == SwitchTag)
            {
                Game1.keyboardDispatcher.Subscriber = null;
                ModEntry.OpenNpcPicker();
                return;
            }

            if (text == ResetTag)
            {
                Session.Clear();
                NpcMemoryStore.Clear(_npc?.Name ?? "");
                Session.Say(MsgRole.Info, "提示", "已忘掉之前和 " + (_npc?.displayName ?? "对方") + " 说过的话。");
                Game1.playSound("trashcan");
                return;
            }

            base.SubmitPreset(text);
        }

        /// <summary>快捷话题按钮上显示的文字。</summary>
        protected override string ChipLabel(string raw)
        {
            return raw switch
            {
                SwitchTag => "换个人聊",
                ResetTag => "重新开始",
                _ => raw
            };
        }

        // ---------------- 好感度联动 ----------------

        public override void update(Microsoft.Xna.Framework.GameTime time)
        {
            bool busyNow = Session.Busy;

            if (_wasBusy && !busyNow)
                AwardFriendship();

            _wasBusy = busyNow;

            base.update(time);
        }

        private void AwardFriendship()
        {
            var cfg = ModEntry.Config;
            if (cfg == null || cfg.ChatFriendshipPoints <= 0 || _npc == null)
                return;

            var player = Game1.player;
            if (player?.friendshipData == null || !player.friendshipData.ContainsKey(_npc.Name))
                return;

            int cap = Math.Max(0, cfg.ChatFriendshipDailyCap);
            int gained = ModEntry.TodayFriendship(_npc.Name);
            int add = Math.Min(cfg.ChatFriendshipPoints, cap - gained);

            if (add <= 0)
                return;

            var fs = player.friendshipData[_npc.Name];
            int max = player.spouse == _npc.Name ? 3500 : 2500;
            int real = Math.Min(add, Math.Max(0, max - fs.Points));

            if (real <= 0)
                return;

            fs.Points += real;
            ModEntry.AddTodayFriendship(_npc.Name, real);

            Session.Say(MsgRole.Info, "好感", "（你对 " + (_npc.displayName ?? "对方") + " 的好感 +" + real + "）");
        }

        // ---------------- 其它 ----------------

        protected override string BuildSystemPrompt(string question)
            => NpcPersona.Build(_npc, null);

        protected override string EmptyHint() => "对他说点什么…";

        protected override string ExtraButtonLabel() => _vanillaBox != null ? "原版对话" : null;

        protected override void OnExtraButton()
        {
            if (_vanillaBox == null)
                return;

            Game1.activeClickableMenu = _vanillaBox;
            Game1.playSound("bigSelect");
        }

        protected override void OnCleared()
        {
            NpcMemoryStore.Clear(_npc?.Name ?? "");

            if (Session.Lines.Count == 0)
                Session.Say(MsgRole.Info, "提示", "已清空与 " + (_npc?.displayName ?? "对方") + " 的聊天记录。");
        }
    }
}
