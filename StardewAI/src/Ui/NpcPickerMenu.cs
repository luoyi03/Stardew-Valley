using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewAI.Ui
{
    /// <summary>NPC 选择面板：列出全地图村民，可搜索、可筛选，点一下就开聊。</summary>
    public sealed class NpcPickerMenu : IClickableMenu
    {
        private sealed class Entry
        {
            public NPC Npc;
            public string Name = "";
            public string Place = "";
            public int Hearts;
            public bool Met;
            public bool Nearby;
            public string Birthday = "";
            public bool BirthdayToday;
        }

        private enum Filter { All, Nearby, Datable, Met }

        private readonly List<Entry> _all = new();
        private readonly List<Entry> _shown = new();

        private Filter _filter = Filter.All;
        private string _query = "";
        private TextBox _search;
        private Rectangle _searchRect;
        private int _titleY;   // 标题行 y（按字体实测行高往下排，见 Relayout）
        private int _subY;     // 副标题行 y
        private readonly List<(Rectangle Rect, string Label, Filter Kind)> _chips = new();
        private Rectangle _listRect;
        private int _scroll;
        private int _maxScroll;
        private int _contentHeight;
        private int _hover = -1;
        private int _hoverChip = -1;
        private bool _scrolling;
        private int _scrollGrab;

        public NpcPickerMenu() : base(0, 0, 100, 100, false)
        {
            Build();
            Relayout();

            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + 12, 40, 40),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);
        }

        // ---------------- 数据 ----------------

        private void Build()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var raw = new List<NPC>();

            void Add(NPC n)
            {
                if (n == null || !n.IsVillager)
                    return;
                if (string.IsNullOrEmpty(n.Name))
                    return;
                if (seen.Add(n.Name))
                    raw.Add(n);
            }

            if (Game1.currentLocation?.characters != null)
            {
                foreach (NPC n in Game1.currentLocation.characters)
                    Add(n);
            }

            foreach (NPC n in Utility.getAllCharacters())
                Add(n);

            string here = Game1.currentLocation?.Name ?? "";
            var player = Game1.player;

            foreach (NPC n in raw)
            {
                int hearts = 0;
                bool met = false;

                if (player?.friendshipData != null && player.friendshipData.TryGetValue(n.Name, out var fs))
                {
                    hearts = fs.Points / 250;
                    met = true;
                }

                string place = n.currentLocation?.Name ?? "";
                bool nearby = string.Equals(place, here, StringComparison.Ordinal);

                var e = new Entry
                {
                    Npc = n,
                    Name = n.displayName ?? n.Name,
                    Place = nearby ? "就在你身边" : place,
                    Hearts = hearts,
                    Met = met,
                    Nearby = nearby,
                    Birthday = BirthdayOf(n, out bool today),
                    BirthdayToday = today
                };

                e.Birthday = e.Birthday;
                _all.Add(e);
            }

            _all.Sort((a, b) =>
            {
                if (a.Nearby != b.Nearby) return a.Nearby ? -1 : 1;
                if (a.BirthdayToday != b.BirthdayToday) return a.BirthdayToday ? -1 : 1;
                if (a.Hearts != b.Hearts) return b.Hearts.CompareTo(a.Hearts);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });

            ApplyFilter();
        }

        private static string BirthdayOf(NPC npc, out bool today)
        {
            today = false;

            try
            {
                var fs = Ref<string>(npc, "Birthday_Season", "birthday_Season");
                var fd = Ref<int>(npc, "Birthday_Day", "birthday_Day");

                if (string.IsNullOrEmpty(fs) || fd <= 0)
                    return "";

                today = string.Equals(fs, Game1.currentSeason, StringComparison.OrdinalIgnoreCase) && fd == Game1.dayOfMonth;
                return SeasonZh(fs) + fd + "日";
            }
            catch
            {
                return "";
            }
        }

        private static string SeasonZh(string season)
        {
            return season switch
            {
                "spring" => "春",
                "summer" => "夏",
                "fall" => "秋",
                "winter" => "冬",
                _ => season ?? ""
            };
        }

        private static T Ref<T>(object obj, params string[] names)
        {
            foreach (string name in names)
            {
                try
                {
                    var p = ModEntry.Hlp?.Reflection.GetProperty<T>(obj, name, false);
                    if (p != null)
                        return p.GetValue();

                    var f = ModEntry.Hlp?.Reflection.GetField<T>(obj, name, false);
                    if (f != null)
                        return f.GetValue();
                }
                catch { /* 换下一个名字 */ }
            }

            return default;
        }

        private void ApplyFilter()
        {
            _shown.Clear();
            string q = (_query ?? "").Trim();

            foreach (var e in _all)
            {
                if (_filter == Filter.Nearby && !e.Nearby) continue;
                if (_filter == Filter.Met && !e.Met) continue;
                if (_filter == Filter.Datable && !IsDatable(e.Npc)) continue;

                if (q.Length > 0)
                {
                    bool hit = e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                               || (e.Npc.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                               || e.Place.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }

                _shown.Add(e);
            }

            _contentHeight = _shown.Count * RowH + 12;
            _maxScroll = Math.Max(0, _contentHeight - _listRect.Height);
            _scroll = Math.Clamp(_scroll, 0, _maxScroll);
        }

        private static bool IsDatable(NPC npc)
        {
            try
            {
                bool v = Ref<bool>(npc, "datable", "Datable");
                return v;
            }
            catch
            {
                return false;
            }
        }

        // ---------------- 布局 ----------------

        private const int RowH = 68;

        private void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            width = Math.Max(520, Math.Min(uiW - 24, 1100));
            height = Math.Max(420, Math.Min(uiH - 24, 980));
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            // 标题用 dialogueFont，行高比小字大不少；原先写死的 16 / 46 / 64 会让标题压住副标题、
            // 副标题再压住搜索框。这里按实测行高依次往下排。
            _titleY = yPositionOnScreen + 14;
            _subY = _titleY + (int)Math.Ceiling(Ui.Head.MeasureString("Hg").Y) + 6;

            int searchY = _subY + Ui.LineHeight(Ui.Body) + 6;
            int listY = searchY + 44 + 16 + 40;   // 搜索框（高 44）+ 间隔 + 筛选按钮行（高 30 + 下方 10）

            _searchRect = new Rectangle(xPositionOnScreen + 24, searchY, width - 48, 44);
            _listRect = new Rectangle(xPositionOnScreen + 20, listY, width - 40, height - (listY - yPositionOnScreen) - 24);

            if (_search == null)
            {
                Texture2D tex = null;
                try { tex = Game1.content.Load<Texture2D>("LooseSprites\\textBox"); } catch { /* ignore */ }

                _search = new TextBox(tex, null, Game1.smallFont, Game1.textColor);
                _search.OnEnterPressed += _ => { };
                Game1.keyboardDispatcher.Subscriber = _search;
            }

            _search.X = _searchRect.X;
            _search.Y = _searchRect.Y;
            _search.Width = _searchRect.Width;
            _search.Height = _searchRect.Height;

            _contentHeight = _shown.Count * RowH + 12;
            _maxScroll = Math.Max(0, _contentHeight - _listRect.Height);
        }

        // ---------------- 交互 ----------------

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
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

            foreach (var chip in _chips)
            {
                if (chip.Rect.Contains(x, y))
                {
                    _filter = chip.Kind;
                    ApplyFilter();
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            if (_searchRect.Contains(x, y))
            {
                _search.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _search;
                return;
            }

            if (!_listRect.Contains(x, y))
                return;

            int rel = y - _listRect.Y + _scroll;
            int index = rel / RowH;

            if (index < 0 || index >= _shown.Count)
                return;

            Game1.playSound("bigSelect");
            OpenChat(_shown[index].Npc);
        }

        private void OpenChat(NPC npc)
        {
            if (npc == null)
                return;

            Game1.keyboardDispatcher.Subscriber = null;
            ModEntry.OpenNpcChat(npc, null);
        }

        public override void releaseLeftClick(int x, int y)
        {
            _scrolling = false;
            base.releaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int step = 68;
            if (ModEntry.Config?.InvertScroll == true)
                step = -step;
            _scroll = Math.Clamp(_scroll + direction * step, 0, _maxScroll);
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);

            _hover = -1;
            if (_listRect.Contains(x, y))
            {
                int idx = (y - _listRect.Y + _scroll) / RowH;
                if (idx >= 0 && idx < _shown.Count)
                    _hover = idx;
            }

            _hoverChip = -1;
            for (int i = 0; i < _chips.Count; i++)
            {
                if (_chips[i].Rect.Contains(x, y))
                    _hoverChip = i;
            }

            if (_scrolling && _maxScroll > 0)
            {
                int max = _listRect.Height - 40;
                int ty = Math.Clamp(y - _scrollGrab, _listRect.Y, _listRect.Bottom - 40);
                _scroll = max > 0 ? (int)((ty - _listRect.Y) / (float)max * _maxScroll) : 0;
                _scroll = Math.Clamp(_scroll, 0, _maxScroll);
            }
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _search?.Update();

            string now = _search?.Text ?? "";
            if (!string.Equals(now, _query, StringComparison.Ordinal))
            {
                _query = now;
                ApplyFilter();
            }
        }

        protected override void cleanupBeforeExit()
        {
            try
            {
                if (Game1.keyboardDispatcher?.Subscriber == _search)
                    Game1.keyboardDispatcher.Subscriber = null;
            }
            catch { /* ignore */ }

            base.cleanupBeforeExit();
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Relayout();
        }

        // ---------------- 绘制 ----------------

        public override void draw(SpriteBatch b)
        {
            Ui.Fill(b, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), new Color(0, 0, 0, 130));

            Game1.DrawBox(xPositionOnScreen, yPositionOnScreen, width, height);
            Ui.Fill(b, new Rectangle(xPositionOnScreen + 8, yPositionOnScreen + 8, width - 16, height - 16), Ui.PageBg);

            Utility.drawTextWithShadow(b, "找谁聊天", Game1.dialogueFont,
                new Vector2(xPositionOnScreen + 24, _titleY), Ui.Green);

            b.DrawString(Ui.Body, "不用跑到他身边，全地图的村民都能聊。搜索框支持中文名。",
                new Vector2(xPositionOnScreen + 24, _subY), Ui.Dim);

            _search.Draw(b, false);
            if (string.IsNullOrEmpty(_search.Text))
                b.DrawString(Ui.Body, "搜名字…", new Vector2(_searchRect.X + 12, _searchRect.Y + 13), Ui.Dim);

            DrawChips(b);

            Ui.Box(b, _listRect, new Color(0x29, 0x24, 0x1B), new Color(0x44, 0x3C, 0x2E));

            Ui.BeginClip(b, _listRect);
            DrawRows(b);
            Ui.EndClip(b);

            if (_maxScroll > 0)
            {
                var track = new Rectangle(_listRect.Right - 4, _listRect.Y, 6, _listRect.Height);
                Ui.Fill(b, track, new Color(0x44, 0x3C, 0x2E));
                int h = Math.Max(30, (int)(_listRect.Height * (float)_listRect.Height / Math.Max(1, _contentHeight)));
                int ty = _listRect.Y + (int)(_scroll / (float)_maxScroll * (_listRect.Height - h));
                Ui.Fill(b, new Rectangle(track.X, ty, track.Width, h), Ui.Gold);
            }

            base.draw(b);
            drawMouse(b);
        }

        private void DrawChips(SpriteBatch b)
        {
            _chips.Clear();

            var items = new (string Label, Filter Kind)[]
            {
                ("全部", Filter.All),
                ("附近", Filter.Nearby),
                ("可结婚", Filter.Datable),
                ("已认识", Filter.Met)
            };

            int x = _listRect.X;
            int y = _listRect.Y - 40;

            foreach (var item in items)
            {
                int w = (int)Ui.Measure(Ui.Body, item.Label) + 24;
                var rect = new Rectangle(x, y, w, 30);
                bool on = _filter == item.Kind;
                bool hover = rect.Contains(Game1.getMouseX(), Game1.getMouseY());

                Ui.Box(b, rect, on ? new Color(0x4A, 0x5C, 0x34) : (hover ? Ui.Hover : Ui.PanelBg),
                    on || hover ? Ui.Gold : Ui.Line);
                b.DrawString(Ui.Body, item.Label, new Vector2(x + 12, y + 7), on ? Ui.Gold : Ui.Dim);

                _chips.Add((rect, item.Label, item.Kind));
                x += w + 8;
            }
        }

        private void DrawRows(SpriteBatch b)
        {
            if (_shown.Count == 0)
            {
                string t = "没有匹配的村民，换个关键词试试";
                b.DrawString(Ui.Body, t,
                    new Vector2(_listRect.X + 16, _listRect.Y + 20), Ui.Dim);
                return;
            }

            for (int i = 0; i < _shown.Count; i++)
            {
                var e = _shown[i];
                int y = _listRect.Y + i * RowH - _scroll;
                if (y > _listRect.Bottom || y + RowH < _listRect.Y)
                    continue;

                bool hover = _hover == i;
                Ui.Fill(b, new Rectangle(_listRect.X + 4, y + 3, _listRect.Width - 16, RowH - 8),
                    hover ? new Color(0x33, 0x2D, 0x22) : new Color(0x2B, 0x26, 0x1D));

                DrawSprite(b, e.Npc, _listRect.X + 12, y + 4);

                int tx = _listRect.X + 62;
                b.DrawString(Ui.Body, e.Name, new Vector2(tx, y + 10), e.BirthdayToday ? Ui.Gold : Ui.Ink);

                string info = "好感 " + e.Hearts + "/10" + (e.Birthday.Length > 0 ? "   生日 " + e.Birthday : "");
                b.DrawString(Ui.Body, info, new Vector2(tx, y + 32), Ui.Dim);
                b.DrawString(Ui.Body, e.Place, new Vector2(tx, y + 50), e.Nearby ? Ui.Green : Ui.Dim);

                if (e.BirthdayToday)
                {
                    string tag = "今天生日";
                    float tw = Ui.Measure(Ui.Body, tag);
                    b.DrawString(Ui.Body, tag, new Vector2(_listRect.Right - 24 - tw, y + 10), Ui.Gold);
                }
                else if (hover)
                {
                    string tag = "点击聊天 >";
                    float tw = Ui.Measure(Ui.Body, tag);
                    b.DrawString(Ui.Body, tag, new Vector2(_listRect.Right - 24 - tw, y + 10), Ui.Green);
                }
            }
        }

        private static void DrawSprite(SpriteBatch b, NPC npc, int x, int y)
        {
            try
            {
                Texture2D tex = npc.Sprite?.Texture;
                var src = npc.Sprite?.SourceRect ?? new Rectangle(0, 0, 16, 32);

                if (tex == null)
                    return;

                float scale = 1.6f;
                int w = (int)(src.Width * scale);
                int h = (int)(src.Height * scale);
                b.Draw(tex, new Rectangle(x + (44 - w) / 2, y + (56 - h), w, h), src, Color.White);
            }
            catch { /* ignore */ }
        }
    }
}
