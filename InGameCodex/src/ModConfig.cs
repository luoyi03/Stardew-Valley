using System;
using System.Collections.Generic;
using StardewModdingAPI;
using InGameCodex.Ai;

namespace InGameCodex
{
    /// <summary>InGameCodex 的可配置项（写入 config.json）。</summary>
    public class ModConfig
    {
        // ---------------- 开关 ----------------

        /// <summary>打开/关闭图鉴面板的按键。</summary>
        public string ToggleKey { get; set; } = "F6";

        // ---------------- 界面 ----------------

        /// <summary>列表和详情里是否同时显示英文名。</summary>
        public bool ShowEnglishName { get; set; } = true;

        /// <summary>获取确认方式：Smart = 数量为 1 且普通品质时直接给，否则二次确认；Always = 每次都确认；Never = 从不确认。</summary>
        public string ConfirmMode { get; set; } = "Smart";

        /// <summary>收藏的限定 ID 列表（如 (O)24）。</summary>
        public List<string> Favorites { get; set; } = new();

        /// <summary>最近查看过的限定 ID 列表，最新的在最前，最多 50 条。</summary>
        public List<string> Recents { get; set; } = new();

        /// <summary>界面整体缩放（1.0 = 原始大小；文字、行高、图标一起放大，建议 1.0 ~ 2.0）。</summary>
        public float UiScale { get; set; } = 1.4f;

        // ---------------- AI 问答（S9） ----------------

        /// <summary>AI 问答相关设置（原 StardewAI 的 config.json，首次启动自动复制过来）。</summary>
        public AiConfig Ai { get; set; } = new();

        // ---------------- 辅助 ----------------

        public bool TryToggleKey(out SButton key) => TryParse(ToggleKey, out key);

        /// <summary>是否需要二次确认。</summary>
        public bool NeedConfirm(int count, int quality, bool forceAsk)
        {
            if (string.Equals(ConfirmMode, "Never", StringComparison.OrdinalIgnoreCase))
                return false;
            if (forceAsk)
                return true;
            if (string.Equals(ConfirmMode, "Always", StringComparison.OrdinalIgnoreCase))
                return true;
            return count > 1 || quality != 0;
        }

        private static bool TryParse(string raw, out SButton key)
        {
            key = SButton.None;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            return Enum.TryParse(raw.Trim(), true, out key) && key != SButton.None;
        }
    }
}
