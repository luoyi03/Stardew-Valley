using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace InGameGuide
{
    public partial class GuideMenu
    {
        private const int SearchRowH = 46;

        private readonly ScrollBar _resScroll = new();

        private string _query = "";
        private string _typeFilter = "";
        private bool _onlyO;
        private List<ItemRow> _filtered = new();
        private bool _dirty = true;

        private Rectangle _chipsRect;
        private Rectangle _resultRect;

        private readonly List<Hit> _chips = new();
        private Hit _clearBtn;
        private Hit _onlyBtn;

        private TextBox _searchBox;
        private int _hoverRow = -1;
        private int _copiedRow = -1;
        private int _copiedTimer;

        /// <summary>代码列宽度：按当前可见行的最长代码自动伸缩，避免 (M)CursedMannequinFemale 这种溢出去。</summary>
        private int _codeColW = 170;
        private int _codeColMax = 170;

        // ---------------- 初始化 ----------------
        private void InitSearchUi()
        {
            var tex = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
            _searchBox = new TextBox(tex, ModEntry.Pixel, Game1.smallFont, Game1.textColor)
            {
                Text = "",
                Selected = false
            };
            _searchBox.OnEnterPressed += sender => SetSearchFocus(false);

            _clearBtn = new Hit { Tag = "clear", Label = "清空" };
            _onlyBtn = new Hit { Tag = "only", Label = "仅可命名获取" };
        }

        private void RelayoutSearch()
        {
            if (_searchBox == null)
                return;

            int cx = _contentRect.X, cy = _contentRect.Y, cw = _contentRect.Width, ch = _contentRect.Height;

            int btnW = 110, onlyW = 190;
            int boxW = Math.Max(200, Math.Min(520, cw - btnW - onlyW - 40));
            _searchBox.X = cx;
            _searchBox.Y = cy + 4;
            _searchBox.Width = boxW;
            _searchBox.Height = 56;

            int bx = cx + boxW + 12;
            _clearBtn.Bounds = new Rectangle(bx, cy + 6, btnW, 50);
            _onlyBtn.Bounds = new Rectangle(bx + btnW + 8, cy + 6, onlyW, 50);
            _onlyBtn.On = _onlyO;

            int chipY = cy + 70;
            BuildChips(cx, chipY, cw, out int rows);
            _chipsRect = new Rectangle(cx, chipY, cw, rows * ChipH);

            int resultY = Math.Min(chipY + rows * ChipH + 8, cy + ch - 130);
            int resH = Math.Max(80, cy + ch - resultY - 30);
            _resultRect = new Rectangle(cx, resultY, Math.Max(200, cw - ScrollW - 8), resH);

            EnsureLayoutSearch();
        }

        private const int ChipH = 36;

        private void BuildChips(int cx, int cy, int cw, out int rows)
        {
            _chips.Clear();
            var db = ModEntry.Db;
            var f = Ui.Body;
            int x = cx, y = cy;

            var keys = new List<string> { "" };
            if (db?.Order != null)
                keys.AddRange(db.Order);

            foreach (string key in keys)
            {
                int cnt = key.Length == 0
                    ? (db?.Rows?.Count ?? 0)
                    : (db.Counts.TryGetValue(key, out int c) ? c : 0);
                if (key.Length > 0 && cnt == 0)
                    continue;

                string label = key.Length == 0 ? "全部 " + cnt : db.Label(key) + " " + cnt;
                int w = (int)f.MeasureString(label).X + 28;
                if (x > cx && x + w > cx + cw)
                {
                    x = cx;
                    y += ChipH;
                }
                _chips.Add(new Hit { Tag = key, Label = label, Bounds = new Rectangle(x, y, w, ChipH - 4) });
                x += w + 6;
            }
            rows = (y - cy) / ChipH + 1;
        }

        private void EnsureLayoutSearch()
        {
            _resScroll.View = _resultRect.Height;
            _resScroll.Content = _filtered.Count * SearchRowH + 8;
            _resScroll.Track = new Rectangle(_resultRect.Right + 2, _resultRect.Y, ScrollW, Math.Max(10, _resultRect.Height));
        }

        // ---------------- 过滤 ----------------
        private void EnsureFiltered()
        {
            if (_dirty)
                ComputeFiltered();
        }

        private void ComputeFiltered()
        {
            var db = ModEntry.Db;
            string q = (_query ?? "").Trim();
            string ql = q.ToLowerInvariant();

            IEnumerable<ItemRow> src = db.Rows;
            if (_onlyO)
                src = src.Where(r => r.IsSpawnable);
            if (!string.IsNullOrEmpty(_typeFilter))
                src = src.Where(r => r.Type == _typeFilter);

            if (ql.Length > 0)
                _filtered = src.Where(r => Match(r, ql)).OrderBy(r => Score(r, ql)).ToList();
            else
                _filtered = src.ToList();

            _dirty = false;
            _codeColMax = 170;   // 换了搜索条件才重置列宽
            _resScroll.Content = _filtered.Count * SearchRowH + 8;
            _resScroll.Pos = 0;
        }

        private static bool Match(ItemRow r, string q)
        {
            if (r.Code.StartsWith(q, StringComparison.Ordinal))
                return true;
            if (r.Zh.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (r.En.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (ModEntry.Db.Label(r.Type).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // 模糊兜底：输错一点点也能找到（「抚摸器」→「自动抚摸机」，「洒水器」→「优质洒水器」）
            return LooseMatch(r, q);
        }

        /// <summary>模糊匹配：去掉最后一个字再比对，或二元字组有一半重合。</summary>
        private static bool LooseMatch(ItemRow r, string q)
        {
            if (q.Length < 2)
                return false;

            string zh = r.Zh ?? "";
            string en = r.En ?? "";

            // ① 少一个字：「抚摸器」→「抚摸」
            string shorter = q.Substring(0, q.Length - 1);
            if (shorter.Length >= 2 &&
                (zh.IndexOf(shorter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 en.IndexOf(shorter, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            // ② 二元字组重合过半
            if (q.Length >= 3 || zh.Length >= 2)
            {
                var a = Grams(zh);
                var b = Grams(q);
                if (a.Count > 0 && b.Count > 0)
                {
                    int shared = a.Count(g => b.Contains(g));
                    if (shared * 2 >= b.Count)
                        return true;
                }
            }

            return false;
        }

        private static List<string> Grams(string s)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(s) || s.Length < 2)
                return res;

            for (int i = 0; i + 1 < s.Length; i++)
                res.Add(s.Substring(i, 2));

            return res;
        }

        private static int Score(ItemRow r, string q)
        {
            if (r.Code == q)
                return 0;
            string en = r.En?.ToLowerInvariant() ?? "";
            string zh = r.Zh?.ToLowerInvariant() ?? "";
            if (zh == q || en == q)
                return 1;
            if (en.StartsWith(q))
                return 2;
            if (zh.StartsWith(q))
                return 3;
            if (r.Code.StartsWith(q))
                return 4;
            if (en.Contains(q) || zh.Contains(q))
                return 5;
            return 6;
        }

        private Rectangle RowRect(int i)
            => new(_resultRect.X, _resultRect.Y + i * SearchRowH - _resScroll.Pos, _resultRect.Width, SearchRowH - 2);

        private Rectangle GetButtonRect(int i)
        {
            var r = RowRect(i);
            return new Rectangle(r.X + 4, r.Y + 4, 128, r.Height - 8);
        }

        // ---------------- 更新 ----------------
        private void UpdateSearch(GameTime time)
        {
            if (_copiedTimer > 0)
            {
                _copiedTimer--;
                if (_copiedTimer == 0)
                    _copiedRow = -1;
            }
            if (_tab != 1 || _searchBox == null)
                return;

            _searchBox.Update();
            string cur = _searchBox.Text ?? "";
            if (cur != _query)
            {
                _query = cur;
                _dirty = true;
                EnsureFiltered();
                EnsureLayoutSearch();
            }
        }

        // ---------------- 绘制 ----------------
        private void DrawSearch(SpriteBatch b)
        {
            EnsureFiltered();
            if (_searchBox == null)
                return;

            var f = Ui.Body;
            _searchBox.Draw(b);

            if (string.IsNullOrEmpty(_searchBox.Text) && !_searchBox.Selected)
            {
                string ph = "搜索：防风草 / Parsnip / 24 / (O) …";
                b.DrawString(f, ph, new Vector2(_searchBox.X + 14, _searchBox.Y + 14), Ui.Dim);
            }

            DrawButton(b, _clearBtn);
            DrawButton(b, _onlyBtn);

            string stat = $"匹配 {_filtered.Count} / {ModEntry.Db.Rows.Count} 条";
            float sw = f.MeasureString(stat).X;
            b.DrawString(f, stat, new Vector2(_contentRect.Right - sw - 4, _searchBox.Y + 16), Ui.Dim);

            foreach (var chip in _chips)
            {
                bool on = chip.Tag == _typeFilter || (chip.Tag.Length == 0 && _typeFilter.Length == 0);
                Ui.Fill(b, chip.Bounds, on ? new Color(0x4F, 0x7D, 0x3F) : Ui.PanelBg2);
                Ui.Box(b, chip.Bounds, Color.Transparent, on ? Ui.Green : Ui.Line);
                b.DrawString(f, chip.Label, new Vector2(chip.Bounds.X + 11, chip.Bounds.Y + 4), on ? Color.White : Ui.Ink);
            }

            Ui.BeginClip(b, _resultRect);
            int first = Math.Max(0, _resScroll.Pos / SearchRowH - 1);
            int last = Math.Min(_filtered.Count, first + _resultRect.Height / SearchRowH + 2);
            UpdateCodeColumnWidth(f, first, last);
            for (int i = first; i < last; i++)
                DrawRow(b, _filtered[i], i);
            Ui.EndClip(b);

            _resScroll.Draw(b);

            b.DrawString(f, "左边绿色大方块就是「获取」按钮，点它直接进背包（右键行也行）；点条目其它位置＝复制代码。绿色＝物体类，可用 [代码] 走动物命名彩蛋；金色＝其余类型。",
                new Vector2(_contentRect.X, _contentRect.Bottom - 22), Ui.Dim);
        }

        private void UpdateCodeColumnWidth(SpriteFont f, int first, int last)
        {
            int cap = Math.Max(200, (int)(_resultRect.Width * 0.5f));

            for (int i = first; i < last && i < _filtered.Count; i++)
            {
                float w = f.MeasureString(_filtered[i].CopyText ?? "").X + 26f;
                if (w > _codeColMax)
                    _codeColMax = (int)w;
            }

            // 只增不减：滚动时不会因为换了一批行就左右乱跳
            _codeColW = Math.Min(Math.Max(_codeColMax, 170), cap);
        }

        private void DrawTip(SpriteBatch b, SpriteFont f, string text)
        {
            var sz = f.MeasureString(text);
            var rect = new Rectangle(
                (int)(Game1.getMouseX() + 20),
                (int)(Game1.getMouseY() + 20),
                (int)sz.X + 20,
                (int)sz.Y + 14);

            if (rect.Right > Game1.uiViewport.Width - 8)
                rect.X = Game1.uiViewport.Width - 8 - rect.Width;
            if (rect.Bottom > Game1.uiViewport.Height - 8)
                rect.Y = Game1.uiViewport.Height - 8 - rect.Height;

            Ui.Fill(b, rect, new Color(0x1E, 0x1A, 0x14));
            Ui.Box(b, rect, Color.Transparent, Ui.Gold);
            b.DrawString(f, text, new Vector2(rect.X + 10, rect.Y + 7), Ui.Gold);
        }

        private void DrawRow(SpriteBatch b, ItemRow item, int i)
        {
            var f = Ui.Body;
            var r = RowRect(i);
            bool copied = i == _copiedRow;

            Ui.Fill(b, r, copied ? new Color(0x4F, 0x7D, 0x3F)
                                 : (i == _hoverRow ? Ui.Hover
                                 : (i % 2 == 0 ? Ui.PanelBg : Ui.PanelBg2)));

            // 列宽：[获取按钮] [名称] [类型] [代码]，代码列随内容伸缩
            int codeW = _codeColW;
            const int typeW = 130, pad = 10, btnW = 128;
            int typeX = r.Right - pad - codeW - 12 - typeW;
            int nameX = r.X + pad + btnW + 14;
            int nameW = Math.Max(60, typeX - nameX - 12);

            // 「获取」按钮：亮绿底 + 白字 + 亮描边，鼠标移上去反白，右侧的代码框同一风格
            var btn = GetButtonRect(i);
            bool btnHover = btn.Contains(Game1.getMouseX(), Game1.getMouseY());
            Ui.Fill(b, btn, btnHover ? new Color(0xC8, 0xE6, 0xA8) : new Color(0x3E, 0x8B, 0x36));
            Ui.Box(b, btn, Color.Transparent, btnHover ? Color.White : new Color(0xB6, 0xE8, 0x8C));
            Ui.Box(b, new Rectangle(btn.X + 1, btn.Y + 1, btn.Width - 2, btn.Height - 2), Color.Transparent,
                btnHover ? Color.White : new Color(0x7F, 0xC0, 0x5A));

            var bsz = f.MeasureString("获取");
            b.DrawString(f, "获取",
                new Vector2(btn.X + (btn.Width - bsz.X) / 2, btn.Y + (btn.Height - bsz.Y) / 2),
                btnHover ? Color.Black : Color.White);

            var hf = Ui.Head;
            string name = Truncate(item.DisplayName, hf, nameW);
            var nsz = hf.MeasureString(name);
            b.DrawString(hf, name, new Vector2(nameX, r.Y + (r.Height - nsz.Y) / 2), Ui.Ink);

            string typeLabel = Truncate(ModEntry.Db.Label(item.Type), f, typeW - 8);
            var tsz = f.MeasureString(typeLabel);
            b.DrawString(f, typeLabel, new Vector2(typeX + (typeW - tsz.X) / 2, r.Y + (r.Height - tsz.Y) / 2), Ui.Dim);

            var chipRect = new Rectangle(r.Right - pad - codeW, r.Y + 4, codeW, r.Height - 8);
            string codeLabel = Truncate(item.CopyText, f, chipRect.Width - 18);
            Ui.Fill(b, chipRect, item.IsSpawnable ? new Color(0x33, 0x5B, 0x28) : new Color(0x5A, 0x45, 0x22));
            Ui.Box(b, chipRect, Color.Transparent, item.IsSpawnable ? Ui.Green : Ui.Gold);
            var csz = f.MeasureString(codeLabel);
            b.DrawString(f, codeLabel,
                new Vector2(chipRect.X + (chipRect.Width - csz.X) / 2, chipRect.Y + (chipRect.Height - csz.Y) / 2),
                item.IsSpawnable ? Ui.Green : Ui.Gold);

            // 悬停提示：按钮说"获取"，代码格说"复制"并给出完整代码（长 id 被省略号截断时很有用）
            if (btnHover)
                DrawTip(b, f, "点击这里获取：" + item.DisplayName);
            else if (chipRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
                DrawTip(b, f, "点击复制：" + item.CopyText);
        }

        private void DrawButton(SpriteBatch b, Hit hit)
        {
            if (hit == null || hit.Bounds == Rectangle.Empty)
                return;
            bool hover = hit.Bounds.Contains(Game1.getMouseX(), Game1.getMouseY());
            Ui.Fill(b, hit.Bounds, hit.On ? new Color(0x4F, 0x7D, 0x3F) : (hover ? Ui.Hover : Ui.PanelBg2));
            Ui.Box(b, hit.Bounds, Color.Transparent, hit.On ? Ui.Green : Ui.Line);
            var sz = Ui.Body.MeasureString(hit.Label);
            b.DrawString(Ui.Body, hit.Label,
                new Vector2(hit.Bounds.X + (hit.Bounds.Width - sz.X) / 2, hit.Bounds.Y + (hit.Bounds.Height - sz.Y) / 2),
                hit.On ? Color.White : Ui.Ink);
        }

        // ---------------- 输入 ----------------
        private void ReceiveSearchClick(int x, int y)
        {
            if (_resScroll.TryGrab(x, y))
                return;

            if (_clearBtn.Bounds.Contains(x, y))
            {
                _searchBox.Text = "";
                _query = "";
                _dirty = true;
                EnsureFiltered();
                EnsureLayoutSearch();
                SetSearchFocus(true);
                Game1.playSound("smallSelect");
                return;
            }

            if (_onlyBtn.Bounds.Contains(x, y))
            {
                _onlyO = !_onlyO;
                _onlyBtn.On = _onlyO;
                _dirty = true;
                EnsureFiltered();
                EnsureLayoutSearch();
                Game1.playSound("smallSelect");
                return;
            }

            foreach (var chip in _chips)
            {
                if (chip.Bounds.Contains(x, y))
                {
                    _typeFilter = chip.Tag;
                    _dirty = true;
                    EnsureFiltered();
                    EnsureLayoutSearch();
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            var boxRect = new Rectangle(_searchBox.X, _searchBox.Y, _searchBox.Width, _searchBox.Height);
            if (boxRect.Contains(x, y))
            {
                SetSearchFocus(true);
                return;
            }

            if (_resultRect.Contains(x, y))
            {
                int first = Math.Max(0, _resScroll.Pos / SearchRowH - 1);
                int last = Math.Min(_filtered.Count, first + _resultRect.Height / SearchRowH + 2);
                for (int i = first; i < last; i++)
                {
                    if (GetButtonRect(i).Contains(x, y))
                    {
                        GiveItem(_filtered[i], i);
                        return;
                    }
                    if (RowRect(i).Contains(x, y))
                    {
                        CopyItem(_filtered[i], i);
                        return;
                    }
                }
            }

            SetSearchFocus(false);
        }

        /// <summary>右键行：直接获取这一行的物品。</summary>
        private void ReceiveSearchRightClick(int x, int y)
        {
            if (!_resultRect.Contains(x, y))
                return;

            int first = Math.Max(0, _resScroll.Pos / SearchRowH - 1);
            int last = Math.Min(_filtered.Count, first + _resultRect.Height / SearchRowH + 2);

            for (int i = first; i < last; i++)
            {
                if (RowRect(i).Contains(x, y))
                {
                    GiveItem(_filtered[i], i);
                    return;
                }
            }
        }

        private void GiveItem(ItemRow item, int rowIndex)
        {
            _copiedRow = rowIndex;
            _copiedTimer = 60;
            string msg = ItemSpawner.Give(item, 1, 0);
            ShowToast(msg);
            Game1.playSound(msg.Contains("背包已满") ? "throwDownITem" : "coin");
        }

        private void CopyItem(ItemRow item, int rowIndex)
        {
            string text = item.CopyText;
            bool ok = WinClipboard.SetText(text);
            _copiedRow = rowIndex;
            _copiedTimer = 60;
            ShowToast(ok
                ? "已复制 " + text + "   在命名框按 Ctrl+V 粘贴"
                : "复制失败，请手动输入：" + text);
            Game1.playSound("coin");
        }

        private void SetSearchFocus(bool on)
        {
            if (_searchBox == null)
                return;
            _searchBox.Selected = on;
            Game1.keyboardDispatcher.Subscriber = on ? _searchBox : null;
        }

        private bool SearchFocused() => _searchBox != null && _searchBox.Selected;
    }
}
