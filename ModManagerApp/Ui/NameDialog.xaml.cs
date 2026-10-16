using System;
using System.Windows;
using System.Windows.Input;

namespace SMM
{
    /// <summary>
    /// 给一个 Mod 改中文名。保存时会写进该 Mod 的 manifest.json 的 Name 字段，
    /// 这样游戏里（SMAPI 控制台 / 游戏内 Mod 管理器）也显示中文名。
    /// </summary>
    public partial class NameDialog : Window
    {
        private readonly AppEnv _env;
        private readonly LocalMod _mod;

        /// <summary>保存 / 还原成功后的提示文字，给外面弹 Toast 用。</summary>
        public string ResultMessage { get; private set; }

        public NameDialog(AppEnv env, LocalMod mod)
        {
            _env = env;
            _mod = mod;
            InitializeComponent();

            string cur = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
            TxtTitle.Text = "给「" + ModNames.Display(mod) + "」改中文名";

            string orig = string.IsNullOrWhiteSpace(mod.OriginalName) ? cur : mod.OriginalName;
            string sub = $"UniqueID：{mod.Id}　当前版本：{mod.Version ?? "?"}";
            if (!string.IsNullOrWhiteSpace(mod.Dir))
                sub += "\n目录：" + mod.Dir;
            TxtSub.Text = sub;

            // 内置对照表里有中文名就预填上，省得手打（点保存才会真正写进 manifest）
            TxtName.Text = ModNames.Chinese(mod) ?? cur;
            TxtOld.Text = string.Equals(orig, cur, StringComparison.Ordinal)
                ? "原名（英文）：" + orig
                : "原名（英文）：" + orig + "　·　现在显示的是上面这个名字";

            BtnRestore.IsEnabled = ModNamer.HasChineseName(env, mod);
            Loaded += (_, _) => { TxtName.Focus(); TxtName.SelectAll(); };
        }

        private void TxtName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            Save();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            string name = TxtName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("先在上面填一个名字。");
                return;
            }

            try
            {
                if (AppEnv.IsGameRunning())
                {
                    var r = Dlg.Show(this,
                        "游戏正在运行。可以改，但游戏里要等下次重启才会显示新名字。继续吗？",
                        "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes)
                        return;
                }

                ResultMessage = ModNamer.Apply(_env, _mod, name);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("保存失败：" + ex.Message);
            }
        }

        private void BtnRestore_Click(object sender, RoutedEventArgs e)
        {
            var r = Dlg.Show(this,
                "把这个 Mod 的名字还原成作者原本写的英文名，并删掉这里的中文名记录。\n\n确定要还原吗？",
                "确认还原", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
                return;

            try
            {
                ResultMessage = ModNamer.Restore(_env, _mod);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("还原失败：" + ex.Message);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            TxtError.Text = message;
        }
    }
}
