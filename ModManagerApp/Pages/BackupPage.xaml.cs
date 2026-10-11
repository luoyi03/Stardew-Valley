using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SMM
{
    public partial class BackupPage : UserControl, IPage
    {
        private readonly IShell _shell;
        private readonly ObservableCollection<BackupEntry> _items = new();
        private bool _busy;

        private AppEnv Env => _shell.Env;

        public BackupPage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();
            List.ItemsSource = _items;
            TxtPath.Text = "备份目录：" + Env.BackupDir;
        }

        public void OnActivated()
        {
            TxtPath.Text = "备份目录：" + Env.BackupDir;
            _ = RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            if (_busy)
                return;
            _busy = true;
            _shell.SetBusy("正在读取备份…");
            try
            {
                var list = await Task.Run(() => Backups.List(Env));
                _items.Clear();
                foreach (var e in list)
                    _items.Add(e);

                long total = list.Sum(x => x.SizeBytes);
                TxtInfo.Text = list.Count == 0
                    ? "还没有备份。更新 / 删除 Mod 时会自动在这里留一份，方便随时还原。"
                    : $"共 {list.Count} 份备份，占用 {BackupEntry.FormatSize(total)}。"
                      + $"每个 Mod 最多保留 {Env.Config.BackupKeep} 份（可以在设置里改）。"
                      + "「还原」会把备份内容写回 Mods 目录（覆盖当前版本，必要时请先关掉游戏）。";
                _shell.SetStatus($"备份 {list.Count} 份，共 {BackupEntry.FormatSize(total)}");
            }
            catch (Exception ex)
            {
                _shell.Toast("读取备份失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private void BtnOpenDir_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Env.BackupDir);

        private async void BtnClean_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            var keep = Env.Config.BackupKeep;
            var stale = _items
                .GroupBy(x => x.UniqueId ?? x.FolderName, StringComparer.OrdinalIgnoreCase)
                .SelectMany(g => g.OrderByDescending(x => x.Time).Skip(Math.Max(1, keep)))
                .ToList();

            if (stale.Count == 0)
            {
                _shell.Toast($"每个 Mod 都只保留了 {keep} 份以内的备份，没有需要清理的。");
                return;
            }

            if (Dlg.Show(this,
                    $"将删除 {stale.Count} 份较早的备份（每个 Mod 只保留最近 {keep} 份），确定吗？",
                    "清理备份", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            await Task.Run(() =>
            {
                foreach (var e in stale)
                    Backups.Delete(e);
            });
            await RefreshAsync();
            _shell.Toast($"已清理 {stale.Count} 份备份");
        }

        private async void BtnRowRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            if (!((sender as FrameworkElement)?.DataContext is BackupEntry entry))
                return;

            if (AppEnv.IsGameRunning())
            {
                Dlg.Show(this, "游戏正在运行，请先关闭游戏再还原。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (Dlg.Show(this,
                    $"把「{entry.Title}」还原成 {entry.Version ?? "?"}（备份时间 {entry.TimeText}）？\n"
                    + "会覆盖 Mods 目录里的当前版本。",
                    "确认还原", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            _busy = true;
            _shell.SetBusy("正在还原…");
            try
            {
                string message = null;
                await Task.Run(() =>
                {
                    var installed = ModScanner.Scan(Env.ModsPath, Env.DisabledDir);
                    Backups.Restore(Env, entry, installed, out message);
                });
                await _shell.ReloadModsAsync();
                await RefreshAsync();
                _shell.Toast(message ?? "已还原");
            }
            catch (Exception ex)
            {
                _shell.Toast("还原失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
            }
        }

        private async void BtnRowDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            if (!((sender as FrameworkElement)?.DataContext is BackupEntry entry))
                return;

            if (Dlg.Show(this, $"删除「{entry.Title}」{entry.TimeText} 的这份备份？",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            await Task.Run(() => Backups.Delete(entry));
            _items.Remove(entry);
            _shell.Toast("备份已删除");
        }
    }
}
