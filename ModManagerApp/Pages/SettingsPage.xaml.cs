using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SMM
{
    public partial class SettingsPage : UserControl, IPage
    {
        private readonly IShell _shell;

        private AppEnv Env => _shell.Env;

        public SettingsPage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();
            LoadFromConfig();
        }

        public void OnActivated()
        {
            LoadFromConfig();
            UpdateGameState();
        }

        private void LoadFromConfig()
        {
            var cfg = Env.Config;

            TxtGamePath.Text = Env.GamePath ?? cfg.GamePath ?? "";
            TxtNexusKey.Text = cfg.NexusApiKey ?? "";
            TxtGithubToken.Text = cfg.GitHubToken ?? "";
            ChkBackupUpdate.IsChecked = cfg.BackupBeforeUpdate;
            ChkBackupDelete.IsChecked = cfg.BackupBeforeDelete;
            ChkPrerelease.IsChecked = cfg.IncludePrerelease;
            ChkCheckOnStart.IsChecked = cfg.CheckOnStart;
            TxtKeep.Text = Math.Max(1, cfg.BackupKeep).ToString();

            var kb = new StringBuilder();
            if (cfg.ExtraUpdateKeys != null)
            {
                foreach (var kv in cfg.ExtraUpdateKeys)
                    kb.AppendLine(kv.Key + " = " + kv.Value);
            }
            TxtExtraKeys.Text = kb.ToString().TrimEnd();

            TxtIgnored.Text = cfg.IgnoredModIds == null ? "" : string.Join(Environment.NewLine, cfg.IgnoredModIds);

            string mode = string.IsNullOrWhiteSpace(cfg.BrowserMode) ? Browser.Auto : cfg.BrowserMode;
            for (int i = 0; i < CmbBrowser.Items.Count; i++)
            {
                if (CmbBrowser.Items[i] is ComboBoxItem it && (it.Tag as string) == mode)
                {
                    CmbBrowser.SelectedIndex = i;
                    break;
                }
            }
            TxtBrowserPath.Text = cfg.BrowserPath ?? "";
            UpdateBrowserState();

            ChkWatch.IsChecked = cfg.WatchFolderEnabled;
            TxtWatchFolder.Text = cfg.WatchFolder ?? "";
            string watchMode = string.Equals(cfg.WatchInstallMode, "auto", StringComparison.OrdinalIgnoreCase)
                ? "auto" : "ask";
            for (int i = 0; i < CmbWatchMode.Items.Count; i++)
            {
                if (CmbWatchMode.Items[i] is ComboBoxItem it && (it.Tag as string) == watchMode)
                {
                    CmbWatchMode.SelectedIndex = i;
                    break;
                }
            }
            UpdateWatchState();

            TxtNexusState.Text = string.IsNullOrWhiteSpace(cfg.NexusApiKey)
                ? "还没填 Key。填好后点「测试连接」可以验证是否有效，也能看出是免费账号还是 Premium。"
                : "已保存 Key。点「测试连接」可以验证有效性和账号类型。";

            UpdateGameState();
        }

        private void UpdateGameState()
        {
            var sb = new StringBuilder();
            bool valid = GameLocator.IsValid(Env.GamePath);
            sb.Append(valid ? "✓ 目录有效　" : "✗ 目录无效（找不到 Stardew Valley.exe / Mods）　");
            sb.Append(Env.ModsPath != null && Directory.Exists(Env.ModsPath)
                ? $"Mods：{Env.ModsPath}　"
                : "Mods 目录不存在　");
            sb.Append(Env.SmapiVersion == null ? "SMAPI：未检测到（Mod 需要先装 SMAPI）　" : $"SMAPI：{Env.SmapiVersion}　");
            sb.Append("备份 / 数据目录：" + Env.DataDir);
            TxtGameState.Text = sb.ToString();
        }

        // ==================== 目录 ====================

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择星露谷安装目录（含 Stardew Valley.exe 的那一层）"
            };
            if (Directory.Exists(TxtGamePath.Text))
                dlg.InitialDirectory = TxtGamePath.Text;
            if (dlg.ShowDialog(Dlg.Owner(this)) != true)
                return;

            TxtGamePath.Text = dlg.FolderName;
            UpdateGameState();
        }

        private void BtnDetect_Click(object sender, RoutedEventArgs e)
        {
            string found = GameLocator.Detect();
            if (string.IsNullOrEmpty(found))
            {
                _shell.Toast("没自动找到游戏目录。请点「浏览…」手动选择含 Stardew Valley.exe 的那一层。");
                return;
            }
            TxtGamePath.Text = found;
            UpdateGameState();
            _shell.Toast("找到游戏目录：" + found + "（记得点「保存设置」）");
        }

        private void BtnGetNexusKey_Click(object sender, RoutedEventArgs e)
            => App.OpenUrl(NexusSource.KeyPageUrl);

        private void BtnGetGithubToken_Click(object sender, RoutedEventArgs e)
            => App.OpenUrl("https://github.com/settings/tokens/new?description=StardewModManager");

        // ==================== 下载文件夹监控 ====================

        private bool WatchAutoMode()
            => (CmbWatchMode.SelectedItem as ComboBoxItem)?.Tag as string == "auto";

        private void UpdateWatchState()
        {
            if (TxtWatchState == null || ChkWatch == null)
                return;

            bool on = ChkWatch.IsChecked == true;
            string folder = (TxtWatchFolder.Text ?? "").Trim();

            if (!on)
            {
                TxtWatchState.Text = "监控已关闭：新下载的 zip 不会自动处理，需要手动拖进「Mod 管理」页。";
                return;
            }
            if (folder.Length == 0)
            {
                TxtWatchState.Text = "还没设置文件夹。填上浏览器保存下载文件的目录（或者点「浏览…」）。";
                return;
            }
            if (!Directory.Exists(folder))
            {
                TxtWatchState.Text = "这个文件夹不存在：" + folder;
                return;
            }

            TxtWatchState.Text = $"正在监控：{folder}"
                + (WatchAutoMode() ? "　发现新 zip 会直接被安装。" : "　发现新 zip 会先弹窗问你。")
                + "　只处理程序运行期间新出现的文件；已经躺在里面的旧包可以用「立即检查」手动扫。";
        }

        private void ChkWatch_Changed(object sender, RoutedEventArgs e) => UpdateWatchState();

        private void TxtWatchFolder_TextChanged(object sender, TextChangedEventArgs e) => UpdateWatchState();

        private void CmbWatchMode_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWatchState();

        private void BtnWatchBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择要监控的下载文件夹" };
            string cur = (TxtWatchFolder.Text ?? "").Trim();
            if (Directory.Exists(cur))
                dlg.InitialDirectory = cur;

            if (dlg.ShowDialog(Dlg.Owner(this)) != true)
                return;

            TxtWatchFolder.Text = dlg.FolderName;
            UpdateWatchState();
        }

        private async void BtnWatchScan_Click(object sender, RoutedEventArgs e)
        {
            BtnWatchScan.IsEnabled = false;
            try
            {
                await SaveAsync();          // 先保存，保证按界面上的设置来
                int n = await _shell.CheckWatchFolderNowAsync();
                if (n > 0)
                    _shell.Toast($"扫到 {n} 个压缩包，已按上面的方式处理完");
            }
            catch (Exception ex)
            {
                _shell.Toast("扫描失败：" + ex.Message);
            }
            finally
            {
                BtnWatchScan.IsEnabled = true;
            }
        }

        // ==================== 浏览器 ====================

        private string SelectedBrowserMode()
            => (CmbBrowser.SelectedItem as ComboBoxItem)?.Tag as string ?? Browser.Auto;

        /// <summary>按界面上的当前选择造一个临时配置，用来即时显示 / 试打开。</summary>
        private AppConfig BrowserProbe() => new()
        {
            BrowserMode = SelectedBrowserMode(),
            BrowserPath = (TxtBrowserPath.Text ?? "").Trim()
        };

        private void UpdateBrowserState()
        {
            if (TxtBrowserPath == null)
                return;   // 初始化期间 XAML 还没建完

            bool custom = SelectedBrowserMode() == Browser.Custom;
            TxtBrowserPath.IsEnabled = custom;
            BtnBrowserBrowse.IsEnabled = custom;

            var probe = BrowserProbe();
            TxtBrowserState.Text = "当前会使用：" + Browser.Describe(probe)
                + (probe.BrowserMode == Browser.Auto ? "（想强制指定就选 Edge 或 Chrome）" : "");
        }

        private void CmbBrowser_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBrowserState();

        private void BtnBrowserBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择浏览器程序",
                Filter = "浏览器程序 (*.exe)|*.exe|所有文件 (*.*)|*.*"
            };
            string cur = (TxtBrowserPath.Text ?? "").Trim();
            if (cur.Length > 0 && File.Exists(cur))
                dlg.InitialDirectory = Path.GetDirectoryName(cur);

            if (dlg.ShowDialog(Dlg.Owner(this)) != true)
                return;

            TxtBrowserPath.Text = dlg.FileName;
            UpdateBrowserState();
        }

        private void BtnBrowserTest_Click(object sender, RoutedEventArgs e)
        {
            var probe = BrowserProbe();
            try
            {
                Browser.OpenUrl("https://smapi.io", probe);
                _shell.Toast("已用「" + Browser.Describe(probe) + "」打开 SMAPI 官网");
            }
            catch (Exception ex)
            {
                _shell.Toast("打开失败：" + ex.Message);
            }
        }

        private async void BtnTestNexus_Click(object sender, RoutedEventArgs e)
        {
            BtnTestNexus.IsEnabled = false;
            TxtNexusState.Text = "正在验证…";
            try
            {
                var acc = await NexusSource.ValidateAsync(TxtNexusKey.Text?.Trim());
                TxtNexusState.Text = acc.IsPremium
                    ? $"✓ 验证成功：{acc.Name}（Premium 账号，可以接口直接下载 Mod）"
                    : $"✓ 验证成功：{acc.Name}（免费账号 —— 能浏览榜单、能检测更新，"
                      + "但接口不给下载直链，程序会引导你到网页下载 zip 后再拖进来安装）";
            }
            catch (Exception ex)
            {
                TxtNexusState.Text = "✗ " + ex.Message;
            }
            finally
            {
                BtnTestNexus.IsEnabled = true;
            }
        }

        /// <summary>把游戏内 ModManager 里已经填好的 Key / 更新源一次性搬过来（之后两边互不相干）。</summary>
        private async void BtnImportInGame_Click(object sender, RoutedEventArgs e)
        {
            string path = string.IsNullOrWhiteSpace(Env.ModsPath)
                ? null
                : Path.Combine(Env.ModsPath, "ModManager", "config.json");

            if (path == null || !File.Exists(path))
            {
                _shell.Toast("没找到游戏内 ModManager 的配置：" + (path ?? "（游戏目录未设置）"));
                return;
            }

            try
            {
                using var doc = JsonUtil.Parse(File.ReadAllText(path));
                var r = doc.RootElement;

                var moved = new List<string>();

                if (r.TryGetProperty("NexusApiKey", out var nk) && !string.IsNullOrWhiteSpace(nk.GetString()))
                {
                    TxtNexusKey.Text = nk.GetString();
                    moved.Add("Nexus API Key");
                }
                if (r.TryGetProperty("GitHubToken", out var gh) && !string.IsNullOrWhiteSpace(gh.GetString()))
                {
                    TxtGithubToken.Text = gh.GetString();
                    moved.Add("GitHub Token");
                }
                if (r.TryGetProperty("ExtraUpdateKeys", out var ex) && ex.ValueKind == JsonValueKind.Object)
                {
                    var lines = new List<string>();
                    foreach (var p in ex.EnumerateObject())
                    {
                        string v = p.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(v))
                            lines.Add(p.Name + " = " + v);
                    }
                    if (lines.Count > 0)
                    {
                        TxtExtraKeys.Text = string.Join(Environment.NewLine, lines);
                        moved.Add($"更新源补写 {lines.Count} 条");
                    }
                }
                if (r.TryGetProperty("IgnoredModIds", out var ig) && ig.ValueKind == JsonValueKind.Array)
                {
                    var ids = ig.EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                    if (ids.Count > 0)
                    {
                        TxtIgnored.Text = string.Join(Environment.NewLine, ids);
                        moved.Add($"忽略列表 {ids.Count} 条");
                    }
                }

                if (moved.Count == 0)
                {
                    _shell.Toast("游戏内的配置里没有可导入的内容（Key 是空的）");
                    return;
                }

                await SaveAsync();
                _shell.Toast("已导入并保存：" + string.Join("、", moved));
            }
            catch (Exception ex)
            {
                _shell.Toast("导入失败：" + ex.Message);
            }
        }

        // ==================== 保存 ====================

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            await SaveAsync();
            _shell.Toast("设置已保存");
        }

        private async Task SaveAsync()
        {
            var cfg = Env.Config;

            string newPath = TxtGamePath.Text?.Trim() ?? "";
            bool pathChanged = !string.Equals(newPath, Env.GamePath?.Trim(), StringComparison.OrdinalIgnoreCase);

            cfg.NexusApiKey = TxtNexusKey.Text?.Trim() ?? "";
            cfg.GitHubToken = TxtGithubToken.Text?.Trim() ?? "";
            cfg.BackupBeforeUpdate = ChkBackupUpdate.IsChecked == true;
            cfg.BackupBeforeDelete = ChkBackupDelete.IsChecked == true;
            cfg.IncludePrerelease = ChkPrerelease.IsChecked == true;
            cfg.CheckOnStart = ChkCheckOnStart.IsChecked == true;

            cfg.BackupKeep = int.TryParse(TxtKeep.Text?.Trim(), out int keep) ? Math.Max(1, Math.Min(50, keep)) : 3;
            TxtKeep.Text = cfg.BackupKeep.ToString();

            cfg.ExtraUpdateKeys = ParseKeyLines(TxtExtraKeys.Text);
            cfg.IgnoredModIds = ParseLines(TxtIgnored.Text);
            cfg.BrowserMode = SelectedBrowserMode();
            cfg.BrowserPath = (TxtBrowserPath.Text ?? "").Trim();
            cfg.WatchFolderEnabled = ChkWatch.IsChecked == true;
            cfg.WatchFolder = (TxtWatchFolder.Text ?? "").Trim();
            cfg.WatchInstallMode = (CmbWatchMode.SelectedItem as ComboBoxItem)?.Tag as string ?? "ask";

            Env.ApplyGamePath(newPath);
            Env.EnsureDirs();
            cfg.Save();

            UpdateGameState();
            _shell.RefreshHeader();
            _shell.RestartDownloadWatcher();   // 改了监控设置立即生效

            if (pathChanged)
            {
                await _shell.ReloadModsAsync();
                _shell.Toast("游戏目录已更新，已重新扫描 Mods");
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            if (Dlg.Show(this,
                    "恢复到默认设置？\n游戏目录、Mod 列表和备份文件不会动，只会清空 API Key、更新源补写和忽略列表等选项。",
                    "恢复默认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            Env.Config.Reset();
            Env.Config.Save();
            LoadFromConfig();
            _shell.Toast("已恢复默认设置");
        }

        private void BtnOpenData_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Env.DataDir);

        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(Env.LogFile))
                App.OpenFile(Env.LogFile);
            else
                _shell.Toast("还没有日志文件");
        }

        // ==================== 解析 ====================

        private static Dictionary<string, string> ParseKeyLines(string text)
        {
            var dict = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(text))
                return dict;

            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;

                int idx = line.IndexOf('=');
                if (idx < 0)
                    idx = line.IndexOf(':') > 0 && line.IndexOf("://") < 0 ? line.IndexOf(':') : -1;
                if (idx <= 0)
                    continue;

                string id = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim().Trim('"');
                if (id.Length == 0 || val.Length == 0)
                    continue;
                dict[id] = val;
            }
            return dict;
        }

        private static List<string> ParseLines(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
                return list;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//"))
                    continue;
                if (!list.Contains(line, StringComparer.OrdinalIgnoreCase))
                    list.Add(line);
            }
            return list;
        }
    }
}
