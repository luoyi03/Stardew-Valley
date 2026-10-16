using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI.Ui
{
    public class BaseChatMenu : IClickableMenu
    {
        private class MsgBox
        {
            public ChatLine Line;
            public List<string> Lines;
            public Rectangle Bounds;
            public int PadX, PadY;

            /// <summary>附带动作按钮的位置（内容坐标系，没有动作时为 0 宽）。</summary>
            public Rectangle ActionBtn;
        }

        private class Chip
        {
            public Rectangle Bounds;
            public string Text;
        }

        protected readonly ChatSession Session;
        protected readonly string Title;
        protected string Subtitle = "";
        protected readonly List<string> QuickPrompts = new();

        protected TextBox Input;
        protected Rectangle ContentRect;
        protected Rectangle InputRect;
        protected int Scroll;
        protected int MaxScroll;

        private Rectangle _sendRect;
        private Rectangle _clearRect;
        private Rectangle _extraRect;
        private Rectangle _voiceRect;
        private Rectangle _settingsRect;
        private Rectangle _scrollTrack;
        private Rectangle _scrollThumb;

        private readonly List<MsgBox> _boxes = new();
        private readonly List<Chip> _chips = new();
        private int _layoutWidth = -1;
        private int _layoutVersion = -1;
        private int _contentHeight;

        private bool _stickBottom = true;
        private bool _scrolling;
        private int _scrollGrab;
        private string _lastInput = "";
        private int _tick;

        public BaseChatMenu(string title, ChatSession session)
            : base(0, 0, 100, 100, false)
        {
            Title = title;
            Session = session;
            Session.SystemPromptBuilder = () => BuildSystemPrompt(_lastInput);
            QuickPrompts.AddRange(GetQuickPrompts());

            Relayout();
            InitInput();

            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + 12, 40, 40),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);
        }

        /// <summary>本面板正在用的会话（「按住说话」要把识别结果送进这里）。</summary>
        public ChatSession Chat => Session;

        protected virtual string BuildSystemPrompt(string question) => "";

        protected virtual List<string> GetQuickPrompts() => new();

        protected virtual void SubmitPreset(string text)
        {
            Input.Text = "";
            _lastInput = text;
            _stickBottom = true;
            Session.Ask(text);
        }

        protected virtual string EmptyHint() => "在下面输入你的问题…";

        /// <summary>快捷话题按钮上显示的文字（子类可把内部标记换成人类可读的标签）。</summary>
        protected virtual string ChipLabel(string raw) => raw;

        /// <summary>供控制台命令直接发送一段话。</summary>
        public void SendText(string text)
        {
            if (Input != null)
                Input.Text = "";
            SubmitPreset(text);
        }

        /// <summary>第三个按钮的文字，返回 null 则不显示。</summary>
        protected virtual string ExtraButtonLabel() => null;

        protected virtual void OnExtraButton() { }

        /// <summary>
        /// 「说话」按钮的文字（返回 null 则不显示）。
        /// 伴侣面板用它做录音开关：聊天框占着键盘，按 V 打不出字，所以给个能点的入口。
        /// </summary>
        protected virtual string VoiceButtonLabel() => null;

        protected virtual void OnVoiceButton() { }

        // ---------------- 布局 ----------------

        // ---------------- 顶部标题区几何 ----------------
        // 标题用的是 dialogueFont，行高比小字大不少；原先标题 / 状态行 / 快捷按钮的 y 是写死的
        // （标题 +18、状态 +34、快捷按钮 = 内容区顶 - 40），中文版字体一变高就会互相压住。
        // 这里统一改成按字体实测行高往下排。

        /// <summary>标题行（dialogueFont）实际占的高度。</summary>
        private static int HeadLineH => (int)Math.Ceiling(Ui.Head.MeasureString("Hg").Y) + 4;

        /// <summary>标题行左上角 y。</summary>
        protected int HeaderTitleY => yPositionOnScreen + 14;

        /// <summary>标题下方小字行（状态 / 副标题）的 y。</summary>
        protected int HeaderSubY => HeaderTitleY + HeadLineH + 2;

        /// <summary>快捷按钮行的 y。</summary>
        protected int HeaderChipsY => HeaderSubY + Ui.LineHeight(Ui.Body) + 4;

        protected void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            width = Math.Max(420, Math.Min(uiW - 24, 1320));
            height = Math.Max(360, Math.Min(uiH - 24, 1000));
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            int pad = 20;
            int chipsH = QuickPrompts.Count > 0 ? 46 : 0;
            int top = chipsH > 0
                ? HeaderChipsY + 34                      // 快捷按钮（高 30）+ 4px 间隙
                : HeaderSubY + Ui.LineHeight(Ui.Body) + 8;

            int inputH = 48;
            int inputY = yPositionOnScreen + height - 24 - inputH;

            bool hasExtra = ExtraButtonLabel() != null;
            bool hasVoice = VoiceButtonLabel() != null;
            int buttons = 2 + (hasExtra ? 1 : 0) + (hasVoice ? 1 : 0);

            ContentRect = new Rectangle(
                xPositionOnScreen + pad,
                top,
                width - pad * 2,
                Math.Max(80, inputY - 20 - top));

            InputRect = new Rectangle(
                xPositionOnScreen + pad,
                inputY,
                Math.Max(140, width - pad * 2 - buttons * 110),
                inputH);

            _settingsRect = new Rectangle(xPositionOnScreen + width - 44 - 96, yPositionOnScreen + 14, 88, 34);
            _sendRect = new Rectangle(InputRect.Right + 10, inputY, 100, inputH);
            _clearRect = new Rectangle(_sendRect.Right + 10, inputY, 100, inputH);

            int nextX = _clearRect.Right + 10;
            _voiceRect = new Rectangle(nextX, inputY, hasVoice ? 132 : 0, inputH);

            if (hasVoice)
                nextX = _voiceRect.Right + 10;

            _extraRect = new Rectangle(nextX, inputY, hasExtra ? 100 : 0, inputH);

            int trackX = ContentRect.Right + 6;
            _scrollTrack = new Rectangle(trackX, ContentRect.Y, 8, ContentRect.Height);
            _scrollThumb = new Rectangle(trackX, ContentRect.Y, 8, 40);

            _layoutWidth = -1;
        }

        private void InitInput()
        {
            Texture2D tex;
            try
            {
                tex = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
            }
            catch
            {
                tex = null;
            }

            Input = new TextBox(tex, null, Game1.smallFont, Game1.textColor);
            Input.X = InputRect.X;
            Input.Y = InputRect.Y;
            Input.Width = InputRect.Width;
            Input.Height = InputRect.Height;
            Input.Selected = true;
            Input.Text = "";
            Input.OnEnterPressed += _ => Submit();

            Game1.keyboardDispatcher.Subscriber = Input;
        }

        // ---------------- 交互 ----------------

        protected void Submit()
        {
            string text = (Input?.Text ?? "").Trim();
            if (text.Length == 0)
                return;

            if (Input != null)
                Input.Text = "";

            _lastInput = text;
            _stickBottom = true;
            Session.Ask(text);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (key == Keys.Enter && Input != null && Input.Selected)
            {
                Game1.playSound("coin");
                Submit();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (_settingsRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                ModEntry.OpenSettings(this);
                return;
            }

            foreach (var chip in _chips)
            {
                if (chip.Bounds.Contains(x, y))
                {
                    Game1.playSound("smallSelect");
                    SubmitPreset(chip.Text);
                    RestoreFocus();
                    return;
                }
            }

            if (_sendRect.Contains(x, y))
            {
                if (Session.Busy)
                {
                    Session.Stop();
                    Game1.playSound("bigDeSelect");
                }
                else
                {
                    Game1.playSound("bigSelect");
                    Submit();
                }
                RestoreFocus();
                return;
            }

            if (_clearRect.Contains(x, y))
            {
                Session.Clear();
                OnCleared();
                Game1.playSound("trashcan");
                RestoreFocus();
                return;
            }

            if (VoiceButtonLabel() != null && _voiceRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                OnVoiceButton();
                RestoreFocus();
                return;
            }

            if (ExtraButtonLabel() != null && _extraRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                OnExtraButton();
                return;
            }

            foreach (var box in _boxes)
            {
                if (string.IsNullOrWhiteSpace(box.Line.Action))
                    continue;

                if (ActionScreenRect(box).Contains(x, y))
                {
                    Game1.playSound("coin");
                    OnLineAction(box.Line);
                    RestoreFocus();
                    return;
                }
            }

            if (MaxScroll > 0 && _scrollTrack.Contains(x, y))
            {
                _scrolling = true;
                _scrollGrab = y - _scrollThumb.Y;
                UpdateScrollByThumb(y);
                return;
            }

            RestoreFocus();
        }

        protected virtual void OnCleared() { }

        public override void releaseLeftClick(int x, int y)
        {
            _scrolling = false;
            base.releaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int step = 60;
            if (ModEntry.Config != null && ModEntry.Config.InvertScroll)
                step = -step;

            Scroll += direction * step;
            Scroll = Math.Clamp(Scroll, 0, MaxScroll);
            _stickBottom = Scroll >= MaxScroll;
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);

            if (Input != null && Game1.activeClickableMenu == this)
                Input.Selected = true;

            if (_scrolling && MaxScroll > 0)
                UpdateScrollByThumb(y);
        }

        private void UpdateScrollByThumb(int y)
        {
            int ty = Math.Clamp(y - _scrollGrab, ContentRect.Y, ContentRect.Bottom - _scrollThumb.Height);
            int max = ContentRect.Height - _scrollThumb.Height;
            Scroll = max > 0 ? (int)((ty - ContentRect.Y) / (float)max * MaxScroll) : 0;
            Scroll = Math.Clamp(Scroll, 0, MaxScroll);
            _stickBottom = Scroll >= MaxScroll;
        }

        public override void update(GameTime time)
        {
            base.update(time);

            _tick++;
            Session.PollUI();
            Input?.Update();

            // 从设置界面返回后重新接管键盘输入
            if (Input != null && Game1.activeClickableMenu == this && Game1.keyboardDispatcher?.Subscriber != Input)
                Game1.keyboardDispatcher.Subscriber = Input;

            EnsureLayout();
        }

        protected void RestoreFocus()
        {
            if (Input == null)
                return;
            Input.Selected = true;
            Game1.keyboardDispatcher.Subscriber = Input;
        }

        protected override void cleanupBeforeExit()
        {
            try
            {
                if (Game1.keyboardDispatcher?.Subscriber == Input)
                    Game1.keyboardDispatcher.Subscriber = null;
                if (Input != null)
                    Input.Selected = false;
            }
            catch { /* ignore */ }

            base.cleanupBeforeExit();
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Relayout();

            if (upperRightCloseButton != null)
                upperRightCloseButton.bounds = new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + 12, 40, 40);
        }

        // ---------------- 排版 ----------------

        private void EnsureLayout()
        {
            if (Input != null)
            {
                Input.X = InputRect.X;
                Input.Y = InputRect.Y;
                Input.Width = InputRect.Width;
            }

            int w = ContentRect.Width;
            if (_layoutWidth == w && _layoutVersion == Session.Version)
                return;

            _layoutWidth = w;
            _layoutVersion = Session.Version;

            var font = Ui.Body;
            int lh = Ui.LineHeight(font);
            const int padX = 14;
            const int padY = 10;
            int maxW = Math.Max(120, (int)(w * 0.88f));

            _boxes.Clear();
            int y = 0;

            foreach (var line in Session.Lines)
            {
                var lines = Ui.Wrap(DisplayText(line), font, maxW - padX * 2);
                float widest = 0f;
                foreach (string s in lines)
                    widest = Math.Max(widest, Ui.Measure(font, s));

                bool plain = line.Role == MsgRole.Info;
                bool hasAction = !string.IsNullOrWhiteSpace(line.Action);
                int bw = plain ? w : Math.Clamp((int)(widest + padX * 2), 90, w);
                int actionH = hasAction ? 40 : 0;
                int bh = (lines.Count + (plain ? 0 : 1)) * lh + padY * 2 + actionH;
                int bx = plain ? 0 : (line.Role == MsgRole.User ? w - bw : 0);

                var box = new MsgBox
                {
                    Line = line,
                    Lines = lines,
                    Bounds = new Rectangle(bx, y, bw, bh),
                    PadX = padX,
                    PadY = padY
                };

                if (hasAction)
                {
                    string label = "获取：" + line.Action;
                    int abw = Math.Min((int)Ui.Measure(font, label) + 28, bw - padX * 2);
                    box.ActionBtn = new Rectangle(bx + padX, y + bh - 34, Math.Max(80, abw), 28);
                }

                _boxes.Add(box);

                y += bh + 14;
            }

            _contentHeight = Math.Max(1, y + 8);
            MaxScroll = Math.Max(0, _contentHeight - ContentRect.Height);

            if (_stickBottom)
                Scroll = MaxScroll;
            else
                Scroll = Math.Clamp(Scroll, 0, MaxScroll);
        }

        // ---------------- 绘制 ----------------

        public override void draw(SpriteBatch b)
        {
            EnsureLayout();

            Ui.Fill(b, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), new Color(0, 0, 0, 120));

            Game1.DrawBox(xPositionOnScreen, yPositionOnScreen, width, height);
            Ui.Fill(b, new Rectangle(xPositionOnScreen + 8, yPositionOnScreen + 8, width - 16, height - 16), Ui.PageBg);
            Ui.Fill(b, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, 1), Ui.Line);
            Ui.Fill(b, new Rectangle(xPositionOnScreen, yPositionOnScreen + height - 1, width, 1), Ui.Line);

            DrawHeader(b);
            DrawChips(b);

            Ui.Box(b, ContentRect, new Color(0x29, 0x24, 0x1B), new Color(0x44, 0x3C, 0x2E));
            DrawMessages(b);

            if (MaxScroll > 0)
                DrawScroll(b);

            DrawBottom(b);

            base.draw(b);
            drawMouse(b);
        }

        protected virtual void DrawHeader(SpriteBatch b)
        {
            int x = xPositionOnScreen + 24;
            int y = HeaderTitleY;

            Utility.drawTextWithShadow(b, Title, Game1.dialogueFont, new Vector2(x, y), Ui.Green);

            string status = StatusText();
            string tail = ModEntry.Config?.Model ?? "";
            if (!string.IsNullOrEmpty(tail))
                status += (status.Length > 0 ? "  |  " : "") + tail;

            b.DrawString(Ui.Body, status, new Vector2(x, HeaderSubY), Ui.Dim);

            if (!string.IsNullOrEmpty(Subtitle))
            {
                float w = Ui.Measure(Ui.Body, Subtitle);
                b.DrawString(Ui.Body, Subtitle, new Vector2(xPositionOnScreen + width - 24 - w, HeaderSubY), Ui.Dim);
            }

            bool hover = _settingsRect.Contains(Game1.getMouseX(), Game1.getMouseY());
            Ui.Box(b, _settingsRect, hover ? Ui.Hover : Ui.PanelBg, hover ? Ui.Gold : Ui.Line);
            DrawButton(b, _settingsRect, "设置", true);
        }

        protected virtual string StatusText()
        {
            if (Session.Busy)
                return "正在思考" + new string('.', (_tick / 20) % 4);
            return "就绪（回车发送 / Esc 关闭）";
        }

        private void DrawChips(SpriteBatch b)
        {
            _chips.Clear();
            if (QuickPrompts.Count == 0)
                return;

            int x = ContentRect.X;
            int y = HeaderChipsY;

            foreach (string raw in QuickPrompts)
            {
                string p = ChipLabel(raw);
                int w = (int)Ui.Measure(Ui.Body, p) + 24;
                if (x + w > ContentRect.Right)
                    break;

                var rect = new Rectangle(x, y, w, 30);
                bool hover = rect.Contains(Game1.getMouseX(), Game1.getMouseY());
                Ui.Box(b, rect, hover ? Ui.Hover : Ui.PanelBg, hover ? Ui.Gold : Ui.Line);
                b.DrawString(Ui.Body, p, new Vector2(x + 12, y + 7), hover ? Ui.Gold : Ui.Dim);

                _chips.Add(new Chip { Bounds = rect, Text = p });
                x += w + 8;
            }
        }

        private void DrawMessages(SpriteBatch b)
        {
            if (_boxes.Count == 0)
            {
                string hint = EmptyHint();
                foreach (var t in new[] { hint })
                {
                    float tw = Ui.Measure(Ui.Body, t);
                    b.DrawString(Ui.Body, t, new Vector2(ContentRect.X + (ContentRect.Width - tw) / 2, ContentRect.Y + 40), Ui.Dim);
                }
                return;
            }

            Ui.BeginClip(b, ContentRect);

            var font = Ui.Body;
            int lh = Ui.LineHeight(font);

            foreach (var box in _boxes)
            {
                int top = ContentRect.Y + box.Bounds.Y - Scroll;
                if (top > ContentRect.Bottom + 40 || top + box.Bounds.Height < ContentRect.Y - 40)
                    continue;

                var line = box.Line;
                Rectangle r = new(ContentRect.X + box.Bounds.X, top, box.Bounds.Width, box.Bounds.Height);

                switch (line.Role)
                {
                    case MsgRole.User:
                        Ui.Box(b, r, Ui.PanelBg, Ui.Gold);
                        break;
                    case MsgRole.Assistant:
                        Ui.Box(b, r, Ui.PanelBg2, Ui.Blue);
                        break;
                    case MsgRole.Error:
                        Ui.Box(b, r, new Color(0x44, 0x2A, 0x24), Ui.Red);
                        break;
                    default:
                        break;
                }

                Color nameColor = line.Role switch
                {
                    MsgRole.User => Ui.Gold,
                    MsgRole.Assistant => Ui.Blue,
                    MsgRole.Error => Ui.Red,
                    _ => Ui.Dim
                };

                b.DrawString(font, line.Name, new Vector2(r.X + box.PadX, r.Y + box.PadY), nameColor);

                int ty = r.Y + box.PadY + (line.Role == MsgRole.Info ? 0 : lh);
                Color textColor = line.Role == MsgRole.Error ? Ui.Red : Ui.Ink;

                foreach (string s in box.Lines)
                {
                    b.DrawString(font, s, new Vector2(r.X + box.PadX, ty), textColor);
                    ty += lh;
                }

                if (Session.Busy && ReferenceEquals(line, Session.Lines.LastOrDefault()) && line.Role == MsgRole.Assistant && (_tick / 15) % 2 == 0)
                    b.DrawString(font, "|", new Vector2(r.X + box.PadX + Ui.Measure(font, box.Lines.LastOrDefault() ?? ""), ty - lh), Ui.Blue);

                if (!string.IsNullOrWhiteSpace(line.Action))
                    DrawActionButton(b, box);
            }

            Ui.EndClip(b);
        }

        /// <summary>流式输出时，[[GIVE:xxx]] 标记还没收完就先不显示出来。</summary>
        private static string DisplayText(ChatLine line)
        {
            string t = line.Text ?? "";
            if (t.Length == 0)
                return t;

            int i = t.IndexOf("[[", StringComparison.Ordinal);
            if (i >= 0 && t.IndexOf("GIVE", i, StringComparison.OrdinalIgnoreCase) >= 0)
                return t.Substring(0, i).TrimEnd();

            return t;
        }

        /// <summary>把内容坐标系的按钮换算成屏幕坐标。</summary>
        private Rectangle ActionScreenRect(MsgBox box)
        {
            var ab = box.ActionBtn;
            return new Rectangle(ContentRect.X + ab.X, ContentRect.Y + ab.Y - Scroll, ab.Width, ab.Height);
        }

        private void DrawActionButton(SpriteBatch b, MsgBox box)
        {
            var rect = ActionScreenRect(box);
            bool hover = rect.Contains(Game1.getMouseX(), Game1.getMouseY());

            Ui.Box(b, rect, hover ? new Color(0x6E, 0xA2, 0x54) : new Color(0x45, 0x66, 0x38), Ui.Green);
            b.DrawString(Ui.Body, "获取：" + box.Line.Action,
                new Vector2(rect.X + 10, rect.Y + (rect.Height - Ui.LineHeight(Ui.Body)) / 2), Ui.Ink);
        }

        /// <summary>点了回答里附带的动作按钮。</summary>
        protected virtual void OnLineAction(ChatLine line) { }

        private void DrawScroll(SpriteBatch b)
        {
            Ui.Fill(b, _scrollTrack, new Color(0x44, 0x3C, 0x2E));

            int h = Math.Max(30, (int)(ContentRect.Height * (float)ContentRect.Height / _contentHeight));
            int max = ContentRect.Height - h;
            int ty = ContentRect.Y + (max > 0 && MaxScroll > 0 ? (int)(Scroll / (float)MaxScroll * max) : 0);
            _scrollThumb = new Rectangle(_scrollTrack.X, ty, _scrollTrack.Width, h);

            Ui.Fill(b, _scrollThumb, Ui.Gold);
        }

        private void DrawBottom(SpriteBatch b)
        {
            Input.Draw(b, false);

            if (Input.Text.Length == 0)
                b.DrawString(Ui.Body, EmptyHint(), new Vector2(InputRect.X + 12, InputRect.Y + 15), Ui.Dim);

            DrawButton(b, _sendRect, Session.Busy ? "停止" : "发送", !Session.Busy);
            DrawButton(b, _clearRect, "清空", true);

            string voice = VoiceButtonLabel();
            if (voice != null)
                DrawButton(b, _voiceRect, voice, true);

            string extra = ExtraButtonLabel();
            if (extra != null)
                DrawButton(b, _extraRect, extra, true);
        }

        private void DrawButton(SpriteBatch b, Rectangle r, string label, bool enabled)
        {
            bool hover = r.Contains(Game1.getMouseX(), Game1.getMouseY()) && enabled;
            Ui.Box(b, r, hover ? Ui.Hover : Ui.PanelBg, enabled ? (hover ? Ui.Gold : Ui.Line) : new Color(0x41, 0x3A, 0x2C));

            float tw = Ui.Measure(Ui.Body, label);
            b.DrawString(Ui.Body, label,
                new Vector2(r.X + (r.Width - tw) / 2, r.Y + (r.Height - Ui.LineHeight(Ui.Body)) / 2),
                enabled ? (hover ? Ui.Gold : Ui.Ink) : Ui.Dim);
        }
    }
}
