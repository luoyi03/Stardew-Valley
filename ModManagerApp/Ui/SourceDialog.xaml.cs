using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SMM
{
    /// <summary>给一个查不到更新的 Mod 手工补写更新源（填进去先验证，避免编号写错）。</summary>
    public partial class SourceDialog : Window
    {
        private readonly AppEnv _env;
        private readonly LocalMod _mod;
        private bool _busy;

        /// <summary>保存成功时，被写入的更新源（如 "Nexus:3753"）。</summary>
        public string SavedKey { get; private set; }

        public SourceDialog(AppEnv env, LocalMod mod)
        {
            _env = env;
            _mod = mod;
            InitializeComponent();

            string name = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
            TxtTitle.Text = "给「" + name + "」补写更新源";

            string cur = mod.Keys == null || mod.Keys.Count == 0
                ? "这个 Mod 的 manifest 里没写 UpdateKeys，所以查不到更新。"
                : "manifest 里现在写的是：" + string.Join("、", mod.Keys.ConvertAll(k => k.Raw))
                  + "（作者写的不对时，在这里填的会优先使用）";

            TxtSub.Text = cur + "\nUniqueID：" + mod.Id + "　当前版本：" + (mod.Version ?? "?");

            string search = SourceProbe.SearchText(mod.Name);
            TxtSearchName.Text = "（" + search + "）";
        }

        // ==================== 验证 ====================

        private async Task<bool> ProbeAsync()
        {
            string input = TxtInput.Text?.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                TxtProbe.Text = "先在上面填一个编号或链接。";
                return false;
            }
            if (_busy)
                return false;

            _busy = true;
            BtnProbe.IsEnabled = false;
            BtnSave.IsEnabled = false;
            TxtProbe.Text = "正在查…";
            bool verified = false;
            try
            {
                string msg = await SourceProbe.DescribeAsync(_env, input);
                TxtProbe.Text = msg;
                verified = msg.StartsWith("✓");
                if (verified)
                    TxtWarn.Text = "";
                else
                    TxtWarn.Text = "⚠ 没能联网确认这个更新源是否正确，确认编号没写错再保存。";
            }
            catch (Exception ex)
            {
                TxtProbe.Text = "✗ " + ex.Message;
                verified = false;
            }
            finally
            {
                _busy = false;
                BtnProbe.IsEnabled = true;
                BtnSave.IsEnabled = true;
            }
            return verified;
        }

        private async void BtnProbe_Click(object sender, RoutedEventArgs e) => await ProbeAsync();

        private async void TxtInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await ProbeAsync();
        }

        // ==================== 保存 ====================

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var key = SourceProbe.Parse(TxtInput.Text);
            if (key == null)
            {
                TxtProbe.Text = "✗ 看不懂这个写法。可以填：Nexus 编号、Nexus 网页链接、GitHub 仓库链接。";
                return;
            }

            bool verified = await ProbeAsync();
            if (!verified)
            {
                var r = Dlg.Show(this,
                    "没能确认这个更新源（可能是编号不对，或者网络查不到）。\n\n将要保存：" + key.Raw
                    + "\n\n确定要保存吗？",
                    "确认保存", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                _env.Config.ExtraUpdateKeys ??= new Dictionary<string, string>();
                _env.Config.ExtraUpdateKeys[_mod.Id] = key.Raw;
                _env.Config.Save();
                _env.Log($"补写更新源：{_mod.Id} = {key.Raw}");
                SavedKey = key.Raw;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                TxtProbe.Text = "✗ 保存失败：" + ex.Message;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ==================== 找编号用的辅助 ====================

        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            string q = SourceProbe.SearchText(_mod.Name);
            if (string.IsNullOrWhiteSpace(q))
            {
                Dlg.Show(this, "这个 Mod 没有名字，没法搜。", "提示");
                return;
            }
            App.OpenUrl("https://www.nexusmods.com/stardewvalley/search/?gsearch=" + Uri.EscapeDataString(q));
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            string q = SourceProbe.SearchText(_mod.Name);
            try
            {
                Clipboard.SetText(q);
                _env.Log("已复制到剪贴板：" + q);
                TxtProbe.Text = "已复制：" + q;
            }
            catch (Exception ex)
            {
                TxtProbe.Text = "复制失败：" + ex.Message;
            }
        }
    }
}
