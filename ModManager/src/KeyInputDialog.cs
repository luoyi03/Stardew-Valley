using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ModManager
{
    /// <summary>给没有更新源的 Mod 手动补一个更新源的小对话框。</summary>
    public sealed class KeyInputDialog : IClickableMenu
    {
        private readonly LocalMod _mod;
        private readonly Action<UpdateKey> _onDone;
        private readonly TextField _field = new() { Hint = "粘贴 Nexus 页面链接，或直接填数字 ID" };

        private Rectangle _ok;
        private Rectangle _cancel;
        private string _error;

        public KeyInputDialog(LocalMod mod, Action<UpdateKey> onDone)
            : base(0, 0, 100, 100, false)
        {
            _mod = mod;
            _onDone = onDone;
            Relayout();
        }

        private void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            width = Math.Min(720, uiW - 60);
            height = 230;
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            _field.Bounds = new Rectangle(xPositionOnScreen + 24, yPositionOnScreen + 96, width - 48, 40);
            _field.Focused = true;
            _ok = new Rectangle(xPositionOnScreen + width - 24 - 120 - 8 - 120, yPositionOnScreen + height - 58, 120, 38);
            _cancel = new Rectangle(xPositionOnScreen + width - 24 - 120, yPositionOnScreen + height - 58, 120, 38);
        }

        public override void draw(SpriteBatch b)
        {
            Ui.Fill(b, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), new Color(0, 0, 0, 160));
            Ui.Box(b, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), Ui.PageBg, Ui.Gold);

            Ui.Text(b, "给「" + Ui.Clip(_mod.Name, Game1.smallFont, width - 260) + "」补一个更新源",
                new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 18), Ui.Gold, Game1.dialogueFont);

            Ui.Text(b, "它自己的 manifest 里没写更新源，所以检查不了。填 Nexus 页面链接或数字 ID 即可：",
                new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 58), Ui.Dim);

            _field.Draw(b);

            if (_error != null)
                Ui.Text(b, _error, new Vector2(xPositionOnScreen + 24, yPositionOnScreen + 144), Ui.Red);

            Ui.Button(b, _ok, "确定", true, _ok.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Green);
            Ui.Button(b, _cancel, "取消", true, _cancel.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Dim);
            Ui.DrawCursor(b);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_cancel.Contains(x, y))
            {
                exitThisMenu(true);
                return;
            }
            if (_ok.Contains(x, y))
            {
                Confirm();
                return;
            }
            _field.Focused = _field.Bounds.Contains(x, y);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (_field.Receive((SButton)key))
                return;
            if (key == Keys.Enter)
            {
                Confirm();
                return;
            }
            if (key == Keys.Escape)
            {
                exitThisMenu(true);
                return;
            }
            base.receiveKeyPress(key);
        }

        private void Confirm()
        {
            var key = UpdateKey.FromInput(_field.Text);
            if (key == null)
            {
                _error = "没看懂这个内容，请填 Nexus 页面链接、Nexus 数字 ID，或 GitHub:用户/仓库";
                return;
            }
            _field.Focused = false;
            Game1.playSound("bigSelect");
            _onDone?.Invoke(key);
            exitThisMenu(true);
        }

        public override bool readyToClose() => true;
    }
}
