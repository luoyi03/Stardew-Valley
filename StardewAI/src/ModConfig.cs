using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace StardewAI
{
    public class ModConfig
    {
        // ---------------- 按键 ----------------

        /// <summary>打开「AI 问答」面板的按键。</summary>
        public string AskKey { get; set; } = "F5";

        /// <summary>与最近的 NPC 开启自由对话的按键。</summary>
        public string NpcChatKey { get; set; } = "F4";

        /// <summary>NPC 对话触发方式：Hotkey = 仅按键触发；Always = 和 NPC 说话时自动接管原版对话。</summary>
        public string NpcMode { get; set; } = "Hotkey";

        /// <summary>Always 模式下不接管的 NPC（显示名，逗号分隔），例如不想被接管的商店 NPC。</summary>
        public string NpcBlacklist { get; set; } = "";

        // ---------------- 模型接口 ----------------

        /// <summary>当前使用的模型方案 id（见 Presets：moyu / zhipu / siliconflow / ollama / custom …）。</summary>
        public string Preset { get; set; } = "custom";

        /// <summary>每个方案各自保存的密钥，切换方案时自动带出来。</summary>
        public Dictionary<string, string> PresetKeys { get; set; } = new();

        /// <summary>OpenAI 兼容接口地址，结尾不要带斜杠。</summary>
        public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";

        /// <summary>API Key。留空则不发送鉴权头（适合本地 Ollama）。</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>模型名称。</summary>
        public string Model { get; set; } = "deepseek-chat";

        /// <summary>采样温度，越小越严谨。</summary>
        public float Temperature { get; set; } = 0.7f;

        /// <summary>单次回答最大 token 数。推理型模型（如 LongCat）思考也要占额度，建议 ≥2000。</summary>
        public int MaxTokens { get; set; } = 2500;

        /// <summary>超时秒数（流式回答是边听边算的整体上限）。</summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>是否流式输出（回答逐个字蹦出来）。关闭则一次性返回。</summary>
        public bool Stream { get; set; } = true;

        /// <summary>发给模型的上下文保留多少「一问一答」。</summary>
        public int MaxHistory { get; set; } = 10;

        // ---------------- 内容增强 ----------------

        /// <summary>是否把当前存档状态（季节/日期/天气/技能/背包等）一起发给模型。</summary>
        public bool IncludeGameState { get; set; } = true;

        /// <summary>是否检索本地教程（InGameGuide 的 guide.json）作为回答依据。</summary>
        public bool UseLocalTutorial { get; set; } = true;

        /// <summary>单次检索注入教程的最大字数。</summary>
        public int MaxTutorialChars { get; set; } = 2500;

        /// <summary>额外追加到系统提示里的内容（比如「回答一律用粤语」）。</summary>
        public string ExtraSystemPrompt { get; set; } = "";

        /// <summary>打开 NPC 对话时，让 NPC 主动说一句招呼话（会消耗一次请求）。</summary>
        public bool NpcAutoGreet { get; set; } = true;

        /// <summary>是否把每个 NPC 的聊天记录存到文件，下次进游戏他还记得你们聊过什么。</summary>
        public bool NpcMemoryEnabled { get; set; } = true;

        /// <summary>每个 NPC 最多记住多少条对话。</summary>
        public int NpcMemoryMessages { get; set; } = 20;

        /// <summary>每聊一轮给 NPC 加多少好感度（0 = 不加，不影响原版数值）。上限见下面一项。</summary>
        public int ChatFriendshipPoints { get; set; } = 0;

        /// <summary>同一个 NPC 每天靠聊天最多能涨多少好感度。</summary>
        public int ChatFriendshipDailyCap { get; set; } = 40;

        /// <summary>允许 AI 直接给玩家物品（回答下方出现「获取」按钮，点了才真的进背包）。</summary>
        public bool AiCanGiveItems { get; set; } = true;

        /// <summary>AI 回答是否避免 Markdown 符号，方便小屏幕阅读。</summary>
        public bool NoMarkdown { get; set; } = true;

        /// <summary>问答面板下方的一键提问按钮。</summary>
        public List<string> QuickPrompts { get; set; } = new()
        {
            "春天种什么最赚钱",
            "矿洞怎么快速下到100层",
            "怎么求婚结婚",
            "送礼和好感度怎么刷最快",
            "鱼王怎么钓",
            "第一年资金怎么安排"
        };

        /// <summary>省积分模式：一键压低上下文轮数、关闭自动打招呼与教程注入、压低回答上限。</summary>
        public bool SaveMode { get; set; } = false;

        /// <summary>滚轮方向反着就来这里改成 true。</summary>
        public bool InvertScroll { get; set; } = false;

        // ---------------- 辅助 ----------------

        public bool TryAskKey(out SButton key) => TryParse(AskKey, out key);
        public bool TryNpcKey(out SButton key) => TryParse(NpcChatKey, out key);

        public bool IsAlways() => string.Equals(NpcMode, "Always", StringComparison.OrdinalIgnoreCase);

        public IEnumerable<string> Blacklist()
        {
            if (string.IsNullOrWhiteSpace(NpcBlacklist))
                yield break;

            foreach (string part in NpcBlacklist.Split(',', '，'))
            {
                string s = part.Trim();
                if (s.Length > 0)
                    yield return s;
            }
        }

        private static bool TryParse(string raw, out SButton key)
        {
            key = SButton.None;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            return Enum.TryParse(raw.Trim(), true, out key) && key != SButton.None;
        }

        /// <summary>深拷贝，用于设置界面的「取消」回滚。</summary>
        public ModConfig Clone()
        {
            string json = System.Text.Json.JsonSerializer.Serialize(this);
            return System.Text.Json.JsonSerializer.Deserialize<ModConfig>(json) ?? new ModConfig();
        }
    }
}
