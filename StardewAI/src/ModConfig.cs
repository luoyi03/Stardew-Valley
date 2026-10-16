using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace StardewAI
{
    public class ModConfig
    {
        // ---------------- 按键 ----------------

        /// <summary>
        /// 打开「AI 问答」面板的按键。默认 "None" = 禁用：
        /// 游戏里的问答面板统一由 InGameCodex 的 F5 提供（它那个 AI 页就是从本 Mod 搬过去的），
        /// 两边都占 F5 会一次弹出两个面板。想让本 Mod 的问答面板也能用，改成 "F5" 或其它键即可
        /// （游戏内设置面板里点一下再按新键也能改，配置里的接口/Key 仍然共用）。
        /// </summary>
        public string AskKey { get; set; } = "None";

        /// <summary>打开「她和我的单独对话」面板的按键（她说的话、你打的字、你语音转的文字都在这里）。</summary>
        public string CompanionKey { get; set; } = "F7";

        /// <summary>
        /// 进存档后自动打开伴侣对话面板。默认【关】—— 面板会打断你正在做的事；
        /// 按住说话 / 她主动搭话都不需要面板（回答会念出来 + 屏幕下方字幕）。
        /// </summary>
        public bool CompanionAutoOpen { get; set; } = false;

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

        // ---------------- 语音：她说话 + 听你说话 ----------------

        /// <summary>语音总开关。关着时不会朗读回答，也不会响应「按住说话」。</summary>
        public bool VoiceEnabled { get; set; } = false;

        /// <summary>「按住说话」的按键：按住录音，松开就转写成文字发给她。默认 V。</summary>
        public string PttKey { get; set; } = "V";

        /// <summary>
        /// 聊天输入框开着时也响应「按住说话」。
        /// 注意：开着的话，这个键就被吃掉了，聊天框里打不出这个字母。
        /// </summary>
        public bool PttInChat { get; set; } = false;

        /// <summary>是否把回答念出来。</summary>
        public bool SpeakReplies { get; set; } = true;

        /// <summary>开语音时顺便要求模型用口语短句回答（更适合朗读）。</summary>
        public bool VoiceShortReplies { get; set; } = true;

        /// <summary>一次回答最多念多少字（0 = 不限）。攻略类长回答建议留 300~400。</summary>
        public int SpeakMaxChars { get; set; } = 400;

        /// <summary>
        /// 语音合成后端：openai = 走 /audio/speech 接口；sapi = Windows 自带语音（离线免安装）；
        /// edge = 调本机 edge-tts；none = 关闭。
        /// </summary>
        public string TtsBackend { get; set; } = "openai";

        /// <summary>语音合成接口地址。留空则用主接口 BaseUrl。</summary>
        public string TtsBaseUrl { get; set; } = "";

        /// <summary>语音合成密钥。留空则用主接口 ApiKey。</summary>
        public string TtsApiKey { get; set; } = "";

        /// <summary>语音模型，如 tts-1、gpt-4o-mini-tts、cosyvoice-v1 等。</summary>
        public string TtsModel { get; set; } = "tts-1";

        /// <summary>音色，如 nova / alloy / shimmer（各家中转站的取值不一定一样）。</summary>
        public string TtsVoice { get; set; } = "nova";

        /// <summary>接口没开 /audio/speech 时自动退回 Windows 自带语音（离线免安装）。</summary>
        public bool TtsFallbackSapi { get; set; } = true;

        /// <summary>把 mp3 转成 wav 再播（mp3 只能走 MCI 的 MPEGVideo，在游戏进程里常常起不来）。</summary>
        public bool TtsConvertToWav { get; set; } = true;

        /// <summary>Windows 自带语音（sapi 后端）的音色，如 Microsoft Huihui Desktop；留空用系统默认。</summary>
        public string SapiVoice { get; set; } = "Microsoft Huihui Desktop";

        /// <summary>返回格式：mp3（默认，体积小）或 wav（个别机器上播放更稳）。</summary>
        public string TtsFormat { get; set; } = "mp3";

        /// <summary>Fish Audio 的接口地址（fish 后端专用，和 openai 后端的 TtsBaseUrl 互不干扰）。</summary>
        public string FishBaseUrl { get; set; } = "https://api.fish.audio/v1";

        /// <summary>Fish Audio 的 API Key（fish 后端专用）。</summary>
        public string FishApiKey { get; set; } = "";

        /// <summary>Fish Audio 用的模型：s2.1-pro-free（免费）/ s2.1-pro / s2-pro。</summary>
        public string FishModel { get; set; } = "s2.1-pro-free";

        /// <summary>Fish 延迟档：balanced（推荐）/ low（最快）/ normal（质量最好）。</summary>
        public string FishLatency { get; set; } = "balanced";

        /// <summary>Fish 音色克隆的参考音频 id（留空 = 用官方默认音色）。</summary>
        public string FishReferenceId { get; set; } = "";

        /// <summary>语速，1 是正常。</summary>
        public float TtsSpeed { get; set; } = 1f;

        /// <summary>音量 0~100（游戏音量不会影响它，因为走的是系统音频）。</summary>
        public int TtsVolume { get; set; } = 100;

        /// <summary>edge-tts 后端用的命令：装了 pip 包填 edge-tts，或填 python 的完整路径。</summary>
        public string EdgeTtsCommand { get; set; } = "edge-tts";

        /// <summary>用常驻的 Python 助手合成（快很多：省掉每句两次进程冷启动）。</summary>
        public bool EdgeUseWorker { get; set; } = true;

        /// <summary>edge-tts 音色，中文女声 zh-CN-XiaoxiaoNeural、zh-CN-XiaoyiNeural 等。</summary>
        public string EdgeTtsVoice { get; set; } = "zh-CN-XiaoxiaoNeural";

        /// <summary>edge-tts 语速，如 +0% / +15% / -10%。</summary>
        public string EdgeTtsRate { get; set; } = "+0%";

        /// <summary>是否允许「按住说话」。</summary>
        public bool SttEnabled { get; set; } = true;

        /// <summary>
        /// 语音识别后端：auto = 先用接口，接口没开就退回 Windows 自带离线识别；
        /// api = 只用云端接口；sapi = 只用 Windows 自带离线识别；none = 关闭。
        /// </summary>
        public string SttBackend { get; set; } = "auto";

        /// <summary>语音识别接口地址。留空则用主接口 BaseUrl。</summary>
        public string SttBaseUrl { get; set; } = "";

        /// <summary>语音识别密钥。留空则用主接口 ApiKey。</summary>
        public string SttApiKey { get; set; } = "";

        /// <summary>识别模型，如 whisper-1、FunAudioLLM/SenseVoiceSmall、gpt-4o-transcribe。</summary>
        public string SttModel { get; set; } = "whisper-1";

        /// <summary>识别语言，中文填 zh；留空让模型自己判断。</summary>
        public string SttLanguage { get; set; } = "zh";

        /// <summary>录音采样率。</summary>
        public int SttSampleRate { get; set; } = 16000;

        /// <summary>短于这个秒数就当成误按，直接丢掉。</summary>
        public float SttMinSeconds { get; set; } = 0.6f;

        /// <summary>一次录音最长多少秒，到点自动结束并发出去。</summary>
        public int SttMaxSeconds { get; set; } = 60;

        /// <summary>语音伴侣的名字。</summary>
        public string CompanionName { get; set; } = "小星";

        /// <summary>自定义伴侣人设（留空用内置的「陪你玩的朋友」人设）。</summary>
        public string CompanionPersona { get; set; } = "";

        /// <summary>她怎么称呼你（比如"老板""老公"）。留空 = 用你的游戏名字。</summary>
        public string PlayerTitle { get; set; } = "";

        /// <summary>进游戏时她先开口跟你打招呼（每次都由她想一句新的，不是固定台词）。</summary>
        public bool CompanionGreet { get; set; } = true;

        // ---------------- 她主动看你正在干什么 ----------------

        /// <summary>她会不会主动搭话（钓到鱼、下到新矿层、技能升级、进新地点、累了、发呆时）。</summary>
        public bool ProactiveChat { get; set; } = true;

        /// <summary>主动搭话也念出来（关掉就只有文字和头顶气泡）。</summary>
        public bool ProactiveSpeak { get; set; } = true;

        /// <summary>两次主动搭话至少隔多少秒。</summary>
        public int ProactiveCooldown { get; set; } = 180;

        /// <summary>攒到事情时真的开口的概率（%），100 = 每次都开口。</summary>
        public int ProactiveChance { get; set; } = 60;

        /// <summary>玩家站着不动多少秒算发呆（0 = 不催）。</summary>
        public int ProactiveIdleSeconds { get; set; } = 150;

        /// <summary>
        /// 定时闲聊：不管有没有发生事情，隔这么多秒没聊过她就主动找你说一句。
        /// 0 = 关闭（默认，只对"发生的事"反应）。
        /// </summary>
        public int ProactiveEverySeconds { get; set; } = 0;

        /// <summary>把「玩家此刻在干什么（手里拿着什么、在矿洞第几层、体力如何）」也告诉模型。</summary>
        public bool ActivityInPrompt { get; set; } = true;

        // ---------------- 辅助 ----------------

        public bool TryAskKey(out SButton key) => TryParse(AskKey, out key);
        public bool TryNpcKey(out SButton key) => TryParse(NpcChatKey, out key);
        public bool TryCompanionKey(out SButton key) => TryParse(CompanionKey, out key);
        public bool TryPttKey(out SButton key) => TryParse(PttKey, out key);

        /// <summary>语音合成实际使用的地址 / 密钥（没单独填就用主接口的）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveTtsUrl => string.IsNullOrWhiteSpace(TtsBaseUrl) ? BaseUrl : TtsBaseUrl.Trim();

        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveTtsKey => string.IsNullOrWhiteSpace(TtsApiKey) ? ApiKey : TtsApiKey.Trim();

        /// <summary>语音识别实际使用的地址 / 密钥（没单独填就用主接口的）。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveSttUrl => string.IsNullOrWhiteSpace(SttBaseUrl) ? BaseUrl : SttBaseUrl.Trim();

        [System.Text.Json.Serialization.JsonIgnore]
        public string EffectiveSttKey => string.IsNullOrWhiteSpace(SttApiKey) ? ApiKey : SttApiKey.Trim();

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
