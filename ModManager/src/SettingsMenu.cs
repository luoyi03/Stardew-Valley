using System;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Menus;

namespace ModManager
{
    public sealed class SettingsMenu : IClickableMenu
    {
        private const int RowH = 58;

        private readonly Action _back;
        private readonly TextField _nexus = new() { Hint = "点这里输入 Nexus API Key", Mask = true };
        private readonly TextField _gh = new() { Hint = "可留空（只是提高 GitHub 限流额度）" };
        private readonly TextField _inbox = new() { Hint = "例：D:\\下载\\星露谷mod" };

        private bool _capturing;
        private readonly ScrollBar _scroll = new();
        private Rectangle _content;
        private Rectangle[] _rows = Array.Empty<Rectangle>();

        private Rectangle _btnSave, _btnReset, _btnClose, _btnKeyPage;
        private Rectangle _pasteNexus, _pasteGh, _pasteInbox;
        private string _hint = "";
        private int _hintTicks;

        private static readonly string[] Labels =
        {
            "Nexus API Key",
            "GitHub Token（可选）",
            "打开界面的快捷键",
            "启动游戏时自动检查",
            "自动检查间隔（小时）",
            "更新前自动备份",
            "包含测试版（Beta）",
            "标题界面显示入口按钮",
            "Mod 下载目录",
            "启动时自动安装下载目录"
        };

        public SettingsMenu(Action back)
            : base(0, 0, 100, 100, false)
        {
            _back = back;
            var cfg = ModEntry.Config;
            _nexus.Text = cfg.NexusApiKey ?? "";
            _gh.Text = cfg.GitHubToken ?? "";
            _inbox.Text = cfg.InboxFolder ?? "";

            Relayout();
            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 46, yPositionOnScreen + 10, 40, 40),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);
        }

        private void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            width = Math.Max(620, Math.Min(uiW - 40, 900));
            height = Math.Max(420, Math.Min(uiH - 40, 660));
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            int cx = xPositionOnScreen + 20;
            _content = new Rectangle(cx, yPositionOnScreen + 62, width - 40 - 12, height - 62 - 66);
            _scroll.Track = new Rectangle(_content.Right + 2, _content.Y, 10, _content.Height);
            _scroll.View = _content.Height;
            _scroll.Content = Labels.Length * RowH + 8;

            _rows = new Rectangle[Labels.Length];
            for (int i = 0; i < Labels.Length; i++)
                _rows[i] = new Rectangle(_content.X, _content.Y + i * RowH, _content.Width - 4, RowH - 8);

            int by = yPositionOnScreen + height - 52;
            _btnKeyPage = new Rectangle(xPositionOnScreen + 20, by, 208, 36);
            _btnReset = new Rectangle(xPositionOnScreen + width - 20 - 108 - 8 - 108, by, 108, 36);
            _btnSave = new Rectangle(xPositionOnScreen + width - 20 - 108, by, 108, 36);
            _btnClose = _btnSave;

            UpdateFieldBounds();
        }

        private void UpdateFieldBounds()
        {
            int scroll = _scroll.Pos;
            if (_rows.Length < 2)
                return;

            int fieldW = Math.Max(160, _content.Width - 250 - 78);
            _nexus.Bounds = new Rectangle(_content.X + 240, _rows[0].Y + 8 - scroll, fieldW, 34);
            _gh.Bounds = new Rectangle(_content.X + 240, _rows[1].Y + 8 - scroll, fieldW, 34);
            _pasteNexus = new Rectangle(_nexus.Bounds.Right + 6, _nexus.Bounds.Y, 72, 34);
            _pasteGh = new Rectangle(_gh.Bounds.Right + 6, _gh.Bounds.Y, 72, 34);

            if (_rows.Length > 8)
            {
                _inbox.Bounds = new Rectangle(_content.X + 240, _rows[8].Y + 8 - scroll, fieldW, 34);
                _pasteInbox = new Rectangle(_inbox.Bounds.Right + 6, _inbox.Bounds.Y, 72, 34);
            }
        }

        private void Paste(TextField field)
        {
            string text = ClipboardUtil.GetText();
            if (string.IsNullOrEmpty(text))
            {
                Toast("剪贴板是空的，或者读不到剪贴板；请手动输入");
                return;
            }
            field.Text = text.Trim();
            field.Focused = true;
            Toast("已从剪贴板粘贴，点「保存」生效");
        }

        private void Toast(string s)
        {
            _hint = s;
            _hintTicks = 300;
        }

        // ---------------- 绘制 ----------------
        public override void draw(SpriteBatch b)
        {
            Ui.Fill(b, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), new Color(0, 0, 0, 130));
            Ui.Box(b, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), Ui.PageBg, Ui.Line);

            Ui.Text(b, "Mod 管理器 · 设置", new Vector2(xPositionOnScreen + 20, yPositionOnScreen + 14), Ui.Gold, Game1.dialogueFont);
            Ui.Text(b, "Nexus Mods 的更新检测与下载需要你自己的免费 API Key（复制后点「粘贴」即可）",
                new Vector2(xPositionOnScreen + 20, yPositionOnScreen + 40), Ui.Dim);

            Ui.Box(b, _content, Ui.PanelBg2, Ui.Line);
            Ui.BeginClip(b, _content);

            var cfg = ModEntry.Config;
            for (int i = 0; i < _rows.Length; i++)
            {
                var r = _rows[i];
                r.Y = _content.Y + i * RowH - _scroll.Pos;
                if (r.Bottom <= _content.Y || r.Y >= _content.Bottom)
                    continue;

                Ui.Fill(b, r, Ui.PanelBg);
                Ui.Text(b, Labels[i], new Vector2(r.X + 14, r.Y + 12), Ui.Ink);

                switch (i)
                {
                    case 0: _nexus.Draw(b); DrawPaste(b, _pasteNexus); break;
                    case 1: _gh.Draw(b); DrawPaste(b, _pasteGh); break;
                    case 2: DrawKeyRow(b, r); break;
                    case 3: DrawToggle(b, r, cfg.AutoCheckOnLaunch); break;
                    case 4: DrawNumber(b, r, cfg.AutoCheckIntervalHours); break;
                    case 5: DrawToggle(b, r, cfg.BackupBeforeUpdate); break;
                    case 6: DrawToggle(b, r, cfg.IncludePrerelease); break;
                    case 7: DrawToggle(b, r, cfg.ShowTitleButton); break;
                    case 8: _inbox.Draw(b); DrawPaste(b, _pasteInbox); break;
                    case 9: DrawToggle(b, r, cfg.AutoInstallInboxOnLaunch); break;
                }
            }

            Ui.EndClip(b);
            _scroll.Draw(b);

            int by = _btnSave.Y;
            Ui.Fill(b, new Rectangle(xPositionOnScreen + 16, by - 12, width - 32, 1), Ui.Line);
            Ui.Button(b, _btnKeyPage, "打开 Nexus Key 申请页", true, _btnKeyPage.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Blue);
            Ui.Button(b, _btnReset, "恢复默认", true, _btnReset.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Red);
            Ui.Button(b, _btnSave, "保存", true, _btnSave.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Green);

            if (!string.IsNullOrEmpty(_hint))
                Ui.Text(b, Ui.Clip(_hint, Game1.smallFont, width - 260), new Vector2(xPositionOnScreen + 240, by + 10), Ui.Gold);

            upperRightCloseButton?.draw(b);
            Ui.DrawCursor(b);
        }

        private void DrawPaste(SpriteBatch b, Rectangle r)
        {
            if (r.Width <= 0)
                return;
            Ui.Button(b, r, "粘贴", true, r.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Blue);
        }

        private void DrawToggle(SpriteBatch b, Rectangle r, bool on)
        {
            var rect = new Rectangle(r.Right - 96, r.Y + 8, 88, 34);
            Ui.Button(b, rect, on ? "开" : "关", true, rect.Contains(Game1.getMouseX(), Game1.getMouseY()), on ? Ui.Green : Ui.Dim);
        }

        private void DrawNumber(SpriteBatch b, Rectangle r, int value)
        {
            var minus = new Rectangle(r.Right - 148, r.Y + 8, 40, 34);
            var plus = new Rectangle(r.Right - 44, r.Y + 8, 40, 34);
            var box = new Rectangle(r.Right - 104, r.Y + 8, 56, 34);
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            Ui.Button(b, minus, "-", true, minus.Contains(mx, my), Ui.Gold);
            Ui.Button(b, plus, "+", true, plus.Contains(mx, my), Ui.Gold);
            Ui.Box(b, box, Ui.PanelBg2, Ui.Line);
            string s = value.ToString();
            var size = Ui.Measure(s);
            Ui.Text(b, s, new Vector2(box.X + (box.Width - size.X) / 2, box.Y + (box.Height - size.Y) / 2), Ui.Ink);
        }

        private void DrawKeyRow(SpriteBatch b, Rectangle r)
        {
            var rect = new Rectangle(r.Right - 200, r.Y + 8, 192, 34);
            string label = _capturing ? "按下新按键…" : (ModEntry.Config.OpenKey.ToString());
            Ui.Button(b, rect, label, true, rect.Contains(Game1.getMouseX(), Game1.getMouseY()), _capturing ? Ui.Gold : Ui.Blue);
        }

        // ---------------- 输入 ----------------
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                Close(false);
                return;
            }
            if (_scroll.TryGrab(x, y))
                return;

            if (_btnSave.Contains(x, y)) { Game1.playSound("smallSelect"); Close(true); return; }
            if (_btnReset.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                ModEntry.Config.Reset();
                _nexus.Text = "";
                _gh.Text = "";
                _inbox.Text = "";
                Toast("已恢复默认设置（点保存生效）");
                return;
            }
            if (_btnKeyPage.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                try { Process.Start(new ProcessStartInfo(NexusSource.KeyPageUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Toast("打开浏览器失败：" + ex.Message); }
                return;
            }

            if (_pasteNexus.Contains(x, y)) { Game1.playSound("smallSelect"); Paste(_nexus); return; }
            if (_pasteGh.Contains(x, y)) { Game1.playSound("smallSelect"); Paste(_gh); return; }
            if (_pasteInbox.Contains(x, y)) { Game1.playSound("smallSelect"); Paste(_inbox); return; }

            bool nexusHit = _nexus.Bounds.Contains(x, y);
            bool ghHit = _gh.Bounds.Contains(x, y);
            bool inboxHit = _inbox.Bounds.Contains(x, y);
            _nexus.Focused = nexusHit;
            _gh.Focused = ghHit;
            _inbox.Focused = inboxHit;
            if (nexusHit || ghHit || inboxHit)
                return;

            if (!_content.Contains(x, y))
                return;

            var cfg = ModEntry.Config;
            for (int i = 0; i < _rows.Length; i++)
            {
                var r = _rows[i];
                r.Y = _content.Y + i * RowH - _scroll.Pos;
                if (!r.Contains(x, y))
                    continue;

                switch (i)
                {
                    case 2:
                        _capturing = true;
                        Toast("按下想用的按键，Esc 取消");
                        break;
                    case 3: cfg.AutoCheckOnLaunch = !cfg.AutoCheckOnLaunch; break;
                    case 4:
                        if (x < r.Right - 100) cfg.AutoCheckIntervalHours = Math.Max(1, cfg.AutoCheckIntervalHours - 1);
                        else cfg.AutoCheckIntervalHours = Math.Min(168, cfg.AutoCheckIntervalHours + 1);
                        break;
                    case 5: cfg.BackupBeforeUpdate = !cfg.BackupBeforeUpdate; break;
                    case 6: cfg.IncludePrerelease = !cfg.IncludePrerelease; break;
                    case 7: cfg.ShowTitleButton = !cfg.ShowTitleButton; break;
                    case 8:
                        Toast("点左边输入框，或点「粘贴」把文件夹路径粘进来");
                        _inbox.Focused = true;
                        break;
                    case 9: cfg.AutoInstallInboxOnLaunch = !cfg.AutoInstallInboxOnLaunch; break;
                }
                Game1.playSound("smallSelect");
                return;
            }
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _scroll.Release();
        }

        public override void receiveScrollWheelAction(int direction)
        {
            _scroll.ScrollBy(direction > 0 ? -60 : 60);
            UpdateFieldBounds();
        }

        public override void receiveKeyPress(Keys key)
        {
            if (_capturing)
            {
                _capturing = false;
                if (key != Keys.Escape && Enum.TryParse<SButton>(key.ToString(), true, out var sb))
                {
                    ModEntry.Config.OpenKey = KeybindList.ForSingle(sb);
                    Toast("快捷键已设为 " + key);
                }
                return;
            }

            var sbtn = (SButton)key;
            if (_nexus.Receive(sbtn) || _gh.Receive(sbtn) || _inbox.Receive(sbtn))
                return;

            if (key == Keys.Escape)
            {
                Close(false);
                return;
            }
            base.receiveKeyPress(key);
        }

        public override bool readyToClose() => true;

        public override void update(GameTime time)
        {
            base.update(time);
            if (_scroll.Dragging)
            {
                _scroll.ApplyDrag(Game1.getMouseY());
                UpdateFieldBounds();
            }
            if (_hintTicks > 0 && --_hintTicks == 0)
                _hint = "";
        }

        private void Close(bool save)
        {
            if (save)
            {
                ModEntry.Config.NexusApiKey = (_nexus.Text ?? "").Trim();
                ModEntry.Config.GitHubToken = (_gh.Text ?? "").Trim();
                ModEntry.Config.InboxFolder = (_inbox.Text ?? "").Trim();
                ModEntry.SaveConfig();
                ModEntry.Service?.RefreshMods();
                Toast("设置已保存");
            }
            _nexus.Focused = false;
            _gh.Focused = false;
            _inbox.Focused = false;
            Game1.playSound("bigDeSelect");
            _back?.Invoke();
        }
    }
}
