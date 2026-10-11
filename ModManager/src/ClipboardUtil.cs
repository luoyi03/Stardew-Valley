namespace ModManager
{
    internal static class ClipboardUtil
    {
        /// <summary>读取系统剪贴板文本；失败返回 null（游戏根目录自带的 TextCopy.dll）。</summary>
        public static string GetText()
        {
            try
            {
                return TextCopy.ClipboardService.GetText();
            }
            catch
            {
                return null;
            }
        }
    }
}
