using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using InGameCodex.Data;

namespace InGameCodex.Ui
{
    /// <summary>图鉴面板的「物品」标签：搜索 + 筛选 + 排序 + 列表 + 右侧常驻详情。</summary>
    public partial class CodexMenu
    {
        private static readonly string[] SortNames = { "名称", "售价", "代码" };

        private TextBox _searchBox;
        private string _query = "";
        private string _typeFilter = "";
        private int _sortMode;
        private bool _onlyMissing;
        private bool _dirty = true;

        private readonly List<CodexItem> _filtered = new List<CodexItem>();
        private readonly List<Hit> _chips = new List<Hit>();
        private readonly ScrollBar _listScroll = new ScrollBar();
        private readonly Dictionary<string, int> _owned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private Rectangle _searchRect, _clearRect, _onlyRect, _sortRect, _favRect;
        private Rectangle _chipsRect, _listRect, _detailRect;

        // ---- S5：详情区顶部的「☆ 收藏」固定条 ----
        private static int DetailHeaderH => Cx.Sc(46);
        private Rectangle _starRect;

        // ---- S3：来源筛选行 + 详情独立滚动（需求文档 §17） ----
        private Rectangle _srcChipsRect;
        private readonly List<Hit> _srcChips = new List<Hit>();
        private string _srcFilter = "";

        private readonly ScrollBar _detailScroll = new ScrollBar();
        private readonly List<DetailLine> _detailLines = new List<DetailLine>();
        private CodexItem _detailFor;

        /// <summary>详情区的一行：文本 + 颜色 + 字体 + 缩进 + 是否分隔线。</summary>
        private class DetailLine
        {
            public string Text;
            public Color Color;
            public bool Head;
            public bool Sep;
            public int Indent;
            public int GapBefore;
            public string Widget;    // S7：非 null 时这一行是自绘控件块（"give" = 获取区）
            public int WidgetH;
        }

        private CodexItem _selected;
        private int _hoverRow = -1;
        private int _iconFailures;
        private bool _iconsDisabled;

        /// <summary>列表绘制 / 命中用的滚动位置：对齐到整行，保证行与列表框严丝合缝（滚动条内部仍用 Pos 保持平滑）。</summary>
        private int ScrollTop => (_listScroll.Pos / RowH) * RowH;

        /// <summary>从别的标签跳过来：切到物品标签，清掉筛选并选中该物品（S4 的 NPC 喜好详情用）。</summary>
        internal void JumpToItem(CodexItem e)
        {
            if (e == null)
                return;

            _typeFilter = "";
            _srcFilter = "";
            _onlyMissing = false;
            _query = "";
            if (_searchBox != null)
                _searchBox.Text = "";
            _dirty = true;

            SetTab(0);
            EnsureFiltered();

            int idx = _filtered.IndexOf(e);
            if (idx < 0)
                return;

            _selected = e;
            _detailFor = null;
            _listScroll.Pos = idx * RowH - _listRect.Height / 2 + RowH / 2;
        }

        // ------------------------------------------------------------------ 初始化

        private void InitItemsUi()
        {
            _searchBox = new TextBox(
                Game1.content.Load<Texture2D>(@"LooseSprites\textBox"),
                ModEntry.Pixel, Cx.Body, Game1.textColor)
            {
                Text = "",
                Selected = false
            };
            _searchBox.OnEnterPressed += _ => SetSearchFocus(false);
        }

        private void RelayoutItems()
        {
            if (_searchBox == null)
                return;

            Rectangle c = _contentRect;

            int searchW = Math.Min(Cx.Sc(420), (int)(c.Width * 0.36));
            _searchRect = new Rectangle(c.X, c.Y, searchW, Cx.Sc(52));
            _searchBox.X = _searchRect.X + Cx.Sc(12);
            _searchBox.Y = _searchRect.Y + Cx.Sc(10);
            _searchBox.Width = _searchRect.Width - Cx.Sc(24);
            _searchBox.Height = _searchRect.Height - Cx.Sc(20);

            int bx = _searchRect.Right + Cx.Sc(10);
            int by = c.Y + Cx.Sc(2);
            _clearRect = new Rectangle(bx, by, Cx.Sc(88), Cx.Sc(48));
            _onlyRect = new Rectangle(bx + Cx.Sc(96), by, Cx.Sc(176), Cx.Sc(48));
            _sortRect = new Rectangle(bx + Cx.Sc(280), by, Cx.Sc(136), Cx.Sc(48));
            _favRect = new Rectangle(bx + Cx.Sc(424), by, Cx.Sc(108), Cx.Sc(48));

            _chipsRect = new Rectangle(c.X, c.Y + Cx.Sc(62), c.Width, Cx.Sc(34));
            LayoutChips();

            _srcChipsRect = new Rectangle(c.X, _chipsRect.Bottom + Cx.Sc(4), c.Width, Cx.Sc(30));
            LayoutSrcChips();

            int bodyTop = _srcChipsRect.Bottom + Cx.Sc(8);
            // v1.4 修复：高度取整行，行永远与列表框对齐且绝不越出内容区
            int avail = c.Bottom - bodyTop;
            int bodyH = avail >= RowH ? (avail / RowH) * RowH : Math.Max(1, avail);
            int listW = (int)(c.Width * 0.55) - 8;

            _listRect = new Rectangle(c.X, bodyTop, listW, bodyH);
            int detailX = _listRect.Right + Cx.Sc(12);
            _detailRect = new Rectangle(detailX, bodyTop, c.Right - detailX, bodyH);

            _listScroll.Track = new Rectangle(_listRect.Right + Cx.Sc(2), _listRect.Y, Cx.Sc(10), _listRect.Height);
            _listScroll.View = _listRect.Height;

            _detailScroll.Track = new Rectangle(_detailRect.Right - Cx.Sc(14), _detailRect.Y + Cx.Sc(6), Cx.Sc(10), _detailRect.Height - Cx.Sc(12));
            _detailScroll.View = _detailRect.Height - DetailHeaderH - Cx.Sc(14);   // S5：详情区顶部留出 DetailHeaderH 的「☆ 收藏」固定条
        }

        private void LayoutChips()
        {
            _chips.Clear();
            int x = _contentRect.X;
            int y = _chipsRect.Y;
            int ch = Cx.Sc(34);

            void Add(string tag, string label)
            {
                int w = (int)Cx.MW(Cx.Body, label) + 26;
                if (x + w > _contentRect.Right && x > _contentRect.X)
                {
                    x = _contentRect.X;
                    y += ch + 6;
                }
                _chips.Add(new Hit { Tag = tag, Label = label, Bounds = new Rectangle(x, y, w, ch) });
                x += w + 6;
            }

            Add("", "全部 " + (ModEntry.Index?.Items.Count ?? 0));
            foreach (CodexType t in CodexTypes.All)
                Add(t.Key, t.Name + " " + (ModEntry.Index?.CountOf(t.Key) ?? 0));

            _chipsRect.Height = y + ch - _chipsRect.Y;
        }

        /// <summary>「来源」筛选行（S3）：全部 + 14 种来源，照 LayoutChips 的自动换行写法。</summary>
        private void LayoutSrcChips()
        {
            _srcChips.Clear();
            int x = _contentRect.X;
            int y = _srcChipsRect.Y;
            int ch = Cx.Sc(30);

            void Add(string tag, string label)
            {
                int w = (int)Cx.MW(Cx.Body, label) + 22;
                if (x + w > _contentRect.Right && x > _contentRect.X)
                {
                    x = _contentRect.X;
                    y += ch + 4;
                }
                _srcChips.Add(new Hit { Tag = tag, Label = label, Bounds = new Rectangle(x, y, w, ch) });
                x += w + 6;
            }

            Add("", "来源：全部");
            foreach (SrcKind k in SrcKindInfo.All)
                Add(k.ToString(), SrcKindInfo.Name(k));

            _srcChipsRect.Height = y + ch - _srcChipsRect.Y;
        }

        // ------------------------------------------------------------------ 筛选与排序

        private void EnsureFiltered()
        {
            if (!_dirty)
                return;
            _dirty = false;

            _filtered.Clear();
            List<CodexItem> all = ModEntry.Index?.Items;
            if (all == null)
            {
                _selected = null;
                return;
            }

            string q = (_query ?? "").Trim();
            IEnumerable<CodexItem> src = all;
            if (!string.IsNullOrEmpty(_typeFilter))
                src = src.Where(e => string.Equals(e.TypeKey, _typeFilter, StringComparison.OrdinalIgnoreCase));

            bool ranked = false;
            if (string.IsNullOrEmpty(q))
            {
                _filtered.AddRange(src);
            }
            else
            {
                var scored = new List<KeyValuePair<int, CodexItem>>();
                foreach (CodexItem e in src)
                {
                    int r = Rank(e, q);
                    if (r >= 0)
                        scored.Add(new KeyValuePair<int, CodexItem>(r, e));
                }
                scored.Sort((a, b) =>
                {
                    if (a.Key != b.Key) return a.Key.CompareTo(b.Key);
                    return string.Compare(a.Value.Name, b.Value.Name, StringComparison.CurrentCulture);
                });
                foreach (var kv in scored)
                    _filtered.Add(kv.Value);
                ranked = true;
            }

            if (_onlyMissing)
                _filtered.RemoveAll(e => OwnedCount(e.Id) > 0);

            // S3：来源筛选（反向索引；节日摊位商店已在「商店」来源内自然覆盖）
            if (!string.IsNullOrEmpty(_srcFilter) && ModEntry.Refs != null
                && Enum.TryParse(_srcFilter, out SrcKind srcKind))
                _filtered.RemoveAll(e => !ModEntry.Refs.HasSource(e, srcKind));

            // 有关键词时保持相关度顺序（名称排序等于不排序），否则按用户选的排序
            if (!(ranked && _sortMode == 0))
            {
                switch (_sortMode)
                {
                    case 1:
                        _filtered.Sort((a, b) =>
                        {
                            int cmp = b.Price.CompareTo(a.Price);
                            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                        });
                        break;
                    case 2:
                        _filtered.Sort(CompareByCode);
                        break;
                    default:
                        _filtered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
                        break;
                }
            }

            _listScroll.Content = _filtered.Count * RowH;
            _listScroll.Pos = 0;

            if (_selected == null || !_filtered.Contains(_selected))
                _selected = _filtered.Count > 0 ? _filtered[0] : null;
        }

        private static int CompareByCode(CodexItem a, CodexItem b)
        {
            bool na = int.TryParse(a.RawId, out int ia);
            bool nb = int.TryParse(b.RawId, out int ib);
            if (na && nb)
            {
                int cmp = ia.CompareTo(ib);
                if (cmp != 0)
                    return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            }
            if (na != nb)
                return na ? -1 : 1;
            return string.Compare(a.RawId, b.RawId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>搜索相关度：越小越靠前，-1 = 不匹配。</summary>
        private static int Rank(CodexItem e, string q)
        {
            string ql = q.ToLowerInvariant();

            if (string.Equals(e.Id, q, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(e.RawId, q, StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(e.Name, q, StringComparison.OrdinalIgnoreCase)) return 2;
            if (!string.IsNullOrEmpty(e.Pinyin) && string.Equals(e.Pinyin, ql, StringComparison.OrdinalIgnoreCase)) return 3;
            if (e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 4;
            if (!string.IsNullOrEmpty(e.Pinyin) && e.Pinyin.StartsWith(ql, StringComparison.OrdinalIgnoreCase)) return 5;
            if (!string.IsNullOrEmpty(e.EnglishName) && e.EnglishName.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 6;
            if (e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return 7;
            if (!string.IsNullOrEmpty(e.EnglishName) && e.EnglishName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return 7;
            if (!string.IsNullOrEmpty(e.Pinyin) && e.Pinyin.IndexOf(ql, StringComparison.OrdinalIgnoreCase) >= 0) return 7;
            if (LooseMatch(e.Name, q)) return 8;
            return -1;
        }

        /// <summary>模糊匹配：去掉末字后整体命中，或任意二字组出现在目标名里。</summary>
        private static bool LooseMatch(string name, string q)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(q) || q.Length < 2)
                return false;

            if (q.Length >= 2 && name.IndexOf(q.Substring(0, q.Length - 1), StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            int hit = 0;
            int total = 0;
            for (int i = 0; i + 1 < q.Length; i++)
            {
                total++;
                if (name.IndexOf(q.Substring(i, 2), StringComparison.OrdinalIgnoreCase) >= 0)
                    hit++;
            }
            return total > 0 && hit * 2 >= total;
        }

        // ------------------------------------------------------------------ 背包与焦点

        internal void RefreshOwned()
        {
            _owned.Clear();
            var inv = Game1.player?.Items;
            if (inv == null)
                return;

            foreach (Item it in inv)
            {
                if (it == null)
                    continue;
                string id = it.QualifiedItemId;
                if (string.IsNullOrEmpty(id))
                    continue;
                _owned[id] = OwnedCount(id) + it.Stack;
            }
        }

        private int OwnedCount(string qid)
            => qid != null && _owned.TryGetValue(qid, out int n) ? n : 0;

        internal bool SearchFocused() => _searchBox != null && _searchBox.Selected;

        internal void SetSearchFocus(bool on)
        {
            if (_searchBox == null)
                return;
            _searchBox.Selected = on;
            Game1.keyboardDispatcher.Subscriber = on ? _searchBox : null;
        }

        // ------------------------------------------------------------------ 交互

        private bool ReceiveItemsClick(int x, int y)
        {
            bool handled = false;

            if (_searchRect.Contains(x, y))
            {
                SetSearchFocus(true);
                handled = true;
            }
            else if (SearchFocused())
            {
                SetSearchFocus(false);
            }

            if (_clearRect.Contains(x, y))
            {
                _query = "";
                if (_searchBox != null)
                    _searchBox.Text = "";
                _dirty = true;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_onlyRect.Contains(x, y))
            {
                _onlyMissing = !_onlyMissing;
                if (_onlyMissing)
                    RefreshOwned();
                _dirty = true;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_sortRect.Contains(x, y))
            {
                _sortMode = (_sortMode + 1) % SortNames.Length;
                _dirty = true;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_favRect.Contains(x, y))
            {
                SetTab(4);   // S5：切到「收藏 · 最近」标签
                return true;
            }

            foreach (Hit chip in _chips)
            {
                if (chip.Bounds.Contains(x, y))
                {
                    _typeFilter = chip.Tag;
                    _dirty = true;
                    Game1.playSound("smallSelect");
                    return true;
                }
            }

            foreach (Hit chip in _srcChips)
            {
                if (chip.Bounds.Contains(x, y))
                {
                    _srcFilter = chip.Tag;
                    _dirty = true;
                    Game1.playSound("smallSelect");
                    return true;
                }
            }

            if (HandleGiveClick(x, y))   // S7：获取区（数量步进 / 品质 / 获取按钮）
                return true;

            if (_detailScroll.TryGrab(x, y))
                return true;

            if (_listScroll.TryGrab(x, y))
                return true;

            if (_listRect.Contains(x, y))
            {
                EnsureFiltered();
                int idx = (ScrollTop + (y - _listRect.Y)) / RowH;
                if (idx >= 0 && idx < _filtered.Count)
                {
                    _selected = _filtered[idx];
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            return handled;
        }

        private bool HeldItemsClick(int x, int y)
        {
            if (_listScroll.Dragging)
            {
                _listScroll.ApplyDrag(y);
                return true;
            }
            if (_detailScroll.Dragging)
            {
                _detailScroll.ApplyDrag(y);
                return true;
            }
            return false;
        }

        private void ReleaseItemsDrag()
        {
            _listScroll.Release();
            _detailScroll.Release();
        }

        private void ScrollItems(int step)
        {
            EnsureFiltered();
            _listScroll.ScrollBy(step * RowH * 3);
        }

        private void ScrollDetail(int step) => _detailScroll.ScrollBy(step * 40);

        private void HoverItems(int x, int y)
        {
            _hoverRow = -1;
            if (!_listRect.Contains(x, y))
                return;

            EnsureFiltered();
            int idx = (ScrollTop + (y - _listRect.Y)) / RowH;
            if (idx >= 0 && idx < _filtered.Count)
                _hoverRow = idx;
        }

        private void UpdateItems(GameTime time)
        {
            if (_searchBox == null)
                return;

            _searchBox.Update();

            if (!string.Equals(_searchBox.Text ?? "", _query ?? "", StringComparison.Ordinal))
            {
                _query = _searchBox.Text ?? "";
                _dirty = true;
                _listScroll.Pos = 0;
            }
        }

        private bool ItemsOnKey(Keys key)
        {
            EnsureFiltered();
            switch (key)
            {
                case Keys.Up: _listScroll.ScrollBy(-RowH); return true;
                case Keys.Down: _listScroll.ScrollBy(RowH); return true;
                case Keys.PageUp: _listScroll.ScrollBy(-_listRect.Height); return true;
                case Keys.PageDown: _listScroll.ScrollBy(_listRect.Height); return true;
                case Keys.Home: _listScroll.Pos = 0; return true;
                case Keys.End: _listScroll.Pos = _listScroll.Max; return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ 绘制

        private void DrawItems(SpriteBatch b)
        {
            EnsureFiltered();

            Cx.Box(b, _searchRect, Cx.PanelBg2, Cx.Line);
            _searchBox.Draw(b);

            DrawButton(b, _clearRect, "清空", false, false);
            DrawButton(b, _onlyRect, _onlyMissing ? "只看我还没有 ✔" : "只看我还没有", _onlyMissing, false);
            DrawButton(b, _sortRect, "排序：" + SortNames[_sortMode], false, false);
            DrawButton(b, _favRect, "收藏夹", false, false);

            string cnt = _filtered.Count + " / " + (ModEntry.Index?.Items.Count ?? 0) + " 条";
            float cw = Cx.MW(Cx.Body, cnt);
            Cx.Text(b, Cx.Body, cnt, new Vector2(_contentRect.Right - cw, _contentRect.Y + 16), Cx.Dim);

            foreach (Hit chip in _chips)
                DrawButton(b, chip.Bounds, chip.Label, string.Equals(chip.Tag, _typeFilter, StringComparison.OrdinalIgnoreCase), true);

            foreach (Hit chip in _srcChips)
                DrawButton(b, chip.Bounds, chip.Label, string.Equals(chip.Tag, _srcFilter, StringComparison.OrdinalIgnoreCase), true);

            Cx.Box(b, _listRect, Cx.PanelBg2, Cx.Line);

            Cx.BeginClip(b, _listRect);
            // v1.4 修复：只画完全落在列表框内的整行（此前上下各多画 1~2 行，Pos 非整行时顶行会画到框外）
            int top = ScrollTop;
            int rows = Math.Max(1, _listRect.Height / RowH);
            int first = Math.Max(0, top / RowH);
            int last = Math.Min(_filtered.Count, first + rows);
            // v1.6：行分两趟画。图标（尤其家具的大贴图）会越出 36x36 格子，而 SDV 的 Item.drawInMenu 内部会
            // 重启 SpriteBatch，使 ScissorRectangle 在图标绘制期间失效（实测图标能画到列表框外）；因此图标画完
            // 后立刻把格子以外的底板补回，等效于把图标裁在格子内；文字放在第二趟，保证不被补底覆盖。
            for (int i = first; i < last; i++)
                DrawRowBase(b, _filtered[i], i);
            for (int i = first; i < last; i++)
                DrawRowText(b, _filtered[i], i);
            Cx.EndClip(b);

            _listScroll.Draw(b);
            DrawDetail(b);
        }

        private void DrawButton(SpriteBatch b, Rectangle r, string label, bool on, bool tight)
        {
            Cx.Fill(b, r, on ? Cx.Selected : Cx.PanelBg);
            Cx.Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), on ? Cx.Gold : Cx.Line);

            string text = tight ? Cx.Truncate(label, Cx.Body, r.Width - 12) : label;
            float tw = Cx.MW(Cx.Body, text);
            float th = Cx.MH(Cx.Body, "Hg");
            Cx.Text(b, Cx.Body, text, new Vector2(r.X + (r.Width - tw) / 2f, r.Y + (r.Height - th) / 2f),
                on ? Color.White : Cx.Ink);
        }

        private Rectangle RowRect(int index)
            => new Rectangle(_listRect.X, _listRect.Y + index * RowH - ScrollTop, _listRect.Width, RowH);

        /// <summary>第一趟：行底 + 图标 + 补底（把图标裁在格子内）。文字见 DrawRowText。</summary>
        private void DrawRowBase(SpriteBatch b, CodexItem e, int index)
        {
            Rectangle row = RowRect(index);
            bool sel = ReferenceEquals(e, _selected);
            bool hov = index == _hoverRow;
            Color bg = sel ? Cx.Selected : (hov ? Cx.Hover : Cx.PanelBg2);

            if (sel || hov)
                Cx.Fill(b, row, bg);
            Cx.Fill(b, new Rectangle(row.X, row.Bottom - 1, row.Width, 1), Cx.Line);

            Rectangle box = new Rectangle(row.X + Cx.Sc(4), row.Y + Cx.Sc(6), Cx.Sc(36), Cx.Sc(36));
            DrawIcon(b, e, box);

            // v1.6 等效裁剪：把图标格子以外、行以内的底板补回（图标可能宽出格子压到文字或相邻行）
            Cx.Fill(b, new Rectangle(row.X, row.Y, box.X - row.X, RowH), bg);
            Cx.Fill(b, new Rectangle(box.Right, row.Y, row.Right - box.Right, RowH), bg);
            Cx.Fill(b, new Rectangle(box.X, row.Y, box.Width, box.Y - row.Y), bg);
            Cx.Fill(b, new Rectangle(box.X, box.Bottom, box.Width, row.Bottom - box.Bottom), bg);
            // 图标还可能越出列表框左右两侧（那是面板底色），补回后重画行底与列表框边线
            Cx.Fill(b, new Rectangle(_listRect.X - Cx.Sc(12), row.Y, Cx.Sc(12), RowH), Cx.PageBg);
            Cx.Fill(b, new Rectangle(_listRect.Right, row.Y, Cx.Sc(12), RowH), Cx.PageBg);
            Cx.Fill(b, new Rectangle(row.X, row.Bottom - 1, row.Width, 1), Cx.Line);
            Cx.Fill(b, new Rectangle(row.X, row.Y, 1, RowH), Cx.Line);
            Cx.Fill(b, new Rectangle(row.Right - 1, row.Y, 1, RowH), Cx.Line);
        }

        /// <summary>第二趟：行内文字（名字 / 副行 / 代码 / 已有徽标）。</summary>
        private void DrawRowText(SpriteBatch b, CodexItem e, int index)
        {
            Rectangle row = RowRect(index);
            bool sel = ReferenceEquals(e, _selected);

            bool owned = OwnedCount(e.Id) > 0;
            string code = "#" + e.RawId;
            float codeW = Cx.MW(Cx.Body, code);

            int tx = row.X + Cx.Sc(56);   // v1.6：文字起点右移与图标格子拉开间距；S5 起随 UiScale 缩放
            int nameMax = (int)(row.Width - (tx - row.X) - codeW - Cx.Sc(26));

            // S5：第二行右侧显示「★」收藏标记与图鉴角标（捐 / 挖 / 钓 / 谱）
            bool fav = ModEntry.Progress?.IsFavorite(e) ?? false;
            string marks = (fav ? "★ " : "") + BadgeText(e);
            float marksW = marks.Length > 0 ? Cx.MW(Cx.Body, marks) + Cx.Sc(10) : 0;

            string shownName = Cx.IsUnnamed(e.Name) ? e.Name + "（游戏未命名）" : e.Name;
            string name = Cx.Truncate(shownName, Cx.Body, Math.Max(Cx.Sc(50), nameMax - (owned ? Cx.Sc(46) : 0) - (int)marksW));
            Cx.Text(b, Cx.Body, name, new Vector2(tx, row.Y + Cx.Sc(-2)), sel ? Color.White : Cx.Ink);

            string sub = e.TypeName;
            if (ModEntry.Config.ShowEnglishName && !string.IsNullOrEmpty(e.EnglishName) && !Cx.IsUnnamed(e.EnglishName))
                sub += " · " + e.EnglishName;
            Cx.Text(b, Cx.Body, Cx.Truncate(sub, Cx.Body, Math.Max(Cx.Sc(50), nameMax - (int)marksW)), new Vector2(tx, row.Y + Cx.Sc(16)), sel ? Cx.Ink : Cx.Dim);

            if (marks.Length > 0)
                Cx.Text(b, Cx.Body, marks, new Vector2(row.Right - marksW, row.Y + Cx.Sc(16)), fav ? Cx.Gold : Cx.Blue);

            Cx.Text(b, Cx.Body, code, new Vector2(row.Right - codeW - Cx.Sc(10), row.Y + Cx.Sc(8)), Cx.Dim);

            if (owned)
            {
                string badge = "已有";
                float bw = Cx.MW(Cx.Body, badge);
                Cx.Text(b, Cx.Body, badge, new Vector2(row.Right - codeW - bw - Cx.Sc(18), row.Y + Cx.Sc(8)), Cx.Green);
            }
        }

        /// <summary>S5：列表行右侧的图鉴角标（捐 = 已捐博物馆 / 挖 = 已挖到古物或已发现矿物 / 钓 = 已钓到 / 谱 = 有配方）。</summary>
        private static string BadgeText(CodexItem e)
        {
            CodexProgress p = ModEntry.Progress;
            if (p == null || !p.IsBuilt || e == null)
                return "";

            string s = "";
            if (p.IsDonated(e))
                s += "捐";
            if (p.IsArtifactFound(e) || p.IsMineralFound(e))
                s += "挖";
            if (p.IsFishCaught(e))
                s += "钓";
            if (p.IsRecipeKnown(e))
                s += "谱";
            return s.Length > 0 ? "[" + s + "]" : "";
        }

        /// <summary>S5：详情里「我的状态」后面补一句图鉴进度。</summary>
        private static string FoundText(CodexItem e)
        {
            CodexProgress p = ModEntry.Progress;
            if (p == null || !p.IsBuilt || e == null)
                return "";

            var parts = new List<string>();
            if (p.IsDonated(e))
                parts.Add("已捐博物馆");
            if (p.IsArtifactFound(e))
                parts.Add("已挖到古物");
            if (p.IsMineralFound(e))
                parts.Add("已发现矿物");
            if (p.IsFishCaught(e))
                parts.Add("已钓到");
            if (p.IsRecipeKnown(e))
                parts.Add("有配方");
            return parts.Count > 0 ? string.Join(" · ", parts) : "";
        }

        /// <summary>S5：详情区顶部「☆ 收藏」按钮的点击（物品页与收藏页共用）。</summary>
        internal bool HandleStarClick(int x, int y)
        {
            if (_tab != 0 && _tab != 4)
                return false;
            if (_selected == null || !_starRect.Contains(x, y))
                return false;

            CodexProgress p = ModEntry.Progress;
            if (p == null || !p.IsBuilt)
            {
                ShowToast("进度数据还没准备好：先进一次存档");
                return true;
            }

            p.ToggleFavorite(_selected);
            Game1.playSound("smallSelect");
            _detailFor = null;   // 让详情重画一次（收藏状态变了）
            return true;
        }

        private void DrawIcon(SpriteBatch b, CodexItem e, Rectangle box)
        {
            if (!_iconsDisabled && !e.IconFailed && e.Item != null)
            {
                try
                {
                    // v1.5：按贴图实际尺寸等比缩放进格子并居中。
                    // 物体/工具/武器/帽子等贴图是 16x16，游戏内部按 scaleSize*4 绘制，box.Width/64 时正好 36px 填满格子；
                    // 家具贴图从 16x16 到 96x96+ 都有、大型可制作物是 16x32，沿用同一比例会出现「一小点」或宽出格子压住文字与相邻行。
                    float k = box.Width / 64f;
                    if (e.Item is StardewValley.Objects.Furniture f)
                    {
                        // v1.6：ItemRegistry 创建的家具有时候 sourceRect 是空的，退回 defaultSourceRect
                        int sw = f.sourceRect.Width, sh = f.sourceRect.Height;
                        if (sw <= 0 || sh <= 0)
                        {
                            sw = f.defaultSourceRect.Width;
                            sh = f.defaultSourceRect.Height;
                        }
                        if (sw > 0 && sh > 0)
                            k = Math.Min(2f, box.Width * 0.9f / Math.Max(1, Math.Max(sw, sh)));
                    }
                    else if (e.Item is StardewValley.Object o && o.bigCraftable.Value)
                    {
                        k = box.Height / 128f;
                    }

                    if (k > 0f)
                    {
                        // 反推绘制原点，让任意尺寸的贴图都以格子中心为中心。
                        Vector2 loc = new Vector2(box.X + box.Width / 2f - 32f * k, box.Y + box.Height / 2f - 32f * k);
                        e.Item.drawInMenu(b, loc, k, 1f, 0.9f, StackDrawType.Hide, Color.White, false);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    e.IconFailed = true;
                    _iconFailures++;
                    if (_iconFailures == 1)
                        ModEntry.Log?.Log("物品贴图绘制失败，改用类别色块：" + e.Id + " / " + ex.Message, LogLevel.Warn);
                    if (_iconFailures >= 20)
                    {
                        _iconsDisabled = true;
                        ModEntry.Log?.Log("贴图失败次数过多，本次会话改用类别色块。", LogLevel.Warn);
                    }
                }
            }

            Cx.Fill(b, box, Cx.TypeColor(e.TypeKey));
            Cx.Fill(b, new Rectangle(box.X, box.Y, box.Width, 1), Cx.Line);
            Cx.Fill(b, new Rectangle(box.X, box.Bottom - 1, box.Width, 1), Cx.Line);

            string key = e.TypeKey;
            float kw = Cx.MW(Cx.Body, key);
            Cx.Text(b, Cx.Body, key, new Vector2(box.X + (box.Width - kw) / 2f, box.Y + Cx.Sc(9)), Cx.Ink);
        }

        private void DrawDetail(SpriteBatch b)
        {
            Cx.Box(b, _detailRect, Cx.PanelBg, Cx.Line);

            CodexItem e = _selected;
            if (e == null)
            {
                Cx.Text(b, Cx.Body, "左边点一条物品查看详情", new Vector2(_detailRect.X + Cx.Sc(16), _detailRect.Y + Cx.Sc(14)), Cx.Dim);
                return;
            }

            if (_detailFor != e)
            {
                _detailFor = e;
                _detailScroll.Pos = 0;
                _giveCount = 1;      // S7：换物品就把数量与品质重置（第 9 轮问答第 5 条）
                _giveQuality = 0;
                CloseConfirm();
                BuildDetailLines(e);
                ModEntry.Progress?.NoteRecent(e);   // S5：进过详情就算「最近查看」
            }

            // S5：详情区顶部固定条 —— 「☆ 收藏」按钮（不随详情滚动）
            bool favorite = ModEntry.Progress?.IsFavorite(e) ?? false;
            _starRect = new Rectangle(_detailRect.X + Cx.Sc(12), _detailRect.Y + Cx.Sc(8), Cx.Sc(130), Cx.Sc(30));
            DrawButton(b, _starRect, favorite ? "★ 已收藏" : "☆ 收藏", favorite, false);
            if (ModEntry.Progress == null || !ModEntry.Progress.IsBuilt)
                Cx.Text(b, Cx.Body, "（进度数据未就绪：先进一次存档）", new Vector2(_starRect.Right + Cx.Sc(12), _detailRect.Y + Cx.Sc(15)), Cx.Dim);
            Cx.Fill(b, new Rectangle(_detailRect.X + Cx.Sc(8), _detailRect.Y + DetailHeaderH - 1, _detailRect.Width - Cx.Sc(16), 1), Cx.Line);

            int px = _detailRect.X + Cx.Sc(16);
            int pw = Math.Max(Cx.Sc(120), _detailRect.Width - Cx.Sc(46));   // 右侧留出滚动条位置
            int lh = Cx.LineHeight(Cx.Body);
            int headLh = Cx.LineHeight(Cx.Head);

            // 先量出每行高度与总高度，再按 _detailScroll.Pos 画（详情区独立滚动，需求文档 §17.4）
            var heights = new List<int>(_detailLines.Count);
            var wraps = new List<List<string>>(_detailLines.Count);
            int total = Cx.Sc(14);
            foreach (DetailLine dl in _detailLines)
            {
                if (dl.Widget != null)
                {
                    heights.Add(dl.WidgetH);
                    wraps.Add(null);
                    total += dl.WidgetH;
                    continue;
                }
                if (dl.Sep)
                {
                    heights.Add(Cx.Sc(11));
                    wraps.Add(null);
                    total += Cx.Sc(11);
                    continue;
                }
                List<string> lines = dl.Head ? Cx.Wrap(dl.Text, Cx.Head, pw) : Cx.Wrap(dl.Text, Cx.Body, pw);
                int h = (dl.Head ? headLh : lh) * Math.Max(1, lines.Count) + dl.GapBefore;
                heights.Add(h);
                wraps.Add(lines);
                total += h;
            }
            total += Cx.Sc(14);
            _detailScroll.Content = total;

            _giveRectsValid = false;   // S7：本帧没有重画获取区时不允许命中（避免滚动出界后还能点到）
            Cx.BeginClip(b, new Rectangle(_detailRect.X + 1, _detailRect.Y + DetailHeaderH, _detailRect.Width - 2, _detailRect.Height - DetailHeaderH - 1));

            int y = _detailRect.Y + DetailHeaderH + Cx.Sc(8) - _detailScroll.Pos;
            for (int i = 0; i < _detailLines.Count; i++)
            {
                DetailLine dl = _detailLines[i];
                int h = heights[i];
                if (y + h < _detailRect.Y || y > _detailRect.Bottom)
                {
                    y += h;
                    continue;
                }

                if (dl.Sep)
                {
                    Cx.Fill(b, new Rectangle(px, y + Cx.Sc(4), pw, 1), Cx.Line);
                    y += h;
                    continue;
                }

                y += dl.GapBefore;
                if (dl.Widget != null)
                {
                    DrawGiveWidget(b, px, y, pw);   // S7：获取区（数量 / 品质 / 获取按钮）
                    y += h;
                    continue;
                }

                int lineH = dl.Head ? headLh : lh;
                int x = px + dl.Indent * Cx.Sc(14);
                List<string> lines = wraps[i];
                for (int k = 0; k < lines.Count; k++)
                {
                    if (y > _detailRect.Bottom)
                        break;
                    Cx.Text(b, dl.Head ? Cx.Head : Cx.Body, lines[k],
                        new Vector2(x, y - (dl.Head ? Cx.Sc(6) : 0)), dl.Color);
                    y += lineH;
                }
            }

            Cx.EndClip(b);
            _detailScroll.Draw(b);

            DrawConfirm(b);   // S7：二次确认弹窗（画在详情与滚动条之上）
        }

        /// <summary>把一条物品的详情组织成行式布局（S3：用途 / 获取来源 / 获取物品）。</summary>
        private void BuildDetailLines(CodexItem e)
        {
            _detailLines.Clear();

            void Raw(string t, Color c, bool head = false, bool sep = false, int indent = 0, int gap = 0)
                => _detailLines.Add(new DetailLine { Text = t, Color = c, Head = head, Sep = sep, Indent = indent, GapBefore = gap });

            Raw(e.Name, Cx.Green, true);
            if (ModEntry.Config.ShowEnglishName && !string.IsNullOrEmpty(e.EnglishName) && !Cx.IsUnnamed(e.EnglishName))
                Raw(e.EnglishName, Cx.Dim);
            if (Cx.IsUnnamed(e.Name))
                Raw("这条物品在游戏数据里没有正式名称（显示为 ???）", Cx.Dim);

            Raw(string.Join("   ", new[] { e.TypeName, "代码 #" + e.RawId, e.Id }), Cx.Ink, false, false, 0, Cx.Sc(6));
            Raw("售价：" + e.PriceText, Cx.Gold);

            int have = OwnedCount(e.Id);
            string status = have > 0 ? $"我的状态：背包 ×{have}" : "我的状态：还没有";
            string found = FoundText(e);
            if (found.Length > 0)
                status += "　· " + found;
            Raw(status, have > 0 ? Cx.Green : Cx.Dim);

            CodexRefIndex refs = ModEntry.Refs;

            // ---------------- 有什么用 ----------------
            Raw("", Cx.Ink, false, true, 0, 2);
            Raw("有什么用", Cx.Gold, true);
            if (refs == null)
            {
                Raw("（用途索引尚未构建：请先进一次存档）", Cx.Dim);
            }
            else
            {
                IReadOnlyList<UseLine> uses = refs.Use(e);
                if (uses == null || uses.Count == 0)
                {
                    Raw("暂无用途数据", Cx.Dim);
                }
                else
                {
                    var order = new List<string>();
                    var groups = new Dictionary<string, List<string>>();
                    foreach (UseLine u in uses)
                    {
                        string text = u.Text ?? "";
                        int i = text.IndexOf('：');
                        string label = i > 0 ? text.Substring(0, i) : "其它";
                        string body = i > 0 ? text.Substring(i + 1) : text;
                        if (!groups.TryGetValue(label, out var list))
                        {
                            list = new List<string>();
                            groups[label] = list;
                            order.Add(label);
                        }
                        if (!list.Contains(body))
                            list.Add(body);
                    }
                    foreach (string label in order)
                    {
                        List<string> list = groups[label];
                        Raw(label, Cx.Ink, false, false, 0, 4);
                        int show = Math.Min(list.Count, 4);
                        for (int i = 0; i < show; i++)
                            Raw("· " + list[i], Cx.Dim, false, false, 1);
                        if (list.Count > show)
                            Raw($"（还有 {list.Count - show} 条）", Cx.Dim, false, false, 1);
                    }
                }
            }

            // ---------------- 怎么获得 ----------------
            Raw("", Cx.Ink, false, true, 0, 4);
            Raw("怎么获得", Cx.Gold, true);
            if (refs == null)
            {
                Raw("（来源索引尚未构建：请先进一次存档）", Cx.Dim);
            }
            else
            {
                IReadOnlyList<SrcLine> srcs = refs.Obtain(e);
                if (srcs == null || srcs.Count == 0)
                {
                    Raw("暂无来源数据（可能来自事件、邮件、随机掉落或 Mod 内容）", Cx.Dim);
                }
                else
                {
                    foreach (SrcKind kind in SrcKindInfo.All)
                    {
                        var list = new List<string>();
                        string pfx = SrcKindInfo.Name(kind) + "：";
                        foreach (SrcLine s in srcs)
                        {
                            if (s.Kind != kind)
                                continue;
                            string text = s.Text ?? "";
                            if (text.StartsWith(pfx, StringComparison.Ordinal))
                                text = text.Substring(pfx.Length);
                            if (!list.Contains(text))
                                list.Add(text);
                        }
                        if (list.Count == 0)
                            continue;

                        Raw(SrcKindInfo.Name(kind) + "（" + list.Count + "）", Cx.Ink, false, false, 0, 4);
                        int show = Math.Min(list.Count, 8);
                        for (int i = 0; i < show; i++)
                            Raw("· " + list[i], Cx.Dim, false, false, 1);
                        if (list.Count > show)
                            Raw($"（还有 {list.Count - show} 处）", Cx.Dim, false, false, 1);
                    }
                }
            }

            // ---------------- 获取物品（S7） ----------------
            Raw("", Cx.Ink, false, true, 0, 4);
            _detailLines.Add(new DetailLine { Widget = GiveWidgetKey, WidgetH = GiveBlockHeight(e), Color = Cx.Ink });   // S7：获取区（自绘控件块）
        }

        // ================= S7：直接获取物品（需求文档 §21） =================

        private const string GiveWidgetKey = "give";
        /// <summary>S7：获取区高度 —— 无品质类型会多一行「为什么没有品质」的说明（v1.10.1）。</summary>
        private static int GiveBlockHeight(CodexItem e)
            => Cx.Sc(118) + (CanQuality(e) ? 0 : Cx.Sc(27));
        private static readonly string[] QualityNames = { "普通", "银", "金", "铱" };

        private int _giveCount = 1;
        private int _giveQuality;

        private bool _giveRectsValid;
        private Rectangle _giveClip;
        private Rectangle _giveBlockRect;
        private readonly Rectangle[] _giveDown = new Rectangle[3];
        private readonly int[] _giveDownStep = { -100, -10, -1 };
        private readonly Rectangle[] _giveUp = new Rectangle[3];
        private readonly int[] _giveUpStep = { 1, 10, 100 };
        private Rectangle _giveCountBox;
        private readonly Rectangle[] _giveQualityRect = new Rectangle[4];
        private Rectangle _giveGoRect;

        private bool _confirmOpen;
        private CodexItem _confirmFor;
        private int _confirmCount, _confirmQuality;
        private Rectangle _confirmOkRect, _confirmCancelRect;

        /// <summary>S7（v1.10.1）：该物品能不能带品质 —— 只有「物体」(`O`) 类的普通物体可以。
        /// 说明：家具 / 壁纸 / 地板 / 饰品在运行时虽然是 `StardewValley.Object` 的子类，但游戏不使用它们的品质，
        /// 故这里额外要求图鉴类型是 `O`，与需求文档 §21.6 的表一致。</summary>
        private static bool CanQuality(CodexItem e)
            => e != null && e.TypeKey == "O" && e.Item is StardewValley.Object o && !o.bigCraftable.Value;

        /// <summary>S7（v1.10.1）：该类型为什么没有品质 —— 按图鉴类型给出原因，代替原来的统一短句。</summary>
        private static string QualityHint(CodexItem e)
        {
            switch (e?.TypeKey)
            {
                case "T":
                    return "工具没有品质：工具靠铁匠铺升级变强（铜 → 钢 → 金 → 铱）";
                case "W":
                    return "武器没有品质";
                case "BC":
                    return "大型可制作物没有品质";
                case "F":
                    return "家具没有品质";
                case "H":
                    return "帽子没有品质";
                case "S":
                case "P":
                    return "衣服没有品质";
                case "B":
                    return "鞋子没有品质";
                case "TR":
                    return "饰品没有品质";
                case "M":
                    return "人偶没有品质";
                case "WP":
                case "FL":
                    return "壁纸和地板没有品质";
                default:
                    return "该类型在游戏里没有品质";
            }
        }

        /// <summary>S7：单堆上限（不可堆叠的类型返回 1）。</summary>
        private static int MaxStack(CodexItem e)
        {
            try
            {
                return Math.Max(1, e?.Item?.maximumStackSize() ?? 1);
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>S7：详情区最后一块 —— 获取区（数量步进 / 品质四档 / 获取按钮 + 状态提示）。</summary>
        private void DrawGiveWidget(SpriteBatch b, int px, int y, int pw)
        {
            CodexItem e = _selected;
            if (e == null)
                return;

            int rowH = Cx.Sc(30);
            int gap = Cx.Sc(6);
            int labelX = px;
            int ctrlX = px + Cx.Sc(58);
            bool stackable = MaxStack(e) > 1;
            bool canQ = CanQuality(e);
            bool ready = Context.IsWorldReady && Game1.player != null;

            _giveBlockRect = new Rectangle(px, y, pw, GiveBlockHeight(e));

            // ---- 第 1 行：数量 ----
            Cx.Text(b, Cx.Body, "数量", new Vector2(labelX, y + Cx.Sc(7)), Cx.Ink);
            if (stackable)
            {
                int x = ctrlX;
                int bw = Cx.Sc(52);
                for (int i = 0; i < 3; i++)
                {
                    _giveDown[i] = new Rectangle(x, y, bw, rowH);
                    DrawButton(b, _giveDown[i], _giveDownStep[i].ToString(), false, false);
                    x += bw + Cx.Sc(4);
                }

                string cnt = "×" + _giveCount;
                int cw = Cx.Sc(96);
                _giveCountBox = new Rectangle(x, y, cw, rowH);
                Cx.Fill(b, _giveCountBox, Cx.PageBg);
                Cx.Fill(b, new Rectangle(_giveCountBox.X, _giveCountBox.Bottom - 1, cw, 1), Cx.Line);
                float tw = Cx.MW(Cx.Body, cnt);
                Cx.Text(b, Cx.Body, cnt, new Vector2(_giveCountBox.X + (cw - tw) / 2f, _giveCountBox.Y + (rowH - Cx.MH(Cx.Body, "Hg")) / 2f), Cx.Gold);
                x += cw + Cx.Sc(4);

                for (int i = 0; i < 3; i++)
                {
                    _giveUp[i] = new Rectangle(x, y, bw, rowH);
                    DrawButton(b, _giveUp[i], "+" + _giveUpStep[i], false, false);
                    x += bw + Cx.Sc(4);
                }
            }
            else
            {
                _giveCountBox = new Rectangle(ctrlX, y, Cx.Sc(300), rowH);
                Cx.Text(b, Cx.Body, "1（不可堆叠：该类型数量固定为 1）", new Vector2(ctrlX, y + Cx.Sc(7)), Cx.Dim);
            }

            // ---- 第 2 行：品质 ----
            int row2 = y + rowH + gap;
            Cx.Text(b, Cx.Body, "品质", new Vector2(labelX, row2 + Cx.Sc(7)), Cx.Ink);
            int qx = ctrlX;
            int qw = Cx.Sc(68);
            for (int i = 0; i < 4; i++)
            {
                _giveQualityRect[i] = new Rectangle(qx, row2, qw, rowH);
                if (canQ)
                    DrawButton(b, _giveQualityRect[i], QualityNames[i], i == _giveQuality, false);
                else
                    DrawDisabledButton(b, _giveQualityRect[i], QualityNames[i]);
                qx += qw + Cx.Sc(4);
            }
            if (!canQ)
            {
                // v1.10.1：不再只写一句「没有品质」，改为按类型写明原因（多占一行，高度见 GiveBlockHeight）
                Cx.Text(b, Cx.Body, "（不可选）", new Vector2(qx + Cx.Sc(4), row2 + Cx.Sc(7)), Cx.Dim);
                Cx.Text(b, Cx.Body, Cx.Truncate("为什么不可选：" + QualityHint(e), Cx.Body, Math.Max(Cx.Sc(120), pw - Cx.Sc(8))),
                    new Vector2(ctrlX, row2 + rowH + Cx.Sc(3)), Cx.Dim);
            }

            // ---- 第 3 行：获取按钮 + 状态提示 ----
            int row3 = row2 + rowH + gap + (canQ ? 0 : Cx.Sc(27));
            _giveGoRect = new Rectangle(px, row3, Cx.Sc(140), Cx.Sc(34));
            if (ready)
                DrawButton(b, _giveGoRect, "获取", true, false);
            else
                DrawDisabledButton(b, _giveGoRect, "获取");

            int showCount = stackable ? _giveCount : 1;
            int showQ = canQ ? _giveQuality : 0;
            string hint = "将获得：" + (showQ > 0 ? QualityNames[showQ] + "品质 " : "") + e.Name + " ×" + showCount;
            hint += ready
                ? (ModEntry.Config.NeedConfirm(showCount, showQ, false) ? "　·　需要二次确认" : "　·　直接获取")
                : "　·　进一次存档才能获取";
            Cx.Text(b, Cx.Body, Cx.Truncate(hint, Cx.Body, Math.Max(Cx.Sc(80), pw - _giveGoRect.Width - Cx.Sc(40))),
                new Vector2(_giveGoRect.Right + Cx.Sc(10), row3 + Cx.Sc(9)), Cx.Dim);

            _giveClip = new Rectangle(_detailRect.X + 1, _detailRect.Y + DetailHeaderH, _detailRect.Width - 2, _detailRect.Height - DetailHeaderH - 1);
            _giveRectsValid = true;
        }

        /// <summary>S7：置灰按钮（画法同 DrawButton，只是底色与文字用暗色）。</summary>
        private void DrawDisabledButton(SpriteBatch b, Rectangle r, string label)
        {
            Cx.Fill(b, r, Cx.PageBg);
            Cx.Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), Cx.Line);
            float tw = Cx.MW(Cx.Body, label);
            float th = Cx.MH(Cx.Body, "Hg");
            Cx.Text(b, Cx.Body, label, new Vector2(r.X + (r.Width - tw) / 2f, r.Y + (r.Height - th) / 2f), Cx.Dim);
        }

        /// <summary>S7：获取区的点击。返回 true 表示这次点击已被获取区处理（含吞掉空白点击）。</summary>
        private bool HandleGiveClick(int x, int y)
        {
            if (!_giveRectsValid || _selected == null || !_giveClip.Contains(x, y))
                return false;

            // 控件必须完整落在详情裁剪区内才可点（滚动到一半的不响应）
            bool Hit(Rectangle r) => r.Width > 0 && r.Height > 0 && _giveClip.Contains(r) && r.Contains(x, y);

            if (Hit(_giveGoRect))
            {
                TryGive();
                return true;
            }

            for (int i = 0; i < 3; i++)
            {
                if (Hit(_giveDown[i]))
                {
                    StepGive(_giveDownStep[i]);
                    return true;
                }
                if (Hit(_giveUp[i]))
                {
                    StepGive(_giveUpStep[i]);
                    return true;
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (!Hit(_giveQualityRect[i]))
                    continue;
                if (!CanQuality(_selected))
                {
                    ShowToast(QualityHint(_selected));
                    return true;
                }
                _giveQuality = i;
                Game1.playSound("smallSelect");
                return true;
            }

            // 点在获取区空白处：吞掉，避免误触下面的滚动条 / 列表
            return Hit(_giveBlockRect);
        }

        private void StepGive(int delta)
        {
            CodexItem e = _selected;
            if (e == null)
                return;
            int max = MaxStack(e) > 1 ? 9999 : 1;
            _giveCount = Math.Max(1, Math.Min(max, _giveCount + delta));
            Game1.playSound("smallSelect");
        }

        /// <summary>S7：点「获取」——按 ConfirmMode 决定直接给还是先弹确认框。</summary>
        private void TryGive()
        {
            CodexItem e = _selected;
            if (e == null)
                return;
            if (!Context.IsWorldReady || Game1.player == null)
            {
                ShowToast("先进一次存档才能获取物品");
                return;
            }

            int count = MaxStack(e) > 1 ? _giveCount : 1;
            int quality = CanQuality(e) ? _giveQuality : 0;
            if (ModEntry.Config.NeedConfirm(count, quality, false))
            {
                _confirmOpen = true;
                _confirmFor = e;
                _confirmCount = count;
                _confirmQuality = quality;
                Game1.playSound("smallSelect");
                return;
            }

            DoGive(e, count, quality);
        }

        /// <summary>S7：关掉确认弹窗（切标签 / 换物品 / 取消时调用）。</summary>
        internal void CloseConfirm()
        {
            _confirmOpen = false;
            _confirmFor = null;
        }

        /// <summary>S7：确认弹窗打开时吃掉全部点击（返回 true = 已处理）。</summary>
        internal bool HandleConfirmClick(int x, int y)
        {
            if (!_confirmOpen)
                return false;

            if (_confirmOkRect.Contains(x, y))
            {
                CodexItem e = _confirmFor;
                int c = _confirmCount;
                int q = _confirmQuality;
                CloseConfirm();
                if (e != null)
                    DoGive(e, c, q);
                return true;
            }

            if (_confirmCancelRect.Contains(x, y))
            {
                CloseConfirm();
                Game1.playSound("cancel");
            }
            return true;   // 弹窗打开期间吞掉其它全部点击
        }

        /// <summary>S7：面板内自绘的二次确认弹窗。</summary>
        private void DrawConfirm(SpriteBatch b)
        {
            if (!_confirmOpen || _confirmFor == null)
                return;

            Rectangle panel = new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height);
            Cx.Fill(b, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), new Color(0, 0, 0, 150));

            int w = Cx.Sc(480);
            int h = Cx.Sc(196);
            Rectangle box = new Rectangle(panel.X + (panel.Width - w) / 2, panel.Y + (panel.Height - h) / 2, w, h);
            Cx.Fill(b, box, Cx.PanelBg);
            Cx.Fill(b, new Rectangle(box.X, box.Y, box.Width, 2), Cx.Gold);
            Cx.Fill(b, new Rectangle(box.X, box.Bottom - 2, box.Width, 2), Cx.Gold);
            Cx.Fill(b, new Rectangle(box.X, box.Y, 2, box.Height), Cx.Gold);
            Cx.Fill(b, new Rectangle(box.Right - 2, box.Y, 2, box.Height), Cx.Gold);

            Cx.Text(b, Cx.Head, "确认获取", new Vector2(box.X + Cx.Sc(20), box.Y + Cx.Sc(10)), Cx.Gold);

            Rectangle icon = new Rectangle(box.X + Cx.Sc(20), box.Y + Cx.Sc(66), Cx.Sc(36), Cx.Sc(36));
            DrawIcon(b, _confirmFor, icon);
            string qn = _confirmQuality > 0 ? QualityNames[_confirmQuality] + "品质 " : "";
            string line = "将获得：" + _confirmFor.Name + " " + qn + "×" + _confirmCount;
            Cx.Text(b, Cx.Body, Cx.Truncate(line, Cx.Body, w - Cx.Sc(84)), new Vector2(icon.Right + Cx.Sc(12), box.Y + Cx.Sc(74)), Cx.Ink);

            _confirmOkRect = new Rectangle(box.Right - Cx.Sc(240), box.Bottom - Cx.Sc(54), Cx.Sc(100), Cx.Sc(34));
            _confirmCancelRect = new Rectangle(box.Right - Cx.Sc(128), box.Bottom - Cx.Sc(54), Cx.Sc(100), Cx.Sc(34));
            DrawButton(b, _confirmOkRect, "确认", true, false);
            DrawButton(b, _confirmCancelRect, "取消", false, false);
            Cx.Text(b, Cx.Body, "Esc 也可以取消", new Vector2(box.X + Cx.Sc(20), box.Bottom - Cx.Sc(44)), Cx.Dim);
        }

        /// <summary>S7：真正把物品给玩家（需求文档 §5.6：进背包，放不下掉在脚下，绝不丢弃）。</summary>
        private void DoGive(CodexItem e, int count, int quality)
        {
            if (e == null || !Context.IsWorldReady || Game1.player == null)
                return;

            count = Math.Max(1, Math.Min(9999, count));
            quality = CanQuality(e) ? Math.Max(0, Math.Min(3, quality)) : 0;
            string qn = quality > 0 ? QualityNames[quality] + "品质 " : "";
            int maxStack = MaxStack(e);
            int given = 0;
            int dropped = 0;

            try
            {
                int remaining = count;
                while (remaining > 0)
                {
                    int take = Math.Min(remaining, maxStack);   // 超过单堆上限时分批，语义与游戏一致
                    Item item = ItemRegistry.Create(e.Id, take, quality);
                    if (item == null)
                    {
                        HudMsg("游戏里没有这个物品：" + e.Id);
                        return;
                    }
                    item.resetState();
                    if (Game1.player.addItemToInventoryBool(item))
                    {
                        given += take;
                    }
                    else
                    {
                        Game1.createItemDebris(item, Game1.player.getStandingPosition(), 1, Game1.player.currentLocation);
                        dropped += take;
                    }
                    remaining -= take;
                }
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log("获取物品失败：" + e.Id + " / " + ex.Message, LogLevel.Warn);
                HudMsg("获取失败：" + ex.Message);
                return;
            }

            string msg;
            if (dropped == 0)
                msg = "已获得：" + e.Name + " " + qn + "×" + given;
            else if (given == 0)
                msg = "背包已满：" + e.Name + " " + qn + "×" + dropped + " 掉在了脚下";
            else
                msg = "已获得：" + e.Name + " " + qn + "×" + given + "，另有 ×" + dropped + " 掉在了脚下";

            HudMsg(msg);
            Game1.playSound("coin");
        }

        private static void HudMsg(string msg) => Game1.addHUDMessage(new HUDMessage(msg, 2));
    }
}
