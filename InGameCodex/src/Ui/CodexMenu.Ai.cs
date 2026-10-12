using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using InGameCodex.Ai;
using StardewValley;
using StardewValley.Menus;

namespace InGameCodex.Ui
{
    public partial class CodexMenu
    {
        private class AiLinkBtn
        {
            public Rectangle Rect;
            public CodexAiLink Link;
        }

        private class AiMsgBox
        {
            public ChatLine Line;
            public List<string> Lines;
            public Rectangle Bounds;
            public int PadX, PadY;

            /// <summary>附带动作按钮的位置（内容坐标系，没有动作时为 0 宽）。</summary>
            public Rectangle ActionBtn;

            /// <summary>回答下方可点击跳转的图鉴条目按钮（内容坐标系）。</summary>
            public List<AiLinkBtn> LinkBtns;
        }

        private class AiChip
        {
            public Rectangle Bounds;
            public string Text;
        }

        protected ChatSession AiSession;
        protected string AiTitle = "";
        protected string AiSubtitle = "";
        protected readonly List<string> AiQuickPrompts = new();

        private bool _aiInited;
        private bool _aiInputWanted = true;   // 输入框是否应聚焦（点击驱动，不看鼠标位置；见 AiKeepInputFocus）
        private int _aiW;     // AI 页设计宽高（1.0 倍排版坐标系，绘制时整体放大 Cx.S 倍）
        private int _aiH;
        private Rectangle _aiDest;

        /// <summary>面板把屏幕坐标交进来，这里换算成 AI 页的设计坐标（界面坐标 = (屏幕 - 内容区原点) / Cx.S）。</summary>
        private int AiDesignX(int screenX) => (int)((screenX - _aiDest.X) / Cx.S);

        private int AiDesignY(int screenY) => (int)((screenY - _aiDest.Y) / Cx.S);

        internal void AiLeftClickScreen(int x, int y) => AiLeftClick(AiDesignX(x), AiDesignY(y));

        internal void AiHoverScreen(int x, int y)
        {
            AiHover(AiDesignX(x), AiDesignY(y));
            base.performHoverAction(x, y);   // 基类（关闭按钮等）用屏幕坐标
        }

        internal void AiHeldScreen(int x, int y)
        {
            if (_aiScrolling && AiMaxScroll > 0)
                AiUpdateThumb(AiDesignY(y));
        }

        internal void AiReleaseScreen(int x, int y)
        {
            AiRelease(AiDesignX(x), AiDesignY(y));
            base.releaseLeftClick(x, y);
        }

        /// <summary>AI 页里的鼠标坐标（设计坐标 = (屏幕 - 贴图位置) / Cx.S）。</summary>
        private int AiMX => AiDesignX(Game1.getMouseX());
        private int AiMY => AiDesignY(Game1.getMouseY());

        protected TextBox AiInput;
        protected Rectangle AiContentRect;
        protected Rectangle AiInputRect;
        protected int AiScroll;
        protected int AiMaxScroll;

        private Rectangle _aiSendRect;
        private Rectangle _aiClearRect;
        private Rectangle _aiExtraRect;
        private Rectangle _aiSettingsRect;
        private Rectangle _aiScrollTrack;
        private Rectangle _aiScrollThumb;

        private readonly List<AiMsgBox> _aiBoxes = new();
        private readonly List<AiChip> _aiChips = new();
        private int _aiLayoutWidth = -1;
        private int _aiLayoutVersion = -1;
        private int _aiContentHeight;

        private bool _aiStickBottom = true;
        private bool _aiScrolling;
        private int _aiScrollGrab;
        private string _aiLastInput = "";

        /// <summary>本次提问本地图鉴/教程有没有给出资料（一条都没给的话，才值得联网去查）。</summary>
        private bool _aiLocalHit;

        /// <summary>本次提问命中的图鉴条目（挂到回答气泡下方，可点击跳转）。</summary>
        private List<CodexAiLink> _aiPendingLinks;
        private int _aiTick;

        // 顶部三行（标题 / 状态 / 快捷按钮）的 y —— 按字体实测行高算，见 AiRelayout
        private int _aiTitleY;
        private int _aiStatusY;
        private int _aiChipsY;

        /// <summary>第一次进入 AI 标签页时初始化：会话、快捷提问、布局、输入框。</summary>
        internal void AiInit()
        {
            if (_aiInited)
                return;

            _aiInited = true;
            AiTitle = "星露谷 AI 助手";
            AiSession = AiRuntime.EnsureSession();
            AiSubtitle = TutorialStore.Status;
            AiSession.SystemPromptBuilder = () => BuildSystemPrompt(_aiLastInput);
            AiSession.WebContextNeeded = AiShouldWebSearch;
            AiSession.WebContextBuilder = AiBuildWebContextAsync;

            if (AiQuickPrompts.Count == 0)
                AiQuickPrompts.AddRange(AiGetQuickPrompts());

            AiRelayout();
            AiInitInput();

            if (AiSession.Lines.Count == 0)
                AiSession.Say(MsgRole.Info, "提示", "可以问任何关于星露谷的问题：教程、攻略、物品用途、赚钱路线……");
        }

        internal ChatSession AiChatSession => AiSession;

        /// <summary>AI 输入框是否正在接收键盘（与其它页的搜索框一致：聚焦时接管键盘）。</summary>
        internal bool AiInputFocused => AiInput != null && AiInput.Selected;

        /// <summary>切换 AI 输入框的键盘焦点。关闭焦点后按键回到面板（Tab 切页 / Esc 关面板）。</summary>
        internal void AiSetFocus(bool on)
        {
            _aiInputWanted = on;
            if (AiInput == null)
                return;
            AiInput.Selected = on;
            if (on)
            {
                Game1.keyboardDispatcher.Subscriber = AiInput;
            }
            else
            {
                if (Game1.keyboardDispatcher?.Subscriber == AiInput)
                    Game1.keyboardDispatcher.Subscriber = null;
                ImeAnchor.Blur();
            }
        }

        private string BuildSystemPrompt(string question)
        {
            var cfg = AiRuntime.Config;
            var sb = new StringBuilder();
            bool localHit = false;   // 本地图鉴/教程这次到底给没给出资料，没给才有必要联网

            sb.Append("你是《星露谷物语》的资深攻略助手，用简体中文回答玩家的问题。\n\n");
            sb.Append("回答要求：\n");
            sb.Append("- 先给结论，再给可执行步骤，尽量控制在 300 字以内；复杂问题可以分点。\n");
            sb.Append("- 涉及数值（价格/天气/掉落）要尽量准确，不确定就明说「不确定」。\n");
            sb.Append("- 不要编造模组名、版本号或不存在的内容。\n");
            sb.Append("- 玩家可能打错字或用简称（例如「依制撒水器」指的就是「铱制洒水器」，「抚摸机」指的是「自动抚摸机」）：先在心里判断他真正想问的物品/人物/事件，按正确的理解回答，绝对不要因为字写错就说看不懂、或者不回答。\n");
            sb.Append("- 你本身就懂《星露谷物语》：下面给的本地资料只是用来校准数值的参考，命中不到也很正常。无论有没有资料，都必须凭自己的知识把问题回答完整；禁止说「本地资料里没有」「教程里没写所以无法回答」这类推脱的话。\n");
            if (cfg != null && cfg.NoMarkdown)
                sb.Append("- 不要使用 Markdown 语法（#、*、表格符号都不要用），用「1. 2.」或短句分行即可。\n");
            if (cfg != null && !string.IsNullOrWhiteSpace(cfg.ExtraSystemPrompt))
                sb.Append("- ").Append(cfg.ExtraSystemPrompt.Trim()).Append("\n");

            if (cfg == null || cfg.UseCodexData)
            {
                CodexAiResult codex = CodexAiData.Build(question, cfg?.MaxCodexChars ?? 2000);
                _aiPendingLinks = codex.Links.Count > 0 ? codex.Links : null;
                if (!string.IsNullOrWhiteSpace(codex.Text))
                {
                    sb.Append(codex.Text);
                    localHit = true;
                }
            }
            else
            {
                _aiPendingLinks = null;
            }

            if (cfg == null || cfg.AiCanGiveItems)
            {
                sb.Append("\n【直接给物品的能力（这是 Mod 给你的真实能力，不是比喻）】\n");
                sb.Append("当玩家明确索要某个物品时，你**可以真的把物品放进他的背包**：在回答的最末尾另起一行，只输出这样一行标记：[[GIVE:物品中文名]]\n");
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
                string reference = TutorialStore.Retrieve(question ?? "", cfg?.MaxTutorialChars ?? 2500);
                if (!string.IsNullOrWhiteSpace(reference))
                {
                    sb.Append("\n【本地教程参考片段（数值上优先采信；它没写到的部分照样用你自己的知识回答）】\n");
                    sb.Append(reference).Append("\n");
                    localHit = true;
                }
            }

            _aiLocalHit = localHit;

            return sb.ToString();
        }

        /// <summary>要不要为这次提问联网查资料：开了联网，而且本地图鉴/教程一条资料都没给出来。</summary>
        private bool AiShouldWebSearch(string question)
        {
            var cfg = AiRuntime.Config;
            if (cfg == null || !cfg.WebSearch)
                return false;

            return !_aiLocalHit && !string.IsNullOrWhiteSpace(question);
        }

        /// <summary>后台线程：去网上现查一次（AnySearch），把网页要点拼成要追加到系统提示的资料段。</summary>
        private static Task<string> AiBuildWebContextAsync(string question)
        {
            var cfg = AiRuntime.Config;
            if (cfg == null || !cfg.WebSearch || string.IsNullOrWhiteSpace(question))
                return Task.FromResult("");

            string q = question.Trim();
            if (q.IndexOf("星露谷", StringComparison.Ordinal) < 0)
                q = "星露谷物语 " + q;   // 带上游戏名，搜索命中率明显更高

            return WebSearch.FetchAsync(q, cfg.WebSearchMaxResults, cfg.WebSearchKey, cfg.MaxWebChars, CancellationToken.None);
        }

        private List<string> AiGetQuickPrompts()
        {
            var list = new List<string>();
            if (AiRuntime.Config?.QuickPrompts != null)
                list.AddRange(AiRuntime.Config.QuickPrompts);
            return list;
        }

        protected virtual void AiSubmitPreset(string text)
        {
            AiInput.Text = "";
            _aiLastInput = text;
            _aiStickBottom = true;
            AiSession.Ask(text);
        }

        private string EmptyHint() => "输入你的问题，回车发送…";

        /// <summary>快捷话题按钮上显示的文字（子类可把内部标记换成人类可读的标签）。</summary>
        protected virtual string AiChipLabel(string raw) => raw;

        /// <summary>供控制台命令直接发送一段话。</summary>
        public void AiSendText(string text)
        {
            if (AiInput != null)
                AiInput.Text = "";
            AiSubmitPreset(text);
        }

        /// <summary>第三个按钮的文字，返回 null 则不显示。</summary>
        protected virtual string AiExtraLabel() => null;

        protected virtual void AiOnExtra() { }

        // ---------------- 布局 ----------------

        /// <summary>一行文字实际占的高度（按字体实测：中文版 dialogueFont 的行高比原先写死的行距大得多）。</summary>
        private static int AiTextH(SpriteFont f) => (int)Math.Ceiling(f.MeasureString("Hg").Y) + 4;

        /// <summary>AI 页布局（设计坐标：整页按 1.0 排版，AiDraw 时再放大贴进内容区）。</summary>
        protected void AiRelayout()
        {
            // 面板内容区每帧都可能变（窗口缩放 / 视口变化），这里始终跟随它
            _aiDest = _contentRect.Width > 0 && _contentRect.Height > 0
                ? _contentRect
                : new Rectangle(0, 0, Math.Max(240, Cx.DesignW - 40), Math.Max(180, Cx.DesignH - 40));

            // 设计尺寸只写进 AI 页自己的字段：IClickableMenu 的 width/height/坐标是面板外框在用的，不能动
            _aiW = Math.Max(420, (int)Math.Round(_aiDest.Width / Cx.S));
            _aiH = Math.Max(300, (int)Math.Round(_aiDest.Height / Cx.S));

            int pad = 20;
            int chipsH = AiQuickPrompts.Count > 0 ? 46 : 0;

            // 顶部三行按实测行高往下排：原先写死的 18 / 52 在中文行高下会让大字标题压住状态行
            _aiTitleY = 14;
            _aiStatusY = _aiTitleY + AiTextH(AiUi.Head) + 2;
            _aiChipsY = _aiStatusY + AiTextH(AiUi.Body) + 4;

            int top = chipsH > 0
                ? _aiChipsY + 34                        // 快捷按钮（高 30）+ 4px 间隙
                : _aiStatusY + AiTextH(AiUi.Body) + 8;

            int inputH = 48;
            int inputY = _aiH - 24 - inputH;
            int buttons = AiExtraLabel() != null ? 3 : 2;

            AiContentRect = new Rectangle(
                pad,
                top,
                _aiW - pad * 2,
                Math.Max(80, inputY - 20 - top));

            AiInputRect = new Rectangle(
                pad,
                inputY,
                Math.Max(140, _aiW - pad * 2 - buttons * 110),
                inputH);

            _aiSettingsRect = new Rectangle(_aiW - 44 - 96, 14, 88, 34);
            _aiSendRect = new Rectangle(AiInputRect.Right + 10, inputY, 100, inputH);
            _aiClearRect = new Rectangle(_aiSendRect.Right + 10, inputY, 100, inputH);
            _aiExtraRect = new Rectangle(_aiClearRect.Right + 10, inputY, 100, inputH);

            int trackX = AiContentRect.Right + 6;
            _aiScrollTrack = new Rectangle(trackX, AiContentRect.Y, 8, AiContentRect.Height);
            _aiScrollThumb = new Rectangle(trackX, AiContentRect.Y, 8, 40);

            _aiLayoutWidth = -1;
        }

        private void AiInitInput()
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

            AiInput = new TextBox(tex, null, Game1.smallFont, Game1.textColor);
            AiInput.X = AiInputRect.X;
            AiInput.Y = AiInputRect.Y;
            AiInput.Width = AiInputRect.Width;
            AiInput.Height = AiInputRect.Height;
            _aiInputWanted = true;
            AiInput.Selected = true;
            AiInput.Text = "";
            AiInput.OnEnterPressed += _ => AiSubmit();

            Game1.keyboardDispatcher.Subscriber = AiInput;
        }

        // ---------------- 交互 ----------------

        protected void AiSubmit()
        {
            string text = (AiInput?.Text ?? "").Trim();
            if (text.Length == 0)
                return;

            if (AiInput != null)
                AiInput.Text = "";

            _aiLastInput = text;
            _aiStickBottom = true;
            AiSession.Ask(text);
        }

        internal void AiKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (key == Keys.Enter && AiInput != null && AiInput.Selected)
            {
                Game1.playSound("coin");
                AiSubmit();
                return;
            }

            base.receiveKeyPress(key);
        }

        internal void AiLeftClick(int x, int y)
        {
            if (_aiSettingsRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                AiRuntime.OpenSettings();
                return;
            }

            foreach (var chip in _aiChips)
            {
                if (chip.Bounds.Contains(x, y))
                {
                    Game1.playSound("smallSelect");
                    AiSubmitPreset(chip.Text);
                    AiRestoreFocus();
                    return;
                }
            }

            if (_aiSendRect.Contains(x, y))
            {
                if (AiSession.Busy)
                {
                    AiSession.Stop();
                    Game1.playSound("bigDeSelect");
                }
                else
                {
                    Game1.playSound("bigSelect");
                    AiSubmit();
                }
                AiRestoreFocus();
                return;
            }

            if (_aiClearRect.Contains(x, y))
            {
                AiSession.Clear();
                AiOnCleared();
                Game1.playSound("trashcan");
                AiRestoreFocus();
                return;
            }

            if (AiExtraLabel() != null && _aiExtraRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                AiOnExtra();
                return;
            }

            foreach (var box in _aiBoxes)
            {
                if (box.LinkBtns != null)
                {
                    foreach (AiLinkBtn lb in box.LinkBtns)
                    {
                        if (AiLinkRect(box, lb).Contains(x, y))
                        {
                            Game1.playSound("smallSelect");
                            AiOnLink(lb.Link);
                            return;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(box.Line.Action))
                    continue;

                if (AiActionRect(box).Contains(x, y))
                {
                    Game1.playSound("coin");
                    AiOnLineAction(box.Line);
                    AiRestoreFocus();
                    return;
                }
            }

            if (AiMaxScroll > 0 && _aiScrollTrack.Contains(x, y))
            {
                _aiScrolling = true;
                _aiScrollGrab = y - _aiScrollThumb.Y;
                AiUpdateThumb(y);
                return;
            }

            if (AiInputRect.Contains(x, y))
            {
                AiSetFocus(true);   // 点输入框 → 明确回到输入框（此前靠 else 分支兜，容易一失焦就打不了字）
                return;
            }

            // 点消息区（含图鉴链接）→ 输入框失焦，恢复面板导航键；点别处 → 回到输入框
            if (AiContentRect.Contains(x, y))
                AiSetFocus(false);
            else
                AiRestoreFocus();
        }

        protected virtual void AiOnCleared() { }

        internal void AiRelease(int x, int y)
        {
            _aiScrolling = false;   // 基类的 releaseLeftClick 由 AiReleaseScreen 用屏幕坐标调用
        }

        internal void AiScrollWheel(int direction)
        {
            int step = 60;
            if (AiRuntime.Config != null && AiRuntime.Config.InvertScroll)
                step = -step;

            AiScroll += direction * step;
            AiScroll = Math.Clamp(AiScroll, 0, AiMaxScroll);
            _aiStickBottom = AiScroll >= AiMaxScroll;
        }

        internal void AiHover(int x, int y)
        {
            // 基类的 performHoverAction 由 AiHoverScreen 用屏幕坐标调用
            if (_aiScrolling && AiMaxScroll > 0)
                AiUpdateThumb(y);
        }

        private void AiUpdateThumb(int y)
        {
            int ty = Math.Clamp(y - _aiScrollGrab, AiContentRect.Y, AiContentRect.Bottom - _aiScrollThumb.Height);
            int max = AiContentRect.Height - _aiScrollThumb.Height;
            AiScroll = max > 0 ? (int)((ty - AiContentRect.Y) / (float)max * AiMaxScroll) : 0;
            AiScroll = Math.Clamp(AiScroll, 0, AiMaxScroll);
            _aiStickBottom = AiScroll >= AiMaxScroll;
        }

        internal void AiUpdate(GameTime time)
        {
            base.update(time);

            _aiTick++;
            AiSession.PollUI();
            AiKeepInputFocus();

            AiEnsureLayout();
            AiSyncIme();
        }

        /// <summary>
        /// 把系统输入法锚到输入框上（候选窗 / 拼音串），见 <see cref="ImeAnchor"/>。
        /// 候选窗默认弹在光标下方，而 AI 输入框贴着屏幕最底部，全屏时整个候选窗都落在画面外，
        /// 玩家就「看不到自己在打什么字」。这里告诉系统输入框在哪，并把拼音串读回来自己画一份。
        /// </summary>
        private void AiSyncIme()
        {
            if (AiInput == null)
                return;

            if (!AiInput.Selected)
            {
                ImeAnchor.Blur();
                return;
            }

            ImeAnchor.Focus(AiScreenRect(AiInputRect));
        }

        /// <summary>AI 页设计坐标矩形 → 屏幕（窗口客户区）像素矩形（与 Cx.ScaledDrawing 的缩放矩阵一致）。</summary>
        private Rectangle AiScreenRect(Rectangle design)
            => new Rectangle(
                _aiDest.X + (int)Math.Round(design.X * Cx.S),
                _aiDest.Y + (int)Math.Round(design.Y * Cx.S),
                (int)Math.Round(design.Width * Cx.S),
                (int)Math.Round(design.Height * Cx.S));

        /// <summary>
        /// 维持输入框的键盘焦点（点击驱动：点输入框 / 按钮 / 快捷提问 → 聚焦，点消息区 → 失焦）。
        /// 为什么不用 SDV 的 TextBox.Update()：它拿「鼠标屏幕坐标」去命中测试矩形（X,Y,Width,Height），
        /// 而 AI 页是设计空间绘制（X/Y 是 1.0 倍设计坐标，上屏时整体乘 Cx.S 再加面板原点），
        /// 屏幕鼠标永远落不进这个矩形，于是它每帧把 Selected 清成 false ⇒ 一是 Draw 里「闪烁时 且 Selected」
        /// 不画光标，二是 RecieveTextInput 开头「未 Selected 直接 return」把敲进来的字符全丢掉，
        /// 表现就是「点输入框没有光标、打不了字」。
        /// </summary>
        private void AiKeepInputFocus()
        {
            if (AiInput == null || !_aiInputWanted)
                return;

            if (!AiInput.Selected)
                AiInput.Selected = true;   // setter 会顺带把键盘订阅者设回本输入框

            // 其它页的搜索框在切页时会无条件把订阅者置 null，这里兜底补回
            if (Game1.keyboardDispatcher?.Subscriber != AiInput)
                Game1.keyboardDispatcher.Subscriber = AiInput;
        }

        protected void AiRestoreFocus()
        {
            _aiInputWanted = true;
            if (AiInput == null)
                return;
            AiInput.Selected = true;
            Game1.keyboardDispatcher.Subscriber = AiInput;
        }

        internal void AiCleanup()
        {
            try
            {
                if (Game1.keyboardDispatcher?.Subscriber == AiInput)
                    Game1.keyboardDispatcher.Subscriber = null;
                if (AiInput != null)
                    AiInput.Selected = false;
            }
            catch { /* ignore */ }

            base.cleanupBeforeExit();
        }

        internal void AiWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            AiRelayout();
        }

        // ---------------- 排版 ----------------

        private void AiEnsureLayout()
        {
            if (AiInput != null)
            {
                AiInput.X = AiInputRect.X;
                AiInput.Y = AiInputRect.Y;
                AiInput.Width = AiInputRect.Width;
            }

            int w = AiContentRect.Width;
            if (_aiLayoutWidth == w && _aiLayoutVersion == AiSession.Version)
                return;

            _aiLayoutWidth = w;
            _aiLayoutVersion = AiSession.Version;

            var font = AiUi.Body;
            int lh = AiUi.LineHeight(font);
            const int padX = 14;
            const int padY = 10;
            const int linkPitch = 34;
            const int linkH2 = 30;
            int maxW = Math.Max(120, (int)(w * 0.88f));

            // 本次提问命中的图鉴条目挂到最新一条回答上（只挂一次，答案流式增长时不重复计算）
            if (_aiPendingLinks != null)
            {
                ChatLine last = null;
                for (int i = AiSession.Lines.Count - 1; i >= 0; i--)
                {
                    if (AiSession.Lines[i].Role == MsgRole.Assistant)
                    {
                        last = AiSession.Lines[i];
                        break;
                    }
                }

                if (last != null)
                {
                    if (last.Links == null)
                        last.Links = _aiPendingLinks;
                    _aiPendingLinks = null;
                }
            }

            _aiBoxes.Clear();
            int y = 0;

            foreach (var line in AiSession.Lines)
            {
                var lines = AiUi.Wrap(AiDisplayText(line), font, maxW - padX * 2);
                float widest = 0f;
                foreach (string s in lines)
                    widest = Math.Max(widest, AiUi.Measure(font, s));

                bool plain = line.Role == MsgRole.Info;
                bool hasAction = !string.IsNullOrWhiteSpace(line.Action);
                int bw = plain ? w : Math.Clamp((int)(widest + padX * 2), 90, w);
                int actionH = hasAction ? 40 : 0;
                int bx = plain ? 0 : (line.Role == MsgRole.User ? w - bw : 0);

                // 回答下方可点击跳转的图鉴条目（放不下换行）
                var linkBtns = new List<AiLinkBtn>();
                int linkRows = 0;
                if (!plain && line.Role == MsgRole.Assistant && line.Links != null && line.Links.Count > 0)
                {
                    int lx = 0;
                    int row = 0;
                    int innerW = Math.Max(60, bw - padX * 2);
                    foreach (CodexAiLink lk in line.Links)
                    {
                        string linkLabel = (lk.IsNpc ? "NPC：" : "物品：") + lk.Label;
                        int lw = Math.Min((int)AiUi.Measure(font, linkLabel) + 22, innerW);
                        if (lx > 0 && lx + lw > innerW)
                        {
                            row++;
                            lx = 0;
                        }

                        linkBtns.Add(new AiLinkBtn
                        {
                            Rect = new Rectangle(bx + padX + lx, row * linkPitch, lw, linkH2),
                            Link = lk
                        });
                        lx += lw + 8;
                    }
                    linkRows = row + 1;
                }
                int linkH = linkRows * linkPitch;

                int bh = (lines.Count + (plain ? 0 : 1)) * lh + padY * 2 + actionH + linkH;

                var box = new AiMsgBox
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
                    int abw = Math.Min((int)AiUi.Measure(font, label) + 28, bw - padX * 2);
                    box.ActionBtn = new Rectangle(bx + padX, y + bh - linkH - 34, Math.Max(80, abw), 28);
                }

                if (linkBtns.Count > 0)
                {
                    int baseY = y + bh - linkH + 2;
                    foreach (AiLinkBtn lb in linkBtns)
                        lb.Rect = new Rectangle(lb.Rect.X, baseY + lb.Rect.Y, lb.Rect.Width, lb.Rect.Height);
                    box.LinkBtns = linkBtns;
                }

                _aiBoxes.Add(box);

                y += bh + 14;
            }

            _aiContentHeight = Math.Max(1, y + 8);
            AiMaxScroll = Math.Max(0, _aiContentHeight - AiContentRect.Height);

            if (_aiStickBottom)
                AiScroll = AiMaxScroll;
            else
                AiScroll = Math.Clamp(AiScroll, 0, AiMaxScroll);
        }

        // ---------------- 绘制 ----------------

        internal void AiDraw(SpriteBatch b)
        {
            if (!_aiInited)
                AiInit();

            AiEnsureLayout();

            // 设计坐标（1.0 倍排版）经缩放矩阵放大 Cx.S 倍画进面板内容区 —— 不切渲染目标
            Cx.ScaledDrawing(b, _aiDest, DrawAiDesign);
        }

        private void DrawAiDesign(SpriteBatch b)
        {
            AiDrawHeader(b);
            AiDrawChips(b);

            AiUi.Box(b, AiContentRect, new Color(0x29, 0x24, 0x1B), new Color(0x44, 0x3C, 0x2E));
            AiDrawMessages(b);

            if (AiMaxScroll > 0)
                AiDrawScroll(b);

            AiDrawBottom(b);
        }

        protected virtual void AiDrawHeader(SpriteBatch b)
        {
            int x = 24;
            int y = _aiTitleY;

            Utility.drawTextWithShadow(b, AiTitle, Game1.dialogueFont, new Vector2(x, y), AiUi.Green);

            string status = AiStatusText();
            string tail = AiRuntime.Config?.Model ?? "";
            if (!string.IsNullOrEmpty(tail))
                status += (status.Length > 0 ? "  |  " : "") + tail;

            b.DrawString(AiUi.Body, status, new Vector2(x, _aiStatusY), AiUi.Dim);

            if (!string.IsNullOrEmpty(AiSubtitle))
            {
                float w = AiUi.Measure(AiUi.Body, AiSubtitle);
                b.DrawString(AiUi.Body, AiSubtitle, new Vector2(_aiW - 24 - w, _aiStatusY), AiUi.Dim);
            }

            bool hover = _aiSettingsRect.Contains(AiMX, AiMY);
            AiUi.Box(b, _aiSettingsRect, hover ? AiUi.Hover : AiUi.PanelBg, hover ? AiUi.Gold : AiUi.Line);
            AiDrawButton(b, _aiSettingsRect, "设置", true);
        }

        protected virtual string AiStatusText()
        {
            if (AiSession.Busy)
                return "正在思考" + new string('.', (_aiTick / 20) % 4);
            return "就绪（回车发送 / Esc 关闭）";
        }

        private void AiDrawChips(SpriteBatch b)
        {
            _aiChips.Clear();
            if (AiQuickPrompts.Count == 0)
                return;

            int x = AiContentRect.X;
            int y = _aiChipsY;

            foreach (string raw in AiQuickPrompts)
            {
                string p = AiChipLabel(raw);
                int w = (int)AiUi.Measure(AiUi.Body, p) + 24;
                if (x + w > AiContentRect.Right)
                    break;

                var rect = new Rectangle(x, y, w, 30);
                bool hover = rect.Contains(AiMX, AiMY);
                AiUi.Box(b, rect, hover ? AiUi.Hover : AiUi.PanelBg, hover ? AiUi.Gold : AiUi.Line);
                b.DrawString(AiUi.Body, p, new Vector2(x + 12, y + 7), hover ? AiUi.Gold : AiUi.Dim);

                _aiChips.Add(new AiChip { Bounds = rect, Text = p });
                x += w + 8;
            }
        }

        private void AiDrawMessages(SpriteBatch b)
        {
            if (_aiBoxes.Count == 0)
            {
                string hint = EmptyHint();
                foreach (var t in new[] { hint })
                {
                    float tw = AiUi.Measure(AiUi.Body, t);
                    b.DrawString(AiUi.Body, t, new Vector2(AiContentRect.X + (AiContentRect.Width - tw) / 2, AiContentRect.Y + 40), AiUi.Dim);
                }
                return;
            }

            AiUi.BeginClip(b, AiContentRect);

            var font = AiUi.Body;
            int lh = AiUi.LineHeight(font);

            foreach (var box in _aiBoxes)
            {
                int top = AiContentRect.Y + box.Bounds.Y - AiScroll;
                if (top > AiContentRect.Bottom + 40 || top + box.Bounds.Height < AiContentRect.Y - 40)
                    continue;

                var line = box.Line;
                Rectangle r = new(AiContentRect.X + box.Bounds.X, top, box.Bounds.Width, box.Bounds.Height);

                switch (line.Role)
                {
                    case MsgRole.User:
                        AiUi.Box(b, r, AiUi.PanelBg, AiUi.Gold);
                        break;
                    case MsgRole.Assistant:
                        AiUi.Box(b, r, AiUi.PanelBg2, AiUi.Blue);
                        break;
                    case MsgRole.Error:
                        AiUi.Box(b, r, new Color(0x44, 0x2A, 0x24), AiUi.Red);
                        break;
                    default:
                        break;
                }

                Color nameColor = line.Role switch
                {
                    MsgRole.User => AiUi.Gold,
                    MsgRole.Assistant => AiUi.Blue,
                    MsgRole.Error => AiUi.Red,
                    _ => AiUi.Dim
                };

                b.DrawString(font, line.Name, new Vector2(r.X + box.PadX, r.Y + box.PadY), nameColor);

                int ty = r.Y + box.PadY + (line.Role == MsgRole.Info ? 0 : lh);
                Color textColor = line.Role == MsgRole.Error ? AiUi.Red : AiUi.Ink;

                foreach (string s in box.Lines)
                {
                    b.DrawString(font, s, new Vector2(r.X + box.PadX, ty), textColor);
                    ty += lh;
                }

                if (AiSession.Busy && ReferenceEquals(line, AiSession.Lines.LastOrDefault()) && line.Role == MsgRole.Assistant && (_aiTick / 15) % 2 == 0)
                    b.DrawString(font, "|", new Vector2(r.X + box.PadX + AiUi.Measure(font, box.Lines.LastOrDefault() ?? ""), ty - lh), AiUi.Blue);

                if (!string.IsNullOrWhiteSpace(line.Action))
                    DrawActionButton(b, box);

                if (box.LinkBtns != null)
                    DrawLinkButtons(b, box);
            }

            AiUi.EndClip(b);
        }

        /// <summary>流式输出时，[[GIVE:xxx]] 标记还没收完就先不显示出来。</summary>
        private static string AiDisplayText(ChatLine line)
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
        private Rectangle AiActionRect(AiMsgBox box)
        {
            var ab = box.ActionBtn;
            return new Rectangle(AiContentRect.X + ab.X, AiContentRect.Y + ab.Y - AiScroll, ab.Width, ab.Height);
        }

        /// <summary>图鉴跳转按钮的屏幕矩形。</summary>
        private Rectangle AiLinkRect(AiMsgBox box, AiLinkBtn btn)
        {
            var lr = btn.Rect;
            return new Rectangle(AiContentRect.X + lr.X, AiContentRect.Y + lr.Y - AiScroll, lr.Width, lr.Height);
        }

        private void DrawLinkButtons(SpriteBatch b, AiMsgBox box)
        {
            foreach (AiLinkBtn lb in box.LinkBtns)
            {
                var rect = AiLinkRect(box, lb);
                bool hover = rect.Contains(AiMX, AiMY);
                var bg = lb.Link.IsNpc ? new Color(0x3A, 0x4A, 0x64) : new Color(0x4A, 0x40, 0x28);
                AiUi.Box(b, rect, hover ? AiUi.Hover : bg, hover ? AiUi.Gold : AiUi.Line);
                string label = (lb.Link.IsNpc ? "NPC：" : "物品：") + lb.Link.Label;
                b.DrawString(AiUi.Body, label,
                    new Vector2(rect.X + 10, rect.Y + (rect.Height - AiUi.LineHeight(AiUi.Body)) / 2),
                    hover ? AiUi.Gold : AiUi.Ink);
            }
        }

        /// <summary>点了回答下方的图鉴跳转按钮：物品 → 图鉴详情，NPC → 喜好页。</summary>
        private void AiOnLink(CodexAiLink lk)
        {
            if (lk == null)
                return;

            if (lk.IsNpc && lk.Npc != null)
            {
                JumpToNpc(lk.Npc);
                return;
            }

            if (lk.Item != null)
                JumpToItem(lk.Item);
        }

        private void DrawActionButton(SpriteBatch b, AiMsgBox box)
        {
            var rect = AiActionRect(box);
            bool hover = rect.Contains(AiMX, AiMY);

            AiUi.Box(b, rect, hover ? new Color(0x6E, 0xA2, 0x54) : new Color(0x45, 0x66, 0x38), AiUi.Green);
            b.DrawString(AiUi.Body, "获取：" + box.Line.Action,
                new Vector2(rect.X + 10, rect.Y + (rect.Height - AiUi.LineHeight(AiUi.Body)) / 2), AiUi.Ink);
        }

        /// <summary>点了回答里附带的动作按钮。</summary>
        private void AiOnLineAction(ChatLine line)
        {
            if (line == null || string.IsNullOrWhiteSpace(line.Action))
                return;

            string result = ItemGiver.Give(line.Action);
            AiSession.Say(MsgRole.Info, "物品", result);
            line.Action = null;
        }

        private void AiDrawScroll(SpriteBatch b)
        {
            AiUi.Fill(b, _aiScrollTrack, new Color(0x44, 0x3C, 0x2E));

            int h = Math.Max(30, (int)(AiContentRect.Height * (float)AiContentRect.Height / _aiContentHeight));
            int max = AiContentRect.Height - h;
            int ty = AiContentRect.Y + (max > 0 && AiMaxScroll > 0 ? (int)(AiScroll / (float)AiMaxScroll * max) : 0);
            _aiScrollThumb = new Rectangle(_aiScrollTrack.X, ty, _aiScrollTrack.Width, h);

            AiUi.Fill(b, _aiScrollThumb, AiUi.Gold);
        }

        private void AiDrawBottom(SpriteBatch b)
        {
            AiInput.Draw(b, false);
            AiDrawComposition(b);
            AiDrawCandidates(b);

            // 聚焦时不画占位提示：它和 SDV 画在 X+16 处的闪烁光标重叠，会把光标盖掉（看起来像点不进去）
            if (AiInput.Text.Length == 0 && !AiInput.Selected)
                b.DrawString(AiUi.Body, EmptyHint(), new Vector2(AiInputRect.X + 12, AiInputRect.Y + 15), AiUi.Dim);

            AiDrawButton(b, _aiSendRect, AiSession.Busy ? "停止" : "发送", !AiSession.Busy);
            AiDrawButton(b, _aiClearRect, "清空", true);

            string extra = AiExtraLabel();
            if (extra != null)
                AiDrawButton(b, _aiExtraRect, extra, true);
        }

        /// <summary>
        /// 把「正在输入、还没上屏」的拼音串画进输入框（跟在已输入文字后面，按下划线）。
        /// 系统本来把它画在自己的候选窗里，而全屏贴底时那个窗可能在画面外 —— 这里补一份，保证看得见。
        /// </summary>
        private void AiDrawComposition(SpriteBatch b)
        {
            string composing = ImeAnchor.Composition;
            if (composing.Length == 0)
                return;

            // 与 SDV TextBox.Draw 的策略一致：超宽时丢掉开头，只显示尾部，这样拼音串能接在可见文字后面
            string shown = AiInput.Text;
            float w = AiUi.Measure(AiUi.Body, shown);
            while (w > AiInputRect.Width - 32 && shown.Length > 0)
            {
                shown = shown.Substring(1);
                w = AiUi.Measure(AiUi.Body, shown);
            }

            float x = AiInputRect.X + 16 + w + 2;
            float y = AiInputRect.Y + 12;          // SDV TextBox 的文本原点（有贴图时是 Y+12）
            Color c = Game1.textColor;

            b.DrawString(AiUi.Body, composing, new Vector2(x, y), c);

            float cw = AiUi.Measure(AiUi.Body, composing);
            AiUi.Fill(b, new Rectangle((int)x, (int)(y + AiUi.Body.MeasureString("Hg").Y + 1), Math.Max(6, (int)cw), 2),
                new Color((byte)c.R, (byte)c.G, (byte)c.B, (byte)150));
        }

        /// <summary>
        /// 在输入框上方自绘候选词条（拼音串已在输入框里自绘）。
        /// 为什么自绘：系统候选窗在全屏下可能整个落在画面外，自己画一份就一定能看见；
        /// 选字仍然交给输入法（它自己吃数字键/空格），这里只负责显示，不抢按键。
        /// </summary>
        private void AiDrawCandidates(SpriteBatch b)
        {
            var cands = ImeAnchor.Candidates;
            if (cands.Count == 0)
                return;

            int start = ImeAnchor.PageStart;
            int size = ImeAnchor.PageSize;
            if (size <= 0 || start + size > cands.Count)
            {
                start = 0;
                size = cands.Count;
            }

            int lh = AiUi.LineHeight(AiUi.Body);
            const int padX = 12;
            const int padY = 6;
            const int gap = 18;
            int h = lh + padY * 2;

            var labels = new List<string>(size);
            float need = padX * 2f;
            for (int i = 0; i < size; i++)
            {
                string t = (i + 1) + "." + cands[start + i];
                labels.Add(t);
                need += AiUi.Measure(AiUi.Body, t) + gap;
            }

            int bw = Math.Min((int)need, Math.Max(120, AiContentRect.Width));
            int bx = AiInputRect.X;
            int by = Math.Max(AiContentRect.Y, AiInputRect.Y - h - 6);   // 别顶出内容区

            AiUi.Box(b, new Rectangle(bx, by, bw, h), AiUi.PanelBg, AiUi.Line);

            float x = bx + padX;
            for (int i = 0; i < labels.Count; i++)
            {
                float tw = AiUi.Measure(AiUi.Body, labels[i]);
                if (x + tw > bx + bw - padX)
                    break;
                bool sel = start + i == ImeAnchor.SelectedIndex;
                b.DrawString(AiUi.Body, labels[i], new Vector2(x, by + padY), sel ? AiUi.Gold : AiUi.Ink);
                x += tw + gap;
            }
        }

        private void AiDrawButton(SpriteBatch b, Rectangle r, string label, bool enabled)
        {
            bool hover = r.Contains(AiMX, AiMY) && enabled;
            AiUi.Box(b, r, hover ? AiUi.Hover : AiUi.PanelBg, enabled ? (hover ? AiUi.Gold : AiUi.Line) : new Color(0x41, 0x3A, 0x2C));

            float tw = AiUi.Measure(AiUi.Body, label);
            b.DrawString(AiUi.Body, label,
                new Vector2(r.X + (r.Width - tw) / 2, r.Y + (r.Height - AiUi.LineHeight(AiUi.Body)) / 2),
                enabled ? (hover ? AiUi.Gold : AiUi.Ink) : AiUi.Dim);
        }
    }
}
