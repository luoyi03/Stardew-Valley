using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewAI.Game;
using StardewAI.Llm;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI.Ui
{
    /// <summary>游戏内可视化设置面板：文本 / 开关 / 数值 / 按键 / 枚举，可直接写回 config.json。</summary>
    public sealed class SettingsMenu : IClickableMenu
    {
        private enum Kind { Text, Bool, Num, Enum, Key }

        private sealed class Row
        {
            public string Label;
            public Kind Type;
            public Func<string> Get;
            public Action<string> Set;
            public string[] Options;
            public float Step;
            public float Min;
            public float Max;
            public TextBox Box;
            public Rectangle Ctrl;
            public bool PendingCapture;
            public string LastApplied;
            public Func<string> Suffix;
            public string[] OptionLabels;
            public Action<string> Changed;

            /// <summary>true = 点一下弹出真正的下拉清单（而不是原地循环切换）。</summary>
            public bool Dropdown;

            /// <summary>属于哪个页签（分组在 BuildRows 里用 Section(...) 划）。</summary>
            public string Group = "";
        }

        private readonly List<Row> _rows = new();

        /// <summary>当前页签要显示的行（分页后绘制/点击都只看它）。</summary>
        private readonly List<Row> _visible = new();

        private readonly List<string> _groups = new();
        private readonly List<Rectangle> _tabRects = new();
        private string _activeGroup = "";
        private string _sectionName = "";
        private int _sectionStart;
        private int _hoverTab = -1;
        private readonly IClickableMenu _returnMenu;
        private ModConfig _snapshot;

        private Rectangle _viewRect;
        private int _scroll;
        private int _maxScroll;
        private int _contentHeight;

        private Rectangle _saveRect, _cancelRect, _defaultRect, _testRect, _sampleRect;
        private int _hoverRow = -1;
        private int _hoverBtn = -1;

        private string _status = "";
        private bool _testing;
        private bool _saved = true;
        private bool _needRefresh;
        private readonly Dictionary<string, int> _beforeSaveInt = new();
        private readonly Dictionary<string, bool> _beforeSaveBool = new();
        private string _testResult;
        private CancellationTokenSource _cts;

        /// <summary>模型行（点一下弹出下拉清单选择）。</summary>
        private Row _modelRow;

        /// <summary>本机网关拉到的模型清单（没连上时用 DefaultModels）。静态：界面重开后不用重新拉。</summary>
        private static List<string> _gatewayModels;

        // ---------------- 下拉清单（点一下展开，点选项即选中） ----------------
        private const int DropItemH = 32;
        private const int DropMaxRows = 10;
        private Row _openDrop;
        private Rectangle _dropRect;
        private int _dropScroll;
        private int _dropHover = -1;

        /// <summary>还没连上网关时，模型行的默认候选。</summary>
        private static readonly string[] DefaultModels = { "hy3", "deepseek-v4.1-flash", "glm-5.3-flash" };

        /// <summary>不管网关返回什么，都不出现在候选里的模型（不想看到的写这儿）。</summary>
        private static readonly string[] HiddenModels = { "hy4-preview-f" };

        /// <summary>不管网关返回什么，这几款始终留在候选里（想固定可选哪些就改这里）。</summary>
        private static readonly string[] StandbyModels = { "hy3", "deepseek-v4.1-flash", "glm-5.3-flash" };

        private static Texture2D _textBoxTex;

        public SettingsMenu(IClickableMenu returnMenu)
            : base(0, 0, 100, 100, false)
        {
            _returnMenu = returnMenu;
            _snapshot = ModEntry.Config.Clone();
            BuildRows();
            Relayout();

            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + 12, 40, 40),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);
        }

        // ---------------- 行定义 ----------------

        private void BuildRows()
        {
            var cfg = ModEntry.Config;

            Section("接口与模型");

            _rows.Add(EnumRow("模型方案（一键切换）", Preset.Ids(), Preset.Labels(),
                () => Preset.Get(cfg.Preset) != null ? cfg.Preset : "custom",
                v => cfg.Preset = v,
                ApplyPreset));

            _rows.Add(Text("接口地址 BaseUrl", () => cfg.BaseUrl, v => cfg.BaseUrl = v.Trim()));
            _rows.Add(Text("密钥 ApiKey", () => cfg.ApiKey, v => cfg.ApiKey = v.Trim(),
                () => "（当前 " + (cfg.ApiKey ?? "").Trim().Length + " 位 · " + (Preset.Get(cfg.Preset)?.Short ?? "自定义") + "）"));
            _modelRow = EnumRow("模型 Model", ModelChoices(), ModelChoices(), () => cfg.Model, v => cfg.Model = v.Trim());
            _modelRow.Dropdown = true;   // 点一下弹出真正的下拉清单
            _rows.Add(_modelRow);
            _rows.Add(Bool("流式输出（一个字一个字蹦）", () => cfg.Stream, v => cfg.Stream = v));

            _rows.Add(Num("回答温度 Temperature", () => cfg.Temperature, v => cfg.Temperature = v, 0.1f, 0f, 2f));
            _rows.Add(Num("单次回答上限 MaxTokens", () => cfg.MaxTokens, v => cfg.MaxTokens = (int)v, 100f, 100f, 8000f));
            _rows.Add(Num("超时秒数", () => cfg.TimeoutSeconds, v => cfg.TimeoutSeconds = (int)v, 30f, 30f, 600f));
            _rows.Add(Num("上下文保留多少轮", () => cfg.MaxHistory, v => cfg.MaxHistory = (int)v, 1f, 1f, 30f));
            _rows.Add(Bool("省积分模式（一键降低开销）", () => cfg.SaveMode, ApplySaveMode));

            Section("回答与教程");

            _rows.Add(Bool("附带存档状态（季节/技能/背包）", () => cfg.IncludeGameState, v => cfg.IncludeGameState = v));
            _rows.Add(Bool("检索本地教程作答", () => cfg.UseLocalTutorial, v => cfg.UseLocalTutorial = v));
            _rows.Add(Num("教程注入字数上限", () => cfg.MaxTutorialChars, v => cfg.MaxTutorialChars = (int)v, 500f, 0f, 10000f));
            _rows.Add(Bool("允许 AI 给物品（回答下方出「获取」按钮）", () => cfg.AiCanGiveItems, v => cfg.AiCanGiveItems = v));
            _rows.Add(Bool("回答不使用 Markdown 符号", () => cfg.NoMarkdown, v => cfg.NoMarkdown = v));
            _rows.Add(Text("额外要求（如：用粤语回答）", () => cfg.ExtraSystemPrompt ?? "", v => cfg.ExtraSystemPrompt = v));

            Section("NPC 与按键");

            _rows.Add(EnumRow("NPC 触发方式", new[] { "Hotkey", "Always" }, new[] { "Hotkey（按键触发）", "Always（说话即接管）" },
                () => cfg.NpcMode ?? "Hotkey", v => cfg.NpcMode = v));
            _rows.Add(Text("NPC 黑名单（逗号分隔）", () => cfg.NpcBlacklist ?? "", v => cfg.NpcBlacklist = v));
            _rows.Add(Bool("NPC 主动打招呼", () => cfg.NpcAutoGreet, v => cfg.NpcAutoGreet = v));
            _rows.Add(Key("打开问答面板的按键", () => cfg.AskKey, v => cfg.AskKey = v));
            _rows.Add(Key("打开伴侣对话的按键（默认 F7）", () => cfg.CompanionKey, v => cfg.CompanionKey = v));
            _rows.Add(Bool("进存档就自动打开伴侣对话（默认关，不影响边玩边聊）", () => cfg.CompanionAutoOpen, v => cfg.CompanionAutoOpen = v));
            _rows.Add(Key("与 NPC 聊天的按键（默认 F4）", () => cfg.NpcChatKey, v => cfg.NpcChatKey = v));
            _rows.Add(Bool("滚轮方向反转", () => cfg.InvertScroll, v => cfg.InvertScroll = v));
            _rows.Add(Text("快捷提问（用 | 分隔）", () => string.Join(" | ", cfg.QuickPrompts ?? new List<string>()), v => cfg.QuickPrompts = SplitList(v)));

            // ---- 语音：她说话 + 听你说话（改完点「保存」写回 config.json） ----
            Section("语音 · 她说话");

            _rows.Add(Bool("语音总开关（她说话 / 听你说话）", () => cfg.VoiceEnabled, v => cfg.VoiceEnabled = v));
            _rows.Add(Key("按住说话的按键（松开自动转写）", () => cfg.PttKey, v => cfg.PttKey = v));
            _rows.Add(Bool("聊天框打开时也能按住说话（会吃掉这个字母）", () => cfg.PttInChat, v => cfg.PttInChat = v));
            _rows.Add(Bool("把回答念出来", () => cfg.SpeakReplies, v => cfg.SpeakReplies = v));
            _rows.Add(Bool("语音时要求模型用口语短句回答", () => cfg.VoiceShortReplies, v => cfg.VoiceShortReplies = v));
            _rows.Add(Num("一次最多念多少字（0 = 不限）", () => cfg.SpeakMaxChars, v => cfg.SpeakMaxChars = (int)v, 50f, 0f, 2000f));

            _rows.Add(EnumRow("语音合成后端", new[] { "openai", "fish", "sapi", "edge", "none" },
                new[] { "openai（魔芋等，走 /audio/speech）", "fish（api.fish.audio，有免费模型）", "sapi（Windows 自带语音，离线）", "edge（本机 edge-tts）", "none（关闭）" },
                () => NormalizeTtsBackend(cfg.TtsBackend),
                v => cfg.TtsBackend = v));
            _rows.Add(Text("Fish 地址", () => cfg.FishBaseUrl ?? "", v => cfg.FishBaseUrl = v.Trim()));
            _rows.Add(Text("Fish 密钥 API Key", () => cfg.FishApiKey ?? "", v => cfg.FishApiKey = v.Trim(),
                () => "（当前 " + (cfg.FishApiKey ?? "").Trim().Length + " 位）"));
            _rows.Add(Text("Fish 模型（s2.1-pro-free 免费）", () => cfg.FishModel ?? "", v => cfg.FishModel = v.Trim()));
            _rows.Add(EnumRow("Fish 延迟档", new[] { "balanced", "low", "normal" },
                new[] { "balanced（推荐）", "low（最快，音质略降）", "normal（质量最好，最慢）" },
                () => string.IsNullOrWhiteSpace(cfg.FishLatency) ? "balanced" : cfg.FishLatency.Trim(),
                v => cfg.FishLatency = v));
            _rows.Add(Text("Fish 克隆音色 id（留空=官方默认音色）", () => cfg.FishReferenceId ?? "", v => cfg.FishReferenceId = v.Trim()));
            _rows.Add(Bool("接口不支持时自动退回 Windows 自带语音", () => cfg.TtsFallbackSapi, v => cfg.TtsFallbackSapi = v));
            _rows.Add(Bool("mp3 自动转 wav 再播（游戏内播放更稳）", () => cfg.TtsConvertToWav, v => cfg.TtsConvertToWav = v));
            _rows.Add(Text("Windows 语音音色（留空用系统默认）", () => cfg.SapiVoice ?? "", v => cfg.SapiVoice = v.Trim(),
                () => "（中文女声一般是 Microsoft Huihui Desktop）"));
            _rows.Add(Text("语音合成地址（留空用主接口）", () => cfg.TtsBaseUrl ?? "", v => cfg.TtsBaseUrl = v.Trim()));
            _rows.Add(Text("语音合成密钥（留空用主密钥）", () => cfg.TtsApiKey ?? "", v => cfg.TtsApiKey = v.Trim(),
                () => "（当前 " + (cfg.TtsApiKey ?? "").Trim().Length + " 位）"));
            _rows.Add(Text("语音模型 TtsModel", () => cfg.TtsModel ?? "", v => cfg.TtsModel = v.Trim()));

            // 音色：常用音色做成下拉直接挑；当前值永远排第一个，
            // 所以以前手打进去的（自定义）音色名不会被这个下拉弄丢。
            string[] voiceIds =
            {
                "female-tianmei", "female-shaonv", "female-yujie", "female-chengshu",
                "Chinese (Mandarin)_Warm_Girl", "zh-CN-XiaoxiaoNeural",
                "nova", "alloy", "male-qn-qingse", "male-qn-jingying"
            };

            string[] voiceLabels =
            {
                "甜美（女）· female-tianmei", "少女（女）· female-shaonv", "御姐（女）· female-yujie", "成熟（女）· female-chengshu",
                "温暖（女）· Warm_Girl", "晓晓（edge 那个）· zh-CN-XiaoxiaoNeural",
                "通用 · nova", "通用 · alloy", "青涩（男）· male-qn-qingse", "精英（男）· male-qn-jingying"
            };

            var voiceChoices = new List<string>();
            var voiceNames = new List<string>();

            string curVoice = string.IsNullOrWhiteSpace(cfg.TtsVoice) ? "female-tianmei" : cfg.TtsVoice.Trim();
            voiceChoices.Add(curVoice);
            voiceNames.Add("当前 · " + curVoice);

            for (int i = 0; i < voiceIds.Length; i++)
            {
                if (HasVoice(voiceChoices, voiceIds[i]))
                    continue;

                voiceChoices.Add(voiceIds[i]);
                voiceNames.Add(voiceLabels[i]);
            }

            var voiceRow = EnumRow("音色 TtsVoice（魔芋/openai 后端用）", voiceChoices.ToArray(), voiceNames.ToArray(),
                () => cfg.TtsVoice, v => cfg.TtsVoice = v);
            voiceRow.Dropdown = true;
            _rows.Add(voiceRow);
            _rows.Add(EnumRow("语音返回格式", new[] { "mp3", "wav" },
                new[] { "mp3", "wav（个别机器上播放更稳）" },
                () => string.Equals(cfg.TtsFormat?.Trim(), "wav", StringComparison.OrdinalIgnoreCase) ? "wav" : "mp3",
                v => cfg.TtsFormat = v));
            _rows.Add(Num("语速", () => cfg.TtsSpeed, v => cfg.TtsSpeed = v, 0.1f, 0.5f, 2f));
            _rows.Add(Num("语音音量（和游戏音量无关）", () => cfg.TtsVolume, v => cfg.TtsVolume = (int)v, 5f, 0f, 100f));
            _rows.Add(Text("edge-tts 命令（装了 pip 包填 edge-tts）", () => cfg.EdgeTtsCommand ?? "", v => cfg.EdgeTtsCommand = v.Trim()));
            _rows.Add(Bool("用常驻助手合成（快很多，推荐）", () => cfg.EdgeUseWorker, v => cfg.EdgeUseWorker = v));
            _rows.Add(Text("edge-tts 音色", () => cfg.EdgeTtsVoice ?? "", v => cfg.EdgeTtsVoice = v.Trim()));
            _rows.Add(Text("edge-tts 语速（如 +15%）", () => cfg.EdgeTtsRate ?? "", v => cfg.EdgeTtsRate = v.Trim()));

            Section("语音 · 听你说");

            _rows.Add(Bool("允许按住说话（语音识别）", () => cfg.SttEnabled, v => cfg.SttEnabled = v));
            _rows.Add(EnumRow("语音识别后端", new[] { "auto", "sapi", "api", "none" },
                new[] { "auto（先用接口，没有就退回 Windows 自带）", "sapi（只用 Windows 自带离线识别）", "api（只用云端接口，更准）", "none（关闭）" },
                () => NormalizeSttBackend(cfg.SttBackend),
                v => cfg.SttBackend = v));
            _rows.Add(Text("识别地址（留空用主接口）", () => cfg.SttBaseUrl ?? "", v => cfg.SttBaseUrl = v.Trim()));
            _rows.Add(Text("识别密钥（留空用主密钥）", () => cfg.SttApiKey ?? "", v => cfg.SttApiKey = v.Trim(),
                () => "（当前 " + (cfg.SttApiKey ?? "").Trim().Length + " 位）"));
            _rows.Add(Text("识别模型 SttModel", () => cfg.SttModel ?? "", v => cfg.SttModel = v.Trim()));
            _rows.Add(Text("识别语言（中文填 zh，留空自动）", () => cfg.SttLanguage ?? "", v => cfg.SttLanguage = v.Trim()));
            _rows.Add(Num("录音短于几秒就当误按", () => cfg.SttMinSeconds, v => cfg.SttMinSeconds = v, 0.1f, 0.2f, 5f));
            _rows.Add(Num("录音最长秒数（到点自动发送）", () => cfg.SttMaxSeconds, v => cfg.SttMaxSeconds = (int)v, 5f, 5f, 300f));

            // ---- 你的伴侣 ----
            Section("伴侣与搭话");

            _rows.Add(Text("伴侣名字", () => cfg.CompanionName ?? "", v => cfg.CompanionName = v.Trim()));
            _rows.Add(Text("她怎么称呼你（留空 = 用你的游戏名字）", () => cfg.PlayerTitle ?? "", v => cfg.PlayerTitle = v.Trim()));
            _rows.Add(Bool("进游戏她先开口打招呼（每次现编，不套模板）", () => cfg.CompanionGreet, v => cfg.CompanionGreet = v));
            _rows.Add(Text("伴侣人设（留空用内置）", () => cfg.CompanionPersona ?? "", v => cfg.CompanionPersona = v));
            _rows.Add(Bool("她知道你此刻在干什么（工具/矿层/体力）", () => cfg.ActivityInPrompt, v => cfg.ActivityInPrompt = v));
            _rows.Add(Bool("她会主动看你在干什么并搭话", () => cfg.ProactiveChat, v => cfg.ProactiveChat = v));
            _rows.Add(Bool("主动搭话也念出来", () => cfg.ProactiveSpeak, v => cfg.ProactiveSpeak = v));
            _rows.Add(Num("两次主动搭话至少隔多少秒", () => cfg.ProactiveCooldown, v => cfg.ProactiveCooldown = (int)v, 15f, 20f, 1800f));
            _rows.Add(Num("攒到事情时开口的概率 %", () => cfg.ProactiveChance, v => cfg.ProactiveChance = (int)v, 5f, 0f, 100f));
            _rows.Add(Num("站着不动多少秒算发呆（0 = 不催）", () => cfg.ProactiveIdleSeconds, v => cfg.ProactiveIdleSeconds = (int)v, 10f, 0f, 600f));
            _rows.Add(Num("没事也隔多少秒主动聊一句（0 = 关闭）", () => cfg.ProactiveEverySeconds, v => cfg.ProactiveEverySeconds = (int)v, 30f, 0f, 3600f));

            EndSections();

            if (_activeGroup.Length == 0 && _groups.Count > 0)
                _activeGroup = _groups[0];

            RebuildVisible();
        }

        // ---------------- 分页 ----------------

        /// <summary>开一个新页签：调用点之前加的那些行归到上一个页签。</summary>
        private void Section(string name)
        {
            CloseSection();
            _sectionName = name;
            _sectionStart = _rows.Count;
        }

        private void EndSections() => CloseSection();

        private void CloseSection()
        {
            if (_sectionName.Length == 0)
                return;

            if (!_groups.Contains(_sectionName))
                _groups.Add(_sectionName);

            for (int i = _sectionStart; i < _rows.Count; i++)
                _rows[i].Group = _sectionName;

            _sectionName = "";
        }

        /// <summary>按当前页签挑出要显示的行。</summary>
        private void RebuildVisible()
        {
            _visible.Clear();

            foreach (var row in _rows)
            {
                if (row.Group == _activeGroup)
                    _visible.Add(row);
            }
        }

        private void SwitchGroup(string group)
        {
            if (group == _activeGroup)
                return;

            _activeGroup = group;
            _openDrop = null;
            RebuildVisible();
            _scroll = 0;
            Relayout();
        }

        /// <summary>页签的位置：标题下面一行。</summary>
        private void LayoutTabs()
        {
            _tabRects.Clear();

            int x = xPositionOnScreen + 24;
            int y = yPositionOnScreen + 48;

            foreach (string g in _groups)
            {
                int w = (int)Ui.Measure(Ui.Body, g) + 28;
                _tabRects.Add(new Rectangle(x, y, w, 28));
                x += w + 8;
            }
        }

        private void DrawTabs(SpriteBatch b)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            _hoverTab = -1;

            for (int i = 0; i < _groups.Count && i < _tabRects.Count; i++)
            {
                var r = _tabRects[i];
                bool on = _groups[i] == _activeGroup;
                bool hover = r.Contains(mx, my);

                if (hover)
                    _hoverTab = i;

                Ui.Box(b, r, on ? Ui.PanelBg : new Color(0x26, 0x21, 0x19), on ? Ui.Gold : Ui.Line);

                float tw = Ui.Measure(Ui.Body, _groups[i]);
                b.DrawString(Ui.Body, _groups[i],
                    new Vector2(r.X + (r.Width - tw) / 2, r.Y + (r.Height - Ui.LineHeight(Ui.Body)) / 2),
                    on ? Ui.Gold : (hover ? Ui.Ink : Ui.Dim));
            }
        }

        /// <summary>
        /// 模型行的候选：当前模型永远排第一位；后面是网关拉到的清单（没拉到就用默认三个），
        /// 但会剔掉 HiddenModels，并且总是补上 StandbyModels（这样 hy3 / GLM-5.3-Flash 不管网关给不给都能选）。
        /// </summary>
        private string[] ModelChoices()
        {
            var list = new List<string>();
            string cur = (ModEntry.Config?.Model ?? "").Trim();
            if (cur.Length > 0)
                list.Add(cur);

            var src = _gatewayModels != null && _gatewayModels.Count > 0
                ? _gatewayModels
                : new List<string>(DefaultModels);

            foreach (string m in src)
                AddModel(list, m);

            // 常备款始终留着（网关清单里没有 hy3 / GLM-5.3-Flash 时也能选）
            foreach (string m in StandbyModels)
                AddModel(list, m);

            return list.ToArray();
        }

        /// <summary>加进候选：去重、跳过空名、跳过 HiddenModels（当前正在用的那个仍然保留，否则看不到自己现在用的是哪个）。</summary>
        private static void AddModel(List<string> list, string id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0)
                return;

            bool dup = false;
            foreach (string s in list)
            {
                if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase))
                {
                    dup = true;
                    break;
                }
            }
            if (dup)
                return;

            bool isCurrent = list.Count > 0 && string.Equals(list[0], id, StringComparison.OrdinalIgnoreCase);
            if (!isCurrent)
            {
                foreach (string hide in HiddenModels)
                {
                    if (string.Equals(hide, id, StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            list.Add(id);
        }

        /// <summary>候选变了以后换掉模型行的选项（当前值不在选项里时也能正常显示）。</summary>
        private void ApplyModelChoices()
        {
            if (_modelRow == null)
                return;

            string[] choices = ModelChoices();
            _modelRow.Options = choices;
            _modelRow.OptionLabels = choices;
        }

        private static bool HasModel(List<string> models, string id)
        {
            foreach (string m in models)
            {
                if (string.Equals(m, id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // ---------------- 下拉清单 ----------------

        private int DropVisible(Row row)
        {
            int count = row?.Options?.Length ?? 0;
            return Math.Min(count, DropMaxRows);
        }

        /// <summary>点模型行：展开 / 收起下拉清单。</summary>
        private void ToggleDrop(Row row, Rectangle ctrl)
        {
            if (_openDrop == row)
            {
                _openDrop = null;
                return;
            }

            ApplyModelChoices();   // 展开前刷新一遍候选（网关清单可能是刚拉到的）

            int count = row.Options?.Length ?? 0;
            if (count == 0)
                return;

            int visible = Math.Min(count, DropMaxRows);
            _openDrop = row;
            _dropScroll = 0;
            _dropHover = -1;

            int w = Math.Max(ctrl.Width, 380);
            w = Math.Min(w, _viewRect.Width - 16);
            int h = visible * DropItemH + 8 + (visible < count ? 16 : 0);
            int x = Math.Clamp(ctrl.X, _viewRect.X + 4, Math.Max(_viewRect.X + 4, _viewRect.Right - w - 4));
            int y = ctrl.Bottom + 2;

            // 下面放不下就翻到控制框上方
            if (y + h > Game1.uiViewport.Height - 8)
                y = Math.Max(8, ctrl.Y - h - 2);

            _dropRect = new Rectangle(x, y, w, h);
        }

        /// <summary>鼠标点在清单里的第几项（-1 = 不在清单里）。</summary>
        private int DropHitIndex(int x, int y)
        {
            if (_openDrop == null || !_dropRect.Contains(x, y))
                return -1;

            int idx = (y - (_dropRect.Y + 4)) / DropItemH + _dropScroll;
            if (idx < 0 || idx >= (_openDrop.Options?.Length ?? 0))
                return -1;

            return idx;
        }

        private void SelectDrop(int idx)
        {
            var row = _openDrop;
            _openDrop = null;
            _dropHover = -1;

            if (row?.Options == null || idx < 0 || idx >= row.Options.Length)
                return;

            string id = row.Options[idx];
            row.Set(id);
            row.Changed?.Invoke(id);
            _saved = false;

            if (row == _modelRow)
                _status = "模型已选：「" + id + "」（记得点「保存」写回 config.json）";
        }

        private void DrawDropdown(SpriteBatch b)
        {
            var row = _openDrop;
            var opts = row.Options ?? Array.Empty<string>();
            var labels = row.OptionLabels ?? opts;
            int visible = DropVisible(row);
            int count = opts.Length;
            int mx = Game1.getMouseX(), my = Game1.getMouseY();

            _dropHover = DropHitIndex(mx, my);

            Ui.Box(b, _dropRect, new Color(0x26, 0x21, 0x19), Ui.Gold);

            for (int i = 0; i < visible; i++)
            {
                int idx = i + _dropScroll;
                if (idx >= count)
                    break;

                var r = new Rectangle(_dropRect.X + 2, _dropRect.Y + 4 + i * DropItemH, _dropRect.Width - 4, DropItemH);
                bool sel = string.Equals(opts[idx], row.Get() ?? "", StringComparison.OrdinalIgnoreCase);
                bool hov = idx == _dropHover;

                if (sel || hov)
                    Ui.Fill(b, r, sel ? Ui.Hover : new Color(0x33, 0x2C, 0x22));

                string text = idx < labels.Length ? labels[idx] : opts[idx];
                b.DrawString(Ui.Body, (sel ? "✓ " : "   ") + text,
                    new Vector2(r.X + 8, r.Y + (DropItemH - Ui.LineHeight(Ui.Body)) / 2),
                    sel ? Ui.Green : Ui.Ink);
            }

            if (count > visible)
            {
                b.DrawString(Ui.Body, "滚轮翻页   " + (_dropScroll + 1) + "-" + Math.Min(_dropScroll + visible, count) + " / " + count,
                    new Vector2(_dropRect.X + 8, _dropRect.Y + 4 + visible * DropItemH + 1), Ui.Dim);
            }
        }

        private static List<string> SplitList(string raw)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
                return res;

            foreach (string part in raw.Split('|'))
            {
                string s = part.Trim();
                if (s.Length > 0)
                    res.Add(s);
            }

            return res;
        }

        private Row Text(string label, Func<string> get, Action<string> set, Func<string> suffix = null)
            => new() { Label = label, Type = Kind.Text, Get = get, Set = set, Suffix = suffix };

        private Row Bool(string label, Func<bool> get, Action<bool> set)
            => new()
            {
                Label = label,
                Type = Kind.Bool,
                Get = () => get() ? "true" : "false",
                Set = v => set(string.Equals(v, "true", StringComparison.OrdinalIgnoreCase))
            };

        private Row Num(string label, Func<float> get, Action<float> set, float step, float min, float max)
            => new()
            {
                Label = label,
                Type = Kind.Num,
                Step = step,
                Min = min,
                Max = max,
                Get = () => Format(get()),
                Set = v =>
                {
                    if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                        set(Math.Clamp(f, min, max));
                }
            };

        private Row EnumRow(string label, string[] options, string[] labels, Func<string> get, Action<string> set, Action<string> changed = null)
            => new()
            {
                Label = label,
                Type = Kind.Enum,
                Options = options,
                OptionLabels = labels,
                Get = get,
                Set = set,
                Changed = changed
            };

        private Row Key(string label, Func<string> get, Action<string> set)
            => new() { Label = label, Type = Kind.Key, Get = get, Set = set };

        /// <summary>音色候选去重（忽略大小写）。</summary>
        private static bool HasVoice(List<string> list, string id)
        {
            foreach (string s in list)
            {
                if (string.Equals(s, id, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>语音合成后端取值兜底：空值或写了别的都当 openai。</summary>
        private static string NormalizeTtsBackend(string raw)
        {
            string v = (raw ?? "").Trim().ToLowerInvariant();

            return v switch
            {
                "sapi" or "windows" => "sapi",
                "edge" or "edge-tts" or "edgetts" => "edge",
                "fish" or "fish-audio" or "fishaudio" => "fish",
                "none" => "none",
                _ => "openai"
            };
        }

        /// <summary>语音识别后端取值兜底：空值或写了别的都当 auto。</summary>
        private static string NormalizeSttBackend(string raw)
        {
            return (raw ?? "").Trim().ToLowerInvariant() switch
            {
                "sapi" or "windows" => "sapi",
                "api" or "openai" => "api",
                "none" => "none",
                _ => "auto"
            };
        }

        private static string Format(float v)
        {
            string s = v.ToString("0.##", CultureInfo.InvariantCulture);
            return s;
        }

        // ---------------- 布局 ----------------

        private void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            width = Math.Max(560, Math.Min(uiW - 24, 1360));
            height = Math.Max(420, Math.Min(uiH - 24, 1000));
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            _viewRect = new Rectangle(xPositionOnScreen + 20, yPositionOnScreen + 82, width - 40, height - 82 - 148);

            LayoutTabs();

            _contentHeight = Math.Max(1, _visible.Count) * RowH + 12;
            _maxScroll = Math.Max(0, _contentHeight - _viewRect.Height);
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);

            int bw = Math.Min(140, (_viewRect.Width - 40) / 5);
            int by = yPositionOnScreen + height - 64;
            int total = bw * 5 + 40;
            int bx = xPositionOnScreen + (width - total) / 2;

            _saveRect = new Rectangle(bx, by, bw, 44);
            _cancelRect = new Rectangle(bx + bw + 10, by, bw, 44);
            _defaultRect = new Rectangle(bx + (bw + 10) * 2, by, bw, 44);
            _testRect = new Rectangle(bx + (bw + 10) * 3, by, bw, 44);
            _sampleRect = new Rectangle(bx + (bw + 10) * 4, by, bw, 44);

            ApplyControlBounds();
        }

        private const int RowH = 56;

        /// <summary>不同版本的输入框限制字段名不一样，找到就一律放宽到 512 字符。</summary>
        private static void RaiseLengthLimit(TextBox box)
        {
            string[] names = { "LengthLimit", "Limit", "MaxLength", "_lengthLimit" };

            foreach (string name in names)
            {
                try
                {
                    var prop = ModEntry.Hlp?.Reflection.GetProperty<int>(box, name, false);
                    if (prop != null)
                    {
                        prop.SetValue(512);
                        return;
                    }

                    var field = ModEntry.Hlp?.Reflection.GetField<int>(box, name, false);
                    if (field != null)
                    {
                        field.SetValue(512);
                        return;
                    }
                }
                catch { /* 版本差异，忽略 */ }
            }
        }

        private void ApplyControlBounds()
        {
            foreach (var row in _rows)
            {
                int w = row.Type switch
                {
                    Kind.Text => Math.Min(760, _viewRect.Width - 280),
                    Kind.Num => 210,
                    Kind.Bool => 130,
                    Kind.Enum => 250,
                    _ => 170
                };

                if (w < 120)
                    w = 120;

                int x = _viewRect.Right - w;
                int y = 0; // 绘制时再叠加滚动
                row.Ctrl = new Rectangle(x, y, w, 40);

                if (row.Type == Kind.Text)
                {
                    if (row.Box == null)
                    {
                        try { _textBoxTex ??= Game1.content.Load<Texture2D>("LooseSprites\\textBox"); } catch { /* ignore */ }
                        row.Box = new TextBox(_textBoxTex, null, Game1.smallFont, Game1.textColor);
                        // 放开输入框可能存在的长度限制，避免长密钥（50+ 位）粘贴进来被截断
                        RaiseLengthLimit(row.Box);
                        row.Box.Text = row.Get() ?? "";
                        row.LastApplied = row.Box.Text;
                    }

                    row.Box.X = x;
                    row.Box.Width = w;
                    row.Box.Height = 40;
                }
            }
        }

        // ---------------- 交互 ----------------

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                if (CancelCapture())
                    return;

                Close();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                Close();
                return;
            }

            // 下拉清单画在最上层、可能盖住别的控件，所以先处理它
            if (_openDrop != null)
            {
                int hit = DropHitIndex(x, y);
                if (hit >= 0)
                {
                    SelectDrop(hit);
                    Game1.playSound("smallSelect");
                    return;
                }

                // 点在清单空白处、或点在原来那一行上（= 再点一下收起）：只收起，不要立刻又展开
                bool keep = _dropRect.Contains(x, y) || _openDrop.Ctrl.Contains(x, y);
                _openDrop = null;
                if (keep)
                    return;
            }

            if (_saveRect.Contains(x, y)) { Save(); return; }
            if (_cancelRect.Contains(x, y)) { Game1.playSound("bigDeSelect"); Close(); return; }
            if (_defaultRect.Contains(x, y)) { ResetDefault(); return; }
            if (_testRect.Contains(x, y)) { TestConnection(); return; }
            if (_sampleRect.Contains(x, y)) { SpeakSample(); return; }

            // 页签
            for (int i = 0; i < _tabRects.Count && i < _groups.Count; i++)
            {
                if (_tabRects[i].Contains(x, y))
                {
                    Game1.playSound("smallSelect");
                    SwitchGroup(_groups[i]);
                    return;
                }
            }

            if (!_viewRect.Contains(x, y))
                return;

            int rel = y - _viewRect.Y + _scroll - 6;
            int index = rel / RowH;
            if (index < 0 || index >= _visible.Count)
                return;

            var row = _visible[index];
            int cx = row.Ctrl.X;
            int cy = _viewRect.Y + 6 + index * RowH - _scroll + 8;
            var ctrl = new Rectangle(cx, cy, row.Ctrl.Width, row.Ctrl.Height);

            if (!ctrl.Contains(x, y))
            {
                if (row.Box != null)
                    Focus(row);
                return;
            }

            Game1.playSound("smallSelect");
            _saved = false;

            switch (row.Type)
            {
                case Kind.Bool:
                    row.Set(row.Get() == "true" ? "false" : "true");
                    break;

                case Kind.Num:
                {
                    int third = Math.Min(46, ctrl.Width / 3);
                    float cur = Parse(row.Get());
                    if (x < ctrl.X + third)
                        row.Set(Format((float)Math.Round(Math.Clamp(cur - row.Step, row.Min, row.Max), 2)));
                    else if (x > ctrl.Right - third)
                        row.Set(Format((float)Math.Round(Math.Clamp(cur + row.Step, row.Min, row.Max), 2)));
                    break;
                }

                case Kind.Enum when row.Dropdown:
                    ToggleDrop(row, ctrl);
                    break;

                case Kind.Enum:
                {
                    string cur = row.Get() ?? "";
                    int i = Array.FindIndex(row.Options, o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase));
                    string next = row.Options[(i + 1) % row.Options.Length];
                    row.Set(next);
                    row.Changed?.Invoke(next);
                    break;
                }

                case Kind.Key:
                    StartCapture(row);
                    break;

                default:
                    Focus(row);
                    break;
            }
        }

        private void Focus(Row row)
        {
            foreach (var r in _rows)
            {
                if (r.Box == null)
                    continue;
                bool on = r == row;
                r.Box.Selected = on;
                if (on)
                    Game1.keyboardDispatcher.Subscriber = r.Box;
            }
        }

        private static float Parse(string s)
            => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;

        // ---------------- 按键捕获 ----------------

        private void StartCapture(Row row)
        {
            foreach (var r in _rows)
                r.PendingCapture = r == row;

            ModEntry.KeyCapture = key =>
            {
                row.PendingCapture = false;
                ModEntry.KeyCapture = null;

                if (key == SButton.Escape)
                {
                    _status = "已取消改键";
                    return;
                }

                row.Set(key.ToString());
                _saved = false;
                _status = "按键已设为 " + key + "（记得点保存）";
            };

            _status = "请按下新的按键（Esc 取消）…";
            Game1.playSound("Cowboy_Footstep");
        }

        private bool CancelCapture()
        {
            foreach (var r in _rows)
            {
                if (!r.PendingCapture)
                    continue;
                r.PendingCapture = false;
                ModEntry.KeyCapture = null;
                _status = "已取消改键";
                return true;
            }
            return false;
        }

        // ---------------- 底部操作 ----------------

        /// <summary>把文本框里的内容同步进配置（每帧都会调，所以改了立刻生效，不必先保存）。</summary>
        private void ApplyRows()
        {
            foreach (var row in _rows)
            {
                if (row.Box == null)
                    continue;

                string now = row.Box.Text ?? "";
                if (now == row.LastApplied)
                    continue;

                row.LastApplied = now;
                row.Set(now);
                _saved = false;
            }

            // 按方案分别记住各自的密钥，切回来时自动带出
            var cfg = ModEntry.Config;
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.Preset) && cfg.Preset != "custom")
            {
                cfg.PresetKeys ??= new Dictionary<string, string>();
                cfg.PresetKeys[cfg.Preset] = (cfg.ApiKey ?? "").Trim();
            }
        }

        /// <summary>切换模型方案：自动填地址/模型，并带出这个方案上次保存的密钥。</summary>
        private void ApplyPreset(string id)
        {
            var cfg = ModEntry.Config;
            cfg.Preset = id;

            if (id == "trio")
            {
                ApplyTrioPreset();
                return;
            }

            var p = Preset.Get(id);
            if (p == null || id == "custom")
            {
                _status = "已切到「自定义」，地址和模型自己填";
                _saved = false;
                return;
            }

            cfg.BaseUrl = p.BaseUrl;
            cfg.Model = p.Model;
            cfg.PresetKeys ??= new Dictionary<string, string>();
            cfg.ApiKey = cfg.PresetKeys.TryGetValue(id, out string k) ? k : "";

            RefreshBoxes();
            _saved = false;

            int len = (cfg.ApiKey ?? "").Trim().Length;
            _status = "已切到「" + p.Label + "」" + (p.NeedsKey
                ? (len > 0 ? "，密钥已自动带出（" + len + " 位）" : "，请在下面填它的密钥（填一次就会记住）")
                : "，本地模型不用密钥");

            if (!string.IsNullOrWhiteSpace(p.Note))
                _status += "  提示：" + p.Note;

            ModEntry.Log.Log("切换模型方案：" + id + " -> " + cfg.BaseUrl + " / " + cfg.Model, LogLevel.Info);
        }

        /// <summary>连上 dsh-connect-trio 的本机回环网关，并自动拉取它支持的模型名。</summary>
        private void ApplyTrioPreset()
        {
            var cfg = ModEntry.Config;
            var info = ShimStore.Read();

            if (info == null)
            {
                RefreshBoxes();
                _saved = false;
                _status = "没找到本机网关文件 " + ShimStore.PrimaryPath + "（要先启动 DSH，且插件带落盘补丁）";
                return;
            }

            cfg.BaseUrl = info.BaseUrl;
            cfg.ApiKey = info.Token;
            if (string.IsNullOrWhiteSpace(cfg.Model))
                cfg.Model = "";

            RefreshBoxes();
            _saved = false;
            _status = "已连上本机网关 " + info.BaseUrl + "，正在拉取模型列表…";

            string url = info.BaseUrl;
            string token = info.Token;

            Task.Run(async () =>
            {
                try
                {
                    // 只要 WorkBuddy 那一路（用 WorkBuddy 额度），并自动剔掉同名却归 Trae/Qoder 的
                    var models = await LlmClient.GetModelsAsync(url, token, CancellationToken.None, "trio-workbuddy");
                    ModEntry.Log.Log("本机网关可得模型（" + models.Count + "）：" + string.Join(", ", models), LogLevel.Info);

                    _gatewayModels = models;

                    string pick = (ModEntry.Config.Model ?? "").Trim();
                    if (pick.Length == 0 || !HasModel(models, pick))
                        pick = HasModel(models, "hy3") ? "hy3" : models[0];

                    ModEntry.Config.Model = pick;
                    ApplyModelChoices();
                    _testResult = "网关有 " + models.Count + " 个模型，当前用「" + pick + "」，点「模型 Model」那行可切换，完整列表见 SMAPI 控制台";
                    _needRefresh = true;
                }
                catch (Exception ex)
                {
                    _testResult = "拉取模型失败（网关没在跑？）：" + ex.Message;
                    ModEntry.Log.Log("拉取本机网关模型失败：" + ex.Message, LogLevel.Warn);
                }
            });
        }

        /// <summary>省积分模式：开启时压低各项开销，关闭时还原开启前的数值。</summary>
        private void ApplySaveMode(bool on)
        {
            var cfg = ModEntry.Config;
            cfg.SaveMode = on;

            if (on)
            {
                _beforeSaveInt["MaxHistory"] = cfg.MaxHistory;
                _beforeSaveBool["NpcAutoGreet"] = cfg.NpcAutoGreet;
                _beforeSaveBool["UseLocalTutorial"] = cfg.UseLocalTutorial;
                _beforeSaveInt["MaxTokens"] = cfg.MaxTokens;

                cfg.MaxHistory = 3;
                cfg.NpcAutoGreet = false;
                cfg.UseLocalTutorial = false;
                cfg.MaxTokens = 800;

                _status = "省积分模式：上下文 3 轮 / 不打招呼 / 不注入教程 / 回答上限 800（关掉会还原原值）";
            }
            else if (_beforeSaveInt.Count > 0 || _beforeSaveBool.Count > 0)
            {
                if (_beforeSaveInt.TryGetValue("MaxHistory", out int h)) cfg.MaxHistory = h;
                if (_beforeSaveBool.TryGetValue("NpcAutoGreet", out bool g)) cfg.NpcAutoGreet = g;
                if (_beforeSaveBool.TryGetValue("UseLocalTutorial", out bool u)) cfg.UseLocalTutorial = u;
                if (_beforeSaveInt.TryGetValue("MaxTokens", out int m)) cfg.MaxTokens = m;

                _beforeSaveInt.Clear();
                _beforeSaveBool.Clear();

                _status = "已关闭省积分模式，恢复了原来的数值";
            }
            else
            {
                _status = "已关闭省积分模式";
            }

            RefreshBoxes();
            _saved = false;
        }

        /// <summary>配置被外部改动后，把文本输入框刷新成最新值。</summary>
        private void RefreshBoxes()
        {
            foreach (var row in _rows)
            {
                if (row.Box == null)
                    continue;
                row.Box.Text = row.Get() ?? "";
                row.LastApplied = row.Box.Text;
            }

            ApplyModelChoices();
        }

        private void Save()
        {
            ApplyRows();
            ModEntry.Hlp.WriteConfig(ModEntry.Config);
            ModEntry.ReloadTutorial();

            _snapshot = ModEntry.Config.Clone();
            _saved = true;
            _status = "已保存到 config.json（当前设置已生效：" + (string.IsNullOrWhiteSpace(ModEntry.Config.Model) ? "未填模型" : ModEntry.Config.Model) + "）";
            Game1.addHUDMessage(new HUDMessage("StardewAI：设置已保存", 2));
            Game1.playSound("money");
        }

        private void ResetDefault()
        {
            ReleaseSubscriber();

            ModEntry.Config = new ModConfig();
            ModEntry.Hlp.WriteConfig(ModEntry.Config);
            ModEntry.ReloadTutorial();
            ModEntry.KeyCapture = null;

            _rows.Clear();
            BuildRows();
            ApplyControlBounds();

            _snapshot = ModEntry.Config.Clone();
            _saved = true;
            _status = "已恢复默认设置";
            Game1.playSound("trashcan");
        }

        private void ReleaseSubscriber()
        {
            try
            {
                foreach (var row in _rows)
                {
                    if (row.Box != null)
                    {
                        row.Box.Selected = false;
                        if (Game1.keyboardDispatcher?.Subscriber == row.Box)
                            Game1.keyboardDispatcher.Subscriber = null;
                    }
                }
            }
            catch { /* ignore */ }
        }

        private void TestConnection()
        {
            if (_testing)
                return;

            // 关键：先把输入框里的内容同步进配置，再测试，否则测的是旧值
            ApplyRows();

            var cfg = ModEntry.Config;
            _testing = true;
            _testResult = null;
            _status = "正在测试 " + cfg.BaseUrl + " …";

            ModEntry.Log.Log(
                "测试连接 -> POST " + (cfg.BaseUrl ?? "").TrimEnd('/') + "/chat/completions" +
                " | 模型=" + cfg.Model + " | 密钥长度=" + (cfg.ApiKey ?? "").Trim().Length +
                (string.IsNullOrWhiteSpace(cfg.ApiKey) ? "（密钥为空）" : ""),
                LogLevel.Info);

            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(15, cfg.TimeoutSeconds)));
            var token = _cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    // 用和正式聊天一样的模式来测（cfg.Stream），才算真测到平时走的那条路；
                    // max_tokens 要给足：hy3 这类推理模型会先输出 reasoning_content，32 个 token 常被推理吃光导致正文为空
                    string reply = await LlmClient.ChatAsync(
                        cfg.BaseUrl, cfg.ApiKey, cfg.Model, 0.1f, 256, cfg.Stream,
                        new List<ChatMsg> { new("user", "只回复两个字：好的") },
                        null, token).ConfigureAwait(false);

                    string ok = string.IsNullOrWhiteSpace(reply) ? "(模型返回了空内容)" : reply.Trim();
                    _testResult = "连接成功：" + Truncate(ok, 80);
                    ModEntry.Log.Log("测试连接成功：" + Truncate(ok, 120), LogLevel.Info);
                }
                catch (Exception ex)
                {
                    string hint = Hint(ex.Message, cfg);
                    _testResult = "连接失败：" + Truncate(ex.Message, 220) + hint;
                    ModEntry.Log.Log("测试连接失败：" + ex.Message + "  " + hint, LogLevel.Warn);
                }
                finally
                {
                    _testing = false;
                }
            });
        }

        /// <summary>根据错误内容给出人话提示。</summary>
        private static string Hint(string message, ModConfig cfg)
        {
            string m = message ?? "";

            // 本机 DSH 网关方案：这条路上的毛病跟公网中转站完全不是一回事，先给专用提示
            if (string.Equals(cfg?.Preset, "trio", StringComparison.OrdinalIgnoreCase) &&
                (m.Contains("无法解析响应") || m.Contains("不是对话数据流")))
                return "  → 本机网关没按对话格式返回数据，先确认 DeepSeek Harness 正在运行；再回设置界面点一次「模型方案（一键切换）」刷新地址密钥";

            if (m.Contains("令牌") || m.Contains("key", StringComparison.OrdinalIgnoreCase) || m.Contains("401"))
                return "  → 多半是密钥不对（注意别把多余空格/引号复制进来，也别把 sk- 漏掉）";
            if (m.Contains("模型不存在") || m.Contains("不存在的模型") || m.Contains("模型名") ||
                m.Contains("model not found", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("unknown model", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("invalid model", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                return "  → 这个模型名接口方可能没有，试试它文档里写的名字（如 deepseek-chat）";
            if (m.Contains("余额") || m.Contains("额度") || m.Contains("quota") || m.Contains("402"))
                return "  → 账户余额/额度用完了";
            if (m.Contains("网络错误"))
                return "  → 网络连不上这个地址，检查 BaseUrl 拼写或是否需要科学上网/代理";
            if (m.Contains("超时"))
                return "  → 超时了，可以在设置里把「超时秒数」调大一点";
            if (m.Contains("推理内容"))
                return "  → 这是推理型模型，把「单次回答上限」调到 3000 以上，或换个非推理模型（更快也更省）";
            if (m.Contains("少了 /v1") || m.Contains("网页"))
                return "  → 接口地址要填到 /v1 结尾（例如 https://www.moyu.cn/v1），不要填网站首页";
            if (m.Contains("HTTP 404"))
                return "  → 路径不对，BaseUrl 一般填到 /v1 结尾即可";
            if (m.Contains("HTTP 5"))
                return "  → 中转站服务端出错，过一会儿再试";
            if (string.IsNullOrWhiteSpace(cfg.BaseUrl))
                return "  → 接口地址是空的";
            return "";
        }

        private static string Truncate(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private void Close()
        {
            _cts?.Cancel();
            ModEntry.KeyCapture = null;
            ReleaseSubscriber();

            // 改过但没点「保存」就关掉 = 放弃这轮改动
            if (!_saved && _snapshot != null)
                ModEntry.Config = _snapshot;

            Game1.playSound("bigDeSelect");
            Game1.activeClickableMenu = _returnMenu;
        }

        protected override void cleanupBeforeExit()
        {
            ReleaseSubscriber();
            base.cleanupBeforeExit();
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Relayout();
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int dir = ModEntry.Config?.InvertScroll == true ? -direction : direction;

            // 下拉清单开着的时候，滚轮翻的是清单本身
            if (_openDrop != null)
            {
                int max = Math.Max(0, (_openDrop.Options?.Length ?? 0) - DropVisible(_openDrop));
                _dropScroll = Math.Clamp(_dropScroll + dir, 0, max);
                return;
            }

            _scroll = Math.Clamp(_scroll + direction * 56, 0, _maxScroll);
        }

        public override void update(GameTime time)
        {
            base.update(time);

            // 边打字边同步进配置，这样聊天和「测试连接」用的永远是最新填的内容
            ApplyRows();

            if (_needRefresh)
            {
                _needRefresh = false;
                RefreshBoxes();
                _saved = false;
            }

            if (_testResult != null)
            {
                _status = _testResult;
                _testResult = null;
            }
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);

            _hoverRow = -1;
            if (_viewRect.Contains(x, y))
            {
                int rel = y - _viewRect.Y + _scroll - 6;
                int idx = rel / RowH;
                if (idx >= 0 && idx < _visible.Count)
                    _hoverRow = idx;
            }

            _hoverBtn = -1;
            if (_saveRect.Contains(x, y)) _hoverBtn = 0;
            else if (_cancelRect.Contains(x, y)) _hoverBtn = 1;
            else if (_defaultRect.Contains(x, y)) _hoverBtn = 2;
            else if (_testRect.Contains(x, y)) _hoverBtn = 3;
            else if (_sampleRect.Contains(x, y)) _hoverBtn = 4;
        }

        /// <summary>按当前设置（哪怕还没点保存）念一句，专门用来挑音色 / 调语速。</summary>
        private void SpeakSample()
        {
            ApplyRows();   // 先把输入框里的内容同步进配置，否则试听的是旧值

            var cfg = ModEntry.Config;
            _status = "试听中：" + cfg.TtsModel + " / " + cfg.TtsVoice + " / 语速 " + cfg.TtsSpeed;
            _testResult = null;

            Voice.VoiceAssistant.Interrupt();

            // 试听句用她自己的名字（以前写死了"小星"）
            string who = string.IsNullOrWhiteSpace(cfg.CompanionName) ? "" : cfg.CompanionName.Trim();
            Voice.VoiceAssistant.SpeakTest(who.Length > 0
                ? "我是" + who + "。今天天气不错，要不要一起去农场看看？"
                : "今天天气不错，要不要一起去农场看看？");
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
        }

        // ---------------- 绘制 ----------------

        public override void draw(SpriteBatch b)
        {
            Ui.Fill(b, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), new Color(0, 0, 0, 130));

            Game1.DrawBox(xPositionOnScreen, yPositionOnScreen, width, height);
            Ui.Fill(b, new Rectangle(xPositionOnScreen + 8, yPositionOnScreen + 8, width - 16, height - 16), Ui.PageBg);

            Utility.drawTextWithShadow(b, "StardewAI 设置", Game1.dialogueFont,
                new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 12), Ui.Green);

            DrawTabs(b);   // 分页：接口与模型 / 回答与教程 / NPC 与按键 / 语音·她说话 / 语音·听你说 / 伴侣与搭话

            Ui.Box(b, _viewRect, new Color(0x29, 0x24, 0x1B), new Color(0x44, 0x3C, 0x2E));

            Ui.BeginClip(b, _viewRect);
            DrawRows(b);
            Ui.EndClip(b);

            if (_maxScroll > 0)
            {
                var track = new Rectangle(_viewRect.Right - 4, _viewRect.Y, 6, _viewRect.Height);
                Ui.Fill(b, track, new Color(0x44, 0x3C, 0x2E));
                int h = Math.Max(30, (int)(_viewRect.Height * (float)_viewRect.Height / _contentHeight));
                int ty = _viewRect.Y + (int)(_scroll / (float)_maxScroll * (_viewRect.Height - h));
                Ui.Fill(b, new Rectangle(track.X, ty, track.Width, h), Ui.Gold);
            }

            string status = string.IsNullOrWhiteSpace(_status)
                ? "改完立刻生效；点「保存」写回 config.json   |   " + TutorialStore.Status
                : _status;

            if (!_saved)
                status += "   [尚未保存]";
            var statusLines = Ui.Wrap(status, Ui.Body, width - 48);
            int sy = _viewRect.Bottom + 8;
            for (int i = 0; i < statusLines.Count && i < 2; i++)
            {
                b.DrawString(Ui.Body, statusLines[i], new Vector2(xPositionOnScreen + 24, sy), _testing ? Ui.Blue : Ui.Gold);
                sy += 16;
            }

            Button(b, _saveRect, "保存", _hoverBtn == 0);
            Button(b, _cancelRect, "取消", _hoverBtn == 1);
            Button(b, _defaultRect, "恢复默认", _hoverBtn == 2);
            Button(b, _testRect, _testing ? "测试中…" : "测试连接", _hoverBtn == 3 && !_testing);
            Button(b, _sampleRect, "试听音色", _hoverBtn == 4);

            if (_openDrop != null)
                DrawDropdown(b);

            base.draw(b);
            drawMouse(b);
        }

        private void DrawRows(SpriteBatch b)
        {
            for (int i = 0; i < _visible.Count; i++)
            {
                var row = _visible[i];
                int y = _viewRect.Y + 6 + i * RowH - _scroll;
                if (y > _viewRect.Bottom || y + RowH < _viewRect.Y)
                    continue;

                bool hover = _hoverRow == i;
                Ui.Fill(b, new Rectangle(_viewRect.X + 4, y + 2, _viewRect.Width - 16, RowH - 8), hover ? new Color(0x30, 0x2A, 0x20) : Color.Transparent);

                var labelColor = hover ? Ui.Ink : Ui.Dim;
                int maxLabel = row.Ctrl.X - (_viewRect.X + 24);
                string suffix = row.Suffix?.Invoke();
                var lines = Ui.Wrap(row.Label + (string.IsNullOrEmpty(suffix) ? "" : " " + suffix), Ui.Body, maxLabel);
                int ly = y + 12;
                foreach (string s in lines)
                {
                    b.DrawString(Ui.Body, s, new Vector2(_viewRect.X + 20, ly), labelColor);
                    ly += Ui.LineHeight(Ui.Body);
                }

                var ctrl = new Rectangle(row.Ctrl.X, y + 8, row.Ctrl.Width, row.Ctrl.Height);
                int mx = Game1.getMouseX(), my = Game1.getMouseY();

                switch (row.Type)
                {
                    case Kind.Text:
                        row.Box.Y = ctrl.Y;
                        row.Box.Draw(b, false);
                        break;

                    case Kind.Bool:
                    {
                        bool on = row.Get() == "true";
                        bool h = ctrl.Contains(mx, my);
                        Ui.Box(b, ctrl, h ? Ui.Hover : Ui.PanelBg, on ? Ui.Green : Ui.Line);
                        Center(b, ctrl, on ? "开启" : "关闭", on ? Ui.Green : Ui.Dim);
                        break;
                    }

                    case Kind.Num:
                    {
                        int third = Math.Min(46, ctrl.Width / 3);
                        bool hl = new Rectangle(ctrl.X, ctrl.Y, third, ctrl.Height).Contains(mx, my);
                        bool hr = new Rectangle(ctrl.Right - third, ctrl.Y, third, ctrl.Height).Contains(mx, my);

                        Ui.Box(b, ctrl, Ui.PanelBg, Ui.Line);
                        Center(b, new Rectangle(ctrl.X, ctrl.Y, third, ctrl.Height), "-", hl ? Ui.Gold : Ui.Ink);
                        Center(b, new Rectangle(ctrl.X + third, ctrl.Y, ctrl.Width - third * 2, ctrl.Height), row.Get() ?? "", Ui.Gold);
                        Center(b, new Rectangle(ctrl.Right - third, ctrl.Y, third, ctrl.Height), "+", hr ? Ui.Gold : Ui.Ink);
                        break;
                    }

                    case Kind.Enum:
                    {
                        bool h = ctrl.Contains(mx, my);
                        string cur = row.Get() ?? "";
                        int idx = Array.FindIndex(row.Options, o => string.Equals(o, cur, StringComparison.OrdinalIgnoreCase));
                        string show = idx >= 0 && row.OptionLabels != null && idx < row.OptionLabels.Length
                            ? row.OptionLabels[idx]
                            : cur;
                        Ui.Box(b, ctrl, h ? Ui.Hover : Ui.PanelBg, h ? Ui.Gold : Ui.Line);
                        Center(b, ctrl, show + "  >", Ui.Ink);
                        break;
                    }

                    case Kind.Key:
                    {
                        bool h = ctrl.Contains(mx, my) || row.PendingCapture;
                        Ui.Box(b, ctrl, row.PendingCapture ? Ui.Hover : (h ? Ui.Hover : Ui.PanelBg),
                            row.PendingCapture ? Ui.Gold : (h ? Ui.Gold : Ui.Line));
                        Center(b, ctrl, row.PendingCapture ? "请按键…" : (row.Get() ?? "无"), row.PendingCapture ? Ui.Gold : Ui.Ink);
                        break;
                    }
                }
            }
        }

        private static void Center(SpriteBatch b, Rectangle r, string text, Color color)
        {
            float tw = Ui.Measure(Ui.Body, text);
            b.DrawString(Ui.Body, text,
                new Vector2(r.X + (r.Width - tw) / 2, r.Y + (r.Height - Ui.LineHeight(Ui.Body)) / 2), color);
        }

        private void Button(SpriteBatch b, Rectangle r, string label, bool hover)
        {
            Ui.Box(b, r, hover ? Ui.Hover : Ui.PanelBg, hover ? Ui.Gold : Ui.Line);
            float tw = Ui.Measure(Ui.Body, label);
            b.DrawString(Ui.Body, label,
                new Vector2(r.X + (r.Width - tw) / 2, r.Y + (r.Height - Ui.LineHeight(Ui.Body)) / 2),
                hover ? Ui.Gold : Ui.Ink);
        }
    }
}
