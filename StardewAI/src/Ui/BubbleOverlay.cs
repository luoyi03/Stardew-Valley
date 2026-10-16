using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace StardewAI.Ui
{
    /// <summary>玩家头顶的世界坐标气泡（她主动搭话时把第一句顶在你头上）。</summary>
    internal static class BubbleOverlay
    {
        private const int Tile = 64;

        private static string _text = "";
        private static DateTime _until = DateTime.MinValue;

        public static void Show(string text, double seconds)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            _text = text.Trim();
            _until = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        }

        public static void Clear()
        {
            _text = "";
            _until = DateTime.MinValue;
        }

        /// <summary>在 Display.RenderedWorld 里调（此时 SpriteBatch 是世界的相机坐标）。</summary>
        public static void Draw(SpriteBatch b)
        {
            try
            {
                if (_text.Length == 0 || DateTime.UtcNow > _until || !Context.IsWorldReady)
                    return;

                Farmer p = Game1.player;
                if (p == null || b == null)
                    return;

                SpriteFont font = Game1.smallFont;
                int lineH = Ui.LineHeight(font);

                int maxW = 320;
                List<string> lines = Ui.Wrap(_text, font, maxW);
                if (lines.Count > 3)
                    lines.RemoveRange(3, lines.Count - 3);

                int w = 24;
                foreach (string line in lines)
                    w = Math.Max(w, (int)Ui.Measure(font, line) + 24);

                int h = lines.Count * lineH + 16;

                // 玩家所在格子的上方（世界坐标）
                float cx = p.Tile.X * Tile + Tile / 2f;
                float cy = p.Tile.Y * Tile - 18f;

                int x = (int)(cx - w / 2f);
                int y = (int)(cy - h);

                Ui.Box(b, new Rectangle(x, y, w, h), new Color(0x1E, 0x1A, 0x14) * 0.92f, Ui.Gold);

                int ty = y + 8;

                foreach (string line in lines)
                {
                    b.DrawString(font, line, new Vector2(x + 12, ty), Ui.Ink);
                    ty += lineH;
                }
            }
            catch
            {
                // 画气泡出问题不能让游戏崩
            }
        }
    }
}
