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
    /// <summary>图鉴面板的「献祭进度」标签（S5）：左边房间列表，右边该房间的收集包与所需物品。</summary>
    public partial class CodexMenu
    {
        private static int RoomH => Cx.Sc(36);
        private static int BundleH => Cx.Sc(104);

        private TextBox _bundleSearchBox;
        private string _bundleQuery = "";
        private bool _bundleOnlyMissing;
        private bool _bundleDirty = true;

        private Rectangle _bSearchRect, _bClearRect, _bOnlyRect, _bRoomRect, _bundleListRect;
        private readonly ScrollBar _roomScroll = new ScrollBar();
        private readonly ScrollBar _bundleScroll = new ScrollBar();

        private readonly List<CodexProgress.BundleView> _bundleShown = new List<CodexProgress.BundleView>();
        private readonly Dictionary<string, int> _roomCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<KeyValuePair<string, int>> _roomShown = new List<KeyValuePair<string, int>>();
        private string _roomName = "";
        private int _roomAll;
        private int _bundleHover = -1;

        private int RoomScrollTop => (_roomScroll.Pos / RoomH) * RoomH;
        private int BundleScrollTop => (_bundleScroll.Pos / BundleH) * BundleH;

        private void InitBundlesUi()
        {
            _bundleSearchBox = new TextBox(
                Game1.content.Load<Texture2D>(@"LooseSprites\textBox"),
                ModEntry.Pixel, Cx.Body, Game1.textColor)
            {
                Text = "",
                Selected = false
            };
            _bundleSearchBox.OnEnterPressed += _ => SetBundleSearchFocus(false);
        }

        private void RelayoutBundles()
        {
            if (_bundleSearchBox == null)
                return;

            Rectangle c = _contentRect;
            int searchW = Math.Min(Cx.Sc(360), (int)(c.Width * 0.30));
            _bSearchRect = new Rectangle(c.X, c.Y, searchW, Cx.Sc(52));
            _bundleSearchBox.X = _bSearchRect.X + Cx.Sc(12);
            _bundleSearchBox.Y = _bSearchRect.Y + Cx.Sc(10);
            _bundleSearchBox.Width = _bSearchRect.Width - Cx.Sc(24);
            _bundleSearchBox.Height = _bSearchRect.Height - Cx.Sc(20);

            int bx = _bSearchRect.Right + Cx.Sc(10);
            int by = c.Y + Cx.Sc(2);
            _bClearRect = new Rectangle(bx, by, Cx.Sc(88), Cx.Sc(48));
            _bOnlyRect = new Rectangle(bx + Cx.Sc(96), by, Cx.Sc(176), Cx.Sc(48));

            int bodyTop = c.Y + Cx.Sc(62);
            int avail = Math.Max(Cx.Sc(80), c.Bottom - bodyTop);
            int roomW = (int)(c.Width * 0.32) - 8;

            _bRoomRect = new Rectangle(c.X, bodyTop, roomW, avail);
            int listX = _bRoomRect.Right + Cx.Sc(12);
            _bundleListRect = new Rectangle(listX, bodyTop, Math.Max(Cx.Sc(160), c.Right - listX), avail);

            _roomScroll.Track = new Rectangle(_bRoomRect.Right + Cx.Sc(2), _bRoomRect.Y, Cx.Sc(10), _bRoomRect.Height);
            _roomScroll.View = _bRoomRect.Height;
            _bundleScroll.Track = new Rectangle(_bundleListRect.Right - Cx.Sc(12), _bundleListRect.Y + Cx.Sc(4), Cx.Sc(10), _bundleListRect.Height - Cx.Sc(8));
            _bundleScroll.View = _bundleListRect.Height;
        }

        private bool BundlesSearchFocused() => _bundleSearchBox != null && _bundleSearchBox.Selected;

        private void SetBundleSearchFocus(bool on)
        {
            if (_bundleSearchBox == null)
                return;
            _bundleSearchBox.Selected = on;
            Game1.keyboardDispatcher.Subscriber = on ? _bundleSearchBox : null;
        }

        // ------------------------------------------------------------------ 筛选

        private void EnsureBundlesFiltered()
        {
            if (!_bundleDirty)
                return;
            _bundleDirty = false;

            _bundleShown.Clear();
            _roomCounts.Clear();
            _roomShown.Clear();
            _roomAll = 0;

            CodexProgress p = ModEntry.Progress;
            if (p == null || !p.IsBuilt)
            {
                _bundleScroll.Content = 0;
                _roomScroll.Content = 0;
                return;
            }

            string q = (_bundleQuery ?? "").Trim();
            foreach (CodexProgress.BundleView b in p.Bundles)
            {
                if (_bundleOnlyMissing && b.Complete)
                    continue;
                if (q.Length > 0 && !BundlesMatch(b, q))
                    continue;

                _roomAll++;
                _roomCounts[b.Room] = (_roomCounts.TryGetValue(b.Room, out int n) ? n : 0) + 1;
            }

            _roomShown.Add(new KeyValuePair<string, int>("", _roomAll));
            foreach (string room in p.Rooms)
            {
                if (_roomCounts.TryGetValue(room, out int cnt) && cnt > 0)
                    _roomShown.Add(new KeyValuePair<string, int>(room, cnt));
            }

            // 选中的房间如果被筛掉了就回到「全部房间」
            bool found = false;
            foreach (var kv in _roomShown)
            {
                if (string.Equals(kv.Key, _roomName, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                _roomName = "";

            string roomKey = _roomName ?? "";
            foreach (CodexProgress.BundleView b in p.Bundles)
            {
                if (_bundleOnlyMissing && b.Complete)
                    continue;
                if (q.Length > 0 && !BundlesMatch(b, q))
                    continue;
                if (roomKey.Length > 0 && !string.Equals(b.Room, roomKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                _bundleShown.Add(b);
            }

            _roomScroll.Content = _roomShown.Count * RoomH;
            _bundleScroll.Content = _bundleShown.Count * BundleH;
            if (_bundleScroll.Pos > _bundleScroll.Max)
                _bundleScroll.Pos = 0;
        }

        private static bool BundlesMatch(CodexProgress.BundleView b, string q)
        {
            if (Contains(b.Name, q) || Contains(b.RoomName, q) || Contains(b.Reward, q))
                return true;
            foreach (CodexProgress.Slot s in b.Slots)
            {
                if (Contains(s.Label, q))
                    return true;
                if (s.Item != null && (Contains(s.Item.EnglishName, q) || StartsWith(s.Item.Pinyin, q)))
                    return true;
            }
            return false;
        }

        private static bool Contains(string text, string q)
            => !string.IsNullOrEmpty(text) && text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool StartsWith(string text, string q)
            => !string.IsNullOrEmpty(text) && text.StartsWith(q, StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ 交互

        private bool ReceiveBundlesClick(int x, int y)
        {
            bool handled = false;
            if (_bSearchRect.Contains(x, y))
            {
                SetBundleSearchFocus(true);
                handled = true;
            }
            else if (BundlesSearchFocused())
            {
                SetBundleSearchFocus(false);
            }

            if (_bClearRect.Contains(x, y))
            {
                _bundleQuery = "";
                if (_bundleSearchBox != null)
                    _bundleSearchBox.Text = "";
                _bundleDirty = true;
                _bundleScroll.Pos = 0;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_bOnlyRect.Contains(x, y))
            {
                _bundleOnlyMissing = !_bundleOnlyMissing;
                _bundleDirty = true;
                _bundleScroll.Pos = 0;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_roomScroll.TryGrab(x, y) || _bundleScroll.TryGrab(x, y))
                return true;

            if (_bRoomRect.Contains(x, y))
            {
                EnsureBundlesFiltered();
                int idx = (RoomScrollTop + (y - _bRoomRect.Y)) / RoomH;
                if (idx >= 0 && idx < _roomShown.Count)
                {
                    _roomName = _roomShown[idx].Key;
                    _bundleDirty = true;
                    _bundleScroll.Pos = 0;
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            if (_bundleListRect.Contains(x, y))
            {
                EnsureBundlesFiltered();
                int idx = (BundleScrollTop + (y - _bundleListRect.Y)) / BundleH;
                if (idx >= 0 && idx < _bundleShown.Count)
                {
                    CodexProgress.BundleView v = _bundleShown[idx];
                    Rectangle r = BundleRect(idx);
                    int relY = y - (r.Y + 42);
                    int relX = x - (r.X + 6);
                    int cellW = CellWidth(r);
                    if (relY >= 0 && relX >= 0 && cellW > 0)
                    {
                        int row = relY / 30;
                        int col = relX / cellW;
                        int si = row * 3 + col;
                        if (row < 2 && col >= 0 && col < 3 && si < Math.Min(v.Slots.Count, 6))
                        {
                            CodexItem it = v.Slots[si].Item;
                            if (it != null)
                            {
                                JumpToItem(it);
                                return true;
                            }
                        }
                    }
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            return handled;
        }

        private bool HeldBundlesClick(int x, int y)
        {
            if (_bundleScroll.Dragging)
            {
                _bundleScroll.ApplyDrag(y);
                return true;
            }
            if (_roomScroll.Dragging)
            {
                _roomScroll.ApplyDrag(y);
                return true;
            }
            return false;
        }

        private void ReleaseBundlesDrag()
        {
            _bundleScroll.Release();
            _roomScroll.Release();
        }

        private void ScrollBundles(int step)
        {
            int mx = Game1.getMouseX();
            if (_bRoomRect.Contains(mx, Game1.getMouseY()) || _roomScroll.Track.Contains(mx, Game1.getMouseY()))
                _roomScroll.ScrollBy(step * RoomH * 2);
            else
                _bundleScroll.ScrollBy(step * BundleH);
        }

        private void HoverBundles(int x, int y)
        {
            _bundleHover = -1;
            if (!_bundleListRect.Contains(x, y))
                return;
            EnsureBundlesFiltered();
            int idx = (BundleScrollTop + (y - _bundleListRect.Y)) / BundleH;
            if (idx >= 0 && idx < _bundleShown.Count)
                _bundleHover = idx;
        }

        private void UpdateBundles(GameTime time)
        {
            if (_bundleSearchBox == null)
                return;
            _bundleSearchBox.Update();
            if (!string.Equals(_bundleSearchBox.Text ?? "", _bundleQuery ?? "", StringComparison.Ordinal))
            {
                _bundleQuery = _bundleSearchBox.Text ?? "";
                _bundleDirty = true;
                _bundleScroll.Pos = 0;
            }
        }

        private bool BundlesOnKey(Keys key)
        {
            EnsureBundlesFiltered();
            switch (key)
            {
                case Keys.Up: _bundleScroll.ScrollBy(-BundleH); return true;
                case Keys.Down: _bundleScroll.ScrollBy(BundleH); return true;
                case Keys.PageUp: _bundleScroll.ScrollBy(-_bundleListRect.Height); return true;
                case Keys.PageDown: _bundleScroll.ScrollBy(_bundleListRect.Height); return true;
                case Keys.Home: _bundleScroll.Pos = 0; return true;
                case Keys.End: _bundleScroll.Pos = _bundleScroll.Max; return true;
            }
            return false;
        }

        /// <summary>切到本页时重新读一次存档进度（献祭进度会随手玩变化）。</summary>
        internal void RefreshBundles()
        {
            ModEntry.Progress?.Refresh();
            _bundleDirty = true;
            _colDirty = true;
        }

        // ------------------------------------------------------------------ 绘制

        private Rectangle BundleRect(int index)
            => new Rectangle(_bundleListRect.X, _bundleListRect.Y + index * BundleH - BundleScrollTop, _bundleListRect.Width, BundleH);

        private static int CellWidth(Rectangle r) => (r.Width - Cx.Sc(12)) / 3;

        private void DrawBundles(SpriteBatch b)
        {
            EnsureBundlesFiltered();

            Cx.Box(b, _bSearchRect, Cx.PanelBg2, Cx.Line);
            _bundleSearchBox.Draw(b);
            DrawButton(b, _bClearRect, "清空", false, false);
            DrawButton(b, _bOnlyRect, _bundleOnlyMissing ? "只看未完成 ✔" : "只看未完成", _bundleOnlyMissing, false);

            CodexProgress p = ModEntry.Progress;
            if (p == null || !p.IsBuilt)
            {
                Cx.Text(b, Cx.Body, "献祭 / 图鉴进度数据还没准备好：先进一次存档再打开面板。",
                    new Vector2(_contentRect.X + Cx.Sc(16), _contentRect.Y + Cx.Sc(90)), Cx.Dim);
                return;
            }

            int done = p.BundleDoneCount;
            int total = p.Bundles.Count;
            string cnt = "已完成 " + done + " / 共 " + total + " 个包";
            float cw = Cx.MW(Cx.Body, cnt);
            Cx.Text(b, Cx.Body, cnt, new Vector2(_contentRect.Right - cw, _contentRect.Y + Cx.Sc(16)), done >= total ? Cx.Green : Cx.Dim);

            // ---- 左：房间 ----
            Cx.Box(b, _bRoomRect, Cx.PanelBg2, Cx.Line);
            Cx.BeginClip(b, _bRoomRect);
            int rtop = RoomScrollTop;
            int rrows = Math.Max(1, _bRoomRect.Height / RoomH);
            int rfirst = rtop / RoomH;
            int rlast = Math.Min(_roomShown.Count, rfirst + rrows);
            for (int i = rfirst; i < rlast; i++)
            {
                var kv = _roomShown[i];
                var rr = new Rectangle(_bRoomRect.X, _bRoomRect.Y + i * RoomH - rtop, _bRoomRect.Width, RoomH);
                bool sel = string.Equals(kv.Key, _roomName ?? "", StringComparison.OrdinalIgnoreCase);
                if (sel)
                    Cx.Fill(b, rr, Cx.Selected);
                Cx.Fill(b, new Rectangle(rr.X, rr.Bottom - 1, rr.Width, 1), Cx.Line);

                string label = kv.Key.Length == 0 ? "全部房间" : RoomLabel(kv.Key);
                Cx.Text(b, Cx.Body, Cx.Truncate(label, Cx.Body, rr.Width - Cx.Sc(70)), new Vector2(rr.X + Cx.Sc(10), rr.Y + Cx.Sc(8)),
                    sel ? Color.White : Cx.Ink);
                string num = kv.Value.ToString();
                float nw = Cx.MW(Cx.Body, num);
                Cx.Text(b, Cx.Body, num, new Vector2(rr.Right - nw - Cx.Sc(14), rr.Y + Cx.Sc(8)), sel ? Cx.Ink : Cx.Dim);
            }
            Cx.EndClip(b);
            _roomScroll.Draw(b);

            // ---- 右：包 ----
            Cx.Box(b, _bundleListRect, Cx.PanelBg2, Cx.Line);
            if (_bundleShown.Count == 0)
            {
                Cx.Text(b, Cx.Body, _bundleOnlyMissing ? "这个范围内没有未完成的收集包。" : "没有匹配的收集包。",
                    new Vector2(_bundleListRect.X + Cx.Sc(12), _bundleListRect.Y + Cx.Sc(12)), Cx.Dim);
            }
            else
            {
                Cx.BeginClip(b, _bundleListRect);
                int top = BundleScrollTop;
                int rows = Math.Max(1, _bundleListRect.Height / BundleH);
                int first = top / BundleH;
                int last = Math.Min(_bundleShown.Count, first + rows);
                for (int i = first; i < last; i++)
                    DrawBundleBlock(b, _bundleShown[i], i);
                Cx.EndClip(b);
            }
            _bundleScroll.Draw(b);
        }

        private string RoomLabel(string room)
        {
            foreach (CodexProgress.BundleView b in _bundleShown)
            {
                if (string.Equals(b.Room, room, StringComparison.OrdinalIgnoreCase))
                    return b.RoomName;
            }
            return room;
        }

        private void DrawBundleBlock(SpriteBatch b, CodexProgress.BundleView v, int index)
        {
            Rectangle r = BundleRect(index);
            bool hov = index == _bundleHover;
            Cx.Fill(b, r, hov ? Cx.Hover : Cx.PanelBg2);
            Cx.Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), Cx.Line);

            Cx.Text(b, Cx.Body, Cx.Truncate(v.Name, Cx.Body, r.Width - Cx.Sc(220)), new Vector2(r.X + Cx.Sc(10), r.Y + Cx.Sc(5)),
                v.Complete ? Cx.Green : Cx.Ink);

            string stat = v.Complete ? "已完成" : "还差 " + v.MissingSlots + " 样";
            float sw = Cx.MW(Cx.Body, stat);
            Cx.Text(b, Cx.Body, stat, new Vector2(r.Right - sw - Cx.Sc(20), r.Y + Cx.Sc(5)), v.Complete ? Cx.Green : Cx.Gold);

            string prog = "（已凑 " + v.DoneSlots + " / " + v.Required + "）";
            float pw2 = Cx.MW(Cx.Body, prog);
            Cx.Text(b, Cx.Body, prog, new Vector2(r.Right - sw - pw2 - Cx.Sc(28), r.Y + Cx.Sc(5)), Cx.Dim);

            string reward = string.IsNullOrEmpty(v.Reward) ? "奖励：（无）" : "奖励：" + v.Reward;
            if (v.RewardPending)
                reward += "　★ 奖励待领取";
            Cx.Text(b, Cx.Body, Cx.Truncate(reward, Cx.Body, r.Width - Cx.Sc(24)), new Vector2(r.X + Cx.Sc(10), r.Y + Cx.Sc(23)),
                v.RewardPending ? Cx.Gold : Cx.Dim);

            int cellW = CellWidth(r);
            int shown = Math.Min(v.Slots.Count, 6);
            for (int i = 0; i < shown; i++)
            {
                CodexProgress.Slot s = v.Slots[i];
                var cell = new Rectangle(r.X + Cx.Sc(6) + (i % 3) * cellW, r.Y + Cx.Sc(44) + (i / 3) * Cx.Sc(30), cellW - Cx.Sc(6), Cx.Sc(26));
                bool ok = s.Done;
                var box = new Rectangle(cell.X + 1, cell.Y + 1, Cx.Sc(22), Cx.Sc(22));
                if (s.Item != null)
                    DrawIcon(b, s.Item, box);
                else
                    Cx.Fill(b, box, Cx.TypeColor("O"));

                Cx.Text(b, Cx.Body, ok ? "✔" : "·", new Vector2(cell.X + Cx.Sc(26), cell.Y + Cx.Sc(5)), ok ? Cx.Green : Cx.Dim);
                string label = s.Label + (s.Stack > 1 ? " ×" + s.Stack : "");
                Cx.Text(b, Cx.Body, Cx.Truncate(label, Cx.Body, cell.Width - Cx.Sc(42)), new Vector2(cell.X + Cx.Sc(40), cell.Y + Cx.Sc(5)),
                    ok ? Cx.Green : Cx.Dim);
            }

            if (v.Slots.Count > shown)
            {
                var note = new Rectangle(r.X + Cx.Sc(6) + (shown % 3) * cellW, r.Y + Cx.Sc(44) + (shown / 3) * Cx.Sc(30), cellW - Cx.Sc(6), Cx.Sc(26));
                Cx.Text(b, Cx.Body, "…等 " + v.Slots.Count + " 件", new Vector2(note.X + Cx.Sc(26), note.Y + Cx.Sc(5)), Cx.Dim);
            }
        }
    }
}