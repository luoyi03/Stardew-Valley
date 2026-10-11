using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace InGameGuide
{
    public partial class GuideMenu : IClickableMenu
    {
        internal class Hit
        {
            public Rectangle Bounds;
            public string Tag;
            public string Label;
            public bool On;
        }

        private const int ChapterItemH = 50;
        private const int ScrollW = 10;

        private int _tab;

        private readonly List<Hit> _tabs = new()
        {
            new Hit { Tag = "doc", Label = "新手教程" },
            new Hit { Tag = "search", Label = "物品代码查询" }
        };

        private Rectangle _contentRect;
        private Rectangle _listRect;
        private Rectangle _viewRect;

        private readonly ScrollBar _docScroll = new();
        private readonly ScrollBar _listScroll = new();

        private int _chapterIndex;
        private List<Op> _layout;
        private int _layoutHeight;
        private int _layoutWidth = -1;
        private int _layoutChapter = -1;

        private int _hoverChapter = -1;
        private int _hoverTab = -1;

        private string _toast = "";
        private int _toastTimer;
        private bool _errored;

        /// <summary>当前标签页索引：0 = 教程，1 = 物品查询。</summary>
        public int Tab => _tab;

        public GuideMenu(int tab)
            : base(0, 0, 100, 100, false)
        {
            _tab = tab;
            InitSearchUi();
            Relayout();
            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + 8, 40, 40),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);
            EnsureLayout();
        }

        // ---------------- 布局 ----------------
        private void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            // 尽量铺满屏幕（UI 缩放 150% 时 uiViewport 会小很多，所以这里留边很少）
            width = Math.Max(360, Math.Min(uiW - 20, 1560));
            height = Math.Max(300, Math.Min(uiH - 20, 1040));
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            int cx = xPositionOnScreen + 16;
            int cy = yPositionOnScreen + 108;
            _contentRect = new Rectangle(cx, cy, width - 32, height - 124);

            // 标签按钮
            int tx = cx;
            foreach (var t in _tabs)
            {
                var f = Ui.Body;
                int w = (int)f.MeasureString(t.Label).X + 36;
                t.Bounds = new Rectangle(tx, yPositionOnScreen + 56, w, 42);
                tx += w + 8;
            }

            if (_tab == 0)
                RelayoutTutorial();
            else
                RelayoutSearch();

            _layoutWidth = -1;
            _layoutChapter = -1;
            EnsureLayout();
            EnsureLayoutSearch();
        }

        private void RelayoutTutorial()
        {
            int cx = _contentRect.X, cy = _contentRect.Y;
            int cw = _contentRect.Width, ch = _contentRect.Height;

            int listW = Math.Max(150, Math.Min(260, (int)(cw * 0.26)));
            _listRect = new Rectangle(cx, cy, listW, ch);

            int docX = cx + listW + 22;
            int docW = Math.Max(240, cw - listW - 22 - ScrollW - 6);
            _viewRect = new Rectangle(docX, cy, docW, ch);

            int availH = ch - 12;
            _listScroll.Content = ChapterCount() * ChapterItemH + 8;
            _listScroll.View = availH;
            _listScroll.Track = new Rectangle(_listRect.Right - ScrollW, cy + 6, ScrollW, availH);

            _docScroll.Track = new Rectangle(_viewRect.Right + 2, cy + 6, ScrollW, ch - 12);
        }

        private int ChapterCount() => ModEntry.Guide?.Chapters?.Count ?? 0;

        private Rectangle ChapterRect(int i)
        {
            int y = _listRect.Y + 6 + i * ChapterItemH - _listScroll.Pos;
            return new Rectangle(_listRect.X + 4, y, _listRect.Width - ScrollW - 12, ChapterItemH - 6);
        }

        // ---------------- 数据 ----------------
        private void EnsureLayout()
        {
            if (_tab != 0 || ModEntry.Guide?.Chapters == null || ChapterCount() == 0)
                return;

            if (_chapterIndex < 0 || _chapterIndex >= ChapterCount())
                _chapterIndex = 0;

            int w = Math.Max(240, _viewRect.Width - 14);
            if (_layout != null && _layoutWidth == w && _layoutChapter == _chapterIndex)
                return;

            _layout = Ui.BuildChapterLayout(ModEntry.Guide.Chapters[_chapterIndex], w);
            _layoutHeight = Ui.ContentHeight(_layout);
            _layoutWidth = w;
            _layoutChapter = _chapterIndex;

            _docScroll.Content = _layoutHeight + 16;
            _docScroll.View = _viewRect.Height;
            _docScroll.Pos = 0;
        }

        // ---------------- 更新 ----------------
        public override void update(GameTime time)
        {
            base.update(time);
            if (_toastTimer > 0)
                _toastTimer--;
            UpdateSearch(time);
        }

        // ---------------- 绘制 ----------------
        public override void draw(SpriteBatch b)
        {
            try
            {
                DrawFrame(b);
                if (_tab == 0)
                    DrawTutorial(b);
                else
                    DrawSearch(b);
                DrawToast(b);
            }
            catch (Exception ex)
            {
                if (!_errored)
                {
                    _errored = true;
                    ModEntry.Log.Log("绘制教程面板时出错：" + ex, LogLevel.Error);
                }
            }
            base.draw(b);
            DrawMouseCursor(b);
        }

        /// <summary>在当前 SpriteBatch 内直接画系统光标（不能调用 Game1 的 drawMouseCursor，那会重复 Begin）。</summary>
        private void DrawMouseCursor(SpriteBatch b)
        {
            if (Game1.options.hardwareCursor)
                return;

            b.Draw(Game1.mouseCursors,
                new Vector2(Game1.getMouseX(), Game1.getMouseY()),
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, Game1.mouseCursor, 16, 16),
                Color.White * Game1.mouseCursorTransparency,
                0f, Vector2.Zero,
                4f + Game1.dialogueButtonScale / 150f,
                SpriteEffects.None, 1f);
        }

        private void DrawFrame(SpriteBatch b)
        {
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White, 1f, false);

            // 游戏菜单底图是浅橙色，浅色文字会看不清；整体铺一层深色内衬
            Ui.Fill(b, new Rectangle(xPositionOnScreen + 16, yPositionOnScreen + 16, width - 32, height - 32), Ui.PageBg);

            string title = string.IsNullOrEmpty(ModEntry.Guide?.Title) ? "星露谷物语 新手图文教程" : ModEntry.Guide.Title;
            b.DrawString(Ui.Head, title, new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 14), Ui.Gold);

            string hint = ModEntry.Config.ToggleKey + " 打开/关闭本面板   ·   Esc 关闭   ·   滚轮滚动";
            float hw = Ui.Body.MeasureString(hint).X;
            b.DrawString(Ui.Body, hint, new Vector2(xPositionOnScreen + width - hw - 56, yPositionOnScreen + 24), Ui.Dim);

            for (int i = 0; i < _tabs.Count; i++)
            {
                var t = _tabs[i];
                bool active = (i == _tab);
                SpriteFont f = Ui.Body;
                Ui.Fill(b, t.Bounds, active ? new Color(0x4F, 0x7D, 0x3F) : (i == _hoverTab ? Ui.Hover : Ui.PanelBg2));
                Ui.Box(b, t.Bounds, Color.Transparent, active ? Ui.Green : Ui.Line);
                var sz = f.MeasureString(t.Label);
                b.DrawString(f, t.Label,
                    new Vector2(t.Bounds.X + (t.Bounds.Width - sz.X) / 2, t.Bounds.Y + (t.Bounds.Height - sz.Y) / 2),
                    active ? Color.White : Ui.Dim);
            }

            upperRightCloseButton?.draw(b);
        }

        private void DrawTutorial(SpriteBatch b)
        {
            EnsureLayout();

            // 左侧分隔线
            Ui.Fill(b, new Rectangle(_listRect.Right + 8, _listRect.Y + 6, 1, _listRect.Height - 12), Ui.Line);

            Ui.BeginClip(b, _listRect);
            for (int i = 0; i < ChapterCount(); i++)
            {
                var ch = ModEntry.Guide.Chapters[i];
                var r = ChapterRect(i);
                if (r.Bottom < _listRect.Y - 10 || r.Y > _listRect.Bottom + 10)
                    continue;

                bool sel = i == _chapterIndex;
                Ui.Fill(b, r, sel ? new Color(0x4F, 0x7D, 0x3F) : (i == _hoverChapter ? Ui.Hover : Ui.PanelBg2));
                Ui.Box(b, r, Color.Transparent, sel ? Ui.Green : Ui.Line);

                string num = (i + 1).ToString();
                Ui.Fill(b, new Rectangle(r.X + 8, r.Y + 10, 26, 26), sel ? Ui.Gold : Ui.Line);
                var ns = Ui.Body.MeasureString(num);
                b.DrawString(Ui.Body, num, new Vector2(r.X + 8 + (26 - ns.X) / 2, r.Y + 10 + (26 - ns.Y) / 2), sel ? new Color(0x2C, 0x27, 0x1F) : Ui.Ink);

                string t = Truncate(ch.Title ?? "", Ui.Body, r.Width - 48);
                b.DrawString(Ui.Body, t, new Vector2(r.X + 42, r.Y + 14), sel ? Color.White : Ui.Ink);
            }
            Ui.EndClip(b);
            _listScroll.Draw(b);

            Ui.BeginClip(b, _viewRect);
            if (_layout != null)
                Ui.DrawOps(_layout, b, new Vector2(_viewRect.X, _viewRect.Y), _docScroll.Pos, _viewRect.Height);
            Ui.EndClip(b);
            _docScroll.Draw(b);
        }

        private string Truncate(string s, SpriteFont f, float maxW)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            if (f.MeasureString(s).X <= maxW)
                return s;
            string cur = "";
            foreach (char c in s)
            {
                if (f.MeasureString(cur + c + "…").X > maxW)
                    break;
                cur += c;
            }
            return cur + "…";
        }

        private void DrawToast(SpriteBatch b)
        {
            if (_toastTimer <= 0 || string.IsNullOrEmpty(_toast))
                return;
            var f = Ui.Body;
            var sz = f.MeasureString(_toast);
            int w = (int)sz.X + 40;
            var r = new Rectangle(xPositionOnScreen + width / 2 - w / 2, yPositionOnScreen + height - 44, w, 34);
            Ui.Fill(b, r, Ui.PanelBg * 0.97f);
            Ui.Box(b, r, Color.Transparent, Ui.Green);
            b.DrawString(f, _toast, new Vector2(r.X + 20, r.Y + 8), Ui.Green);
        }

        private void ShowToast(string msg)
        {
            _toast = msg;
            _toastTimer = 120;
        }

        // ---------------- 输入 ----------------
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                exitThisMenu();
                return;
            }

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Bounds.Contains(x, y))
                {
                    SetTab(i);
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            if (_tab == 0)
            {
                if (_listScroll.TryGrab(x, y))
                    return;
                if (_docScroll.TryGrab(x, y))
                    return;

                if (_listRect.Contains(x, y))
                {
                    for (int i = 0; i < ChapterCount(); i++)
                    {
                        if (ChapterRect(i).Contains(x, y))
                        {
                            if (_chapterIndex == i)
                                return;
                            _chapterIndex = i;
                            EnsureLayout();
                            ShowToast(ModEntry.Guide.Chapters[i].Title ?? "");
                            Game1.playSound("smallSelect");
                            return;
                        }
                    }
                }
            }
            else
            {
                ReceiveSearchClick(x, y);
            }
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            if (_tab == 0)
            {
                if (_listScroll.Dragging) _listScroll.ApplyDrag(y);
                if (_docScroll.Dragging) _docScroll.ApplyDrag(y);
            }
            else if (_resScroll.Dragging)
            {
                _resScroll.ApplyDrag(y);
            }
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _docScroll.Release();
            _listScroll.Release();
            _resScroll.Release();
        }

        /// <summary>物品查询页：右键任意一行 = 直接获取该物品（不想点按钮时的备选路径）。</summary>
        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            if (_tab == 0)
                return;
            ReceiveSearchRightClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            // 游戏传进来的是原始滚动增量（一格可能 ±120），只取方向，避免一格就滚到底
            int step = direction > 0 ? -1 : (direction < 0 ? 1 : 0);
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            if (_tab == 0)
            {
                if (_listRect.Contains(mx, my))
                    _listScroll.ScrollBy(step * 48);
                else
                    _docScroll.ScrollBy(step * 72);
            }
            else
            {
                _resScroll.ScrollBy(step * 56);
            }
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);
            _hoverTab = -1;
            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i].Bounds.Contains(x, y))
                    _hoverTab = i;

            if (_tab == 0)
            {
                _hoverChapter = -1;
                if (_listRect.Contains(x, y))
                {
                    for (int i = 0; i < ChapterCount(); i++)
                        if (ChapterRect(i).Contains(x, y))
                            _hoverChapter = i;
                }
            }
            else
            {
                _hoverRow = -1;
                if (_resultRect.Contains(x, y))
                {
                    int first = _resScroll.Pos / SearchRowH;
                    for (int i = Math.Max(0, first - 1); i < Math.Min(_filtered.Count, first + _resultRect.Height / SearchRowH + 2); i++)
                        if (RowRect(i).Contains(x, y))
                            _hoverRow = i;
                }
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            bool typing = SearchFocused();

            if (key == Keys.Escape)
            {
                if (typing)
                {
                    SetSearchFocus(false);
                    return;
                }
                exitThisMenu(true);
                return;
            }

            if (typing)
                return;

            if (key == Keys.Tab)
            {
                SetTab(_tab == 0 ? 1 : 0);
                return;
            }

            ScrollBar target = _tab == 0 ? _docScroll : _resScroll;
            switch (key)
            {
                case Keys.Down: target.ScrollBy(60); break;
                case Keys.Up: target.ScrollBy(-60); break;
                case Keys.PageDown: target.ScrollBy(target.View - 40); break;
                case Keys.PageUp: target.ScrollBy(-(target.View - 40)); break;
                case Keys.Home: target.Pos = 0; break;
                case Keys.End: target.Pos = target.Max; break;
                case Keys.Left when _tab == 0: PrevChapter(); break;
                case Keys.Right when _tab == 0: NextChapter(); break;
            }
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Relayout();
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            try
            {
                if (SearchFocused())
                    SetSearchFocus(false);
            }
            catch { /* 忽略 */ }
        }

        private void PrevChapter()
        {
            if (ChapterCount() == 0) return;
            _chapterIndex = (_chapterIndex - 1 + ChapterCount()) % ChapterCount();
            EnsureLayout();
            Game1.playSound("smallSelect");
        }

        private void NextChapter()
        {
            if (ChapterCount() == 0) return;
            _chapterIndex = (_chapterIndex + 1) % ChapterCount();
            EnsureLayout();
            Game1.playSound("smallSelect");
        }

        private void SetTab(int tab)
        {
            if (tab == _tab)
                return;
            _tab = tab;
            Relayout();
            if (_tab == 1)
                SetSearchFocus(true);
        }
    }
}
