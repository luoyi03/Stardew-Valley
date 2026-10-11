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
    public partial class BrowsePage : UserControl, IPage
    {
        private readonly IShell _shell;
        private readonly ObservableCollection<BrowseItem> _items = new();
        private bool _busy;

        private AppEnv Env => _shell.Env;

        public BrowsePage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();
            List.ItemsSource = _items;
            UpdateHint();
        }

        public void OnActivated() => UpdateHint();

        /// <summary>说明文字跟着来源变——重点是讲清楚「Nexus 搜不了关键词」这件事。</summary>
        private void UpdateHint()
        {
            if (TxtHint == null)
                return;

            string kind = (CmbSource.SelectedItem as ComboBoxItem)?.Tag as string ?? "latest_added";
            var parts = new List<string>();

            switch (kind)
            {
                case "github":
                    parts.Add("GitHub 可以按关键词搜仓库名：填 automate / fishing / NPC 这类词再点「检索」。");
                    break;

                case "web":
                    parts.Add("「检索」会用浏览器打开 Nexus 的搜索结果页 —— 这是按名字搜 Nexus 的唯一办法，"
                              + "官方接口不提供搜索功能。");
                    break;

                default:
                    parts.Add("Nexus 官方接口不支持按关键词搜索，「检索」只能拉榜单（每榜 10 条）："
                              + "不填关键词就列榜单；填关键词会在三个榜单约 30 条里过滤；"
                              + "填纯数字（如 27517）则精确查那一个 Mod。"
                              + "想按名字搜，把来源改成「Nexus · 去网页搜索」，或点右上角「在网页上搜索 Mod」。");
                    break;
            }

            if (string.IsNullOrWhiteSpace(Env.Config.NexusApiKey))
            {
                parts.Add("还没填 Nexus API Key，Nexus 相关功能用不了（到「设置」里填个免费 Key，GitHub 不需要 Key）。");
                // 没 Key 时 Nexus 榜单读不出来，自动切到 GitHub，免得一进来就是空列表
                if (kind != "github" && kind != "web" && CmbSource.SelectedIndex != 3)
                    CmbSource.SelectedIndex = 3;
            }
            else
            {
                parts.Add("已经在装的 Mod 不用搜：到「Mod 管理」页选中它点「打开网页」就能直达它的 Nexus 页面。");
            }

            parts.Add("免费 Nexus 账号不能走接口直接下载（需要 Premium），但检测更新和浏览都正常；"
                      + "下载不了时点「网页」手动下 zip，zip 落进下载文件夹会被自动安装。");

            TxtHint.Text = string.Join("　", parts);
        }

        private void CmbSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded)
                UpdateHint();
        }

        // ==================== 检索 ====================

        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            string kind = (CmbSource.SelectedItem as ComboBoxItem)?.Tag as string ?? "github";
            await SearchAsync(kind, TxtKeyword.Text?.Trim() ?? "");
        }

        /// <summary>检索指定榜单 / 关键词。kind 取 Nexus 的榜单名（latest_added 等）或 "github"。</summary>
        public async Task SearchAsync(string kind, string keyword)
        {
            if (_busy)
                return;

            string kw = (keyword ?? "").Trim();

            // 「去网页搜索」：没有接口可用，直接用浏览器打开 Nexus 搜索页
            if (kind == "web")
            {
                if (kw.Length == 0)
                {
                    _shell.Toast("先在左边的框里填要搜的名字，再点「检索」");
                    return;
                }
                App.OpenUrl("https://www.nexusmods.com/stardewvalley/search/?gsearch=" + Uri.EscapeDataString(kw));
                _shell.SetStatus("已在浏览器里打开 Nexus 搜索：" + kw);
                _shell.Toast("已经在浏览器里打开 Nexus 搜索结果了");
                return;
            }

            await RunBusyAsync("正在检索…", async () =>
            {
                List<BrowseItem> list;

                if (kind == "github")
                {
                    if (kw.Length == 0)
                    {
                        _shell.Toast("GitHub 搜索请输入关键词，例如 automate、fishing、NPC");
                        list = new List<BrowseItem>();
                    }
                    else
                    {
                        list = await GitHubSource.SearchAsync(kw, Env.Config.GitHubToken);
                    }
                }
                else if (kw.Length > 0 && kw.All(char.IsDigit))
                {
                    // 纯数字当成 Nexus 编号，精确查这一个 Mod
                    _shell.SetStatus($"正在查 Nexus 编号 {kw}…");
                    var one = await NexusSource.ModInfoAsync(kw, Env.Config.NexusApiKey);
                    list = one == null ? new List<BrowseItem>() : new List<BrowseItem> { one };
                }
                else if (kw.Length > 0)
                {
                    // Nexus 没有搜索接口：关键词只能把三个榜单（约 30 条）合起来过滤
                    _shell.SetStatus("Nexus 接口不支持搜索，正在拉三个榜单再过滤…");
                    var all = new List<BrowseItem>();
                    foreach (string chart in new[] { "latest_added", "latest_updated", "trending" })
                    {
                        try { all.AddRange(await NexusSource.ListAsync(chart, Env.Config.NexusApiKey)); }
                        catch { }
                    }

                    string low = kw.ToLowerInvariant();
                    list = all
                        .GroupBy(x => x.Id)
                        .Select(g => g.First())
                        .Where(x => ((x.Name ?? "") + " " + (x.Summary ?? "") + " " + (x.Author ?? ""))
                                    .ToLowerInvariant().Contains(low))
                        .ToList();
                }
                else
                {
                    list = await NexusSource.ListAsync(kind, Env.Config.NexusApiKey);
                }

                MarkInstalled(list);

                _items.Clear();
                foreach (var it in list)
                    _items.Add(it);

                _shell.SetStatus($"检索到 {list.Count} 条结果");
                if (list.Count == 0)
                {
                    _shell.Toast(kind == "github"
                        ? $"没搜到名字带「{kw}」的仓库，换个关键词试试。"
                        : $"榜单里没有匹配「{kw}」的 Mod。\n"
                          + "Nexus 官方接口不支持按关键词搜索，只能：\n"
                          + "· 把来源改成「Nexus · 去网页搜索」按名字搜\n"
                          + "· 或者填 Mod 编号（纯数字，例如 27517）精确查");
                }
            });
        }

        private void MarkInstalled(List<BrowseItem> list)
        {
            var mods = _shell.Mods.Select(m => m.Mod).ToList();
            foreach (var it in list)
            {
                LocalMod m = it.Source == "GitHub"
                    ? mods.FirstOrDefault(x => x.Keys.Any(k => k.Site == "GitHub"
                        && string.Equals(k.Id, it.Id, StringComparison.OrdinalIgnoreCase)))
                    : mods.FirstOrDefault(x => x.Keys.Any(k => k.Site == "Nexus" && k.Id == it.Id));
                it.InstalledVersion = m?.Version;
            }
        }

        private void BtnOpenNexus_Click(object sender, RoutedEventArgs e)
        {
            string kw = TxtKeyword.Text?.Trim();
            string url = string.IsNullOrWhiteSpace(kw)
                ? "https://www.nexusmods.com/stardewvalley/mods/categories"
                : "https://www.nexusmods.com/stardewvalley/search/?gsearch=" + Uri.EscapeDataString(kw);
            App.OpenUrl(url);
        }

        // ==================== 行内操作 ====================

        private async void BtnRowInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            if (!((sender as FrameworkElement)?.DataContext is BrowseItem item))
                return;
            await InstallItemAsync(item);
        }

        private void BtnRowPage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BrowseItem item)
                App.OpenUrl(item.PageUrl);
        }

        private async Task InstallItemAsync(BrowseItem item)
        {
            if (_busy)
                return;

            if (item.Source == "Nexus" && string.IsNullOrWhiteSpace(Env.Config.NexusApiKey))
            {
                _shell.Toast("Nexus 下载需要先在「设置」里填写 API Key");
                _shell.OpenPage("settings");
                return;
            }

            _busy = true;
            _shell.SetBusy("正在安装 " + item.Name + "…");
            try
            {
                var prog = new Progress<InstallProgress>(p =>
                {
                    _shell.SetStatus(p.Phase);
                    _shell.SetProgress(p.Percent);
                });
                string msg = await ModInstaller.InstallBrowseAsync(Env, item, prog, CancellationToken.None);
                await _shell.ReloadModsAsync();
                MarkInstalled(_items.ToList());
                _shell.Toast(msg);
            }
            catch (Exception ex)
            {
                HandleInstallError(ex, item);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
                _shell.SetProgress(0);
            }
        }

        private void HandleInstallError(Exception ex, BrowseItem item)
        {
            string msg = ex.Message ?? "";
            bool manual = msg.Contains("Premium") || msg.Contains("手动下载") || msg.Contains("无法直接下载");

            if (manual && item != null)
            {
                var r = Dlg.Show(this,
                    msg + "\n\n要现在打开 Nexus 下载页面吗？下载好的 zip 直接拖进「Mod 管理」页就能自动安装。",
                    "需要手动下载", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (r == MessageBoxResult.Yes)
                {
                    string url = item.Source == "Nexus" ? NexusSource.FilesUrl(item.Id) : item.PageUrl;
                    App.OpenUrl(url);
                }
                return;
            }

            _shell.Toast("安装失败：" + msg);
        }

        // ==================== 链接 / 编号安装 ====================

        private async void BtnLinkInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            string input = TxtLink.Text?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(input))
                return;

            // 1) 本地 zip / 文件夹
            if (File.Exists(input) || Directory.Exists(input))
            {
                await RunBusyAsync("正在安装…", async () =>
                {
                    bool isFolder = Directory.Exists(input);
                    var prog = new Progress<InstallProgress>(p =>
                    {
                        _shell.SetStatus(p.Phase);
                        _shell.SetProgress(p.Percent);
                    });
                    string msg = await ModInstaller.ImportAsync(Env, input, isFolder, prog, CancellationToken.None);
                    await _shell.ReloadModsAsync();
                    TxtLink.Text = "";
                    _shell.Toast(msg);
                });
                return;
            }

            // 2) Nexus / GitHub 链接或编号
            var key = UpdateKey.FromInput(input);
            if (key != null)
            {
                var item = new BrowseItem
                {
                    Source = key.Site,
                    Id = key.Id,
                    Name = key.Site + " " + key.Id,
                    PageUrl = key.PageUrl
                };

                // 尽量取到真实名字
                if (key.Site == "Nexus" && !string.IsNullOrWhiteSpace(Env.Config.NexusApiKey))
                {
                    try
                    {
                        var info = await NexusSource.ModInfoAsync(key.Id, Env.Config.NexusApiKey);
                        if (info != null)
                            item = info;
                    }
                    catch { }
                }

                await InstallItemAsync(item);
                return;
            }

            // 3) 任意 zip 直链
            if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                await RunBusyAsync("正在下载安装…", async () =>
                {
                    var prog = new Progress<InstallProgress>(p =>
                    {
                        _shell.SetStatus(p.Phase);
                        _shell.SetProgress(p.Percent);
                    });
                    string msg = await ModInstaller.InstallFromUrlAsync(Env, input, prog, CancellationToken.None);
                    await _shell.ReloadModsAsync();
                    TxtLink.Text = "";
                    _shell.Toast(msg);
                });
                return;
            }

            _shell.Toast("没看懂这个输入。可以填：Nexus 网页链接或数字编号、GitHub 仓库链接、zip 直链、本地 zip 路径。");
        }

        // ==================== 忙碌包装 ====================

        private async Task RunBusyAsync(string label, Func<Task> action)
        {
            if (_busy)
                return;
            _busy = true;
            _shell.SetBusy(label);
            BtnSearch.IsEnabled = BtnLinkInstall.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Env.Log("浏览页操作失败：" + ex);
                _shell.Toast("操作失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
                _shell.SetProgress(0);
                BtnSearch.IsEnabled = BtnLinkInstall.IsEnabled = true;
            }
        }
    }
}
