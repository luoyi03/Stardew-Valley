using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace StardewAI.Ui
{
    public static class Ui
    {
        public static SpriteFont Body => Game1.smallFont;
        public static SpriteFont Head => Game1.dialogueFont;

        public static readonly Color Ink = new(0xF2, 0xED, 0xE0);
        public static readonly Color Dim = new(0xB8, 0xAF, 0x9C);
        public static readonly Color Green = new(0xA8, 0xD6, 0x80);
        public static readonly Color Gold = new(0xF2, 0xCC, 0x64);
        public static readonly Color Red = new(0xF0, 0x96, 0x7C);
        public static readonly Color Blue = new(0xA4, 0xD2, 0xEC);

        public static readonly Color PageBg = new(0x1E, 0x1A, 0x14);
        public static readonly Color PanelBg = new(0x33, 0x2D, 0x23);
        public static readonly Color PanelBg2 = new(0x2B, 0x26, 0x1D);
        public static readonly Color Hover = new(0x48, 0x3F, 0x30);
        public static readonly Color Line = new(0x62, 0x58, 0x44);

        private static RasterizerState _scissor;

        public static void Fill(SpriteBatch b, Rectangle r, Color c) => b.Draw(ModEntry.Pixel, r, c);

        public static Texture2D Pixel => ModEntry.Pixel;

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
            _scissor ??= new RasterizerState { ScissorTestEnable = true };
            b.GraphicsDevice.ScissorRectangle = new Rectangle(
                Math.Max(0, clip.X), Math.Max(0, clip.Y),
                Math.Max(0, clip.Width), Math.Max(0, clip.Height));
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, _scissor);
        }

        public static void EndClip(SpriteBatch b)
        {
            b.End();
            b.GraphicsDevice.ScissorRectangle = new Rectangle(0, 0, b.GraphicsDevice.Viewport.Width, b.GraphicsDevice.Viewport.Height);
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
        }

        // ---------- 文本 ----------

        private static readonly Dictionary<char, float> Widths = new();

        public static float CharWidth(SpriteFont f, char c)
        {
            if (Widths.TryGetValue(c, out float w))
                return w;
            w = f.MeasureString(c.ToString()).X;
            Widths[c] = w;
            return w;
        }

        public static float Measure(SpriteFont f, string s) => string.IsNullOrEmpty(s) ? 0f : f.MeasureString(s).X;

        public static List<string> Wrap(string text, SpriteFont f, float maxWidth)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                res.Add("");
                return res;
            }

            maxWidth = Math.Max(24f, maxWidth);

            foreach (string para in text.Replace("\r\n", "\n").Split('\n'))
            {
                string cur = "";
                float w = 0f;

                foreach (char c in para)
                {
                    float cw = CharWidth(f, c);

                    // 行首第一个字永远放行，避免宽度过小导致死循环
                    if (w + cw > maxWidth && cur.Length > 0)
                    {
                        // 避头尾：标点不换行到下一行开头，尽量粘在这一行
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

        private static readonly Dictionary<SpriteFont, int> LineHeights = new();

        public static int LineHeight(SpriteFont f)
        {
            if (LineHeights.TryGetValue(f, out int h))
                return h;
            h = Math.Max(16, (int)f.MeasureString("Hg").Y + 6);
            LineHeights[f] = h;
            return h;
        }
    }
}
