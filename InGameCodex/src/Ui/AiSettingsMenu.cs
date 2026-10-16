using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using InGameCodex.Ai;
using StardewValley;
using StardewValley.Menus;

namespace InGameCodex.Ui
{
    /// <summary>游戏内可视化设置面板：文本 / 开关 / 数值 / 按键 / 枚举，可直接写回 config.json。</summary>
    public sealed class AiSettingsMenu : IClickableMenu
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
        }

        private readonly List<Row> _rows = new();
        private readonly IClickableMenu _returnMenu;
        private AiConfig _snapshot;

        // 设计坐标 <-> 屏幕坐标：整个界面画进「视口 / Cx.S」的离屏贴图再放大，输入要还原回来
        private static int DesignX(int screenX) => (int)(screenX / Cx.S);
        private static int DesignY(int screenY) => (int)(screenY / Cx.S);

        private Rectangle _viewRect;
        private int _scroll;
        private int _maxScroll;
        private int _contentHeight;

        private Rectangle _saveRect, _cancelRect, _defaultRect, _testRect;
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

        /// <summary>还没连上网关时，模型行的默认候选。</summary>
        private static readonly string[] DefaultModels = { "hy3", "deepseek-v4.1-flash", "glm-5.3-flash" };

        /// <summary>不管网关返回什么，都不出现在候选里的模型（不想看到的写这儿）。</summary>
        private static readonly string[] HiddenModels = { "hy4-preview-f" };

        /// <summary>不管网关返回什么，这几款始终留在候选里（想固定可选哪些就改这里）。</summary>
        private static readonly string[] StandbyModels = { "hy3", "deepseek-v4.1-flash", "glm-5.3-flash" };

        // ---------------- 下拉清单（点一下展开，点选项即选中） ----------------
        private const int DropItemH = 32;
        private const int DropMaxRows = 10;
        private Row _openDrop;
        private Rectangle _dropRect;
        private int _dropScroll;
        private int _dropHover = -1;

        private static Texture2D _textBoxTex;

        public AiSettingsMenu(IClickableMenu returnMenu)
            : base(0, 0, 100, 100, false)
        {
            _returnMenu = returnMenu;
            _snapshot = AiRuntime.Config.Clone();
            BuildRows();
            Relayout();

            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + 12, 40, 40),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);
        }

        // ---------------- 行定义 ----------------

        private void BuildRows()
        {
            var cfg = AiRuntime.Config;

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

            _rows.Add(Bool("附带存档状态（季节/技能/背包）", () => cfg.IncludeGameState, v => cfg.IncludeGameState = v));
            _rows.Add(Bool("检索本地教程作答", () => cfg.UseLocalTutorial, v => cfg.UseLocalTutorial = v));
            _rows.Add(Num("教程注入字数上限", () => cfg.MaxTutorialChars, v => cfg.MaxTutorialChars = (int)v, 500f, 0f, 10000f));
            _rows.Add(Bool("注入图鉴数据（物品用途/来源、NPC 喜好）", () => cfg.UseCodexData, v => cfg.UseCodexData = v));
            _rows.Add(Num("图鉴注入字数上限", () => cfg.MaxCodexChars, v => cfg.MaxCodexChars = (int)v, 400f, 0f, 8000f));
            _rows.Add(Bool("联网搜索（本地资料没有时去网上现查）", () => cfg.WebSearch, v => cfg.WebSearch = v));
            _rows.Add(Text("联网搜索密钥（留空用免费匿名额度）", () => cfg.WebSearchKey ?? "", v => cfg.WebSearchKey = v));
            _rows.Add(Num("联网取回条数", () => cfg.WebSearchMaxResults, v => cfg.WebSearchMaxResults = (int)v, 1f, 1f, 5f));
            _rows.Add(Bool("允许 AI 给物品（回答下方出「获取」按钮）", () => cfg.AiCanGiveItems, v => cfg.AiCanGiveItems = v));
            _rows.Add(Bool("回答不使用 Markdown 符号", () => cfg.NoMarkdown, v => cfg.NoMarkdown = v));
            _rows.Add(Text("额外要求（如：用粤语回答）", () => cfg.ExtraSystemPrompt ?? "", v => cfg.ExtraSystemPrompt = v));

            _rows.Add(Key("打开 AI 问答的按键", () => cfg.AskKey, v => cfg.AskKey = v));
            _rows.Add(Bool("滚轮方向反转", () => cfg.InvertScroll, v => cfg.InvertScroll = v));
            _rows.Add(Text("快捷提问（用 | 分隔）", () => string.Join(" | ", cfg.QuickPrompts ?? new List<string>()), v => cfg.QuickPrompts = SplitList(v)));
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

        /// <summary>
        /// 模型行的候选：当前模型永远排第一位；后面是网关拉到的清单（没拉到就用默认三个），
        /// 但会剔掉 HiddenModels，并且总是补上 StandbyModels（这样 hy3 / GLM-5.3-Flash 不管网关给不给都能选）。
        /// </summary>
        private string[] ModelChoices()
        {
            var list = new List<string>();
            string cur = (AiRuntime.Config?.Model ?? "").Trim();
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

        /// <summary>候选变了以后换掉模型行的选项。</summary>
        private void ApplyModelChoices()
        {
            if (_modelRow == null)
                return;

            string[] choices = ModelChoices();
            _modelRow.Options = choices;
            _modelRow.OptionLabels = choices;
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

        private static string Format(float v)
        {
            string s = v.ToString("0.##", CultureInfo.InvariantCulture);
            return s;
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
            if (y + h > Cx.DesignH - 8)
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
            int mx = DesignX(Game1.getMouseX()), my = DesignY(Game1.getMouseY());

            _dropHover = DropHitIndex(mx, my);

            AiUi.Box(b, _dropRect, new Color(0x26, 0x21, 0x19), AiUi.Gold);

            for (int i = 0; i < visible; i++)
            {
                int idx = i + _dropScroll;
                if (idx >= count)
                    break;

                var r = new Rectangle(_dropRect.X + 2, _dropRect.Y + 4 + i * DropItemH, _dropRect.Width - 4, DropItemH);
                bool sel = string.Equals(opts[idx], row.Get() ?? "", StringComparison.OrdinalIgnoreCase);
                bool hov = idx == _dropHover;

                if (sel || hov)
                    AiUi.Fill(b, r, sel ? AiUi.Hover : new Color(0x33, 0x2C, 0x22));

                string text = idx < labels.Length ? labels[idx] : opts[idx];
                b.DrawString(AiUi.Body, (sel ? "✓ " : "   ") + text,
                    new Vector2(r.X + 8, r.Y + (DropItemH - AiUi.LineHeight(AiUi.Body)) / 2),
                    sel ? AiUi.Green : AiUi.Ink);
            }

            if (count > visible)
            {
                b.DrawString(AiUi.Body, "滚轮翻页   " + (_dropScroll + 1) + "-" + Math.Min(_dropScroll + visible, count) + " / " + count,
                    new Vector2(_dropRect.X + 8, _dropRect.Y + 4 + visible * DropItemH + 1), AiUi.Dim);
            }
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

        // ---------------- 布局 ----------------

        private void Relayout()
        {
            // 设计坐标：界面按 1.0 排版，draw 时整体放大 Cx.S 倍
            int uiW = Cx.DesignW;
            int uiH = Cx.DesignH;

            width = Math.Max(560, Math.Min(uiW - 24, 1360));
            height = Math.Max(420, Math.Min(uiH - 24, 1000));
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            _viewRect = new Rectangle(xPositionOnScreen + 20, yPositionOnScreen + 68, width - 40, height - 68 - 148);

            _contentHeight = _rows.Count * RowH + 12;
            _maxScroll = Math.Max(0, _contentHeight - _viewRect.Height);
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);

            int bw = Math.Min(150, (_viewRect.Width - 40) / 4);
            int by = yPositionOnScreen + height - 64;
            int total = bw * 4 + 30;
            int bx = xPositionOnScreen + (width - total) / 2;

            _saveRect = new Rectangle(bx, by, bw, 44);
            _cancelRect = new Rectangle(bx + bw + 10, by, bw, 44);
            _defaultRect = new Rectangle(bx + (bw + 10) * 2, by, bw, 44);
            _testRect = new Rectangle(bx + (bw + 10) * 3, by, bw, 44);

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
                    var prop = AiRuntime.Hlp?.Reflection.GetProperty<int>(box, name, false);
                    if (prop != null)
                    {
                        prop.SetValue(512);
                        return;
                    }

                    var field = AiRuntime.Hlp?.Reflection.GetField<int>(box, name, false);
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
            x = DesignX(x);
            y = DesignY(y);

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

            if (!_viewRect.Contains(x, y))
                return;

            int rel = y - _viewRect.Y + _scroll - 6;
            int index = rel / RowH;
            if (index < 0 || index >= _rows.Count)
                return;

            var row = _rows[index];
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

            AiRuntime.KeyCapture = key =>
            {
                row.PendingCapture = false;
                AiRuntime.KeyCapture = null;

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
                AiRuntime.KeyCapture = null;
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
            var cfg = AiRuntime.Config;
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.Preset) && cfg.Preset != "custom")
            {
                cfg.PresetKeys ??= new Dictionary<string, string>();
                cfg.PresetKeys[cfg.Preset] = (cfg.ApiKey ?? "").Trim();
            }
        }

        /// <summary>切换模型方案：自动填地址/模型，并带出这个方案上次保存的密钥。</summary>
        private void ApplyPreset(string id)
        {
            var cfg = AiRuntime.Config;
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

            AiRuntime.Log.Log("切换模型方案：" + id + " -> " + cfg.BaseUrl + " / " + cfg.Model, LogLevel.Info);
        }

        /// <summary>连上 dsh-connect-trio 的本机回环网关，并自动拉取它支持的模型名。</summary>
        private void ApplyTrioPreset()
        {
            var cfg = AiRuntime.Config;
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
                    AiRuntime.Log.Log("本机网关可得模型（" + models.Count + "）：" + string.Join(", ", models), LogLevel.Info);

                    _gatewayModels = models;

                    string pick = (AiRuntime.Config.Model ?? "").Trim();
                    if (pick.Length == 0 || !HasModel(models, pick))
                        pick = HasModel(models, "hy3") ? "hy3" : models[0];

                    AiRuntime.Config.Model = pick;
                    ApplyModelChoices();
                    _testResult = "网关有 " + models.Count + " 个模型，当前用「" + pick + "」，点「模型 Model」那行可切换，完整列表见 SMAPI 控制台";
                    _needRefresh = true;
                }
                catch (Exception ex)
                {
                    _testResult = "拉取模型失败（网关没在跑？）：" + ex.Message;
                    AiRuntime.Log.Log("拉取本机网关模型失败：" + ex.Message, LogLevel.Warn);
                }
            });
        }

        /// <summary>省积分模式：开启时压低各项开销，关闭时还原开启前的数值。</summary>
        private void ApplySaveMode(bool on)
        {
            var cfg = AiRuntime.Config;
            cfg.SaveMode = on;

            if (on)
            {
                _beforeSaveInt["MaxHistory"] = cfg.MaxHistory;
                _beforeSaveBool["UseLocalTutorial"] = cfg.UseLocalTutorial;
                _beforeSaveInt["MaxTokens"] = cfg.MaxTokens;
                _beforeSaveInt["MaxCodexChars"] = cfg.MaxCodexChars;
                _beforeSaveBool["WebSearch"] = cfg.WebSearch;

                cfg.MaxHistory = 3;
                cfg.UseLocalTutorial = false;
                cfg.MaxTokens = 2000;   // 推理模型的「思考」也吃这份额度，压到 800 会只剩思考、没有正文
                cfg.MaxCodexChars = Math.Max(400, cfg.MaxCodexChars / 2);
                cfg.WebSearch = false;

                _status = "省积分模式：上下文 3 轮 / 不注入教程 / 不联网 / 回答上限 2000 / 图鉴注入减半（关掉会还原原值）";
            }
            else if (_beforeSaveInt.Count > 0 || _beforeSaveBool.Count > 0)
            {
                if (_beforeSaveInt.TryGetValue("MaxHistory", out int h)) cfg.MaxHistory = h;
                if (_beforeSaveBool.TryGetValue("UseLocalTutorial", out bool u)) cfg.UseLocalTutorial = u;
                if (_beforeSaveInt.TryGetValue("MaxTokens", out int m)) cfg.MaxTokens = m;
                if (_beforeSaveInt.TryGetValue("MaxCodexChars", out int cx)) cfg.MaxCodexChars = cx;
                if (_beforeSaveBool.TryGetValue("WebSearch", out bool w)) cfg.WebSearch = w;

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
            AiRuntime.Hlp.WriteConfig(AiRuntime.Config);
            AiRuntime.ReloadTutorial();

            _snapshot = AiRuntime.Config.Clone();
            _saved = true;
            _status = "已保存到 config.json（当前设置已生效：" + (string.IsNullOrWhiteSpace(AiRuntime.Config.Model) ? "未填模型" : AiRuntime.Config.Model) + "）";
            Game1.addHUDMessage(new HUDMessage("InGameCodex：AI 设置已保存", 2));
            Game1.playSound("money");
        }

        private void ResetDefault()
        {
            ReleaseSubscriber();

            AiRuntime.Config = new AiConfig();
            AiRuntime.Hlp.WriteConfig(AiRuntime.Config);
            AiRuntime.ReloadTutorial();
            AiRuntime.KeyCapture = null;

            _rows.Clear();
            BuildRows();
            ApplyControlBounds();

            _snapshot = AiRuntime.Config.Clone();
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

            var cfg = AiRuntime.Config;
            _testing = true;
            _testResult = null;
            _status = "正在测试 " + cfg.BaseUrl + " …";

            AiRuntime.Log.Log(
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
                    AiRuntime.Log.Log("测试连接成功：" + Truncate(ok, 120), LogLevel.Info);
                }
                catch (Exception ex)
                {
                    string hint = Hint(ex.Message, cfg);
                    _testResult = "连接失败：" + Truncate(ex.Message, 220) + hint;
                    AiRuntime.Log.Log("测试连接失败：" + ex.Message + "  " + hint, LogLevel.Warn);
                }
                finally
                {
                    _testing = false;
                }
            });
        }

        /// <summary>根据错误内容给出人话提示。</summary>
        private static string Hint(string message, AiConfig cfg)
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
            AiRuntime.KeyCapture = null;
            ReleaseSubscriber();

            // 改过但没点「保存」就关掉 = 放弃这轮改动
            if (!_saved && _snapshot != null)
                AiRuntime.Config = _snapshot;

            Game1.playSound("bigDeSelect");
            Game1.activeClickableMenu = _returnMenu;

            // 回到图鉴面板且停在 AI 页时，把键盘焦点还给 AI 输入框（否则要再点一下输入框才能打字）
            if (_returnMenu is CodexMenu cm && cm.CurrentTab == 5)
                cm.AiSetFocus(true);
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
            int dir = AiRuntime.Config?.InvertScroll == true ? -direction : direction;

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
            x = DesignX(x);
            y = DesignY(y);
            base.performHoverAction(x, y);

            _hoverRow = -1;
            if (_viewRect.Contains(x, y))
            {
                int rel = y - _viewRect.Y + _scroll - 6;
                int idx = rel / RowH;
                if (idx >= 0 && idx < _rows.Count)
                    _hoverRow = idx;
            }

            _hoverBtn = -1;
            if (_saveRect.Contains(x, y)) _hoverBtn = 0;
            else if (_cancelRect.Contains(x, y)) _hoverBtn = 1;
            else if (_defaultRect.Contains(x, y)) _hoverBtn = 2;
            else if (_testRect.Contains(x, y)) _hoverBtn = 3;
        }

        public override void releaseLeftClick(int x, int y)
        {
            x = DesignX(x);
            y = DesignY(y);
            base.releaseLeftClick(x, y);
        }

        // ---------------- 绘制 ----------------

        public override void draw(SpriteBatch b)
        {
            // 按 1.0 设计尺寸排版，绘制时用缩放矩阵整体放大 Cx.S 倍（移植过来的旧界面不用逐处乘 S；不切渲染目标）
            Cx.ScaledDesign(b, DrawDesign);
            drawMouse(b);
        }

        private void DrawDesign(SpriteBatch b)
        {
            AiUi.Fill(b, new Rectangle(0, 0, Cx.DesignW, Cx.DesignH), new Color(0, 0, 0, 130));

            Game1.DrawBox(xPositionOnScreen, yPositionOnScreen, width, height);
            AiUi.Fill(b, new Rectangle(xPositionOnScreen + 8, yPositionOnScreen + 8, width - 16, height - 16), AiUi.PageBg);

            Utility.drawTextWithShadow(b, "AI 问答设置", Game1.dialogueFont,
                new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 16), AiUi.Green);

            b.DrawString(AiUi.Body, "改完立刻生效；点「保存」会写回 config.json   |   " + TutorialStore.Status,
                new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 44), AiUi.Dim);

            AiUi.Box(b, _viewRect, new Color(0x29, 0x24, 0x1B), new Color(0x44, 0x3C, 0x2E));

            AiUi.BeginClip(b, _viewRect);
            DrawRows(b);
            AiUi.EndClip(b);

            if (_maxScroll > 0)
            {
                var track = new Rectangle(_viewRect.Right - 4, _viewRect.Y, 6, _viewRect.Height);
                AiUi.Fill(b, track, new Color(0x44, 0x3C, 0x2E));
                int h = Math.Max(30, (int)(_viewRect.Height * (float)_viewRect.Height / _contentHeight));
                int ty = _viewRect.Y + (int)(_scroll / (float)_maxScroll * (_viewRect.Height - h));
                AiUi.Fill(b, new Rectangle(track.X, ty, track.Width, h), AiUi.Gold);
            }

            string status = _status + (_saved ? "" : "   [尚未保存]");
            var statusLines = AiUi.Wrap(status, AiUi.Body, width - 48);
            int sy = _viewRect.Bottom + 8;
            for (int i = 0; i < statusLines.Count && i < 2; i++)
            {
                b.DrawString(AiUi.Body, statusLines[i], new Vector2(xPositionOnScreen + 24, sy), _testing ? AiUi.Blue : AiUi.Gold);
                sy += 16;
            }

            Button(b, _saveRect, "保存", _hoverBtn == 0);
            Button(b, _cancelRect, "取消", _hoverBtn == 1);
            Button(b, _defaultRect, "恢复默认", _hoverBtn == 2);
            Button(b, _testRect, _testing ? "测试中…" : "测试连接", _hoverBtn == 3 && !_testing);

            if (_openDrop != null)
                DrawDropdown(b);

            base.draw(b);
        }

        private void DrawRows(SpriteBatch b)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                int y = _viewRect.Y + 6 + i * RowH - _scroll;
                if (y > _viewRect.Bottom || y + RowH < _viewRect.Y)
                    continue;

                bool hover = _hoverRow == i;
                AiUi.Fill(b, new Rectangle(_viewRect.X + 4, y + 2, _viewRect.Width - 16, RowH - 8), hover ? new Color(0x30, 0x2A, 0x20) : Color.Transparent);

                var labelColor = hover ? AiUi.Ink : AiUi.Dim;
                int maxLabel = row.Ctrl.X - (_viewRect.X + 24);
                string suffix = row.Suffix?.Invoke();
                var lines = AiUi.Wrap(row.Label + (string.IsNullOrEmpty(suffix) ? "" : " " + suffix), AiUi.Body, maxLabel);
                int ly = y + 12;
                foreach (string s in lines)
                {
                    b.DrawString(AiUi.Body, s, new Vector2(_viewRect.X + 20, ly), labelColor);
                    ly += AiUi.LineHeight(AiUi.Body);
                }

                var ctrl = new Rectangle(row.Ctrl.X, y + 8, row.Ctrl.Width, row.Ctrl.Height);
                int mx = DesignX(Game1.getMouseX()), my = DesignY(Game1.getMouseY());

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
                        AiUi.Box(b, ctrl, h ? AiUi.Hover : AiUi.PanelBg, on ? AiUi.Green : AiUi.Line);
                        Center(b, ctrl, on ? "开启" : "关闭", on ? AiUi.Green : AiUi.Dim);
                        break;
                    }

                    case Kind.Num:
                    {
                        int third = Math.Min(46, ctrl.Width / 3);
                        bool hl = new Rectangle(ctrl.X, ctrl.Y, third, ctrl.Height).Contains(mx, my);
                        bool hr = new Rectangle(ctrl.Right - third, ctrl.Y, third, ctrl.Height).Contains(mx, my);

                        AiUi.Box(b, ctrl, AiUi.PanelBg, AiUi.Line);
                        Center(b, new Rectangle(ctrl.X, ctrl.Y, third, ctrl.Height), "-", hl ? AiUi.Gold : AiUi.Ink);
                        Center(b, new Rectangle(ctrl.X + third, ctrl.Y, ctrl.Width - third * 2, ctrl.Height), row.Get() ?? "", AiUi.Gold);
                        Center(b, new Rectangle(ctrl.Right - third, ctrl.Y, third, ctrl.Height), "+", hr ? AiUi.Gold : AiUi.Ink);
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
                        AiUi.Box(b, ctrl, h ? AiUi.Hover : AiUi.PanelBg, h ? AiUi.Gold : AiUi.Line);
                        Center(b, ctrl, show + "  >", AiUi.Ink);
                        break;
                    }

                    case Kind.Key:
                    {
                        bool h = ctrl.Contains(mx, my) || row.PendingCapture;
                        AiUi.Box(b, ctrl, row.PendingCapture ? AiUi.Hover : (h ? AiUi.Hover : AiUi.PanelBg),
                            row.PendingCapture ? AiUi.Gold : (h ? AiUi.Gold : AiUi.Line));
                        Center(b, ctrl, row.PendingCapture ? "请按键…" : (row.Get() ?? "无"), row.PendingCapture ? AiUi.Gold : AiUi.Ink);
                        break;
                    }
                }
            }
        }

        private static void Center(SpriteBatch b, Rectangle r, string text, Color color)
        {
            float tw = AiUi.Measure(AiUi.Body, text);
            b.DrawString(AiUi.Body, text,
                new Vector2(r.X + (r.Width - tw) / 2, r.Y + (r.Height - AiUi.LineHeight(AiUi.Body)) / 2), color);
        }

        private void Button(SpriteBatch b, Rectangle r, string label, bool hover)
        {
            AiUi.Box(b, r, hover ? AiUi.Hover : AiUi.PanelBg, hover ? AiUi.Gold : AiUi.Line);
            float tw = AiUi.Measure(AiUi.Body, label);
            b.DrawString(AiUi.Body, label,
                new Vector2(r.X + (r.Width - tw) / 2, r.Y + (r.Height - AiUi.LineHeight(AiUi.Body)) / 2),
                hover ? AiUi.Gold : AiUi.Ink);
        }
    }
}
