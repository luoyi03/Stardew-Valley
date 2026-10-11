using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;

namespace ModManager
{
    /// <summary>极简单行输入框：自己把 SButton 映射成字符，不依赖游戏的键盘订阅器。</summary>
    public sealed class TextField
    {
        public Rectangle Bounds;
        public string Text = "";
        public bool Focused;
        public string Hint = "";
        public bool Mask;
        public int MaxLength = 200;

        public void Draw(SpriteBatch b)
        {
            Color border = Focused ? Ui.Gold : Ui.Line;
            Ui.Box(b, Bounds, Ui.PanelBg2, border);

            string show = Text ?? "";
            if (show.Length == 0 && !Focused)
                show = Hint;
            if (Mask && (Text ?? "").Length > 0)
                show = new string('•', Math.Min(24, Text.Length));

            Color fg = (Text ?? "").Length == 0 && !Focused ? Ui.Dim : Ui.Ink;
            string clipped = Ui.Clip(show, Game1.smallFont, Bounds.Width - 16);

            bool caret = Focused && Game1.currentGameTime != null
                && (int)(Game1.currentGameTime.TotalGameTime.TotalSeconds * 2) % 2 == 0;
            if (caret)
                clipped += "|";

            Ui.Text(b, clipped, new Vector2(Bounds.X + 8, Bounds.Y + (Bounds.Height - Ui.Measure(clipped).Y) / 2), fg);
        }

        /// <summary>返回 true 表示这个按键被输入框吃掉了。</summary>
        public bool Receive(SButton button)
        {
            if (button == SButton.Escape)
            {
                if (Focused)
                {
                    Focused = false;
                    return true;
                }
                return false;
            }
            if (!Focused)
                return false;

            if (button == SButton.Enter)
            {
                Focused = false;
                return true;
            }
            if (button == SButton.Back)
            {
                if (Text.Length > 0)
                    Text = Text.Substring(0, Text.Length - 1);
                return true;
            }
            if (button == SButton.Delete)
            {
                Text = "";
                return true;
            }

            int v = (int)button;
            if (v < 32 || v > 126)
                return false;
            if (Text.Length >= MaxLength)
                return true;

            bool shift = Keyboard.GetState().IsKeyDown(Keys.LeftShift) || Keyboard.GetState().IsKeyDown(Keys.RightShift);
            char c = (char)v;

            if (c >= 'A' && c <= 'Z')
                c = shift ? c : char.ToLowerInvariant(c);
            else if (c >= '0' && c <= '9' && shift)
                c = ")!@#$%^&*(" [c - '0'];
            else if (shift)
                c = Shifted(c);

            Text += c;
            return true;
        }

        private static char Shifted(char c) => c switch
        {
            '-' => '_',
            '=' => '+',
            '[' => '{',
            ']' => '}',
            ';' => ':',
            '\'' => '"',
            ',' => '<',
            '.' => '>',
            '/' => '?',
            '`' => '~',
            '\\' => '|',
            _ => c
        };
    }
}
