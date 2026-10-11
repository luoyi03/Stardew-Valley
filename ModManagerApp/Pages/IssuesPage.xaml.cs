using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SMM
{
    public partial class IssuesPage : UserControl, IPage
    {
        private readonly IShell _shell;
        private readonly ObservableCollection<ModIssue> _items = new();
        private bool _busy;

        private AppEnv Env => _shell.Env;

        public IssuesPage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();
            List.ItemsSource = _items;
            UpdateInfo();
        }

        public void OnActivated() => _ = RecheckAsync();

        private async Task RecheckAsync()
        {
            if (_busy)
                return;
            _busy = true;
            _shell.SetBusy("正在检查 Mod 之间的依赖与冲突…");
            try
            {
                var mods = await Task.Run(() => ModScanner.Scan(Env.ModsPath, Env.DisabledDir));
                var issues = await Task.Run(() => Analyzer.Analyze(Env, mods));

                _items.Clear();
                foreach (var i in issues)
                    _items.Add(i);

                UpdateInfo();
                _shell.SetStatus(issues.Count == 0 ? "没有发现依赖或冲突问题" : $"发现 {issues.Count} 个问题");
            }
            catch (Exception ex)
            {
                _shell.Toast("检查失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
            }
        }

        private void UpdateInfo()
        {
            int errors = _items.Count(i => i.Level == IssueLevel.Error);
            int warns = _items.Count(i => i.Level == IssueLevel.Warning);
            int fixable = _items.Count(i => i.Fix != null);

            TxtInfo.Text = _items.Count == 0
                ? "检查项：重复安装、入口文件冲突、缺失前置、依赖被禁用、依赖版本过低、缺少内容包框架、SMAPI 版本不足、缺少更新源。"
                : $"错误 {errors} 个 · 警告 {warns} 个 · 其中 {fixable} 个可以一键处理。"
                  + "「一键修复」只会做安全的操作（重新启用被依赖的 Mod、删掉多余的重复副本），删掉的都会进备份区。";

            BtnFixAll.IsEnabled = fixable > 0 && !_busy;
        }

        private async void BtnRecheck_Click(object sender, RoutedEventArgs e) => await RecheckAsync();

        private void BtnSmapiPage_Click(object sender, RoutedEventArgs e) => App.OpenUrl("https://smapi.io");

        private void BtnRowFix_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            if (!((sender as FrameworkElement)?.DataContext is ModIssue issue) || issue.Fix == null)
                return;

            if (issue.Kind == "重复安装" || issue.Kind == "入口文件冲突")
            {
                if (Dlg.Show(this,
                        $"将删除多余的副本（会先备份）：\n\n{issue.Detail}",
                        "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }

            try
            {
                issue.Fix();
                _shell.Toast("已处理：" + issue.Kind);
            }
            catch (Exception ex)
            {
                _shell.Toast("处理失败：" + ex.Message);
            }

            _ = AfterFixAsync();
        }

        private async void BtnFixAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            var fixables = _items.Where(i => i.Fix != null).ToList();
            if (fixables.Count == 0)
                return;

            if (Dlg.Show(this,
                    $"将自动处理 {fixables.Count} 个问题：\n\n"
                    + string.Join("\n", fixables.Take(8).Select(i => "· " + i.Kind + "：" + i.ModName))
                    + (fixables.Count > 8 ? $"\n… 共 {fixables.Count} 个" : "")
                    + "\n\n涉及删除的操作都会先备份。继续吗？",
                    "一键修复", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            int ok = 0;
            var errors = new System.Collections.Generic.List<string>();
            foreach (var issue in fixables)
            {
                try
                {
                    issue.Fix();
                    ok++;
                }
                catch (Exception ex)
                {
                    errors.Add(issue.ModName + "：" + ex.Message);
                }
            }

            await AfterFixAsync();
            _shell.Toast(errors.Count == 0
                ? $"已处理 {ok} 个问题"
                : $"处理 {ok} 个，失败 {errors.Count} 个：" + string.Join("；", errors.Take(4)));
        }

        private async Task AfterFixAsync()
        {
            await _shell.ReloadModsAsync();
            await RecheckAsync();
        }
    }
}
