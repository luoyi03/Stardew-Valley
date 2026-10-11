using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using InGameCodex.Data;

namespace InGameCodex.Ui
{
    /// <summary>S4「NPC 喜好」标签：列表 + 五档详情。口径见需求文档 §18。</summary>
    public partial class CodexMenu
    {
        private static int NpcTopH => Cx.Sc(108);      // 详情顶部固定信息区（名字 / 英文名 / 生日 / 可结婚）
        private static int NpcEntryH => Cx.Sc(36);     // 详情里一个物品格子的高度

        private TextBox _npcSearchBox;
        private string _npcQuery = "";
        private int _npcSort;                 // 0 = 名称，1 = 生日
        private bool _npcOnlyMarry;
        private bool _npcSearchFocused;
        private bool _npcDirty = true;

        private readonly List<CodexNpc> _npcFiltered = new List<CodexNpc>();
        private CodexNpc _npcSelected;
        private int _npcHoverRow = -1;

        private readonly ScrollBar _npcListScroll = new ScrollBar();
        private readonly ScrollBar _npcDetailScroll = new ScrollBar();

        private Rectangle _npcSearchRect;
        private Rectangle _npcClearRect;
        private Rectangle _npcSortRect;
        private Rectangle _npcMarryRect;
        private Rectangle _npcListRect;
        private Rectangle _npcDetailRect;
        private Rectangle _npcDetailArea;

        private readonly List<NpcEntry> _npcEntries = new List<NpcEntry>();
        private CodexNpc _npcEntriesFor;
        private NpcEntry _npcHoverEntry;

        private Texture2D _npcPortrait;
        private string _npcPortraitKey;
        private readonly HashSet<string> _npcPortraitMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>详情里的一行：标题 / 类别行 / 物品格子。</summary>
        private sealed class NpcEntry
        {
            public int Y;
            public int Col;
            public int H;
            public string Text;
            public bool Head;
            public CodexItem Item;
        }

        // ------------------------------------------------------------------ 初始化

        private void InitNpcsUi()
        {
            _npcSearchBox = new TextBox(
                Game1.content.Load<Texture2D>("LooseSprites" + (char)92 + "textBox"),
                ModEntry.Pixel, Cx.Body, Game1.textColor)
            {
                Text = "",
                Selected = false
            };
            _npcSearchBox.OnEnterPressed += _ => SetNpcSearchFocus(false);
        }

        private void RelayoutNpcs()
        {
            if (_npcSearchBox == null)
                return;

            Rectangle c = _contentRect;

            int searchW = Math.Min(Cx.Sc(360), (int)(c.Width * 0.30));
            _npcSearchRect = new Rectangle(c.X, c.Y, searchW, Cx.Sc(52));
            _npcSearchBox.X = _npcSearchRect.X + Cx.Sc(12);
            _npcSearchBox.Y = _npcSearchRect.Y + Cx.Sc(10);
            _npcSearchBox.Width = _npcSearchRect.Width - Cx.Sc(24);
            _npcSearchBox.Height = _npcSearchRect.Height - Cx.Sc(20);

            int bx = _npcSearchRect.Right + Cx.Sc(10);
            int by = c.Y + Cx.Sc(2);
            _npcClearRect = new Rectangle(bx, by, Cx.Sc(88), Cx.Sc(48));
            _npcSortRect = new Rectangle(bx + Cx.Sc(96), by, Cx.Sc(152), Cx.Sc(48));
            _npcMarryRect = new Rectangle(bx + Cx.Sc(256), by, Cx.Sc(152), Cx.Sc(48));

            int top = _npcSearchRect.Bottom + Cx.Sc(8);
            int avail = c.Bottom - top;
            int bodyH = avail >= RowH ? (avail / RowH) * RowH : Math.Max(1, avail);
            int listW = (int)(c.Width * 0.42) - 8;
            _npcListRect = new Rectangle(c.X, top, listW, bodyH);
            _npcDetailRect = new Rectangle(_npcListRect.Right + Cx.Sc(12), top, c.Right - (_npcListRect.Right + Cx.Sc(12)), bodyH);

            _npcListScroll.Track = new Rectangle(_npcListRect.Right + Cx.Sc(2), _npcListRect.Y, Cx.Sc(10), _npcListRect.Height);
            _npcListScroll.View = _npcListRect.Height;
            _npcDetailScroll.Track = new Rectangle(_npcDetailRect.Right - Cx.Sc(14), _npcDetailRect.Y + Cx.Sc(6), Cx.Sc(10), _npcDetailRect.Height - Cx.Sc(12));
            _npcDetailScroll.View = Math.Max(Cx.Sc(40), _npcDetailRect.Height - NpcTopH);

            _npcEntriesFor = null;   // 布局变了，详情重新排一遍
        }

        // ------------------------------------------------------------------ 数据

        private void EnsureNpcs()
        {
            if (ModEntry.Npcs == null)
                return;
            try
            {
                ModEntry.Npcs.EnsureBuilt(ModEntry.Index, ModEntry.Refs, ModEntry.Log);
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log("NPC 索引构建失败：" + ex, LogLevel.Error);
            }
        }

        private void EnsureNpcsFiltered()
        {
            if (!_npcDirty)
                return;

            EnsureNpcs();
            _npcFiltered.Clear();

            string q = (_npcQuery ?? "").Trim().ToLowerInvariant();
            List<CodexNpc> all = ModEntry.Npcs != null ? ModEntry.Npcs.Npcs : new List<CodexNpc>();
            foreach (CodexNpc n in all)
            {
                if (_npcOnlyMarry && !n.CanMarry)
                    continue;
                if (q.Length > 0)
                {
                    string name = (n.Name ?? "").ToLowerInvariant();
                    string en = (n.EnglishName ?? "").ToLowerInvariant();
                    string py = (n.Pinyin ?? "").ToLowerInvariant();
                    if (name.IndexOf(q, StringComparison.Ordinal) < 0
                        && en.IndexOf(q, StringComparison.Ordinal) < 0
                        && !py.StartsWith(q, StringComparison.Ordinal))
                        continue;
                }
                _npcFiltered.Add(n);
            }

            if (_npcSort == 1)
                _npcFiltered.Sort(CompareNpcBirthday);
            else
                _npcFiltered.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

            // 「全员通用」固定第一（不参与名称/生日排序）。
            int uni = _npcFiltered.FindIndex(n => n.Universal);
            if (uni > 0)
            {
                CodexNpc u = _npcFiltered[uni];
                _npcFiltered.RemoveAt(uni);
                _npcFiltered.Insert(0, u);
            }

            _npcListScroll.Content = _npcFiltered.Count * RowH;
            _npcListScroll.Pos = 0;
            _npcDirty = false;

            if (_npcSelected == null || !_npcFiltered.Contains(_npcSelected))
            {
                _npcSelected = _npcFiltered.Count > 0 ? _npcFiltered[0] : null;
                _npcEntriesFor = null;
            }
        }

        private static int CompareNpcBirthday(CodexNpc a, CodexNpc b)
        {
            int da = a.DayOfYear;
            int db = b.DayOfYear;
            if (da < 0 && db < 0)
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            if (da < 0)
                return 1;
            if (db < 0)
                return -1;
            int cmp = da.CompareTo(db);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
        }

        /// <summary>生日相对世界日的说明（无存档时不显示剩余天数）。</summary>
        private static string BirthdaySuffix(CodexNpc n)
        {
            if (Game1.player == null || !n.HasBirthday)
                return "";
            int today = CurrentDayOfYear();
            int left = (n.DayOfYear - today + 112) % 112;
            return left == 0 ? "（今天生日）" : "（距今还有 " + left + " 天）";
        }

        private static int CurrentDayOfYear()
        {
            int season = 0;
            string s = (Game1.currentSeason ?? "").ToLowerInvariant();
            if (s.StartsWith("summer"))
                season = 1;
            else if (s.StartsWith("fall"))
                season = 2;
            else if (s.StartsWith("winter"))
                season = 3;
            int day = Math.Max(1, Game1.dayOfMonth);
            return season * 28 + (day - 1);
        }

        private Texture2D GetPortrait(string key)
        {
            if (key == null || _npcPortraitMissing.Contains(key))
                return null;
            if (_npcPortrait != null && _npcPortraitKey == key)
                return _npcPortrait;
            try
            {
                Texture2D t = Game1.content.Load<Texture2D>("Portraits" + (char)92 + key);
                if (t == null || t.Width < 64 || t.Height < 64)
                {
                    _npcPortraitMissing.Add(key);
                    return null;
                }
                _npcPortrait = t;
                _npcPortraitKey = key;
                return t;
            }
            catch
            {
                _npcPortraitMissing.Add(key);
                _npcPortrait = null;
                _npcPortraitKey = key;
                return null;
            }
        }

        // ------------------------------------------------------------------ 交互

        private void SetNpcSearchFocus(bool on)
        {
            _npcSearchFocused = on;
            if (_npcSearchBox == null)
                return;
            _npcSearchBox.Selected = on;
            Game1.keyboardDispatcher.Subscriber = on ? _npcSearchBox : null;
        }

        private bool NpcSearchFocused() => _npcSearchFocused;

        private bool ReceiveNpcsClick(int x, int y)
        {
            if (_npcSearchRect.Contains(x, y))
            {
                SetNpcSearchFocus(true);
                return true;
            }

            if (_npcSearchFocused)
                SetNpcSearchFocus(false);

            if (_npcClearRect.Contains(x, y))
            {
                _npcQuery = "";
                _npcSearchBox.Text = "";
                _npcDirty = true;
                Game1.playSound("drumkit6");
                return true;
            }

            if (_npcSortRect.Contains(x, y))
            {
                _npcSort = (_npcSort + 1) % 2;
                _npcDirty = true;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_npcMarryRect.Contains(x, y))
            {
                _npcOnlyMarry = !_npcOnlyMarry;
                _npcDirty = true;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_npcDetailScroll.TryGrab(x, y))
                return true;
            if (_npcListScroll.TryGrab(x, y))
                return true;

            if (_npcListRect.Contains(x, y))
            {
                int idx = NpcRowIndexAt(y);
                if (idx >= 0 && idx < _npcFiltered.Count)
                {
                    if (!ReferenceEquals(_npcSelected, _npcFiltered[idx]))
                    {
                        _npcSelected = _npcFiltered[idx];
                        _npcEntriesFor = null;
                        _npcDetailScroll.Pos = 0;
                        Game1.playSound("smallSelect");
                    }
                    return true;
                }
            }

            NpcEntry e = NpcEntryAt(x, y);
            if (e != null && e.Item != null)
            {
                JumpToItem(e.Item);
                return true;
            }

            return false;
        }

        private bool HeldNpcsClick(int x, int y)
        {
            if (_npcListScroll.Dragging)
            {
                _npcListScroll.ApplyDrag(y);
                return true;
            }
            if (_npcDetailScroll.Dragging)
            {
                _npcDetailScroll.ApplyDrag(y);
                return true;
            }
            return false;
        }

        private void ReleaseNpcsDrag()
        {
            _npcListScroll.Release();
            _npcDetailScroll.Release();
        }

        private void ScrollNpcs(int step) => _npcListScroll.ScrollBy(step * RowH * 3);

        private void ScrollNpcDetail(int step) => _npcDetailScroll.ScrollBy(step * 40);

        private void UpdateNpcs(GameTime time)
        {
            if (_npcSearchBox == null)
                return;
            string t = _npcSearchBox.Text ?? "";
            if (t != _npcQuery)
            {
                _npcQuery = t;
                _npcDirty = true;
            }
        }

        private bool NpcsOnKey(Keys key)
        {
            if (key == Keys.Up)
            {
                MoveNpcSelection(-1);
                return true;
            }
            if (key == Keys.Down)
            {
                MoveNpcSelection(1);
                return true;
            }
            if (key == Keys.PageUp)
            {
                MoveNpcSelection(-Math.Max(1, _npcListRect.Height / RowH));
                return true;
            }
            if (key == Keys.PageDown)
            {
                MoveNpcSelection(Math.Max(1, _npcListRect.Height / RowH));
                return true;
            }
            if (key == Keys.Home)
            {
                SelectNpcIndex(0);
                return true;
            }
            if (key == Keys.End)
            {
                SelectNpcIndex(_npcFiltered.Count - 1);
                return true;
            }
            return false;
        }

        private void MoveNpcSelection(int delta)
        {
            if (_npcFiltered.Count == 0)
                return;
            int idx = _npcSelected == null ? 0 : _npcFiltered.IndexOf(_npcSelected);
            SelectNpcIndex(idx < 0 ? 0 : idx + delta);
        }

        /// <summary>从别的标签跳过来：切到 NPC 标签，清掉筛选并选中该 NPC（S9 的 AI 回答链接用）。</summary>
        internal void JumpToNpc(CodexNpc n)
        {
            if (n == null)
                return;

            _npcQuery = "";
            _npcOnlyMarry = false;
            if (_npcSearchBox != null)
                _npcSearchBox.Text = "";
            _npcDirty = true;

            SetTab(1);
            EnsureNpcsFiltered();

            int idx = _npcFiltered.IndexOf(n);
            if (idx < 0)
                return;

            _npcSelected = n;
            _npcEntriesFor = null;
            _npcDetailScroll.Pos = 0;
            _npcListScroll.Pos = idx * RowH - _npcListRect.Height / 2 + RowH / 2;
            Game1.playSound("smallSelect");
        }

        private void SelectNpcIndex(int idx)
        {
            if (_npcFiltered.Count == 0)
                return;
            idx = Math.Max(0, Math.Min(_npcFiltered.Count - 1, idx));
            _npcSelected = _npcFiltered[idx];
            _npcEntriesFor = null;
            _npcDetailScroll.Pos = 0;
            NpcEnsureVisible(idx);
            Game1.playSound("smallSelect");
        }

        private void NpcEnsureVisible(int idx)
        {
            int y = idx * RowH;
            if (y < _npcListScroll.Pos)
                _npcListScroll.Pos = y;
            else if (y + RowH > _npcListScroll.Pos + _npcListRect.Height)
                _npcListScroll.Pos = y + RowH - _npcListRect.Height;
        }

        private int NpcScrollTop() => (_npcListScroll.Pos / RowH) * RowH;

        private int NpcRowIndexAt(int y) => (NpcScrollTop() + (y - _npcListRect.Y)) / RowH;

        private NpcEntry NpcEntryAt(int x, int y)
        {
            if (_npcSelected == null || _npcDetailArea.Contains(x, y) == false)
                return null;
            int top = _npcDetailScroll.Pos;
            int colW = Math.Max(80, (_npcDetailRect.Width - 46) / 2);
            foreach (NpcEntry e in _npcEntries)
            {
                if (e.Item == null)
                    continue;
                int ey = _npcDetailArea.Y + e.Y - top;
                int ex = _npcDetailArea.X + 10 + e.Col * colW;
                if (new Rectangle(ex - 4, ey, colW - 6, e.H).Contains(x, y))
                    return e;
            }
            return null;
        }

        private void HoverNpcs(int x, int y)
        {
            _npcHoverRow = -1;
            _npcHoverEntry = null;
            if (_npcListRect.Contains(x, y))
            {
                int idx = NpcRowIndexAt(y);
                if (idx >= 0 && idx < _npcFiltered.Count)
                    _npcHoverRow = idx;
            }
            _npcHoverEntry = NpcEntryAt(x, y);
        }

        // ------------------------------------------------------------------ 绘制

        private void DrawNpcs(SpriteBatch b)
        {
            EnsureNpcsFiltered();
            Rectangle c = _contentRect;

            Cx.Fill(b, _npcSearchRect, Cx.PanelBg2);
            _npcSearchBox.Draw(b);
            DrawButton(b, _npcClearRect, "清空", false, false);
            DrawButton(b, _npcSortRect, "排序：" + (_npcSort == 1 ? "生日" : "名称"), false, true);
            DrawButton(b, _npcMarryRect, "只看可结婚", _npcOnlyMarry, false);

            int total = ModEntry.Npcs != null ? ModEntry.Npcs.Npcs.Count : 0;
            string count = _npcFiltered.Count + " / " + total + " 位";
            float cw = Cx.MW(Cx.Body, count);
            Cx.Text(b, Cx.Body, count, new Vector2(c.Right - cw, c.Y + Cx.Sc(16)), Cx.Dim);

            Cx.Box(b, _npcListRect, Cx.PanelBg2, Cx.Line);
            Cx.BeginClip(b, _npcListRect);
            int top = NpcScrollTop();
            int rows = Math.Max(1, _npcListRect.Height / RowH);
            int first = Math.Max(0, top / RowH);
            int last = Math.Min(_npcFiltered.Count, first + rows);
            for (int i = first; i < last; i++)
                DrawNpcRowBase(b, i, top);
            for (int i = first; i < last; i++)
                DrawNpcRowText(b, i, top);
            Cx.EndClip(b);
            _npcListScroll.Draw(b);

            DrawNpcDetail(b);
        }

        private Rectangle NpcRowRect(int index, int top)
            => new Rectangle(_npcListRect.X, _npcListRect.Y + index * RowH - top, _npcListRect.Width, RowH);

        private void DrawNpcRowBase(SpriteBatch b, int index, int top)
        {
            CodexNpc n = _npcFiltered[index];
            Rectangle row = NpcRowRect(index, top);
            bool sel = ReferenceEquals(n, _npcSelected);
            bool hov = index == _npcHoverRow;
            Color bg = sel ? Cx.Selected : (hov ? Cx.Hover : Cx.PanelBg2);
            Cx.Fill(b, row, bg);

            Rectangle box = new Rectangle(row.X + Cx.Sc(4), row.Y + Cx.Sc(6), Cx.Sc(36), Cx.Sc(36));
            DrawNpcPortrait(b, n, box);

            // 头像贴着格子画，万一贴图越界，把格子外补回行底色（不依赖 Scissor 裁剪）
            FillSafe(b, new Rectangle(row.X, row.Y, box.X - row.X, RowH), bg);
            FillSafe(b, new Rectangle(box.X, row.Y, box.Width, box.Y - row.Y), bg);
            FillSafe(b, new Rectangle(box.Right, box.Y, row.Right - box.Right, box.Height), bg);
            FillSafe(b, new Rectangle(row.X, box.Bottom, row.Width, row.Bottom - box.Bottom), bg);
            FillSafe(b, new Rectangle(_npcListRect.X - Cx.Sc(12), row.Y, Cx.Sc(12), RowH), Cx.PageBg);
            FillSafe(b, new Rectangle(_npcListRect.Right, row.Y, Cx.Sc(12), RowH), Cx.PageBg);
            Cx.Fill(b, new Rectangle(row.X, row.Bottom - 1, row.Width, 1), Cx.Line);
        }

        private static void FillSafe(SpriteBatch b, Rectangle r, Color c)
        {
            if (r.Width > 0 && r.Height > 0)
                Cx.Fill(b, r, c);
        }

        private void DrawNpcPortrait(SpriteBatch b, CodexNpc n, Rectangle box)
        {
            Texture2D tex = n.Universal ? null : GetPortrait(n.Key);
            if (tex != null)
            {
                float k = box.Width / 64f;
                Vector2 loc = new Vector2(box.X + box.Width / 2f - 32f * k, box.Y + box.Height / 2f - 32f * k);
                b.Draw(tex, loc, new Rectangle(0, 0, 64, 64), Color.White, 0f, Vector2.Zero, k, SpriteEffects.None, 0.9f);
                return;
            }

            Cx.Fill(b, box, new Color(Cx.PanelBg.R + 24, Cx.PanelBg.G + 20, Cx.PanelBg.B + 14));
            string ch = string.IsNullOrEmpty(n.Name) ? "?" : n.Name.Substring(0, 1);
            Vector2 size = new Vector2(Cx.MW(Cx.Body, ch), Cx.MH(Cx.Body, ch));
            Cx.Text(b, Cx.Body, ch,
                new Vector2(box.X + (box.Width - size.X) / 2f, box.Y + (box.Height - size.Y) / 2f), Cx.Ink);
        }

        private void DrawNpcRowText(SpriteBatch b, int index, int top)
        {
            CodexNpc n = _npcFiltered[index];
            Rectangle row = NpcRowRect(index, top);
            bool sel = ReferenceEquals(n, _npcSelected);
            Color ink = sel ? Color.White : Cx.Ink;

            int rightW = 0;
            if (n.CanMarry)
            {
                float mw = Cx.MW(Cx.Body, "可结婚");
                Cx.Text(b, Cx.Body, "可结婚", new Vector2(row.Right - mw - Cx.Sc(12), row.Y + Cx.Sc(16)), sel ? Cx.Ink : Cx.Gold);
                rightW = (int)mw + Cx.Sc(20);
            }

            int tx = row.X + Cx.Sc(56);
            int maxW = Math.Max(Cx.Sc(40), row.Width - Cx.Sc(56) - rightW - Cx.Sc(8));
            Cx.Text(b, Cx.Body, Cx.Truncate(n.Name, Cx.Body, maxW), new Vector2(tx, row.Y + Cx.Sc(-2)), ink);

            string sub = n.Universal ? "所有 NPC 通用" : (n.HasBirthday ? "生日 " + n.BirthdayText : "生日未知");
            Cx.Text(b, Cx.Body, Cx.Truncate(sub, Cx.Body, maxW), new Vector2(tx, row.Y + Cx.Sc(16)), sel ? Cx.Ink : Cx.Dim);
        }

        private void DrawNpcDetail(SpriteBatch b)
        {
            Cx.Box(b, _npcDetailRect, Cx.PageBg, Cx.Line);
            if (_npcSelected == null)
            {
                Cx.Text(b, Cx.Body, "左边列表里选一位 NPC", new Vector2(_npcDetailRect.X + Cx.Sc(16), _npcDetailRect.Y + Cx.Sc(16)), Cx.Dim);
                return;
            }

            CodexNpc n = _npcSelected;
            if (!ReferenceEquals(_npcEntriesFor, n))
                RebuildNpcEntries(n);

            int px = _npcDetailRect.X + Cx.Sc(16);
            int pw = _npcDetailRect.Width - Cx.Sc(46);
            int y = _npcDetailRect.Y + Cx.Sc(14);

            Cx.Text(b, Cx.Head, n.Name, new Vector2(px, y - Cx.Sc(6)), Cx.Green);
            y += (int)Cx.MH(Cx.Head, n.Name) + Cx.Sc(6);

            if (n.Universal)
            {
                Cx.Text(b, Cx.Body, "所有 NPC 通用（不混进各个 NPC 的列表）", new Vector2(px, y), Cx.Dim);
                y += Cx.LineHeight(Cx.Body) + Cx.Sc(4);
            }
            else
            {
                Cx.Text(b, Cx.Body, n.EnglishName, new Vector2(px, y), Cx.Dim);
                y += Cx.LineHeight(Cx.Body) + Cx.Sc(2);
                string birth = n.HasBirthday ? n.BirthdayText + BirthdaySuffix(n) : "未知";
                Cx.Text(b, Cx.Body, "生日：" + birth, new Vector2(px, y), Cx.Ink);
                y += Cx.LineHeight(Cx.Body) + Cx.Sc(2);
                Cx.Text(b, Cx.Body, n.CanMarry ? "可结婚" : "不可结婚", new Vector2(px, y), n.CanMarry ? Cx.Gold : Cx.Dim);
                y += Cx.LineHeight(Cx.Body) + Cx.Sc(4);
            }

            Cx.Fill(b, new Rectangle(px, y, Math.Max(Cx.Sc(20), pw - Cx.Sc(10)), 2), Cx.Line);
            y += Cx.Sc(8);

            _npcDetailArea = new Rectangle(_npcDetailRect.X + 1, y, _npcDetailRect.Width - 2, Math.Max(Cx.Sc(20), _npcDetailRect.Bottom - y - 2));
            _npcDetailScroll.View = _npcDetailArea.Height;

            Cx.BeginClip(b, _npcDetailArea);
            int top = _npcDetailScroll.Pos;
            foreach (NpcEntry e in _npcEntries)
            {
                int ey = _npcDetailArea.Y + e.Y - top;
                if (ey + e.H <= _npcDetailArea.Y || ey >= _npcDetailArea.Bottom)
                    continue;
                DrawNpcEntry(b, e, ey, pw);
            }
            Cx.EndClip(b);
            _npcDetailScroll.Draw(b);
        }

        private void DrawNpcEntry(SpriteBatch b, NpcEntry e, int ey, int pw)
        {
            if (e.Item == null)
            {
                if (e.Head)
                {
                    Cx.Text(b, Cx.Head, e.Text, new Vector2(_npcDetailArea.X + Cx.Sc(8), ey - Cx.Sc(4)), Cx.Gold);
                    Cx.Fill(b, new Rectangle(_npcDetailArea.X + Cx.Sc(8), ey + e.H - Cx.Sc(8), Math.Max(Cx.Sc(20), pw - Cx.Sc(20)), 1), Cx.Line);
                }
                else
                {
                    Cx.Text(b, Cx.Body, e.Text, new Vector2(_npcDetailArea.X + Cx.Sc(16), ey + Cx.Sc(2)), Cx.Dim);
                }
                return;
            }

            int colW = Math.Max(Cx.Sc(80), pw / 2);
            int x = _npcDetailArea.X + Cx.Sc(10) + e.Col * colW;
            bool hov = ReferenceEquals(_npcHoverEntry, e);
            DrawIcon(b, e.Item, new Rectangle(x, ey + Cx.Sc(3), Cx.Sc(30), Cx.Sc(30)));
            string nm = Cx.Truncate(e.Item.NameOrId, Cx.Body, colW - Cx.Sc(46));
            Cx.Text(b, Cx.Body, nm, new Vector2(x + Cx.Sc(36), ey + Cx.Sc(9)), hov ? Cx.Gold : Cx.Ink);
        }

        private void RebuildNpcEntries(CodexNpc n)
        {
            _npcEntries.Clear();
            _npcEntriesFor = n;
            _npcHoverEntry = null;
            if (n == null)
            {
                _npcDetailScroll.Content = 0;
                return;
            }

            int y = 0;
            for (int d = 0; d < n.Tiers.Count; d++)
            {
                CodexNpcTier t = n.Tiers[d];
                _npcEntries.Add(new NpcEntry { Y = y, H = Cx.Sc(30), Head = true, Text = t.Label + " · " + t.Count + " 件" });
                y += Cx.Sc(30);

                foreach (string cat in t.Categories)
                {
                    _npcEntries.Add(new NpcEntry { Y = y, H = Cx.Sc(20), Text = "· " + cat });
                    y += Cx.Sc(20);
                }

                if (t.Items.Count == 0 && t.Categories.Count == 0)
                {
                    _npcEntries.Add(new NpcEntry { Y = y, H = Cx.Sc(20), Text = "· 无" });
                    y += Cx.Sc(20);
                }

                for (int i = 0; i < t.Items.Count; i += 2)
                {
                    _npcEntries.Add(new NpcEntry { Y = y, H = NpcEntryH, Col = 0, Item = t.Items[i] });
                    if (i + 1 < t.Items.Count)
                        _npcEntries.Add(new NpcEntry { Y = y, H = NpcEntryH, Col = 1, Item = t.Items[i + 1] });
                    y += NpcEntryH;
                }

                int hidden = t.Count - t.Items.Count;
                if (hidden > 0)
                {
                    _npcEntries.Add(new NpcEntry { Y = y, H = Cx.Sc(20), Text = "还有 " + hidden + " 条" });
                    y += Cx.Sc(20);
                }
                y += Cx.Sc(10);
            }

            _npcDetailScroll.Content = y;
            _npcDetailScroll.Pos = 0;
        }
    }
}