using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SMM
{
    /// <summary>通用的「填一行字」对话框：标题 / 说明 / 初值 / 校验函数都由调用方给。</summary>
    public partial class TextInputDialog : Window
    {
        private readonly Func<string, string> _validate;
        private readonly bool _captureKeys;

        /// <summary>用户确认后的文本。</summary>
        public string ResultText { get; private set; }

        public TextInputDialog(string title, string subtitle, string label, string initial,
            Func<string, string> validate, string hint = null, string extraTitle = null, string extra = null,
            bool captureKeys = false)
        {
            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "输入" : title;
            TxtTitle.Text = title ?? "";
            TxtSub.Text = subtitle ?? "";
            TxtLabel.Text = label ?? "值";
            TxtHint.Text = hint ?? "";
            TxtValue.Text = initial ?? "";
            _validate = validate;
            _captureKeys = captureKeys;

            if (!string.IsNullOrWhiteSpace(extra))
            {
                TxtExtraTitle.Text = extraTitle ?? "说明";
                TxtExtra.Text = extra;
                BoxExtra.Visibility = Visibility.Visible;
            }

            if (captureKeys)
            {
                BoxCapture.Visibility = Visibility.Visible;
                TxtCaptureHint.Text = "组合键：按住修饰键再按主键（例如按住 Shift 按 F5 → 记成 LeftShift + F5）。"
                                      + "支持 F1-F24、字母、D0-D9、方向键、Space/Enter/Tab 等；"
                                      + "鼠标右键/中键也能按（左键用于点这个框，要绑左键请手打 MouseLeft）。"
                                      + "按 Esc 清空重来。";
            }

            if (_captureKeys)
            {
                // 等窗口真正激活后再抢焦点：在 Loaded 里 Focus 常常不起作用，按键就被输入框吃掉了
                Activated += (_, _) => FocusCaptureBox();
                Loaded += (_, _) => FocusCaptureBox();
            }
            else
            {
                Loaded += (_, _) => { TxtValue.Focus(); TxtValue.SelectAll(); };
            }
        }

        private void FocusCaptureBox()
        {
            if (!_captureKeys || !IsLoaded)
                return;
            BoxCapture.Focus();
            Keyboard.Focus(BoxCapture);
        }

        // ==================== 按键捕获（按一下键就识别，不用手打键名）====================

        /// <summary>
        /// 在窗口这一层收按键——比挂在捕获框上可靠得多（焦点在别处也能认）。
        /// 只有焦点在「输入框」里时才让路（那时是在手打键名）。
        /// </summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_captureKeys || e.Handled)
                return;

            if (Keyboard.FocusedElement is TextBox)
                return;      // 正在手打，不抢

            if (e.Key == Key.Return || e.Key == Key.Enter)
            {
                // Enter = 确认（想绑 Enter 键就在输入框里手打 "Enter"）
                e.Handled = true;
                Save();
                return;
            }

            if (e.Key == Key.Tab)
                return;      // Tab 留给切换焦点（想绑 Tab 就在输入框里手打 "Tab"）

            if (e.Key == Key.Escape)
            {
                // Esc = 清空重来（想绑 Esc 键就在上面输入框手打 "Escape"）
                TxtCapture.Text = "点这里，然后按下你要用的键";
                TxtValue.Text = "";
                TxtError.Text = "";
                e.Handled = true;
                return;
            }

            string combo = Combo(KeyFrom(e),
                IsDown(Key.LeftShift), IsDown(Key.RightShift),
                IsDown(Key.LeftCtrl), IsDown(Key.RightCtrl),
                IsDown(Key.LeftAlt), IsDown(Key.RightAlt));
            if (combo == null)
                return;

            e.Handled = true;
            ApplyCaptured(combo);
        }

        /// <summary>自检用：等价于"按下这个键"（不经过 WPF 事件路由）。</summary>
        internal bool SimulateKeyPress(Key key, bool leftShift = false, bool rightShift = false,
            bool leftCtrl = false, bool rightCtrl = false, bool leftAlt = false, bool rightAlt = false)
        {
            string combo = Combo(key, leftShift, rightShift, leftCtrl, rightCtrl, leftAlt, rightAlt);
            if (combo == null)
                return false;

            ApplyCaptured(combo);
            return true;
        }

        private void ApplyCaptured(string combo)
        {
            TxtValue.Text = combo;
            TxtCapture.Text = "已记录：" + combo + "（想在输入框里手打就点上面的输入框）";
            TxtError.Text = "";
        }

        private void BoxCapture_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            BoxCapture.BorderBrush = (System.Windows.Media.Brush)FindResource("B.Accent");
            if (string.IsNullOrEmpty(TxtCapture.Text) || TxtCapture.Text.StartsWith("点这里", StringComparison.Ordinal))
                TxtCapture.Text = "现在按你要用的键…";
        }

        private void BoxCapture_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            BoxCapture.BorderBrush = (System.Windows.Media.Brush)FindResource("B.Border");
        }

        private void TxtValue_GotFocus(object sender, RoutedEventArgs e)
        {
            if (!_captureKeys)
                return;
            TxtCapture.Text = "（正在手打键名）想用按键识别就点上面的框，或直接点这里再按也行";
        }

        private void BoxCapture_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                BoxCapture.Focus();     // 左键只用来聚焦，不然一点就把 MouseLeft 记进去了
                return;
            }

            e.Handled = true;
            string name = e.ChangedButton switch
            {
                MouseButton.Right => "MouseRight",
                MouseButton.Middle => "MouseMiddle",
                MouseButton.XButton1 => "MouseX1",
                MouseButton.XButton2 => "MouseX2",
                _ => null
            };
            if (name == null)
                return;

            TxtValue.Text = name;
            TxtCapture.Text = "已记录：" + name;
            TxtError.Text = "";
        }

        /// <summary>把按键事件翻译成 SMAPI 的键名（组合键 = 修饰键 + 主键）。</summary>
        internal static string Combo(Key key, bool leftShift, bool rightShift,
            bool leftCtrl, bool rightCtrl, bool leftAlt, bool rightAlt)
        {
            string main = KeyName(key);
            if (main == null)
                return null;

            var parts = new List<string>();

            // 主键本身就是修饰键时，就只记它（例如只绑 LeftControl）
            if (!IsModifier(main))
            {
                if (leftShift) parts.Add("LeftShift");
                if (rightShift) parts.Add("RightShift");
                if (leftCtrl) parts.Add("LeftControl");
                if (rightCtrl) parts.Add("RightControl");
                if (leftAlt) parts.Add("LeftAlt");
                if (rightAlt) parts.Add("RightAlt");
            }

            parts.Add(main);
            return string.Join(" + ", parts.Distinct());
        }

        /// <summary>Alt 这类键会走 SystemKey，统一取出来。</summary>
        private static Key KeyFrom(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

        private static bool IsDown(Key k) => Keyboard.GetKeyStates(k) == KeyStates.Down;

        private static bool IsModifier(string name)
            => name is "LeftShift" or "RightShift" or "LeftControl" or "RightControl"
                or "LeftAlt" or "RightAlt";

        /// <summary>WPF 的按键 → SMAPI 键名（不在名单里的返回 null）。</summary>
        internal static string KeyName(Key k)
        {
            if (k >= Key.F1 && k <= Key.F24)
                return "F" + (k - Key.F1 + 1);
            if (k >= Key.A && k <= Key.Z)
                return k.ToString();
            if (k >= Key.D0 && k <= Key.D9)
                return "D" + (k - Key.D0);
            if (k >= Key.NumPad0 && k <= Key.NumPad9)
                return "NumPad" + (k - Key.NumPad0);

            if (IsModifier(k.ToString().Replace("Ctrl", "Control")))
                return k switch
                {
                    Key.LeftShift => "LeftShift",
                    Key.RightShift => "RightShift",
                    Key.LeftCtrl => "LeftControl",
                    Key.RightCtrl => "RightControl",
                    Key.LeftAlt => "LeftAlt",
                    Key.RightAlt => "RightAlt",
                    _ => null
                };

            return k switch
            {
                Key.Space => "Space",
                Key.Return => "Enter",
                Key.Escape => "Escape",
                Key.Tab => "Tab",
                Key.Back => "Back",
                Key.Insert => "Insert",
                Key.Delete => "Delete",
                Key.Home => "Home",
                Key.End => "End",
                Key.PageUp => "PageUp",
                Key.PageDown => "PageDown",
                Key.Up => "Up",
                Key.Down => "Down",
                Key.Left => "Left",
                Key.Right => "Right",
                Key.CapsLock => "CapsLock",
                Key.PrintScreen => "PrintScreen",
                Key.Pause => "Pause",
                Key.OemComma => "OemComma",
                Key.OemPeriod => "OemPeriod",
                Key.OemMinus => "OemMinus",
                Key.OemPlus => "OemPlus",
                Key.OemTilde => "OemTilde",
                Key.OemOpenBrackets => "OemOpenBrackets",
                Key.OemCloseBrackets => "OemCloseBrackets",
                Key.OemPipe => "OemPipe",
                Key.OemQuotes => "OemQuotes",
                Key.OemSemicolon => "OemSemicolon",
                Key.OemQuestion => "OemQuestion",
                Key.OemBackslash => "OemBackslash",
                Key.VolumeMute => "VolumeMute",
                Key.VolumeUp => "VolumeUp",
                Key.VolumeDown => "VolumeDown",
                _ => null
            };
        }

        private void TxtValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            Save();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            string v = (TxtValue.Text ?? "").Trim();
            string err = _validate?.Invoke(v);
            if (err != null)
            {
                TxtError.Text = err;
                return;
            }

            ResultText = v;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
