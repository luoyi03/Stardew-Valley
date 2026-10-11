using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace InGameCodex.Ui
{
    /// <summary>
    /// 星露谷图鉴面板：物品 / NPC 喜好 / 献祭进度 / 图鉴完成度 / 收藏 · 最近 五个标签页。
    /// 本阶段（S2）只实现「物品」标签；其余标签画占位说明，S4/S5 阶段填充。
    /// </summary>
    public partial class CodexMenu : IClickableMenu
    {
        internal class Hit
        {
            public Rectangle Bounds;
            public string Tag;
            public string Label;
        }

        private static int TabH => Cx.Sc(42);
        private static int RowH => Cx.Sc(44);

        private readonly List<Hit> _tabs = new List<Hit>
        {
            new Hit { Tag = "items", Label = "物品" },
            new Hit { Tag = "npc", Label = "NPC 喜好" },
            new Hit { Tag = "bundle", Label = "献祭进度" },
            new Hit { Tag = "collection", Label = "图鉴完成度" },
            new Hit { Tag = "fav", Label = "收藏 · 最近" },
            new Hit { Tag = "ai", Label = "AI 问答" }
        };

        private int _tab;
        private Rectangle _contentRect;
        private int _hoverTab = -1;
        private string _toast = "";
        private int _toastTimer;
        private bool _errored;

        public int CurrentTab => _tab;

        public CodexMenu(int tab)
            : base(0, 0, 100, 100, false)
        {
            _tab = Math.Max(0, Math.Min(_tabs.Count - 1, tab));
            InitItemsUi();
            InitNpcsUi();
            InitBundlesUi();
            InitCollectionUi();
            InitFavsUi();
            Relayout();
            if (_tab == 0)
            {
                RefreshOwned();   // v1.4 修复：打开面板时先统计背包，否则详情永远显示「还没有」
                SetSearchFocus(true);
            }
            else if (_tab == 4)
            {
                SelectFirstFav();
            }
            else if (_tab == 5)
            {
                AiInit();
            }
        }

        internal void ShowToast(string msg)
        {
            _toast = msg ?? "";
            _toastTimer = 120;
        }

        private void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            // 面板铺满视口（v1.9.1）：不再设 1560x1040 固定上限，全屏玩家可充分利用屏幕空间。
            width = Math.Max(420, uiW - 20);
            height = Math.Max(320, uiH - 20);
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - Cx.Sc(44), yPositionOnScreen + Cx.Sc(8), Cx.Sc(40), Cx.Sc(40)),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);

            int tx = xPositionOnScreen + Cx.Sc(22);
            int ty = yPositionOnScreen + Cx.Sc(56);
            foreach (Hit t in _tabs)
            {
                int w = (int)Cx.MW(Cx.Body, t.Label) + Cx.Sc(36);
                t.Bounds = new Rectangle(tx, ty, w, TabH);
                tx += w + Cx.Sc(8);
            }

            _contentRect = new Rectangle(
                xPositionOnScreen + Cx.Sc(16),
                ty + TabH + Cx.Sc(10),
                width - Cx.Sc(32),
                height - (ty + TabH + Cx.Sc(10) - yPositionOnScreen) - Cx.Sc(16));

            RelayoutItems();
            RelayoutNpcs();
            RelayoutBundles();
            RelayoutCollection();
            RelayoutFavs();
            AiRelayout();
        }

        private void SetTab(int tab)
        {
            tab = Math.Max(0, Math.Min(_tabs.Count - 1, tab));
            if (tab == _tab)
                return;

            _tab = tab;
            CloseConfirm();   // S7：切标签时关掉二次确认弹窗
            if (_tab == 0)
            {
                RefreshOwned();
                SetSearchFocus(true);
            }
            else
            {
                SetSearchFocus(false);
            }
            SetNpcSearchFocus(false);
            SetBundleSearchFocus(false);
            SetCollectionSearchFocus(false);

            if (_tab == 2)
                ModEntry.Progress?.Refresh();   // 献祭进度会随手玩变化，切页时重读一次

            if (_tab != 5)
            {
                AiSetFocus(false);
            }
            else
            {
                AiInit();
                AiRestoreFocus();   // 切回 AI 页时把键盘焦点还给输入框（否则要再点一下才能打字）
            }

            _bundleDirty = true;
            _colDirty = true;

            Relayout();   // 物品页与收藏页共用 _detailRect，切页时按当前页重新布局

            if (_tab == 4)
                SelectFirstFav();

            Game1.playSound("smallSelect");
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            Relayout();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (HandleConfirmClick(x, y))   // S7：二次确认弹窗打开时吃掉全部点击（含标题栏与标签）
                return;

            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                exitThisMenu(playSound);
                return;
            }

            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Bounds.Contains(x, y))
                {
                    SetTab(i);
                    return;
                }
            }

            if (HandleStarClick(x, y))   // 详情区顶部的「☆ 收藏」按钮（物品页与收藏页共用）
                return;

            if (_tab == 0 && ReceiveItemsClick(x, y))
                return;

            if (_tab == 1 && ReceiveNpcsClick(x, y))
                return;

            if (_tab == 2 && ReceiveBundlesClick(x, y))
                return;

            if (_tab == 3 && ReceiveCollectionClick(x, y))
                return;

            if (_tab == 4 && ReceiveFavsClick(x, y))
                return;

            if (_tab == 5)
            {
                // AI 页的坐标是「设计空间」（1.0 倍排版，绘制时整体放大 Cx.S 倍），必须走 *Screen 包装换算
                AiLeftClickScreen(x, y);
                return;
            }

            base.receiveLeftClick(x, y, playSound);
        }

        public override void leftClickHeld(int x, int y)
        {
            if (_tab == 0 && HeldItemsClick(x, y))
                return;
            if (_tab == 1 && HeldNpcsClick(x, y))
                return;
            if (_tab == 2 && HeldBundlesClick(x, y))
                return;
            if (_tab == 3 && HeldCollectionClick(x, y))
                return;
            if (_tab == 4 && HeldFavsClick(x, y))
                return;
            if (_tab == 5)
            {
                AiHeldScreen(x, y);   // AI 页拖动滚动条
                return;
            }
            base.leftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            if (_tab == 0)
                ReleaseItemsDrag();
            if (_tab == 1)
                ReleaseNpcsDrag();
            if (_tab == 2)
                ReleaseBundlesDrag();
            if (_tab == 3)
                ReleaseCollectionDrag();
            if (_tab == 4)
                ReleaseFavsDrag();
            if (_tab == 5)
                AiReleaseScreen(x, y);
            base.releaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            if (_confirmOpen)   // S7：确认弹窗打开时不滚动
                return;

            int step = direction > 0 ? -1 : 1;
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            if (_tab == 0 && (_detailRect.Contains(mx, my) || _detailScroll.Track.Contains(mx, my)))
            {
                ScrollDetail(step);
                return;
            }

            if (_tab == 0 && (_listRect.Contains(mx, my) || _listScroll.Track.Contains(mx, my) || _chipsRect.Contains(mx, my) || _srcChipsRect.Contains(mx, my)))
            {
                ScrollItems(step);
                return;
            }

            if (_tab == 1 && (_npcDetailRect.Contains(mx, my) || _npcDetailScroll.Track.Contains(mx, my)))
            {
                ScrollNpcDetail(step);
                return;
            }

            if (_tab == 1 && (_npcListRect.Contains(mx, my) || _npcListScroll.Track.Contains(mx, my)))
            {
                ScrollNpcs(step);
                return;
            }

            if (_tab == 2 && (_bundleListRect.Contains(mx, my) || _bundleScroll.Track.Contains(mx, my)
                || _bRoomRect.Contains(mx, my) || _roomScroll.Track.Contains(mx, my)))
            {
                ScrollBundles(step);
                return;
            }

            if (_tab == 3 && (_cListRect.Contains(mx, my) || _cListScroll.Track.Contains(mx, my)
                || _cSectionRect.Contains(mx, my) || _cSectionScroll.Track.Contains(mx, my)))
            {
                ScrollCollection(step);
                return;
            }

            if (_tab == 4 && (_detailRect.Contains(mx, my) || _detailScroll.Track.Contains(mx, my)))
            {
                ScrollDetail(step);
                return;
            }

            if (_tab == 4 && (_favListRect.Contains(mx, my) || _favScroll.Track.Contains(mx, my)))
            {
                ScrollFavs(step);
                return;
            }

            if (_tab == 5)
            {
                AiScrollWheel(direction);
                return;
            }

            base.receiveScrollWheelAction(direction);
        }

        public override void performHoverAction(int x, int y)
        {
            _hoverTab = -1;
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Bounds.Contains(x, y))
                {
                    _hoverTab = i;
                    break;
                }
            }

            if (_tab == 0)
                HoverItems(x, y);
            else if (_tab == 1)
                HoverNpcs(x, y);
            else if (_tab == 2)
                HoverBundles(x, y);
            else if (_tab == 3)
                HoverCollection(x, y);
            else if (_tab == 4)
                HoverFavs(x, y);
            else if (_tab == 5)
                AiHoverScreen(x, y);

            base.performHoverAction(x, y);
        }

        public override void receiveKeyPress(Keys key)
        {
            bool typing = (_tab == 0 && SearchFocused())
                || (_tab == 1 && NpcSearchFocused())
                || (_tab == 2 && BundlesSearchFocused())
                || (_tab == 3 && CollectionSearchFocused())
                || (_tab == 5 && AiInputFocused);

            if (key == Keys.Escape)
            {
                if (_confirmOpen)
                {
                    CloseConfirm();   // S7：先取消二次确认弹窗，不关面板
                    Game1.playSound("cancel");
                    return;
                }
                if (typing)
                {
                    if (_tab == 5)
                    {
                        // §23.3.7：AI 输入框聚焦时 Esc 直接关面板，不切标签
                        exitThisMenu(true);
                        return;
                    }
                    SetSearchFocus(false);
                    SetNpcSearchFocus(false);
                    SetBundleSearchFocus(false);
                    SetCollectionSearchFocus(false);
                    return;
                }
                exitThisMenu(true);
                return;
            }

            // §23.3.7：输入框聚焦时面板导航键全部让位（Tab 不切页，回车/退格交给输入框）
            if (typing)
                return;

            if (key == Keys.Tab)
            {
                SetTab((_tab + 1) % _tabs.Count);
                return;
            }

            if (key == Keys.Left || key == Keys.Right)
            {
                SetTab((_tab + (key == Keys.Right ? 1 : _tabs.Count - 1)) % _tabs.Count);
                return;
            }

            if (_tab == 0 && ItemsOnKey(key))
                return;

            if (_tab == 1 && NpcsOnKey(key))
                return;

            if (_tab == 2 && BundlesOnKey(key))
                return;

            if (_tab == 3 && CollectionOnKey(key))
                return;

            if (_tab == 4 && FavsOnKey(key))
                return;

            if (_tab == 5)
            {
                AiKeyPress(key);
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void update(GameTime time)
        {
            base.update(time);

            if (_toastTimer > 0)
            {
                _toastTimer--;
                if (_toastTimer == 0)
                    _toast = "";
            }

            if (_tab == 0)
                UpdateItems(time);
            else if (_tab == 1)
                UpdateNpcs(time);
            else if (_tab == 2)
                UpdateBundles(time);
            else if (_tab == 3)
                UpdateCollection(time);
            else if (_tab == 5)
                AiUpdate(time);
        }

        protected override void cleanupBeforeExit()
        {
            SetSearchFocus(false);
            SetNpcSearchFocus(false);
            SetBundleSearchFocus(false);
            SetCollectionSearchFocus(false);
            AiSetFocus(false);
            AiCleanup();
            base.cleanupBeforeExit();
        }

        public override void draw(SpriteBatch b)
        {
            bool ok = true;

            if (!_errored)
            {
                try
                {
                    DrawFrame(b);
                    if (_tab == 0)
                        DrawItems(b);
                    else if (_tab == 1)
                        DrawNpcs(b);
                    else if (_tab == 2)
                        DrawBundles(b);
                    else if (_tab == 3)
                        DrawCollection(b);
                    else if (_tab == 4)
                        DrawFavs(b);
                    else if (_tab == 5)
                        AiDraw(b);
                    else
                        DrawPlaceholder(b);

                    if (string.IsNullOrEmpty(_toast) == false)
                        DrawToast(b);
                }
                catch (Exception ex)
                {
                    _errored = true;
                    ok = false;
                    ModEntry.Log?.Log("图鉴面板绘制出错（本页不再重绘，避免刷屏）：" + ex, LogLevel.Error);
                    try { b.End(); } catch { }
                }
            }

            if (ok)
            {
                try { base.draw(b); } catch { }
            }

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

            Cx.Fill(b, new Rectangle(xPositionOnScreen + Cx.Sc(8), yPositionOnScreen + Cx.Sc(8), width - Cx.Sc(16), height - Cx.Sc(16)), Cx.PageBg);

            Cx.Text(b, Cx.Head, "星露谷图鉴", new Vector2(xPositionOnScreen + Cx.Sc(22), yPositionOnScreen + Cx.Sc(10)), Cx.Gold);

            string hint = ModEntry.Config.ToggleKey + " 开关 · Esc 关闭 · Tab / ←→ 切页";
            float hw = Cx.MW(Cx.Body, hint);
            Cx.Text(b, Cx.Body, hint, new Vector2(xPositionOnScreen + width - hw - Cx.Sc(58), yPositionOnScreen + Cx.Sc(28)), Cx.Dim);

            for (int i = 0; i < _tabs.Count; i++)
            {
                Hit t = _tabs[i];
                bool on = i == _tab;
                bool hov = i == _hoverTab;
                Cx.Fill(b, t.Bounds, on ? Cx.Selected : (hov ? Cx.Hover : Cx.PanelBg));
                Cx.Fill(b, new Rectangle(t.Bounds.X, t.Bounds.Bottom - 2, t.Bounds.Width, 2), on ? Cx.Gold : Cx.Line);

                float tw = Cx.MW(Cx.Body, t.Label);
                float th = Cx.MH(Cx.Body, "Hg");
                Cx.Text(b, Cx.Body, t.Label,
                    new Vector2(t.Bounds.X + (t.Bounds.Width - tw) / 2f, t.Bounds.Y + (t.Bounds.Height - th) / 2f),
                    on ? Color.White : Cx.Dim);
            }

            if (upperRightCloseButton != null)
                upperRightCloseButton.draw(b);
        }

        private void DrawPlaceholder(SpriteBatch b)
        {
            Hit t = _tabs[_tab];
            string stage = t.Tag == "npc" ? "S4" : "S5";
            string title = t.Label;

            string sub;
            switch (t.Tag)
            {
                case "npc":
                    sub = "这里会列出每位 NPC 的五档喜好（最爱 / 喜欢 / 普通 / 不喜欢 / 讨厌）+ 生日 + 是否可结婚。";
                    break;
                case "bundle":
                    sub = "这里会列出社区中心各收集包的完成进度，并可只看还差什么。";
                    break;
                case "collection":
                    sub = "这里会列出图鉴完成度：已发现物品数 / 已捐赠博物馆 / 已钓到的鱼。";
                    break;
                default:
                    sub = "这里会列出你收藏的物品与最近查看过的物品。";
                    break;
            }

            int cx = _contentRect.X + _contentRect.Width / 2;
            int cy = _contentRect.Y + _contentRect.Height / 2 - Cx.Sc(40);

            float tw = Cx.MW(Cx.Head, title);
            Cx.Text(b, Cx.Head, title, new Vector2(cx - tw / 2f, cy), Cx.Gold);

            string line = "本标签页在 " + stage + " 阶段实现";
            float lw = Cx.MW(Cx.Body, line);
            Cx.Text(b, Cx.Body, line, new Vector2(cx - lw / 2f, cy + Cx.Sc(56)), Cx.Ink);

            int yy = cy + Cx.Sc(88);
            foreach (string s in Cx.Wrap(sub, Cx.Body, Math.Min(Cx.Sc(760), _contentRect.Width - Cx.Sc(80))))
            {
                float w = Cx.MW(Cx.Body, s);
                Cx.Text(b, Cx.Body, s, new Vector2(cx - w / 2f, yy), Cx.Dim);
                yy += Cx.LineHeight(Cx.Body);
            }
        }

        private void DrawToast(SpriteBatch b)
        {
            int alpha = _toastTimer > 100 ? (120 - _toastTimer) * 12 : (_toastTimer < 20 ? _toastTimer * 12 : 255);
            alpha = Math.Max(0, Math.Min(255, alpha));

            float w = Cx.MW(Cx.Body, _toast) + Cx.Sc(40);
            var r = new Rectangle((int)(_contentRect.X + (_contentRect.Width - w) / 2f), _contentRect.Bottom - Cx.Sc(54), (int)w, Cx.Sc(40));

            Cx.Box(b, r, new Color(Cx.PanelBg2.R, Cx.PanelBg2.G, Cx.PanelBg2.B) * (alpha / 255f), Cx.Gold * (alpha / 255f));
            Cx.Text(b, Cx.Body, _toast, new Vector2(r.X + Cx.Sc(20), r.Y + Cx.Sc(10)), Cx.Ink * (alpha / 255f));
        }
    }
}
