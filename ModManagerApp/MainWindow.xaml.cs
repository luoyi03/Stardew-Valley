using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SMM
{
    /// <summary>页面被切换到时回调。</summary>
    public interface IPage
    {
        void OnActivated();
    }

    public partial class MainWindow : Window, IShell
    {
        public AppEnv Env { get; private set; }
        public ObservableCollection<ModStatus> Mods { get; } = new();

        private readonly Dictionary<string, UserControl> _pages = new();
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private bool _started;

        private DownloadWatcher _watcher;
        private readonly Queue<string> _zipQueue = new();
        private bool _zipBusy;

        /// <summary>每 3 秒看一眼游戏在不在跑，用来更新顶栏提示。</summary>
        private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _gameWasRunning;

        public MainWindow()
        {
            Env = AppEnv.Create();
            InitializeComponent();

            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                ToastBox.Visibility = Visibility.Collapsed;
            };

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            TxtVersion.Text = "版本 " + (ver == null ? "1.0.0" : ver.ToString(3));

            UpdateHeader();
            UpdateGameRunningBadge();
            _gameTimer.Tick += (_, _) => UpdateGameRunningBadge();
            _gameTimer.Start();

            NavList.SelectedIndex = 0;
            Loaded += async (_, _) => await StartAsync();
        }

        private async Task StartAsync()
        {
            if (_started)
                return;
            _started = true;

            Env.EnsureDirs();

            if (!GameLocator.IsValid(Env.GamePath))
            {
                SetStatus("还没有找到星露谷目录，请在「设置」里指定游戏目录");
                Toast("没找到星露谷安装目录。请点右上角「更换游戏目录」，选择含 Stardew Valley.exe 的那一层。");
                OpenPage("settings");
                return;
            }

            await ReloadModsAsync();

            if (Env.SmapiVersion == null)
                Toast("这个目录里没检测到 SMAPI。Mod 需要先装 SMAPI 才能生效：https://smapi.io");

            if (Env.Config.CheckOnStart)
                OpenPage("mods");

            RestartDownloadWatcher();
        }

        protected override void OnClosed(EventArgs e)
        {
            _gameTimer.Stop();
            _watcher?.Dispose();
            _watcher = null;
            base.OnClosed(e);
        }

        // ==================== 下载文件夹监控 ====================

        public void RestartDownloadWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;

            var cfg = Env.Config;
            if (!cfg.WatchFolderEnabled)
            {
                Env.Log("下载文件夹监控：已关闭");
                return;
            }

            string folder = cfg.WatchFolder;
            if (string.IsNullOrWhiteSpace(folder))
                return;
            if (!Directory.Exists(folder))
            {
                Env.Log("下载文件夹监控：目录不存在，已停用 → " + folder);
                return;
            }

            _watcher = new DownloadWatcher(folder, OnZipReady);
            _watcher.Start();
            Env.Log($"下载文件夹监控已启动：{folder}（模式：{cfg.WatchInstallMode}）");
        }

        /// <summary>FileSystemWatcher 的回调在后台线程，切回界面线程再处理。</summary>
        private void OnZipReady(string path)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _zipQueue.Enqueue(path);
                _ = DrainZipQueueAsync();
            }));
        }

        public async Task<int> CheckWatchFolderNowAsync()
        {
            string folder = Env.Config.WatchFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Toast("监控目录不存在：" + (folder ?? "(未设置)"));
                return 0;
            }

            var found = await Task.Run(() => DownloadWatcher.ScanFolder(folder));
            if (found.Count == 0)
            {
                Toast($"「{folder}」里没找到能识别的 SMAPI Mod 压缩包");
                return 0;
            }

            foreach (var (path, _) in found)
            {
                if (!_zipQueue.Contains(path))
                    _zipQueue.Enqueue(path);
            }

            await DrainZipQueueAsync();
            return found.Count;
        }

        private async Task DrainZipQueueAsync()
        {
            if (_zipBusy)
                return;

            _zipBusy = true;
            try
            {
                while (_zipQueue.Count > 0)
                    await HandleZipAsync(_zipQueue.Dequeue());
            }
            finally
            {
                _zipBusy = false;
            }
        }

        private async Task HandleZipAsync(string path)
        {
            ZipPeekResult info;
            try
            {
                info = await Task.Run(() => ZipPeek.Read(path));
            }
            catch (Exception ex)
            {
                Env.Log("读取压缩包失败：" + path + " → " + ex.Message);
                return;
            }

            if (info == null)
            {
                // 不是 SMAPI Mod 包（用户下载的其他东西），安静跳过
                Env.Log("跳过（不是 SMAPI Mod 包）：" + path);
                Toast($"「{Path.GetFileName(path)}」不是 SMAPI Mod 包，已跳过");
                return;
            }

            var existing = Mods
                .Select(m => m.Mod)
                .FirstOrDefault(m => string.Equals(m.Id, info.Id, StringComparison.OrdinalIgnoreCase));

            // 已经装的就是这个版本，没必要再问一遍
            if (existing != null && !string.IsNullOrWhiteSpace(existing.Version)
                && string.Equals(existing.Version.Trim(), info.VersionText.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Env.Log($"跳过（已是该版本 {info.VersionText}）：{path}");
                Toast($"「{info.DisplayName}」你已经装了 {info.VersionText}，跳过");
                return;
            }

            bool auto = string.Equals(Env.Config.WatchInstallMode, "auto", StringComparison.OrdinalIgnoreCase);

            if (!auto)
            {
                string headline = existing == null
                    ? $"发现新 Mod：{info.DisplayName} {info.VersionText}"
                    : $"发现更新包：{info.DisplayName} {info.VersionText}";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(headline);
                if (existing != null)
                    sb.AppendLine($"你当前装的是 {existing.Version}");
                if (info.ModCount > 1)
                    sb.AppendLine($"（这个包里包含 {info.ModCount} 个 Mod，会一起安装）");
                sb.AppendLine();
                sb.AppendLine("文件：" + Path.GetFileName(path));
                sb.AppendLine("位置：" + path);
                if (AppEnv.IsGameRunning())
                    sb.AppendLine();
                if (AppEnv.IsGameRunning())
                    sb.AppendLine("⚠ 游戏正在运行，请先关闭游戏再安装。");
                sb.AppendLine();
                sb.Append("要现在安装吗？（安装前会自动备份旧版本，可以在「备份回滚」里还原）");

                var r = Dlg.Show(this, sb.ToString(), "下载文件夹监控",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes)
                {
                    Env.Log("用户跳过了：" + path);
                    return;
                }
            }

            await InstallZipAsync(path);
        }

        private async Task InstallZipAsync(string path)
        {
            if (AppEnv.IsGameRunning())
            {
                Toast("游戏正在运行，没法安装。关掉游戏后点「设置 → 立即检查下载文件夹」再装一次。");
                return;
            }

            SetBusy("正在安装 " + Path.GetFileName(path) + "…");
            SetStatus("正在安装 " + Path.GetFileName(path) + "…");
            try
            {
                var prog = new Progress<InstallProgress>(p =>
                {
                    SetStatus(p.Phase);
                    SetProgress(p.Percent);
                });
                string msg = await ModInstaller.ImportAsync(Env, path, false, prog, default);
                Env.Log("下载文件夹监控安装成功：" + msg);
                await ReloadModsAsync();
                Toast(msg);
            }
            catch (Exception ex)
            {
                Env.Log("下载文件夹监控安装失败：" + ex);
                Toast("安装失败：" + ex.Message);
            }
            finally
            {
                SetBusy(null);
                SetProgress(0);
                SetStatus($"{Mods.Count} 个 Mod");
            }
        }

        // ==================== IShell ====================

        public async Task ReloadModsAsync()
        {
            SetStatus("正在扫描 Mods 目录…");

            string modsPath = Env.ModsPath;
            string disabled = Env.DisabledDir;
            var scanned = await Task.Run(() => ModScanner.Scan(modsPath, disabled));

            var previous = Mods.ToDictionary(m => m.Mod.Dir ?? "", m => m, StringComparer.OrdinalIgnoreCase);
            Mods.Clear();
            foreach (var m in scanned)
            {
                var status = new ModStatus { Mod = m };
                if (previous.TryGetValue(m.Dir ?? "", out var old)
                    && old.Status != "unknown" && old.Status != "checking" && old.Mod.Version == m.Version)
                {
                    status.Status = old.Status;
                    status.LatestVersion = old.LatestVersion;
                    status.SourceName = old.SourceName;
                    status.PageUrl = old.PageUrl;
                    status.Message = old.Message;
                    status.Plan = old.Plan;
                    status.ManualDownload = old.ManualDownload;
                }
                else if (m.Disabled)
                {
                    status.Status = "disabled";
                }
                Mods.Add(status);
            }

            int updates = Mods.Count(m => m.HasUpdate);
            int disabledCount = Mods.Count(m => m.Mod.Disabled);
            SetStatus($"{Mods.Count} 个 Mod（{disabledCount} 个已禁用）"
                      + (updates > 0 ? $" · {updates} 个可更新" : ""));
        }

        public void SetStatus(string text)
        {
            TxtStatus.Text = text ?? "";
        }

        public void SetBusy(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                TxtBusy.Visibility = Visibility.Collapsed;
                return;
            }
            TxtBusy.Text = text;
            TxtBusy.Visibility = Visibility.Visible;
        }

        public void SetProgress(int percent)
        {
            if (percent <= 0)
            {
                BarStatus.Visibility = Visibility.Collapsed;
                BarStatus.Value = 0;
                return;
            }
            BarStatus.Visibility = Visibility.Visible;
            BarStatus.Value = Math.Max(0, Math.Min(100, percent));
        }

        public void OpenPage(string key)
        {
            for (int i = 0; i < NavList.Items.Count; i++)
            {
                if (NavList.Items[i] is ListBoxItem item && (item.Tag as string) == key)
                {
                    NavList.SelectedIndex = i;
                    return;
                }
            }
        }

        public void Toast(string message)
        {
            TxtToast.Text = message ?? "";
            ToastBox.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ==================== 界面事件 ====================

        private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(NavList.SelectedItem is ListBoxItem item) || !(item.Tag is string key))
                return;

            if (!_pages.TryGetValue(key, out var page))
            {
                page = key switch
                {
                    "mods" => new ModsPage(this),
                    "browse" => new BrowsePage(this),
                    "backup" => new BackupPage(this),
                    "issues" => new IssuesPage(this),
                    "mp" => new MultiplayerPage(this),
                    _ => new SettingsPage(this)
                };
                _pages[key] = page;
            }

            PageHost.Content = page;
            (page as IPage)?.OnActivated();
        }

        private async void BtnChangeGame_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择星露谷安装目录（含 Stardew Valley.exe 的那一层）",
                Multiselect = false
            };
            if (Directory.Exists(Env.GamePath))
                dlg.InitialDirectory = Env.GamePath;

            if (dlg.ShowDialog(Dlg.Owner(this)) != true)
                return;

            string path = dlg.FolderName;
            if (!GameLocator.IsValid(path))
            {
                var r = Dlg.Show(this,
                    "这个目录里既没有 Stardew Valley.exe，也没有 Mods 文件夹，确定要用它吗？\n\n" + path,
                    "确认目录", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes)
                    return;
            }

            Env.ApplyGamePath(path);
            Env.EnsureDirs();
            UpdateHeader();
            await ReloadModsAsync();
            Toast("游戏目录已切换到：" + path);
        }

        private void BtnOpenData_Click(object sender, RoutedEventArgs e)
        {
            App.OpenFolder(Env.DataDir);
        }

        // ==================== 打开游戏 ====================

        private void BtnPlay_Click(object sender, RoutedEventArgs e)
        {
            if (AppEnv.IsGameRunning())
            {
                Toast("游戏已经在运行了。想装 / 更新 Mod 的话，先关掉游戏再操作。");
                UpdateGameRunningBadge();
                return;
            }

            try
            {
                string exe = GameLocator.Launch(Env);
                bool viaSmapi = exe.IndexOf("ModdingAPI", StringComparison.OrdinalIgnoreCase) >= 0;
                Toast(viaSmapi
                    ? "正在启动游戏（走 SMAPI，Mod 会正常加载）…"
                    : "⚠ 游戏目录里没找到 SMAPI，这次会以原版启动，Mod 不会加载");
                UpdateGameRunningBadge();
            }
            catch (Exception ex)
            {
                Env.Log("启动游戏失败：" + ex);
                Toast("启动失败：" + ex.Message);
            }
        }

        /// <summary>顶栏那个「游戏运行中」小标 + 打开游戏按钮的可用状态。</summary>
        private void UpdateGameRunningBadge()
        {
            bool running;
            try { running = AppEnv.IsGameRunning(); }
            catch { running = false; }

            BadgeRunning.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            BtnPlay.IsEnabled = !running;

            if (_gameWasRunning && !running)
                Env.Log("检测到游戏已退出");
            _gameWasRunning = running;
        }

        public void RefreshHeader() => UpdateHeader();

        /// <summary>当前显示的页面（--shot 截图模式要用）。</summary>
        public UserControl CurrentPage => PageHost.Content as UserControl;

        public void UpdateHeader()
        {
            TxtGamePath.Text = string.IsNullOrEmpty(Env.GamePath) ? "（未设置游戏目录）" : Env.GamePath;
            TxtSmapi.Text = Env.SmapiVersion == null ? "未检测到 SMAPI" : "SMAPI " + Env.SmapiVersion;
        }
    }
}
