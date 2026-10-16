using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewAI.Voice;
using StardewValley;

namespace StardewAI.Ui
{
    /// <summary>屏幕下方的字幕/状态条：显示她正在说的那句话，或「正在听你说话」的提示。</summary>
    internal static class SubtitleOverlay
    {
        public static void Draw(SpriteBatch b)
        {
            try
            {
                DrawInner(b);
            }
            catch
            {
                // 画字幕出问题绝不能让游戏崩
            }
        }

        private static void DrawInner(SpriteBatch b)
        {
            if (b == null || Game1.graphics?.GraphicsDevice == null)
                return;

            var cfg = ModEntry.Config;
            if (cfg == null || !cfg.VoiceEnabled)
                return;

            string title = null;
            string text = null;

            if (VoiceAssistant.Mic.Active)
            {
                title = "正在听你说";
                text = "按住 " + (string.IsNullOrWhiteSpace(cfg.PttKey) ? "V" : cfg.PttKey) +
                       " 说完松开即可 · 已录 " + VoiceAssistant.Mic.Seconds.ToString("0.0") + " 秒";
            }
            else if (!string.IsNullOrWhiteSpace(VoiceAssistant.Status))
            {
                title = "语音";
                text = VoiceAssistant.Status;
            }
            else if (!string.IsNullOrWhiteSpace(VoiceAssistant.Player.Subtitle))
            {
                title = string.IsNullOrWhiteSpace(VoiceAssistant.Player.Speaker) ? "语音" : VoiceAssistant.Player.Speaker;
                text = VoiceAssistant.Player.Subtitle;
            }

            if (string.IsNullOrWhiteSpace(text))
                return;

            SpriteFont font = Game1.smallFont;
            int lineH = Ui.LineHeight(font);
            Viewport vp = Game1.graphics.GraphicsDevice.Viewport;

            int width = Math.Min(Math.Max(420, vp.Width - 200), 900);
            List<string> lines = Ui.Wrap(text, font, width - 32);

            int height = 12 + (title != null ? lineH : 0) + lines.Count * lineH + 12;
            int x = (vp.Width - width) / 2;
            int y = Math.Max(24, vp.Height - 170 - height);

            // 半透明底 + 细边框，压住游戏画面方便看清字
            Ui.Box(b, new Rectangle(x, y, width, height), new Color(0x14, 0x12, 0x0E) * 0.88f, Ui.Line);

            int ty = y + 12;

            if (title != null)
            {
                b.DrawString(font, title, new Vector2(x + 16, ty), Ui.Gold);
                ty += lineH;
            }

            foreach (string line in lines)
            {
                b.DrawString(font, line, new Vector2(x + 16, ty), Ui.Ink);
                ty += lineH;
            }
        }
    }
}
