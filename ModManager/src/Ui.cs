using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace ModManager
{
    public static class Ui
    {
        public static SpriteFont Body => Game1.smallFont;
        public static SpriteFont Head => Game1.dialogueFont;

        public static readonly Color Ink = new(0xF2, 0xED, 0xE0);
        public static readonly Color Dim = new(0xC4, 0xBB, 0xA8);
        public static readonly Color Green = new(0xA8, 0xD6, 0x80);
        public static readonly Color Gold = new(0xF2, 0xCC, 0x64);
        public static readonly Color Red = new(0xF0, 0x96, 0x7C);
        public static readonly Color Blue = new(0xA4, 0xD2, 0xEC);
        public static readonly Color PageBg = new(0x22, 0x1D, 0x16);
        public static readonly Color PanelBg = new(0x33, 0x2D, 0x23);
        public static readonly Color PanelBg2 = new(0x2B, 0x26, 0x1D);
        public static readonly Color Hover = new(0x44, 0x3B, 0x2C);
        public static readonly Color Line = new(0x62, 0x58, 0x44);
        public static readonly Color HiLine = new(0x7E, 0x93, 0x5C);

        private static RasterizerState _scissorState;

        public static void Fill(SpriteBatch b, Rectangle r, Color c) => b.Draw(ModEntry.Pixel, r, c);

        public static void Box(SpriteBatch b, Rectangle r, Color bg, Color border)
        {
            Fill(b, r, bg);
            Fill(b, new Rectangle(r.X, r.Y, r.Width, 1), border);
            Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), border);
            Fill(b, new Rectangle(r.X, r.Y, 1, r.Height), border);
            Fill(b, new Rectangle(r.Right - 1, r.Y, 1, r.Height), border);
        }

        public static void BeginClip(SpriteBatch b, Rectangle clip)
        {
            b.End();
            _scissorState ??= new RasterizerState { ScissorTestEnable = true };
            b.GraphicsDevice.ScissorRectangle = new Rectangle(
                Math.Max(0, clip.X), Math.Max(0, clip.Y),
                Math.Max(0, clip.Width), Math.Max(0, clip.Height));
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, _scissorState);
        }

        public static void EndClip(SpriteBatch b)
        {
            b.End();
            b.GraphicsDevice.ScissorRectangle = new Rectangle(0, 0, b.GraphicsDevice.Viewport.Width, b.GraphicsDevice.Viewport.Height);
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
        }

        public static void Text(SpriteBatch b, string s, Vector2 pos, Color c, SpriteFont f = null)
            => b.DrawString(f ?? Body, s ?? "", pos, c);

        public static Vector2 Measure(string s, SpriteFont f = null)
            => (f ?? Body).MeasureString(s ?? "");

        private static readonly Dictionary<char, float> _widths = new();

        private static float CharWidth(SpriteFont f, char c)
        {
            if (_widths.TryGetValue(c, out float w))
                return w;
            w = f.MeasureString(c.ToString()).X;
            _widths[c] = w;
            return w;
        }

        /// <summary>按最大宽度截断，超出的部分用 … 表示。</summary>
        public static string Clip(string s, SpriteFont f, float maxWidth)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            f ??= Body;
            if (Measure(s, f).X <= maxWidth)
                return s;

            float w = CharWidth(f, '…');
            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                float cw = CharWidth(f, c);
                if (w + cw > maxWidth)
                    break;
                sb.Append(c);
                w += cw;
            }
            return sb.ToString() + "…";
        }

        /// <summary>自定义菜单里游戏不会画光标，需要自己补一笔。</summary>
        public static void DrawCursor(SpriteBatch b)
        {
            if (Game1.options.hardwareCursor)
                return;
            b.Draw(Game1.mouseCursors,
                new Vector2(Game1.getMouseX(), Game1.getMouseY()),
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, Game1.mouseCursor, 16, 16),
                Color.White * Game1.mouseCursorTransparency,
                0f, Vector2.Zero,
                4f + Game1.dialogueButtonScale / 150f,
                SpriteEffects.None, 1f);
        }

        public static void Button(SpriteBatch b, Rectangle r, string label, bool enabled, bool hover, Color accent)
        {
            Color bg = !enabled ? PanelBg2 : (hover ? Hover : PanelBg);
            Color border = !enabled ? Line : accent;
            Color fg = !enabled ? Dim : (hover ? Color.White : accent);

            Box(b, r, bg, border);

            string txt = Clip(label, Body, r.Width - 12);
            var size = Measure(txt);
            Text(b, txt, new Vector2(r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2), fg);
        }

        public static void ProgressBar(SpriteBatch b, Rectangle r, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            Box(b, r, PanelBg2, Line);
            int w = (int)((r.Width - 4) * percent / 100.0);
            if (w > 0)
                Fill(b, new Rectangle(r.X + 2, r.Y + 2, w, r.Height - 4), HiLine);

            string txt = percent + "%";
            var size = Measure(txt);
            Text(b, txt, new Vector2(r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2), Ink);
        }

        public static List<string> Wrap(string text, SpriteFont f, float maxWidth)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                res.Add("");
                return res;
            }
            f ??= Body;
            maxWidth = Math.Max(24f, maxWidth);

            foreach (string para in text.Split('\n'))
            {
                string cur = "";
                float w = 0f;
                foreach (char c in para)
                {
                    float cw = CharWidth(f, c);
                    if (w + cw > maxWidth && cur.Length > 0)
                    {
                        res.Add(cur);
                        cur = c.ToString();
                        w = cw;
                    }
                    else
                    {
                        cur += c;
                        w += cw;
                    }
                }
                res.Add(cur);
            }
            return res;
        }
    }
}
