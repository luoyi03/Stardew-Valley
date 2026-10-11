using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using InGameCodex.Data;

namespace InGameCodex.Ui
{
    /// <summary>图鉴面板的「收藏 · 最近」标签（S5）：左边收藏与最近查看两段，右边复用物品详情。</summary>
    public partial class CodexMenu
    {
        private static int FavRowH => Cx.Sc(36);

        private Rectangle _favListRect, _favClearRect;
        private readonly ScrollBar _favScroll = new ScrollBar();
        private readonly List<CodexItem> _favRows = new List<CodexItem>();
        private readonly List<string> _favRowText = new List<string>();
        private int _favHover = -1;

        private int FavScrollTop => (_favScroll.Pos / FavRowH) * FavRowH;

        private void InitFavsUi()
        {
            // 本页没有输入框，无需初始化控件
        }

        /// <summary>只在收藏页覆盖共享的 `_detailRect`（物品页与收藏页共用右侧详情区）。</summary>
        private void RelayoutFavs()
        {
            if (_tab != 4)
                return;

            Rectangle c = _contentRect;
            _favClearRect = new Rectangle(c.X, c.Y + Cx.Sc(2), Cx.Sc(200), Cx.Sc(48));

            int bodyTop = c.Y + Cx.Sc(62);
            int avail = Math.Max(Cx.Sc(80), c.Bottom - bodyTop);
            int listW = (int)(c.Width * 0.42) - 8;

            _favListRect = new Rectangle(c.X, bodyTop, listW, avail);
            int detailX = _favListRect.Right + Cx.Sc(12);
            _detailRect = new Rectangle(detailX, bodyTop, Math.Max(Cx.Sc(160), c.Right - detailX), avail);

            _favScroll.Track = new Rectangle(_favListRect.Right - Cx.Sc(12), _favListRect.Y + Cx.Sc(4), Cx.Sc(10), _favListRect.Height - Cx.Sc(8));
            _favScroll.View = _favListRect.Height;
            _detailScroll.Track = new Rectangle(_detailRect.Right - Cx.Sc(14), _detailRect.Y + Cx.Sc(6), Cx.Sc(10), _detailRect.Height - Cx.Sc(12));
            _detailScroll.View = _detailRect.Height - DetailHeaderH - Cx.Sc(14);
        }

        // ------------------------------------------------------------------ 数据

        private void EnsureFavRows()
        {
            _favRows.Clear();
            _favRowText.Clear();

            CodexProgress p = ModEntry.Progress;
            List<CodexItem> favs = p != null ? p.ItemsByIds(ModEntry.Config?.Favorites) : new List<CodexItem>();
            List<CodexItem> recents = p != null ? p.ItemsByIds(ModEntry.Config?.Recents) : new List<CodexItem>();

            _favRowText.Add("收藏 · " + favs.Count + " 件");
            _favRows.Add(null);
            foreach (CodexItem e in favs)
            {
                _favRows.Add(e);
                _favRowText.Add("");
            }

            _favRowText.Add("最近查看 · " + recents.Count + " 件");
            _favRows.Add(null);
            foreach (CodexItem e in recents)
            {
                _favRows.Add(e);
                _favRowText.Add("");
            }

            _favScroll.Content = _favRows.Count * FavRowH;
            if (_favScroll.Pos > _favScroll.Max)
                _favScroll.Pos = 0;
        }

        /// <summary>切到收藏页时选中第一条（收藏优先）。</summary>
        internal void SelectFirstFav()
        {
            EnsureFavRows();
            _favScroll.Pos = 0;
            foreach (CodexItem e in _favRows)
            {
                if (e == null)
                    continue;
                _selected = e;
                _detailFor = null;
                return;
            }
        }

        // ------------------------------------------------------------------ 交互

        private bool ReceiveFavsClick(int x, int y)
        {
            if (_favClearRect.Contains(x, y))
            {
                ModEntry.Progress?.ClearRecents();
                _favScroll.Pos = 0;
                Game1.playSound("smallSelect");
                return true;
            }

            if (HandleGiveClick(x, y))   // S7：收藏页的详情同样有获取区
                return true;

            if (_favScroll.TryGrab(x, y))
                return true;

            if (_favListRect.Contains(x, y))
            {
                EnsureFavRows();
                int idx = (FavScrollTop + (y - _favListRect.Y)) / FavRowH;
                if (idx >= 0 && idx < _favRows.Count && _favRows[idx] != null)
                {
                    _selected = _favRows[idx];
                    _detailFor = null;
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            return false;
        }

        private bool HeldFavsClick(int x, int y)
        {
            if (_favScroll.Dragging)
            {
                _favScroll.ApplyDrag(y);
                return true;
            }
            return false;
        }

        private void ReleaseFavsDrag()
        {
            _favScroll.Release();
        }

        private void ScrollFavs(int step)
        {
            _favScroll.ScrollBy(step * FavRowH * 3);
        }

        private void HoverFavs(int x, int y)
        {
            _favHover = -1;
            if (!_favListRect.Contains(x, y))
                return;
            EnsureFavRows();
            int idx = (FavScrollTop + (y - _favListRect.Y)) / FavRowH;
            if (idx >= 0 && idx < _favRows.Count)
                _favHover = idx;
        }

        private bool FavsOnKey(Keys key)
        {
            switch (key)
            {
                case Keys.Up: _favScroll.ScrollBy(-FavRowH); return true;
                case Keys.Down: _favScroll.ScrollBy(FavRowH); return true;
                case Keys.PageUp: _favScroll.ScrollBy(-_favListRect.Height); return true;
                case Keys.PageDown: _favScroll.ScrollBy(_favListRect.Height); return true;
                case Keys.Home: _favScroll.Pos = 0; return true;
                case Keys.End: _favScroll.Pos = _favScroll.Max; return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 绘制

        private void DrawFavs(SpriteBatch b)
        {
            EnsureFavRows();

            DrawButton(b, _favClearRect, "清空最近查看", false, false);
            Cx.Text(b, Cx.Body, "★ = 已收藏（在右边详情里点「☆ 收藏」切换）",
                new Vector2(_favClearRect.Right + 14, _contentRect.Y + 16), Cx.Dim);

            Cx.Box(b, _favListRect, Cx.PanelBg2, Cx.Line);
            Cx.BeginClip(b, _favListRect);
            int top = FavScrollTop;
            int rows = Math.Max(1, _favListRect.Height / FavRowH);
            int first = top / FavRowH;
            int last = Math.Min(_favRows.Count, first + rows);
            for (int i = first; i < last; i++)
                DrawFavRow(b, i);
            Cx.EndClip(b);
            _favScroll.Draw(b);

            DrawDetail(b);
        }

        private void DrawFavRow(SpriteBatch b, int index)
        {
            Rectangle r = new Rectangle(_favListRect.X, _favListRect.Y + index * FavRowH - FavScrollTop, _favListRect.Width, FavRowH);
            CodexItem e = _favRows[index];

            if (e == null)
            {
                Cx.Fill(b, r, Cx.PanelBg);
                Cx.Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), Cx.Line);
                Cx.Text(b, Cx.Body, _favRowText[index], new Vector2(r.X + Cx.Sc(10), r.Y + Cx.Sc(9)), Cx.Gold);
                return;
            }

            bool sel = ReferenceEquals(e, _selected);
            bool hov = index == _favHover;
            Color bg = sel ? Cx.Selected : (hov ? Cx.Hover : Cx.PanelBg2);

            Cx.Fill(b, r, bg);
            Cx.Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), Cx.Line);

            var box = new Rectangle(r.X + Cx.Sc(4), r.Y + Cx.Sc(6), Cx.Sc(28), Cx.Sc(28));
            DrawIcon(b, e, box);
            // 图标可能越出格子（家具大贴图），把格子以外的行底补回来（等效裁剪；绘制图标期间 Scissor 会失效）
            Cx.Fill(b, new Rectangle(box.X, r.Y, box.Width, box.Y - r.Y), bg);
            Cx.Fill(b, new Rectangle(box.X, box.Bottom, box.Width, r.Bottom - box.Bottom), bg);
            Cx.Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), Cx.Line);

            bool fav = ModEntry.Progress?.IsFavorite(e) ?? false;
            string star = fav ? "★" : "";
            float sw = star.Length > 0 ? Cx.MW(Cx.Body, star) + Cx.Sc(10) : 0;

            string label = Cx.IsUnnamed(e.Name) ? e.Name + "（游戏未命名）" : e.Name;
            Cx.Text(b, Cx.Body, Cx.Truncate(label, Cx.Body, (int)(r.Width - Cx.Sc(46) - sw - Cx.Sc(8))),
                new Vector2(r.X + Cx.Sc(40), r.Y + Cx.Sc(9)), sel ? Color.White : Cx.Ink);
            if (star.Length > 0)
                Cx.Text(b, Cx.Body, star, new Vector2(r.Right - sw - Cx.Sc(4), r.Y + Cx.Sc(9)), Cx.Gold);
        }
    }
}