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
    /// <summary>图鉴面板的「图鉴完成度」标签（S5）：左边六项进度，右边还缺哪些（可点进物品详情）。</summary>
    public partial class CodexMenu
    {
        private static int SectionH => Cx.Sc(52);
        private static int ColRowH => Cx.Sc(30);

        private sealed class ColCell
        {
            public CodexItem Item;
            public string Label = "";
            public bool Header;
            public int Row;
            public int Col;
        }

        private TextBox _colSearchBox;
        private string _colQuery = "";
        private bool _colDirty = true;
        private Rectangle _cSearchRect, _cClearRect, _cSectionRect, _cListRect;
        private readonly ScrollBar _cSectionScroll = new ScrollBar();
        private readonly ScrollBar _cListScroll = new ScrollBar();
        private readonly List<ColCell> _colCells = new List<ColCell>();
        private int _colSection;
        private int _colRows;
        private int _colHoverRow = -1;
        private int _colHoverCol = -1;

        private int ColScrollTop => (_cListScroll.Pos / ColRowH) * ColRowH;

        private void InitCollectionUi()
        {
            _colSearchBox = new TextBox(
                Game1.content.Load<Texture2D>(@"LooseSprites\textBox"),
                ModEntry.Pixel, Cx.Body, Game1.textColor)
            {
                Text = "",
                Selected = false
            };
            _colSearchBox.OnEnterPressed += _ => SetCollectionSearchFocus(false);
        }

        private void RelayoutCollection()
        {
            if (_colSearchBox == null)
                return;

            Rectangle c = _contentRect;
            int searchW = Math.Min(Cx.Sc(360), (int)(c.Width * 0.30));
            _cSearchRect = new Rectangle(c.X, c.Y, searchW, Cx.Sc(52));
            _colSearchBox.X = _cSearchRect.X + Cx.Sc(12);
            _colSearchBox.Y = _cSearchRect.Y + Cx.Sc(10);
            _colSearchBox.Width = _cSearchRect.Width - Cx.Sc(24);
            _colSearchBox.Height = _cSearchRect.Height - Cx.Sc(20);
            _cClearRect = new Rectangle(_cSearchRect.Right + Cx.Sc(10), c.Y + Cx.Sc(2), Cx.Sc(88), Cx.Sc(48));

            int bodyTop = c.Y + Cx.Sc(62);
            int avail = Math.Max(Cx.Sc(80), c.Bottom - bodyTop);
            int secW = (int)(c.Width * 0.36) - 8;
            _cSectionRect = new Rectangle(c.X, bodyTop, secW, avail);
            int listX = _cSectionRect.Right + Cx.Sc(12);
            _cListRect = new Rectangle(listX, bodyTop, Math.Max(Cx.Sc(160), c.Right - listX), avail);

            _cSectionScroll.Track = new Rectangle(_cSectionRect.Right + Cx.Sc(2), _cSectionRect.Y, Cx.Sc(10), _cSectionRect.Height);
            _cSectionScroll.View = _cSectionRect.Height;
            _cListScroll.Track = new Rectangle(_cListRect.Right - Cx.Sc(12), _cListRect.Y + Cx.Sc(4), Cx.Sc(10), _cListRect.Height - Cx.Sc(8));
            _cListScroll.View = _cListRect.Height;
        }

        private bool CollectionSearchFocused() => _colSearchBox != null && _colSearchBox.Selected;

        private void SetCollectionSearchFocus(bool on)
        {
            if (_colSearchBox == null)
                return;
            _colSearchBox.Selected = on;
            Game1.keyboardDispatcher.Subscriber = on ? _colSearchBox : null;
        }

        private static int ColCellWidth(Rectangle r) => Math.Max(Cx.Sc(60), (r.Width - Cx.Sc(16)) / 3);

        // ------------------------------------------------------------------ 组织缺失清单

        private void EnsureCollectionFiltered()
        {
            if (!_colDirty)
                return;
            _colDirty = false;
            _colCells.Clear();
            _colRows = 0;

            CodexProgress p = ModEntry.Progress;
            if (p == null || !p.IsBuilt || p.Sections.Count == 0)
            {
                _cListScroll.Content = 0;
                _cSectionScroll.Content = 0;
                return;
            }

            _colSection = Math.Max(0, Math.Min(p.Sections.Count - 1, _colSection));
            CodexProgress.Section sec = p.Sections[_colSection];
            string q = (_colQuery ?? "").Trim();

            int row = 0;
            int col = 0;
            string group = null;

            void Header(string text)
            {
                col = 0;
                _colCells.Add(new ColCell { Header = true, Label = text, Row = row, Col = 0 });
                row++;
                group = text;
            }

            void Add(CodexItem item, string label)
            {
                _colCells.Add(new ColCell { Item = item, Label = label, Row = row, Col = col });
                col++;
                if (col >= 3)
                {
                    col = 0;
                    row++;
                }
            }

            if (sec.Kind >= 4)
            {
                foreach (string recipe in sec.RecipeNames)
                {
                    if (p.IsRecipeNameKnown(recipe))
                        continue;
                    CodexItem outItem = p.RecipeOutput(recipe);
                    string label = outItem != null ? outItem.Name : recipe;
                    if (q.Length > 0 && !NameMatches(outItem, label, q))
                        continue;
                    Add(outItem, label);
                }
            }
            else
            {
                foreach (CodexItem e in sec.Missing)
                {
                    if (e == null)
                        continue;
                    if (q.Length > 0 && !NameMatches(e, e.Name, q))
                        continue;
                    if (sec.Kind == 0 && !string.Equals(group, e.TypeName, StringComparison.Ordinal))
                        Header(e.TypeName);
                    Add(e, e.Name);
                }
            }

            _colRows = row + (col > 0 ? 1 : 0);
            _cListScroll.Content = _colRows * ColRowH;
            _cSectionScroll.Content = p.Sections.Count * SectionH;
            if (_cListScroll.Pos > _cListScroll.Max)
                _cListScroll.Pos = 0;
        }

        private static bool NameMatches(CodexItem e, string label, string q)
        {
            if (!string.IsNullOrEmpty(label) && label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (e == null)
                return false;
            if (!string.IsNullOrEmpty(e.EnglishName) && e.EnglishName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return !string.IsNullOrEmpty(e.Pinyin) && e.Pinyin.StartsWith(q, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ 交互

        private bool ReceiveCollectionClick(int x, int y)
        {
            bool handled = false;
            if (_cSearchRect.Contains(x, y))
            {
                SetCollectionSearchFocus(true);
                handled = true;
            }
            else if (CollectionSearchFocused())
            {
                SetCollectionSearchFocus(false);
            }

            if (_cClearRect.Contains(x, y))
            {
                _colQuery = "";
                if (_colSearchBox != null)
                    _colSearchBox.Text = "";
                _colDirty = true;
                _cListScroll.Pos = 0;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_cSectionScroll.TryGrab(x, y) || _cListScroll.TryGrab(x, y))
                return true;

            if (_cSectionRect.Contains(x, y))
            {
                int idx = (_cSectionScroll.Pos / SectionH * SectionH + (y - _cSectionRect.Y)) / SectionH;
                CodexProgress p = ModEntry.Progress;
                if (p != null && idx >= 0 && idx < p.Sections.Count)
                {
                    _colSection = idx;
                    _colDirty = true;
                    _cListScroll.Pos = 0;
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            if (_cListRect.Contains(x, y))
            {
                EnsureCollectionFiltered();
                int row = (ColScrollTop + (y - _cListRect.Y)) / ColRowH;
                int col = (x - _cListRect.X - 8) / ColCellWidth(_cListRect);
                if (col >= 0 && col < 3)
                {
                    foreach (ColCell cell in _colCells)
                    {
                        if (cell.Header || cell.Row != row || cell.Col != col)
                            continue;
                        if (cell.Item != null)
                        {
                            JumpToItem(cell.Item);
                            return true;
                        }
                        Game1.playSound("smallSelect");
                        return true;
                    }
                }
                return true;
            }

            return handled;
        }

        private bool HeldCollectionClick(int x, int y)
        {
            if (_cListScroll.Dragging)
            {
                _cListScroll.ApplyDrag(y);
                return true;
            }
            if (_cSectionScroll.Dragging)
            {
                _cSectionScroll.ApplyDrag(y);
                return true;
            }
            return false;
        }

        private void ReleaseCollectionDrag()
        {
            _cListScroll.Release();
            _cSectionScroll.Release();
        }

        private void ScrollCollection(int step)
        {
            int my = Game1.getMouseY();
            int mx = Game1.getMouseX();
            if (_cSectionRect.Contains(mx, my) || _cSectionScroll.Track.Contains(mx, my))
                _cSectionScroll.ScrollBy(step * SectionH);
            else
                _cListScroll.ScrollBy(step * ColRowH * 3);
        }

        private void HoverCollection(int x, int y)
        {
            _colHoverRow = -1;
            _colHoverCol = -1;
            if (!_cListRect.Contains(x, y))
                return;
            EnsureCollectionFiltered();
            _colHoverRow = (ColScrollTop + (y - _cListRect.Y)) / ColRowH;
            _colHoverCol = (x - _cListRect.X - 8) / ColCellWidth(_cListRect);
        }

        private void UpdateCollection(GameTime time)
        {
            if (_colSearchBox == null)
                return;
            _colSearchBox.Update();
            if (!string.Equals(_colSearchBox.Text ?? "", _colQuery ?? "", StringComparison.Ordinal))
            {
                _colQuery = _colSearchBox.Text ?? "";
                _colDirty = true;
                _cListScroll.Pos = 0;
            }
        }

        private bool CollectionOnKey(Keys key)
        {
            EnsureCollectionFiltered();
            switch (key)
            {
                case Keys.Up: _cListScroll.ScrollBy(-ColRowH); return true;
                case Keys.Down: _cListScroll.ScrollBy(ColRowH); return true;
                case Keys.PageUp: _cListScroll.ScrollBy(-_cListRect.Height); return true;
                case Keys.PageDown: _cListScroll.ScrollBy(_cListRect.Height); return true;
                case Keys.Home: _cListScroll.Pos = 0; return true;
                case Keys.End: _cListScroll.Pos = _cListScroll.Max; return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 绘制

        private void DrawCollection(SpriteBatch b)
        {
            EnsureCollectionFiltered();

            Cx.Box(b, _cSearchRect, Cx.PanelBg2, Cx.Line);
            _colSearchBox.Draw(b);
            DrawButton(b, _cClearRect, "清空", false, false);

            CodexProgress p = ModEntry.Progress;
            if (p == null || !p.IsBuilt)
            {
                Cx.Text(b, Cx.Body, "图鉴进度数据还没准备好：先进一次存档再打开面板。",
                    new Vector2(_contentRect.X + Cx.Sc(16), _contentRect.Y + Cx.Sc(90)), Cx.Dim);
                return;
            }

            if (p.Sections.Count > _colSection)
            {
                CodexProgress.Section sec = p.Sections[_colSection];
                string cnt = "还缺 " + Math.Max(0, sec.Total - sec.Found) + " 件";
                float cw = Cx.MW(Cx.Body, cnt);
                Cx.Text(b, Cx.Body, cnt, new Vector2(_contentRect.Right - cw, _contentRect.Y + Cx.Sc(16)), Cx.Dim);
            }

            if (!p.SaveReadable)
                Cx.Text(b, Cx.Body, "（没读到存档进度：下面按「还没完成」显示）",
                    new Vector2(_contentRect.Right - Cx.Sc(320), _contentRect.Y + Cx.Sc(40)), Cx.Gold);

            // ---- 左：六项进度 ----
            Cx.Box(b, _cSectionRect, Cx.PanelBg2, Cx.Line);
            Cx.BeginClip(b, _cSectionRect);
            int stop = (_cSectionScroll.Pos / SectionH) * SectionH;
            for (int i = 0; i < p.Sections.Count; i++)
            {
                var rr = new Rectangle(_cSectionRect.X, _cSectionRect.Y + i * SectionH - stop, _cSectionRect.Width, SectionH);
                if (rr.Bottom < _cSectionRect.Y || rr.Y > _cSectionRect.Bottom)
                    continue;

                CodexProgress.Section sec = p.Sections[i];
                bool sel = i == _colSection;
                if (sel)
                    Cx.Fill(b, rr, Cx.Selected);
                Cx.Fill(b, new Rectangle(rr.X, rr.Bottom - 1, rr.Width, 1), Cx.Line);

                Cx.Text(b, Cx.Body, sec.Title, new Vector2(rr.X + Cx.Sc(10), rr.Y + Cx.Sc(6)), sel ? Color.White : Cx.Ink);
                string num = sec.Found + " / " + sec.Total;
                float nw = Cx.MW(Cx.Body, num);
                Cx.Text(b, Cx.Body, num, new Vector2(rr.Right - nw - Cx.Sc(16), rr.Y + Cx.Sc(6)),
                    sec.Found >= sec.Total && sec.Total > 0 ? Cx.Green : (sel ? Cx.Ink : Cx.Dim));

                var bar = new Rectangle(rr.X + Cx.Sc(10), rr.Y + Cx.Sc(30), Math.Max(Cx.Sc(20), rr.Width - Cx.Sc(26)), Cx.Sc(8));
                Cx.Fill(b, bar, Cx.PageBg);
                if (sec.Total > 0)
                {
                    int fw = (int)(bar.Width * Math.Min(1f, sec.Found / (float)sec.Total));
                    if (fw > 0)
                        Cx.Fill(b, new Rectangle(bar.X, bar.Y, fw, bar.Height), sec.Found >= sec.Total ? Cx.Green : Cx.Gold);
                }
            }
            Cx.EndClip(b);
            _cSectionScroll.Draw(b);

            // ---- 右：还缺哪些 ----
            Cx.Box(b, _cListRect, Cx.PanelBg2, Cx.Line);
            Cx.BeginClip(b, _cListRect);
            int top = ColScrollTop;
            int cellW = ColCellWidth(_cListRect);
            foreach (ColCell cell in _colCells)
            {
                var rr = new Rectangle(_cListRect.X + Cx.Sc(8) + cell.Col * cellW,
                    _cListRect.Y + cell.Row * ColRowH - top, cellW - Cx.Sc(6), ColRowH - Cx.Sc(2));
                if (rr.Bottom < _cListRect.Y || rr.Y > _cListRect.Bottom)
                    continue;

                if (cell.Header)
                {
                    Cx.Text(b, Cx.Body, cell.Label, new Vector2(rr.X + Cx.Sc(2), rr.Y + Cx.Sc(6)), Cx.Gold);
                    Cx.Fill(b, new Rectangle(rr.X + Cx.Sc(2), rr.Bottom - Cx.Sc(2), Math.Max(Cx.Sc(20), _cListRect.Width - Cx.Sc(24)), 1), Cx.Line);
                    continue;
                }

                bool hov = cell.Row == _colHoverRow && cell.Col == _colHoverCol;
                if (hov)
                    Cx.Fill(b, rr, Cx.Hover);

                var box = new Rectangle(rr.X + Cx.Sc(2), rr.Y + Cx.Sc(2), Cx.Sc(22), Cx.Sc(22));
                if (cell.Item != null)
                    DrawIcon(b, cell.Item, box);
                else
                {
                    Cx.Fill(b, box, Cx.PanelBg);
                    Cx.Text(b, Cx.Body, "谱", new Vector2(box.X + Cx.Sc(8), box.Y + Cx.Sc(5)), Cx.Dim);
                }

                string label = cell.Item != null && Cx.IsUnnamed(cell.Item.Name) ? cell.Label + "（未命名）" : cell.Label;
                Cx.Text(b, Cx.Body, Cx.Truncate(label, Cx.Body, cellW - Cx.Sc(40)), new Vector2(rr.X + Cx.Sc(28), rr.Y + Cx.Sc(6)),
                    hov ? Color.White : Cx.Ink);
            }
            Cx.EndClip(b);
            _cListScroll.Draw(b);

            if (_colCells.Count == 0)
                Cx.Text(b, Cx.Body, "这一项已经全部完成，或者没有匹配的结果。",
                    new Vector2(_cListRect.X + Cx.Sc(12), _cListRect.Y + Cx.Sc(12)), Cx.Dim);
        }
    }
}