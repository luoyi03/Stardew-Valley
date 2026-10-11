using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace InGameCodex.Ui
{
    /// <summary>图鉴面板的绘制基础（配色与 InGameGuide 保持一致，游戏内观感统一）。</summary>
    public static class Cx
    {
        public static SpriteFont Body => Game1.smallFont;
        public static SpriteFont Head => Game1.dialogueFont;

        /// <summary>全局 UI 缩放：文字按此比例绘制，布局常量按 <see cref="Sc"/> 换算（来自 config.json 的 UiScale）。</summary>
        public static float S = 1.4f;

        /// <summary>把基准像素换算成当前缩放下的像素。</summary>
        public static int Sc(float v) => (int)Math.Round(v * S);

        /// <summary>按缩放绘制一段文字（原点 / 旋转不变，位置与 4 参数版 DrawString 一致）。</summary>
        public static void Text(SpriteBatch b, SpriteFont f, string s, Vector2 pos, Color c)
            => b.DrawString(f, s, pos, c, 0f, Vector2.Zero, S, SpriteEffects.None, 0.9f);

        /// <summary>按缩放测量文字宽度。</summary>
        public static float MW(SpriteFont f, string s) => f.MeasureString(s).X * S;

        /// <summary>按缩放测量文字高度。</summary>
        public static float MH(SpriteFont f, string s) => f.MeasureString(s).Y * S;

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
        public static readonly Color Selected = new(0x4F, 0x7D, 0x3F);

        private static RasterizerState _scissorState;

        public static void Fill(SpriteBatch b, Rectangle r, Color c) => b.Draw(ModEntry.Pixel, r, c);

        /// <summary>设计视口尺寸：界面按 1.0 排版时能用的逻辑宽高（真实视口 / S）。</summary>
        internal static int DesignW => Math.Max(320, (int)Math.Round(Game1.uiViewport.Width / S));

        /// <summary>设计视口高度。</summary>
        internal static int DesignH => Math.Max(240, (int)Math.Round(Game1.uiViewport.Height / S));

        // ===== 设计空间绘制（S9：AI 页与 AI 设置界面）=====
        // 这两个界面是整体搬来的旧代码，按 1.0 倍排版；绘制时给 SpriteBatch 加一个「缩放 S 倍 + 平移 dest」的变换矩阵，
        // 于是界面源码不用逐处乘 S，显示大小与其它页一致。
        //
        // ⚠ 这里**绝不能切渲染目标**（v1.12.1 缺陷根因）：SDV 的 Game1.DrawMenu 是
        //   `PushUIMode()`（把渲染目标切到 Game1.game1.uiScreen）→ 绘制菜单 → `PopUIMode()`；
        //   菜单批次其实是画在一个离屏 RT 上的。如果按常规去 `GraphicsDevice.SetRenderTarget(rt)`、
        //   再 `SetRenderTarget(null)`，后面的绘制就落到**后台缓冲**上（后台缓冲不参与 UI 合成），
        //   于是 AI 页内容与鼠标全都看不见。用矩阵就完全不动渲染目标。
        private static bool _inDesign;
        private static float _designScale = 1f;
        private static Point _designOrigin;

        /// <summary>当前设计变换（不在设计空间时为恒等）。</summary>
        private static Matrix Transform => _inDesign
            ? Matrix.CreateScale(_designScale, _designScale, 1f) * Matrix.CreateTranslation(_designOrigin.X, _designOrigin.Y, 0f)
            : Matrix.Identity;

        /// <summary>把整个设计视口按 S 倍放大铺满屏幕绘制（AI 设置界面用）。输入坐标要自己 / S 还原。</summary>
        internal static void ScaledDesign(SpriteBatch b, Action<SpriteBatch> draw)
        {
            Viewport vp = b.GraphicsDevice.Viewport;
            ScaledDrawing(b, new Rectangle(0, 0, vp.Width, vp.Height), draw);
        }

        /// <summary>把 dest 区域当作「设计坐标 (0,0)-(dest/S)」来绘制（AI 页用）。</summary>
        internal static void ScaledDrawing(SpriteBatch b, Rectangle dest, Action<SpriteBatch> draw)
        {
            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null,
                Matrix.CreateScale(S, S, 1f) * Matrix.CreateTranslation(dest.X, dest.Y, 0f));

            _inDesign = true;
            _designScale = S;
            _designOrigin = new Point(dest.X, dest.Y);
            try
            {
                draw(b);
            }
            finally
            {
                _inDesign = false;
                _designScale = 1f;
                _designOrigin = Point.Zero;

                // 还原成游戏 DrawMenu / PopUIMode 用的那套 Begin 参数（Game1.cs 里就是这几个）
                b.End();
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null);
            }
        }

        /// <summary>设计坐标 → 当前渲染目标坐标（裁剪矩形用的是渲染目标像素，变换矩阵对它无效）。</summary>
        private static Rectangle ClipToTarget(SpriteBatch b, Rectangle r)
        {
            if (!_inDesign)
            {
                return new Rectangle(
                    Math.Max(0, r.X), Math.Max(0, r.Y),
                    Math.Max(0, r.Width), Math.Max(0, r.Height));
            }

            var screen = new Rectangle(
                _designOrigin.X + (int)Math.Round(r.X * _designScale),
                _designOrigin.Y + (int)Math.Round(r.Y * _designScale),
                Math.Max(1, (int)Math.Round(r.Width * _designScale)),
                Math.Max(1, (int)Math.Round(r.Height * _designScale)));
            return Rectangle.Intersect(screen, b.GraphicsDevice.Viewport.Bounds);
        }

        public static void Box(SpriteBatch b, Rectangle r, Color bg, Color border)
        {
            Fill(b, r, bg);
            Fill(b, new Rectangle(r.X, r.Y, r.Width, 1), border);
            Fill(b, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), border);
            Fill(b, new Rectangle(r.X, r.Y, 1, r.Height), border);
            Fill(b, new Rectangle(r.Right - 1, r.Y, 1, r.Height), border);
        }

        /// <summary>名字只有问号或空白：游戏数据里这条物品没有正式名称（界面显示为 ???）。</summary>
        public static bool IsUnnamed(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;
            foreach (char ch in name)
            {
                if (ch != '?' && !char.IsWhiteSpace(ch))
                    return false;
            }
            return true;
        }

        public static void BeginClip(SpriteBatch b, Rectangle clip)
        {
            b.End();
            _scissorState ??= new RasterizerState { ScissorTestEnable = true };
            b.GraphicsDevice.ScissorRectangle = ClipToTarget(b, clip);
            // 设计空间里重开批次时必须把变换矩阵带上，否则这一批会按 1.0 倍画到错位置
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, _scissorState, null, Transform);
        }

        public static void EndClip(SpriteBatch b)
        {
            b.End();
            b.GraphicsDevice.ScissorRectangle = new Rectangle(0, 0, b.GraphicsDevice.Viewport.Width, b.GraphicsDevice.Viewport.Height);
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, null, null, Transform);
        }

        private static readonly Dictionary<char, float> _widths = new();

        public static float CharWidth(SpriteFont f, char c)
        {
            if (_widths.TryGetValue(c, out float w))
                return w;
            w = f.MeasureString(c.ToString()).X * S;
            _widths[c] = w;
            return w;
        }

        /// <summary>按像素宽度折行（中文逐字算宽，换行符也认）。</summary>
        public static List<string> Wrap(string text, SpriteFont f, float maxWidth)
        {
            var res = new List<string>();
            if (string.IsNullOrEmpty(text))
                return res;

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
            h = Math.Max(Sc(16), (int)((f.MeasureString("Hg").Y + 4) * S));
            _lineHeights[f] = h;
            return h;
        }

        public static string Truncate(string s, SpriteFont f, float maxW)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            if (f.MeasureString(s).X * S <= maxW)
                return s;

            string cur = "";
            foreach (char c in s)
            {
                if (f.MeasureString(cur + c + "…").X * S > maxW)
                    break;
                cur += c;
            }
            return cur + "…";
        }

        /// <summary>物品类别 -> 降级色块颜色（贴图不可用时的兜底）。</summary>
        public static Color TypeColor(string key)
        {
            switch (key)
            {
                case "O": return new Color(0x5E, 0x8A, 0x4B);
                case "BC": return new Color(0x4B, 0x74, 0x8A);
                case "T": return new Color(0x8A, 0x7A, 0x4B);
                case "W": return new Color(0x8A, 0x4B, 0x4B);
                case "F": return new Color(0x6B, 0x5A, 0x8A);
                case "S": return new Color(0x8A, 0x5A, 0x74);
                case "H": return new Color(0x74, 0x5A, 0x8A);
                case "P": return new Color(0x4B, 0x6A, 0x8A);
                case "B": return new Color(0x5A, 0x6A, 0x4B);
                case "TR": return new Color(0x8A, 0x6A, 0x3B);
                case "M": return new Color(0x7A, 0x7A, 0x7A);
                case "WP": return new Color(0x4B, 0x8A, 0x7A);
                case "FL": return new Color(0x8A, 0x8A, 0x4B);
                default: return new Color(0x66, 0x66, 0x66);
            }
        }
    }
}
