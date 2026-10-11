using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace InGameCodex.Ai
{
    public class AiConfig
    {
        // ---------------- 按键 ----------------

        /// <summary>打开「AI 问答」的按键（S9：F5 从 StardewAI 接管过来）。</summary>
        public string AskKey { get; set; } = "F5";




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

        /// <summary>是否检索本地教程（guide.json）作为回答依据。</summary>
        public bool UseLocalTutorial { get; set; } = true;

        /// <summary>是否用本工具自己的图鉴索引（物品用途/来源、NPC 喜好）作为回答依据（S9 新增）。</summary>
        public bool UseCodexData { get; set; } = true;

        /// <summary>单次注入图鉴数据片段的最大字数（S9 新增）。</summary>
        public int MaxCodexChars { get; set; } = 2000;

        /// <summary>单次检索注入教程的最大字数。</summary>
        public int MaxTutorialChars { get; set; } = 2500;

        /// <summary>额外追加到系统提示里的内容（比如「回答一律用粤语」）。</summary>
        public string ExtraSystemPrompt { get; set; } = "";






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



        private static bool TryParse(string raw, out SButton key)
        {
            key = SButton.None;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            return Enum.TryParse(raw.Trim(), true, out key) && key != SButton.None;
        }

        /// <summary>深拷贝，用于设置界面的「取消」回滚。</summary>
        public AiConfig Clone()
        {
            string json = System.Text.Json.JsonSerializer.Serialize(this);
            return System.Text.Json.JsonSerializer.Deserialize<AiConfig>(json) ?? new AiConfig();
        }
    }
}