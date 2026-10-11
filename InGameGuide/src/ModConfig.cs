using System;
using StardewModdingAPI;

namespace InGameGuide
{
    public class ModConfig
    {
        /// <summary>打开「新手教程」面板的按键。</summary>
        public string ToggleKey { get; set; } = "F2";

        /// <summary>直接打开「物品代码查询」面板的按键。</summary>
        public string QueryKey { get; set; } = "F3";

        public bool TryToggleKey(out SButton key) => TryParseSButton(ToggleKey, out key);
        public bool TryQueryKey(out SButton key) => TryParseSButton(QueryKey, out key);

        private static bool TryParseSButton(string raw, out SButton key)
        {
            key = SButton.None;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            return Enum.TryParse(raw.Trim(), true, out key) && key != SButton.None;
        }
    }
}
