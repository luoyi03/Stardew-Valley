using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace InGameGuide
{
    /// <summary>一条预计算好的绘制指令。坐标相对于内容区左上角。</summary>
    public class Op
    {
        public bool IsText;
        public Rectangle Rect;
        public Color Color;
        public string Text;
        public SpriteFont Font;
        public Vector2 Pos;
    }

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
        /// <summary>整块面板的内衬底色（深，保证浅色文字可读）。</summary>
        public static readonly Color PageBg = new(0x22, 0x1D, 0x16);
        public static readonly Color PanelBg = new(0x33, 0x2D, 0x23);
        public static readonly Color PanelBg2 = new(0x2B, 0x26, 0x1D);
        public static readonly Color Hover = new(0x44, 0x3B, 0x2C);
        public static readonly Color Line = new(0x62, 0x58, 0x44);
        public static readonly Color HiLine = new(0x7E, 0x93, 0x5C);

        private static RasterizerState _scissorState;

        // ---------- 基础绘制 ----------
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

        // ---------- 文本折行 ----------
        private static readonly Dictionary<char, float> _widths = new();

        public static float CharWidth(SpriteFont f, char c)
        {
            if (_widths.TryGetValue(c, out float w))
                return w;
            w = f.MeasureString(c.ToString()).X;
            _widths[c] = w;
            return w;
        }

        public static List<string> Wrap(string text, SpriteFont f, float maxWidth)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                res.Add("");
                return res;
            }
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

        private static readonly Dictionary<SpriteFont, int> _lineHeights = new();

        public static int LineHeight(SpriteFont f)
        {
            if (_lineHeights.TryGetValue(f, out int h))
                return h;
            h = Math.Max(16, (int)f.MeasureString("Hg").Y + 4);
            _lineHeights[f] = h;
            return h;
        }

        // ---------- 内容布局 ----------
        public static List<Op> BuildChapterLayout(Chapter ch, int width)
        {
            var ops = new List<Op>();
            SpriteFont bf = Body, hf = Head;
            int y = 12;
            int lh = LineHeight(bf);

            // 章节标题
            foreach (var line in Wrap(ch.Title ?? "", hf, width))
            {
                ops.Add(new Op { IsText = true, Text = line, Font = hf, Color = Green, Pos = new Vector2(0, y) });
                y += LineHeight(hf);
            }
            y += 2;
            foreach (var line in Wrap(ch.Sub ?? "", bf, width))
            {
                ops.Add(new Op { IsText = true, Text = line, Font = bf, Color = Dim, Pos = new Vector2(0, y) });
                y += lh;
            }
            y += 16;

            int liIndex = 0;
            foreach (var blk in ch.Blocks ?? new List<Block>())
            {
                switch (blk.T)
                {
                    case "h3": LayoutHeading(ops, blk.X ?? "", width, ref y, hf, Green, true); break;
                    case "h4": LayoutHeading(ops, blk.X ?? "", width, ref y, bf, Gold, false); break;
                    case "p": LayoutText(ops, blk.X ?? "", width, ref y, bf, Ink, 0, lh, 10); break;
                    case "cap": LayoutText(ops, blk.X ?? "", width, ref y, bf, Dim, 0, lh, 8); break;

                    case "note":
                    {
                        Color accent = blk.K switch
                        {
                            "tip" => Green,
                            "warn" => Red,
                            "info" => Blue,
                            _ => Gold
                        };
                        LayoutCard(ops, blk.X ?? "", width, ref y, bf, Ink, accent, lh);
                        break;
                    }

                    case "li":
                    {
                        liIndex++;
                        string bullet = blk.O ? liIndex + "." : "•";
                        LayoutBullet(ops, blk.X ?? "", bullet, width, ref y, bf, Ink, lh);
                        break;
                    }

                    case "flow":
                        LayoutCard(ops, string.Join("  →  ", blk.Xs ?? new List<string>()), width, ref y, bf, Green, GreenDim(), lh);
                        break;

                    case "box":
                        LayoutBox(ops, blk.Title ?? "", blk.Xs, width, ref y, bf, lh);
                        break;

                    case "tbl":
                        LayoutTable(ops, blk.Rows, width, ref y, bf, lh);
                        break;
                }
            }

            y += 24;
            return ops;
        }

        private static Color GreenDim() => new(0x6E, 0xA2, 0x54);

        private static void AddRect(List<Op> ops, int x, int y, int w, int h, Color c)
            => ops.Add(new Op { Rect = new Rectangle(x, y, w, h), Color = c });

        private static void LayoutHeading(List<Op> ops, string text, int width, ref int y, SpriteFont f, Color c, bool big)
        {
            int gap = big ? 20 : 14;
            y += gap;
            int lh = LineHeight(f);
            var lines = Wrap(text, f, width - 14);
            int h = lines.Count * lh;
            AddRect(ops, 0, y, 4, h, c);
            foreach (var line in lines)
            {
                ops.Add(new Op { IsText = true, Text = line, Font = f, Color = c, Pos = new Vector2(14, y) });
                y += lh;
            }
            y += big ? 6 : 4;
        }

        private static void LayoutText(List<Op> ops, string text, int width, ref int y, SpriteFont f, Color c, int indent, int lh, int gapAfter)
        {
            y += 4;
            foreach (var line in Wrap(text, f, width - indent))
            {
                ops.Add(new Op { IsText = true, Text = line, Font = f, Color = c, Pos = new Vector2(indent, y) });
                y += lh;
            }
            y += gapAfter;
        }

        private static void LayoutBullet(List<Op> ops, string text, string bullet, int width, ref int y, SpriteFont f, Color c, int lh)
        {
            y += 2;
            int indent = 24;
            ops.Add(new Op { IsText = true, Text = bullet, Font = f, Color = Gold, Pos = new Vector2(6, y) });
            foreach (var line in Wrap(text, f, width - indent))
            {
                ops.Add(new Op { IsText = true, Text = line, Font = f, Color = c, Pos = new Vector2(indent, y) });
                y += lh;
            }
            y += 2;
        }

        private static void LayoutCard(List<Op> ops, string text, int width, ref int y, SpriteFont f, Color c, Color accent, int lh)
        {
            y += 8;
            var lines = Wrap(text, f, width - 34);
            int h = lines.Count * lh + 16;
            AddRect(ops, 0, y, width, h, PanelBg);
            AddRect(ops, 0, y, 4, h, accent);
            int ty = y + 8;
            foreach (var line in lines)
            {
                ops.Add(new Op { IsText = true, Text = line, Font = f, Color = c, Pos = new Vector2(16, ty) });
                ty += lh;
            }
            y += h + 8;
        }

        private static void LayoutBox(List<Op> ops, string title, List<string> xs, int width, ref int y, SpriteFont f, int lh)
        {
            y += 8;
            int ty = y + 8;

            // 先算文字，再画背景，避免背景把文字盖住
            var text = new List<Op>();
            if (!string.IsNullOrEmpty(title))
            {
                foreach (var line in Wrap(title, f, width - 34))
                {
                    text.Add(new Op { IsText = true, Text = line, Font = f, Color = Green, Pos = new Vector2(16, ty) });
                    ty += lh;
                }
                ty += 2;
            }
            foreach (string s in xs ?? new List<string>())
            {
                if (string.IsNullOrEmpty(s))
                    continue;
                foreach (var line in Wrap(s, f, width - 34))
                {
                    text.Add(new Op { IsText = true, Text = line, Font = f, Color = Ink, Pos = new Vector2(16, ty) });
                    ty += lh;
                }
            }
            int h = Math.Max(lh + 16, ty + 8 - y);
            AddRect(ops, 0, y, width, h, PanelBg);
            AddRect(ops, 0, y, 4, h, Gold);
            ops.AddRange(text);
            y += h + 8;
        }

        private static void LayoutTable(List<Op> ops, List<List<string>> rows, int width, ref int y, SpriteFont f, int lh)
        {
            if (rows == null || rows.Count == 0)
                return;

            y += 10;
            int cols = 0;
            foreach (var r in rows) cols = Math.Max(cols, r.Count);
            if (cols == 0) return;

            const int MinCol = 72;
            const int MaxCol = 300;
            float[] natural = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                foreach (var r in rows)
                {
                    string s = c < r.Count ? r[c] ?? "" : "";
                    if (s.Length == 0) continue;
                    float w = 0;
                    foreach (var line in Wrap(s, f, MaxCol))
                        w = Math.Max(w, Measure(f, line));
                    natural[c] = Math.Max(natural[c], Math.Min(w + 16f, MaxCol));
                }
                natural[c] = Math.Max(natural[c], MinCol);
            }

            float[] colW = new float[cols];
            float total = 0;
            foreach (float n in natural) total += n;

            if (total <= width)
            {
                Array.Copy(natural, colW, cols);
            }
            else
            {
                // 先保证每列最小值，余量按超出比例分配
                float baseSum = 0, extraSum = 0;
                for (int c = 0; c < cols; c++)
                {
                    colW[c] = Math.Min(natural[c], MinCol);
                    baseSum += colW[c];
                    extraSum += Math.Max(0f, natural[c] - colW[c]);
                }
                float rest = width - baseSum;
                if (extraSum > 0 && rest > 0)
                {
                    for (int c = 0; c < cols; c++)
                        colW[c] += Math.Max(0f, natural[c] - colW[c]) / extraSum * rest;
                }
            }

            float tableW = 0;
            foreach (float w in colW) tableW += w;

            float[] colX = new float[cols];
            float cx = 0;
            for (int c = 0; c < cols; c++) { colX[c] = cx; cx += colW[c]; }

            int rowY = y;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                int maxLines = 1;
                var cells = new List<List<string>>(cols);
                for (int c = 0; c < cols; c++)
                {
                    string s = c < r.Count ? r[c] ?? "" : "";
                    var lines = Wrap(s, f, colW[c] - 14);
                    cells.Add(lines);
                    maxLines = Math.Max(maxLines, lines.Count);
                }
                int rh = maxLines * lh + 8;

                Color bg = i == 0 ? new Color(0x44, 0x3C, 0x2E) : (i % 2 == 0 ? PanelBg2 : PanelBg);
                Color fg = i == 0 ? Gold : Ink;
                AddRect(ops, 0, rowY, (int)tableW, rh, bg);

                for (int c = 0; c < cols; c++)
                {
                    int ty = rowY + 4;
                    foreach (var line in cells[c])
                    {
                        ops.Add(new Op { IsText = true, Text = line, Font = f, Color = fg, Pos = new Vector2(colX[c] + 7, ty) });
                        ty += lh;
                    }
                }
                AddRect(ops, 0, rowY + rh - 1, (int)tableW, 1, Line);
                rowY += rh;
            }
            AddRect(ops, 0, y - 4, 3, rowY - y + 8, Gold);
            y = rowY + 10;
        }

        private static float Measure(SpriteFont f, string s) => string.IsNullOrEmpty(s) ? 0 : f.MeasureString(s).X;

        // ---------- 按可视范围绘制 ----------
        public static void DrawOps(List<Op> ops, SpriteBatch b, Vector2 origin, int scroll, int viewHeight)
        {
            foreach (var op in ops)
            {
                // 剔除判断必须用「相对内容顶部」的坐标，不能拿绝对屏幕坐标和视口高度比
                float top = op.IsText ? op.Pos.Y : op.Rect.Y;
                float bottom = op.IsText ? op.Pos.Y + LineHeight(op.Font) : op.Rect.Bottom;
                if (bottom < scroll - 8 || top > scroll + viewHeight + 8)
                    continue;

                float oy = origin.Y + op.Pos.Y - scroll;
                if (op.IsText)
                    b.DrawString(op.Font, op.Text, new Vector2(origin.X + op.Pos.X, oy), op.Color);
                else
                    Fill(b, new Rectangle((int)(origin.X + op.Rect.X), (int)(origin.Y + op.Rect.Y - scroll), op.Rect.Width, op.Rect.Height), op.Color);
            }
        }

        public static int ContentHeight(List<Op> ops)
        {
            int max = 0;
            foreach (var op in ops)
                max = Math.Max(max, op.IsText ? (int)(op.Pos.Y + LineHeight(op.Font)) : op.Rect.Bottom);
            return max;
        }
    }
}
