using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SMM
{
    public partial class MultiplayerPage : UserControl, IPage
    {
        private readonly IShell _shell;
        private readonly ObservableCollection<DiffEntry> _items = new();
        private readonly DispatcherTimer _timer;
        private bool _busy;
        private bool _checkingFirewall;
        private List<string> _missingExes = new();

        private AppEnv Env => _shell.Env;

        public MultiplayerPage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();
            List.ItemsSource = _items;

            // 开房状态每 3 秒自动看一次：你在游戏里开好房，切回来就已经是「房间已开」
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background,
                (_, _) => Tick(), Dispatcher);
            _timer.Start();

            UpdateEnv();
            UpdateEmptyState();
        }

        public void OnActivated() => UpdateEnv();

        /// <summary>定时刷新开房状态（没在看这个页面就跳过，不做无用功）。</summary>
        private void Tick()
        {
            if (!IsVisible || _busy)
                return;
            ApplyRoom(NetInfo.Room());
        }

        private Brush Res(string key) => (Brush)FindResource(key);

        // ==================== 环境自检 ====================

        private void UpdateEnv()
        {
            string game = GameLocator.GameVersion(Env.GamePath) ?? "未检测到";
            TxtEnv.Text = $"游戏版本 {game}　·　SMAPI {Env.SmapiVersion ?? "未检测到"}";

            ApplyRoom(NetInfo.Room());

            var ips = NetInfo.LocalIPv4();
            if (ips.Count == 0)
            {
                TxtIpBig.Text = "没找到局域网 IP";
                TxtIpBig.FontSize = 20;
                TxtIpBig.Foreground = Res("B.Danger");
                TxtIpPort.Text = "";
                TxtIpOther.Text = "先确认电脑连着路由器（网线、WiFi 都行），然后点「重新检测」";
            }
            else
            {
                TxtIpBig.Text = ips[0].Address;
                TxtIpBig.FontSize = 30;
                TxtIpBig.Foreground = Res("B.Ok");
                TxtIpPort.Text = $":{NetInfo.StardewPort}　·　{ips[0].Adapter}";

                var others = ips.Skip(1).ToList();
                TxtIpOther.Text = others.Count == 0
                    ? "（只有这一个可用地址）"
                    : "其他地址（不在同一个网络时用不上）："
                      + string.Join("、", others.Select(x => x.Address + (x.Virtual ? "(虚拟网卡)" : "")));
            }
            BtnCopyIp.IsEnabled = BtnInvite.IsEnabled = ips.Count > 0;

            _ = RefreshFirewallAsync();
        }

        private void ApplyRoom(NetInfo.RoomStatus room)
        {
            DotRoom.Foreground = Res(room.Level == "ok" ? "B.Ok"
                : room.Level == "warn" ? "B.Warn" : "B.Info");
            TxtRoom.Text = room.Text;
            TxtRoomHint.Text = room.Hint;
        }

        private async Task RefreshFirewallAsync()
        {
            if (_checkingFirewall)
                return;
            _checkingFirewall = true;
            try
            {
                DotFirewall.Foreground = Res("B.TextDim");
                TxtFirewall.Text = "正在检查防火墙…";
                TxtFirewallHint.Text = "";

                var info = await NetInfo.CheckFirewallAsync(Env.GamePath);

                DotFirewall.Foreground = Res(info.Level == "ok" ? "B.Ok"
                    : info.Level == "warn" ? "B.Warn" : "B.Info");
                TxtFirewall.Text = info.Text;
                TxtFirewallHint.Text = info.Hint;
                BtnFirewall.Visibility = info.NeedsFix ? Visibility.Visible : Visibility.Collapsed;
                _missingExes = info.Missing;
            }
            catch (Exception ex)
            {
                Env.Log("防火墙检查失败：" + ex);
                DotFirewall.Foreground = Res("B.Warn");
                TxtFirewall.Text = "防火墙检查失败：" + ex.Message;
            }
            finally
            {
                _checkingFirewall = false;
            }
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            UpdateEnv();
            _shell.Toast("已重新检测联机环境");
        }

        private void BtnInvite_Click(object sender, RoutedEventArgs e)
        {
            string text = NetInfo.InviteText();
            if (string.IsNullOrWhiteSpace(text))
            {
                _shell.Toast("还没拿到局域网 IP，先点「重新检测」");
                return;
            }
            try
            {
                Clipboard.SetText(text);
                _shell.Toast("已复制邀请信息，直接粘给好友就行");
            }
            catch (Exception ex)
            {
                _shell.Toast("复制失败：" + ex.Message);
            }
        }

        private void BtnCopyIp_Click(object sender, RoutedEventArgs e)
        {
            var ips = NetInfo.LocalIPv4();
            if (ips.Count == 0)
                return;
            try
            {
                Clipboard.SetText(ips[0].Address);
                _shell.Toast("已复制 IP：" + ips[0].Address + "（" + ips[0].Adapter + "）");
            }
            catch (Exception ex)
            {
                _shell.Toast("复制失败：" + ex.Message);
            }
        }

        private async void BtnFirewall_Click(object sender, RoutedEventArgs e)
        {
            if (_missingExes == null || _missingExes.Count == 0)
                return;

            BtnFirewall.IsEnabled = false;
            try
            {
                // UAC 弹窗期间不能卡住界面线程，所以丢到后台跑
                await Task.Run(() => NetInfo.FixFirewall(_missingExes));
                _shell.Toast("已放行：" + string.Join("、", _missingExes.Select(Path.GetFileName))
                             + "\n好友现在就能连进来了");
            }
            catch (Exception ex)
            {
                _shell.Toast("没能自动放行（" + ex.Message + "）。手动来：设置 → 网络和 Internet → "
                             + "Windows 防火墙 → 允许应用通过防火墙 → 勾上 StardewModdingAPI.exe");
            }
            finally
            {
                BtnFirewall.IsEnabled = true;
            }
            await RefreshFirewallAsync();
        }

        private void BtnPlay2_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AppEnv.IsGameRunning())
                {
                    _shell.Toast("游戏已经在运行了，进去点「一起玩」就行");
                    return;
                }
                GameLocator.Launch(Env);
                _shell.Toast("正在启动游戏，进去后点「一起玩」→ 主持 / 加入");
            }
            catch (Exception ex)
            {
                _shell.Toast("启动失败：" + ex.Message);
            }
        }

        // ==================== 导出 ====================

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var manifest = ModManifest.Build(Env, _shell.Mods.Select(m => m.Mod));

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "保存联机 Mod 清单",
                    FileName = $"联机Mod清单_{DateTime.Now:yyyyMMdd}.json",
                    Filter = "清单文件 (*.json)|*.json|文本清单 (*.txt)|*.txt",
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                };
                if (dlg.ShowDialog(Dlg.Owner(this)) != true)
                    return;

                bool asText = dlg.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(dlg.FileName, asText ? manifest.ToText() : manifest.ToJson(),
                    new UTF8Encoding(false));

                Env.Log($"已导出联机 Mod 清单（{manifest.Mods.Count} 个）：{dlg.FileName}");
                _shell.Toast($"已导出 {manifest.Mods.Count} 个 Mod 的清单：{Path.GetFileName(dlg.FileName)}\n"
                             + "把这个文件发给好友，他导入后就知道该补什么了");
            }
            catch (Exception ex)
            {
                _shell.Toast("导出失败：" + ex.Message);
            }
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var manifest = ModManifest.Build(Env, _shell.Mods.Select(m => m.Mod));
                Clipboard.SetText(manifest.ToText());
                _shell.Toast($"已复制 {manifest.Mods.Count} 个 Mod 的清单文本，直接粘给好友就行");
            }
            catch (Exception ex)
            {
                _shell.Toast("复制失败：" + ex.Message);
            }
        }

        // ==================== 导入比对 ====================

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择好友发来的清单",
                Filter = "清单文件 (*.json;*.txt)|*.json;*.txt|所有文件 (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            if (dlg.ShowDialog(Dlg.Owner(this)) != true)
                return;

            string text;
            try
            {
                text = File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                _shell.Toast("读取文件失败：" + ex.Message);
                return;
            }
            Compare(text, Path.GetFileName(dlg.FileName));
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            string text;
            try
            {
                text = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                _shell.Toast("读剪贴板失败：" + ex.Message);
                return;
            }
            Compare(text, "剪贴板里的清单");
        }

        private async void Compare(string text, string from)
        {
            var manifest = ModManifest.Parse(text, out string error);
            if (manifest == null)
            {
                _shell.Toast("这看起来不是一份 Mod 清单：" + error);
                return;
            }

            await RunBusy("正在比对…", async () =>
            {
                var mine = await Task.Run(() => ModScanner.Scan(Env.ModsPath, Env.DisabledDir));
                var diff = await Task.Run(() => ManifestDiff.Compare(Env, manifest, mine));

                _items.Clear();
                foreach (var e in diff.Entries)
                    _items.Add(e);
                UpdateEmptyState();

                var head = new StringBuilder();
                head.Append($"{from}：好友有 {manifest.Mods.Count} 个 Mod");
                if (!string.IsNullOrWhiteSpace(manifest.GameVersion) || !string.IsNullOrWhiteSpace(manifest.SmapiVersion))
                    head.Append($"（游戏 {manifest.GameVersion ?? "?"} / SMAPI {manifest.SmapiVersion ?? "?"}）");
                head.Append($"。你这边一致 {diff.SameCount} 个");
                if (diff.MissingCount > 0)
                    head.Append($"，**缺 {diff.MissingCount} 个**".Replace("**", ""));
                if (diff.VersionCount > 0)
                    head.Append($"，{diff.VersionCount} 个版本不一样");
                if (diff.ExtraCount > 0)
                    head.Append($"，你多装了 {diff.ExtraCount} 个");

                TxtDiff.Text = head + "\n" + string.Join("\n", diff.Warnings.Select(w => "· " + w));
                _shell.SetStatus(head.ToString());
                Env.Log("联机清单比对：" + head);

                if (diff.Entries.Count == 0)
                    _shell.Toast("两边 Mod 完全一致，可以直接联机了");
            });
        }

        private void UpdateEmptyState()
        {
            TxtEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ==================== 行内操作 ====================

        private async void BtnRowInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            if (!((sender as FrameworkElement)?.DataContext is DiffEntry entry))
                return;

            var key = entry.Source == null ? null : (UpdateKey.Parse(entry.Source) ?? UpdateKey.FromInput(entry.Source));
            if (key == null || key.Site != "GitHub")
            {
                _shell.Toast("这个来源没法自动装，点「打开网页」手动下载 zip");
                return;
            }

            await RunBusy("正在安装 " + entry.Name + "…", async () =>
            {
                var item = new BrowseItem
                {
                    Source = "GitHub",
                    Id = key.Id,
                    Name = entry.Name,
                    PageUrl = entry.PageUrl
                };
                var prog = new Progress<InstallProgress>(p =>
                {
                    _shell.SetStatus(p.Phase);
                    _shell.SetProgress(p.Percent);
                });
                string msg = await ModInstaller.InstallBrowseAsync(Env, item, prog, CancellationToken.None);
                await _shell.ReloadModsAsync();
                _shell.Toast(msg + "\n装完再点一次「导入好友的清单比对」看看还差什么");
            });
        }

        private void BtnRowPage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DiffEntry entry
                && !string.IsNullOrEmpty(entry.PageUrl))
                App.OpenUrl(entry.PageUrl);
        }

        // ==================== 忙碌包装 ====================

        private async Task RunBusy(string label, Func<Task> action)
        {
            if (_busy)
                return;
            _busy = true;
            _shell.SetBusy(label);
            BtnExport.IsEnabled = BtnCopy.IsEnabled = BtnImport.IsEnabled = BtnPaste.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Env.Log("联机页操作失败：" + ex);
                _shell.Toast("操作失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
                _shell.SetProgress(0);
                BtnExport.IsEnabled = BtnCopy.IsEnabled = BtnImport.IsEnabled = BtnPaste.IsEnabled = true;
            }
        }
    }
}
