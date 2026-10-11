using System.Windows;

namespace SMM
{
    /// <summary>把对话框的宿主统一成主窗口，避免 UserControl 找不到 Window 参数。</summary>
    internal static class Dlg
    {
        public static MessageBoxResult Show(object owner, string text, string caption = null,
            MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            var window = owner as Window
                         ?? (owner as DependencyObject is DependencyObject d ? Window.GetWindow(d) : null)
                         ?? Application.Current?.MainWindow;
            if (window == null || !window.IsLoaded)
                return MessageBox.Show(text, caption ?? "星露谷 Mod 管理器", button, icon);
            return MessageBox.Show(window, text, caption ?? "星露谷 Mod 管理器", button, icon);
        }

        public static Window Owner(object owner)
            => owner as Window
               ?? (owner as DependencyObject is DependencyObject d ? Window.GetWindow(d) : null)
               ?? Application.Current?.MainWindow;
    }
}
