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
        private bool _quietSkip;      // 启动补扫时不弹「不是 Mod 包」的提示

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
            await CatchUpDownloadsAsync();
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

            var folders = Env.WatchFolders();
            if (folders.Count == 0)
            {
                Env.Log("下载文件夹监控：没找到可用的下载目录，已停用 → " + (cfg.WatchFolder ?? "(自动)"));
                return;
            }

            _watcher = new DownloadWatcher(folders, OnZipReady);
            _watcher.Start();
            Env.Log($"下载文件夹监控已启动：{string.Join("；", folders)}（模式：{cfg.WatchInstallMode}）");
        }

        /// <summary>
        /// 补扫「上次关掉程序之后」新下载的 zip。
        /// 以前只有开着程序时才会识别新下载的包，关着的时候下的包得手动点「立即检查」；
        /// 现在启动时拿上次运行时间比一下，自动补一遍（还是按「弹窗问我 / 自动安装」的设置来）。
        /// </summary>
        private async Task CatchUpDownloadsAsync()
        {
            DateTime? last = Env.Config.LastRunUtc;

            // 先记下这次运行的时间，下次启动才能知道哪些是「之后」新下的
            Env.Config.LastRunUtc = DateTime.UtcNow;
            Env.Config.Save();

            if (!Env.Config.WatchFolderEnabled)
                return;

            var folders = Env.WatchFolders();
            if (folders.Count == 0)
                return;

            // 第一次运行没有参考时间：只看最近 2 小时下载的，免得把整个下载文件夹的旧包全翻出来
            DateTime since = last ?? DateTime.UtcNow.AddHours(-2);

            var zips = await Task.Run(() => DownloadWatcher.FindNewZips(folders, since));
            if (zips.Count == 0)
                return;

            Env.Log($"补扫上次运行后新下载的压缩包：{zips.Count} 个");
            Toast($"发现上次关闭后新下载的 {zips.Count} 个压缩包，正在处理…");

            foreach (string z in zips)
            {
                if (!_zipQueue.Contains(z))
                    _zipQueue.Enqueue(z);
            }

            _quietSkip = true;      // 补扫可能扫到用户下载的非 Mod 文件，不逐个弹提示
            try { await DrainZipQueueAsync(); }
            finally { _quietSkip = false; }
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
            var folders = Env.WatchFolders();
            if (folders.Count == 0)
            {
                Toast("没有可用的下载目录，请到「设置」里指定一个：" + (Env.Config.WatchFolder ?? "(自动)"));
                return 0;
            }

            var found = await Task.Run(() => DownloadWatcher.ScanFolder(folders));
            var usable = found.Where(r => r.Ok || r.IsTranslation).ToList();   // Mod 包 + 翻译包都处理
            if (usable.Count == 0)
            {
                Toast(ExplainNoMods(folders, found));
                return 0;
            }

            foreach (var r in usable)
            {
                if (!_zipQueue.Contains(r.Path))
                    _zipQueue.Enqueue(r.Path);
            }

            await DrainZipQueueAsync();
            return usable.Count;
        }

        /// <summary>一个能识别都没扫到时，说清楚是「没包」还是「包有问题」。</summary>
        private static string ExplainNoMods(List<string> folders, List<ZipPeekResult> found)
        {
            string where = string.Join("、", folders);
            if (found.Count == 0)
                return $"「{where}」里一个压缩包都没有";

            var bad = found.Where(r => !r.IsTranslation
                                       && (r.HasManifest || (r.Error ?? "").Contains("翻译包"))).ToList();
            if (bad.Count > 0)
                return $"「{where}」里的压缩包都没识别出来：" + string.Join("；",
                    bad.Take(3).Select(r => Path.GetFileName(r.Path) + " → " + r.Error));

            return $"「{where}」里的 {found.Count} 个压缩包都不是 SMAPI Mod 包（没有 manifest.json），已跳过";
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

            // 走哪条路由 RouteZip 统一决定（翻译包没有 manifest、Ok 是 false，但它是能装的，
            // 必须抢在「识别失败」分支前面处理，否则就会被当成"不是 Mod"跳过）。
            switch (RouteZip(info))
            {
                case "translation":
                    await HandleTranslationAsync(path, info);
                    return;
                case "mod":
                    break;
                default:
                    {
                        // 没识别出来。有 manifest 却读不出来的（manifest 写坏了、带注释解不开等）必须告诉用户，
                        // 否则看起来就是「没反应」；压根不是 Mod 包的只安静跳过，免得启动补扫时弹一堆提示。
                        string reason = info?.Error ?? "读不出来";
                        Env.Log($"识别失败（{reason}）：{path}");
                        if (info != null && info.HasManifest)
                            Toast($"「{Path.GetFileName(path)}」识别失败：{reason}");
                        else if (!_quietSkip)
                            Toast($"「{Path.GetFileName(path)}」不是 SMAPI Mod 包，已跳过");
                        return;
                    }
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

        /// <summary>
        /// 一个压缩包该怎么处理：
        /// translation = 翻译包（没有 manifest，但能对上已装 Mod，合进它的 i18n）；
        /// mod = 能直接安装的 Mod 包；skip = 跳过（含"不是 Mod 包"与"读不出来"）。
        /// 翻译包必须单独判：Ok 只表示「能当 Mod 直接装」，而翻译包的 Ok 是 false
        /// （早期就是因为先判 Ok，翻译包被当成"不是 Mod"跳过了）。
        /// </summary>
        internal static string RouteZip(ZipPeekResult info)
        {
            if (info == null)
                return "skip";
            if (info.IsTranslation)
                return "translation";
            return info.Ok ? "mod" : "skip";
        }

        /// <summary>翻译包：问一句，然后合进对应 Mod 的 i18n（同名旧文件先备份）。</summary>
        private async Task HandleTranslationAsync(string path, ZipPeekResult info)
        {
            bool auto = string.Equals(Env.Config.WatchInstallMode, "auto", StringComparison.OrdinalIgnoreCase);
            if (!auto)
            {
                var r = Dlg.Show(this,
                    $"发现翻译包：{Path.GetFileName(path)}\n\n"
                    + $"会装给：「{info.TranslationTarget}」\n"
                    + "写入文件：" + string.Join("、", info.TranslationFiles.Take(6)) + "\n\n"
                    + @"（同名旧文件会先备份到 数据目录\i18n-backup，随时能找回）"
                    + "\n\n要现在安装吗？",
                    "下载文件夹监控", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes)
                {
                    Env.Log("用户跳过了翻译包：" + path);
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
                var before = NewModNamePrompt.Snapshot(Env);
                string msg = await ModInstaller.ImportAsync(Env, path, false, prog, default);
                Env.Log("下载文件夹监控安装成功：" + msg);
                await ReloadModsAsync();
                Toast(msg);
                RevealNewMods(before);                                // 让新装的在「Mod 管理」页里露出来
                await NewModNamePrompt.AskAsync(Env, before, this);   // 新装的 Mod 问一句中文名
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
            Env.Log($"重新扫描 Mods：{Mods.Count} 个（{disabledCount} 个已禁用）");
        }

        /// <summary>
        /// 新装的 Mod 在「Mod 管理」页里露出来（清搜索/筛选、选中并滚动过去）。
        /// 页面还没创建过就不用管——等切过去时列表本来就是最新的。
        /// </summary>
        private void RevealNewMods(HashSet<string> before)
        {
            if (before == null)
                return;

            var fresh = Mods.Where(m => !before.Contains(m.Id)).Select(m => m.Id).ToList();
            if (fresh.Count == 0)
                return;

            Env.Log($"本次新装 {fresh.Count} 个：{string.Join("、", fresh)}（列表现在共 {Mods.Count} 个）");

            RevealMods(fresh);
        }

        public void RevealMods(IEnumerable<string> ids)
        {
            var list = (ids ?? Enumerable.Empty<string>()).ToList();
            if (list.Count == 0)
                return;

            if (_pages.TryGetValue("mods", out var page) && page is ModsPage mp)
                mp.RevealMods(list);
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
