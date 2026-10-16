using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace SMM
{
    public partial class ModsPage : UserControl, IPage
    {
        private readonly IShell _shell;
        private readonly ICollectionView _view;
        private readonly ListCollectionView _listView;
        private bool _busy;
        private string _sortKey = "Name";
        private bool _sortDesc;
        private CancellationTokenSource _cts;

        private AppEnv Env => _shell.Env;

        public ModsPage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();

            List.ItemsSource = shell.Mods;
            _view = CollectionViewSource.GetDefaultView(shell.Mods);
            _listView = _view as ListCollectionView;
            _view.Filter = FilterRow;
            ApplySort();

            shell.Mods.CollectionChanged += (_, _) => UpdateSummary();
            UpdateSummary();
            UpdateSelectionPanel();
        }

        public void OnActivated()
        {
            _view?.Refresh();
            UpdateSummary();
            UpdateSelectionPanel();
        }

        /// <summary>
        /// 装完 Mod 之后把它「露出来」：清掉搜索框、筛选切回「全部」、刷新视图，
        /// 再选中并滚动到它。否则用户搜索框里还留着字、或者筛选停在「可更新」这类条件上，
        /// 新装的 Mod（状态是"未检查"）就被挡在列表外，看起来像没装上。
        /// </summary>
        public void RevealMods(IEnumerable<string> ids)
        {
            var wanted = new HashSet<string>(ids ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
                return;

            bool cleared = false;
            if (!string.IsNullOrEmpty(TxtSearch.Text))
            {
                TxtSearch.Text = "";         // 会触发 TxtSearch_TextChanged → _view.Refresh()
                cleared = true;
            }
            if (CmbFilter.SelectedIndex != 0)
            {
                CmbFilter.SelectedIndex = 0;
                cleared = true;
            }
            _view.Refresh();

            var targets = _shell.Mods.Where(m => wanted.Contains(m.Id)).ToList();
            if (targets.Count == 0)
                return;

            List.SelectedItems.Clear();
            foreach (var t in targets)
                List.SelectedItems.Add(t);

            try { List.ScrollIntoView(targets[0]); } catch { }

            if (cleared)
                Env.Log("新装的 Mod 之前被搜索/筛选挡着，已切回「全部」并选中："
                        + string.Join("、", targets.Select(t => t.DisplayName)));
        }

        // ==================== 筛选 / 排序 ====================

        private bool FilterRow(object o)
        {
            if (!(o is ModStatus s))
                return false;

            string q = TxtSearch.Text?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                string hay = (s.DisplayName + " " + s.Name + " " + s.Id + " " + s.AuthorText).ToLowerInvariant();
                if (!hay.Contains(q.ToLowerInvariant()))
                    return false;
            }

            return CmbFilter.SelectedIndex switch
            {
                1 => s.HasUpdate,
                2 => s.Status == "latest",
                3 => s.Status == "manual" || s.Status == "error",
                4 => s.Mod.Disabled,
                5 => s.Mod.IsDuplicate,
                _ => true
            };
        }

        private void ApplySort()
        {
            if (_listView == null)
                return;
            var comparer = new ModComparer(_sortKey, _sortDesc);
            _listView.CustomSort = comparer;
        }

        private sealed class ModComparer : IComparer
        {
            private readonly string _key;
            private readonly bool _desc;

            public ModComparer(string key, bool desc)
            {
                _key = key;
                _desc = desc;
            }

            public int Compare(object x, object y)
            {
                var a = x as ModStatus;
                var b = y as ModStatus;
                if (a == null || b == null)
                    return 0;

                int c = _key switch
                {
                    "VersionText" => SemVer.TryParse(a.VersionText, out var av) && SemVer.TryParse(b.VersionText, out var bv)
                        ? av.CompareTo(bv) : string.Compare(a.VersionText, b.VersionText, StringComparison.OrdinalIgnoreCase),
                    "LatestVersion" => string.Compare(a.LatestVersion ?? "", b.LatestVersion ?? "", StringComparison.OrdinalIgnoreCase),
                    "SourceText" => string.Compare(a.SourceText, b.SourceText, StringComparison.OrdinalIgnoreCase),
                    "StatusText" => string.Compare(a.StatusText, b.StatusText, StringComparison.OrdinalIgnoreCase),
                    "DetailText" => string.Compare(a.DetailText, b.DetailText, StringComparison.OrdinalIgnoreCase),
                    _ => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)
                };
                return _desc ? -c : c;
            }
        }

        private static readonly Dictionary<string, string> SortKeys = new()
        {
            ["Mod 名称"] = "Name",
            ["版本"] = "VersionText",
            ["最新版"] = "LatestVersion",
            ["来源"] = "SourceText",
            ["状态"] = "StatusText",
            ["说明"] = "DetailText"
        };

        private void List_HeaderClick(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is GridViewColumnHeader header) || header.Column == null)
                return;
            if (!(header.Column.Header is string title) || !SortKeys.TryGetValue(title, out string key))
                return;

            if (_sortKey == key)
                _sortDesc = !_sortDesc;
            else
            {
                _sortKey = key;
                _sortDesc = false;
            }
            ApplySort();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view.Refresh();
            UpdateSummary();
        }

        private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
                return;
            _view.Refresh();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            int total = _shell.Mods.Count;
            int shown = _view?.Cast<object>().Count() ?? total;
            int updates = _shell.Mods.Count(m => m.HasUpdate);
            int disabled = _shell.Mods.Count(m => m.Mod.Disabled);
            TxtSummary.Text = $"共 {total} 个 · 显示 {shown} 个 · 可更新 {updates} 个 · 已禁用 {disabled} 个";
            BtnUpdateAll.IsEnabled = updates > 0 && !_busy;
        }

        // ==================== 选中项 ====================

        private List<ModStatus> Selected() => List.SelectedItems.Cast<ModStatus>().ToList();

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionPanel();

        private void UpdateSelectionPanel()
        {
            var sel = Selected();
            if (sel.Count == 0)
            {
                TxtSelTitle.Text = "在上面选一个 Mod 查看详情";
                TxtSelMeta.Text = "";
                TxtSelDesc.Text = "";
                BtnSelUpdate.IsEnabled = BtnSelToggle.IsEnabled = BtnSelPage.IsEnabled =
                    BtnSelFolder.IsEnabled = BtnSelDelete.IsEnabled =
                    BtnSelSource.IsEnabled = BtnSelCopy.IsEnabled = BtnSelName.IsEnabled =
                    BtnSelGroup.IsEnabled = false;
                return;
            }

            BtnSelUpdate.IsEnabled = BtnSelToggle.IsEnabled = BtnSelPage.IsEnabled =
                BtnSelFolder.IsEnabled = BtnSelDelete.IsEnabled =
                BtnSelSource.IsEnabled = BtnSelCopy.IsEnabled = BtnSelName.IsEnabled =
                BtnSelGroup.IsEnabled = true;

            if (sel.Count > 1)
            {
                TxtSelTitle.Text = $"已选中 {sel.Count} 个 Mod";
                TxtSelMeta.Text = string.Join("、", sel.Take(6).Select(s => s.Name))
                                  + (sel.Count > 6 ? " …" : "");
                TxtSelDesc.Text = "可以用上方按钮批量启用 / 禁用 / 删除 / 复制名称（一次一行）。";
                BtnSelPage.IsEnabled = BtnSelFolder.IsEnabled = false;
                BtnSelSource.IsEnabled = false;      // 补源一次只能填一个
                BtnSelName.IsEnabled = false;        // 改中文名一次也只能改一个
                return;
            }

            var s = sel[0];
            var m = s.Mod;
            string selTitle = ModNames.Display(m);
            TxtSelTitle.Text = selTitle;

            // 显示名里已经带上英文原名时就不用再单列一行「原名」
            bool showOriginal = !string.IsNullOrWhiteSpace(m.OriginalName)
                                 && !string.Equals(m.OriginalName, m.Name, StringComparison.Ordinal)
                                 && !selTitle.Contains(m.OriginalName, StringComparison.Ordinal);

            TxtSelMeta.Text = $"v{m.Version}　·　作者：{(string.IsNullOrEmpty(m.Author) ? "—" : m.Author)}　·　"
                              + (m.ContentPackForId != null ? $"内容包（{m.ContentPackForId}）　·　" : "")
                              + $"状态：{s.StatusText}"
                              + (s.LatestVersion != null ? $"（最新 {s.LatestVersion}）" : "")
                              + (showOriginal ? $"　·　原名：{m.OriginalName}" : "");
            TxtSelDesc.Text = string.IsNullOrWhiteSpace(m.Description)
                ? $"路径：{m.Dir}"
                : m.Description + "\n路径：" + m.Dir;

            BtnSelToggle.Content = m.Disabled ? "启用" : "禁用";
            BtnSelUpdate.Content = s.HasUpdate ? $"更新到 {s.LatestVersion}" : "更新到最新";
            BtnSelUpdate.IsEnabled = s.HasUpdate && s.CanUpdate;
            BtnSelPage.IsEnabled = !string.IsNullOrEmpty(s.PageUrl);
            BtnSelFolder.IsEnabled = Directory.Exists(m.Dir);
        }

        // ==================== 检查更新 ====================

        private async void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            await RunBusyAsync("检查更新中…", async () =>
            {
                _cts = new CancellationTokenSource();
                var phase = new Progress<string>(s => _shell.SetStatus(s));
                await UpdateChecker.CheckAllAsync(Env, _shell.Mods.ToList(), phase, _cts.Token);

                int u = _shell.Mods.Count(m => m.HasUpdate);
                _shell.SetStatus(u > 0 ? $"检查完成：{u} 个 Mod 可以更新" : "检查完成：全部都是最新版本");
                _shell.Toast(u > 0 ? $"发现 {u} 个可更新的 Mod" : "全部都是最新版本");
            });
        }

        private async void BtnUpdateAll_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var todo = _shell.Mods.Where(m => m.CanUpdate).ToList();
            if (todo.Count == 0)
            {
                _shell.Toast("没有可自动更新的 Mod。无法自动下载的请点「打开网页」手动下载。");
                return;
            }

            await RunBusyAsync($"正在更新 {todo.Count} 个 Mod…", async () =>
            {
                int ok = 0;
                var errors = new List<string>();
                for (int i = 0; i < todo.Count; i++)
                {
                    var s = todo[i];
                    _shell.SetStatus($"（{i + 1}/{todo.Count}）正在更新 {s.Name}…");
                    bool done = await UpdateOneAsync(s, errors);
                    if (done)
                        ok++;
                }
                await _shell.ReloadModsAsync();
                _shell.Toast(errors.Count == 0
                    ? $"已更新 {ok} 个 Mod，重启游戏后生效"
                    : $"成功 {ok} 个，失败 {errors.Count} 个：\n" + string.Join("\n", errors.Take(6)));
            });
        }

        private async Task<bool> UpdateOneAsync(ModStatus s, List<string> errors)
        {
            try
            {
                s.Phase = "准备下载…";
                var plan = await UpdateChecker.ResolvePlanAsync(Env, s);
                if (plan == null)
                {
                    s.ManualDownload = true;
                    s.Message = "这个来源没有可直接下载的文件，请点「打开网页」手动下载 zip 后导入";
                    errors.Add(s.Name + "：需要手动下载");
                    return false;
                }

                var prog = new Progress<InstallProgress>(p =>
                {
                    s.Phase = p.Phase;
                    s.Progress = p.Percent;
                });

                await ModInstaller.UpdateAsync(Env, s.Mod, plan, prog, CancellationToken.None);
                return true;
            }
            catch (Exception ex)
            {
                s.Message = ex.Message;
                s.ManualDownload = true;
                errors.Add(s.Name + "：" + ex.Message);
                return false;
            }
            finally
            {
                s.Phase = null;
                s.Progress = 0;
            }
        }

        // ==================== 单个操作 ====================

        private async void BtnSelUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count == 0)
                return;

            await RunBusyAsync("正在更新…", async () =>
            {
                var errors = new List<string>();
                int ok = 0;
                foreach (var s in sel)
                {
                    if (!s.CanUpdate)
                    {
                        errors.Add(s.Name + "：无可自动下载的文件");
                        continue;
                    }
                    _shell.SetStatus($"正在更新 {s.Name}…");
                    if (await UpdateOneAsync(s, errors))
                        ok++;
                }
                await _shell.ReloadModsAsync();
                _shell.Toast(errors.Count == 0 ? $"已更新 {ok} 个 Mod" : string.Join("\n", errors.Take(6)));
            });
        }

        private async void BtnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count == 0)
                return;
            await ToggleAsync(sel);
        }

        private async void BtnSelToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count == 0)
                return;
            await ToggleAsync(sel);
        }

        private async Task ToggleAsync(List<ModStatus> sel)
        {
            if (AppEnv.IsGameRunning())
            {
                var r = Dlg.Show(this,
                    "游戏正在运行。现在改变 Mod 启停状态不会立刻生效，要重启游戏才行。继续吗？",
                    "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes)
                    return;
            }

            await RunBusyAsync("正在切换启用状态…", async () =>
            {
                var all = _shell.Mods.Select(x => x.Mod).ToList();
                var msgs = new List<string>();
                foreach (var s in sel)
                {
                    try
                    {
                        if (!s.Mod.Disabled)
                        {
                            string msg = ModControl.Disable(Env, s.Mod, all);
                            if (msg != null)
                                msgs.Add(msg);
                            continue;
                        }

                        // 启用前先把同一个互斥组里还开着的其它 Mod 停用（同组只留一个，例如换马皮肤）
                        foreach (var t in ModGroups.ToDisable(Env, s.Mod, all))
                        {
                            string m2 = ModControl.Disable(Env, t, all);
                            if (m2 != null)
                                msgs.Add(m2 + "（同互斥组，自动停用）");
                        }

                        string enabled = ModControl.Enable(Env, s.Mod);
                        if (enabled != null)
                            msgs.Add(enabled);
                    }
                    catch (Exception ex)
                    {
                        msgs.Add(s.Name + "：" + ex.Message);
                    }
                }
                await _shell.ReloadModsAsync();
                _shell.Toast(string.Join("\n", msgs.Take(6)));
            });
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count == 0)
                return;
            await DeleteAsync(sel);
        }

        private async void BtnSelDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count == 0)
                return;
            await DeleteAsync(sel);
        }

        private async Task DeleteAsync(List<ModStatus> sel)
        {
            string names = string.Join("\n", sel.Take(8).Select(s => "· " + s.Name + (s.Mod.Disabled ? "（已禁用）" : "")));
            var confirm = Dlg.Show(this,
                (Env.Config.BackupBeforeDelete
                    ? "确定删除下面这些 Mod 吗？\n删除前会自动留一份备份，之后可以在「备份回滚」里恢复。\n\n"
                    : "确定删除下面这些 Mod 吗？配置里关闭了删除前备份，删了就找不回来了。\n\n")
                + names + (sel.Count > 8 ? $"\n… 共 {sel.Count} 个" : ""),
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
                return;

            await RunBusyAsync("正在删除…", async () =>
            {
                var errs = new List<string>();
                int ok = 0;
                foreach (var s in sel)
                {
                    try
                    {
                        Analyzer.MoveToTrash(Env, s.Mod, "删除前备份");
                        Env.Log($"已删除 {s.Name}（{s.Mod.Dir}）");
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        errs.Add(s.Name + "：" + Installer.Describe(ex));
                    }
                }
                await _shell.ReloadModsAsync();
                _shell.Toast(errs.Count == 0
                    ? $"已删除 {ok} 个 Mod" + (Env.Config.BackupBeforeDelete ? "（已留备份）" : "")
                    : string.Join("\n", errs.Take(6)));
            });
        }

        private async void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择下载好的 Mod 压缩包（可多选）",
                Filter = "Mod 压缩包 (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z|所有文件 (*.*)|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog(Dlg.Owner(this)) != true)
                return;

            await ImportFilesAsync(dlg.FileNames);
        }

        private async Task ImportFilesAsync(IEnumerable<string> files)
        {
            var list = files.Where(f => File.Exists(f)).ToList();
            if (list.Count == 0)
                return;

            await RunBusyAsync("正在安装压缩包…", async () =>
            {
                var before = NewModNamePrompt.Snapshot(Env);      // 装完拿它对比出「新装的」
                var msgs = new List<string>();
                foreach (string f in list)
                {
                    _shell.SetStatus("正在安装 " + Path.GetFileName(f) + " …");
                    try
                    {
                        var prog = new Progress<InstallProgress>(p =>
                        {
                            _shell.SetStatus(p.Phase + " " + Path.GetFileName(f));
                            _shell.SetProgress(p.Percent);
                        });
                        msgs.Add(await ModInstaller.ImportAsync(Env, f, false, prog, CancellationToken.None));
                    }
                    catch (Exception ex)
                    {
                        msgs.Add(Path.GetFileName(f) + "：" + ex.Message);
                    }
                }
                await _shell.ReloadModsAsync();
                _shell.Toast(string.Join("\n", msgs.Take(6)));
                RevealMods(_shell.Mods.Where(m => !before.Contains(m.Id)).Select(m => m.Id));
                await NewModNamePrompt.AskAsync(Env, before, _shell);
            });
        }

        private void BtnOpenMods_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Env.ModsPath);

        // ==================== 改中文名 / 补写更新源 / 复制名称 ====================

        private async void BtnSelName_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            var sel = Selected();
            if (sel.Count != 1)
            {
                _shell.Toast("改中文名一次只能改一个 Mod");
                return;
            }

            var mod = sel[0].Mod;
            var dlg = new NameDialog(Env, mod) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true)
                return;

            await _shell.ReloadModsAsync();
            RevealMods(new[] { mod.Id });          // 名字变了就选中它露出来（顺便清搜索/筛选，免得被挡住）
            _shell.Toast(dlg.ResultMessage ?? "名字已更新");
        }

        private async void BtnSelSource_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            var sel = Selected();
            if (sel.Count != 1)
            {
                _shell.Toast("补写更新源一次只能填一个 Mod");
                return;
            }

            var mod = sel[0].Mod;
            var dlg = new SourceDialog(Env, mod) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true)
                return;

            string saved = dlg.SavedKey;
            await _shell.ReloadModsAsync();

            // 立刻按新源查一次，让状态和版本马上刷新
            var status = _shell.Mods.FirstOrDefault(m =>
                string.Equals(m.Mod.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            if (status != null)
            {
                await RunBusyAsync($"正在按新更新源检查 {status.Name}…", async () =>
                {
                    await UpdateChecker.CheckAllAsync(Env, new List<ModStatus> { status }, null, default);
                });
            }

            _shell.Toast($"已给「{mod.Name}」补上更新源：{saved}");
        }

        /// <summary>给选中的 Mod 设置互斥组（支持多选：一次把几套皮肤都归到「马皮肤」）。</summary>
        private async void BtnSelGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            var sel = Selected();
            if (sel.Count == 0)
                return;

            var all = _shell.Mods.Select(x => x.Mod).ToList();
            var dlg = new ExclusiveGroupDialog(Env, sel.Select(x => x.Mod).ToList(), all)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() != true)
                return;

            await RunBusyAsync("正在应用互斥组…", async () =>
            {
                var msgs = new List<string> { dlg.ResultMessage ?? "互斥组已更新" };

                // 刚分好组时，如果组里还有多个启用着，只留选中的第一个，其余自动停用
                var first = sel[0].Mod;
                if (!first.Disabled && ModGroups.GroupOf(Env, first) != null)
                {
                    var stop = ModGroups.ToDisable(Env, first, all);
                    foreach (var t in stop)
                    {
                        string msg = ModControl.Disable(Env, t, all);
                        if (msg != null)
                            msgs.Add(msg + "（同互斥组，只留一个启用）");
                    }
                }

                await _shell.ReloadModsAsync();
                _shell.Toast(string.Join("\n", msgs.Take(6)));
            });
        }

        private void BtnSelCopy_Click(object sender, RoutedEventArgs e)
        {
            var sel = Selected();
            if (sel.Count == 0)
                return;

            var names = sel
                .Select(s => SourceProbe.SearchText(s.Name))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            if (names.Count == 0)
            {
                _shell.Toast("这些 Mod 没有名字，没得复制");
                return;
            }

            string text = string.Join(Environment.NewLine, names);
            try
            {
                Clipboard.SetText(text);
                Env.Log("已复制 Mod 名称：" + text.Replace(Environment.NewLine, " | "));

                if (names.Count == 1)
                {
                    string full = sel[0].Name;
                    _shell.Toast(string.Equals(text, full, StringComparison.Ordinal)
                        ? $"已复制名称：{text}"
                        : $"已复制：{text}\n（完整名字是「{full}」，拿去 N 网搜索用英文名才搜得到）");
                }
                else
                {
                    _shell.Toast($"已复制 {names.Count} 个名称，一行一个");
                }
            }
            catch (Exception ex)
            {
                _shell.Toast("复制失败（剪贴板被别的程序占用？）：" + ex.Message);
            }
        }

        private void BtnSelPage_Click(object sender, RoutedEventArgs e)
        {
            var s = Selected().FirstOrDefault();
            if (s != null && !string.IsNullOrEmpty(s.PageUrl))
                App.OpenUrl(s.PageUrl);
        }

        private void BtnSelFolder_Click(object sender, RoutedEventArgs e)
        {
            var s = Selected().FirstOrDefault();
            if (s != null)
                App.OpenFolder(s.Mod.Dir);
        }

        // ==================== 拖入压缩包 ====================

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop))
                return;
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] paths))
                return;
            e.Handled = true;

            var zips = paths.Where(Installer.IsArchiveName).ToList();
            var folders = paths.Where(p => Directory.Exists(p)).ToList();

            if (zips.Count == 0 && folders.Count == 0)
            {
                _shell.Toast("只支持拖入 zip / rar / 7z 压缩包，或已解压的 Mod 文件夹");
                return;
            }

            if (folders.Count > 0)
                await RunBusyAsync("正在安装…", async () =>
                {
                    var before = NewModNamePrompt.Snapshot(Env);
                    var msgs = new List<string>();
                    foreach (string f in folders)
                    {
                        try
                        {
                            msgs.Add(await ModInstaller.ImportAsync(Env, f, true, null, CancellationToken.None));
                        }
                        catch (Exception ex)
                        {
                            msgs.Add(Path.GetFileName(f) + "：" + ex.Message);
                        }
                    }
                    await _shell.ReloadModsAsync();
                    _shell.Toast(string.Join("\n", msgs.Take(6)));
                    RevealMods(_shell.Mods.Where(m => !before.Contains(m.Id)).Select(m => m.Id));
                    await NewModNamePrompt.AskAsync(Env, before, _shell);
                });

            if (zips.Count > 0)
                await ImportFilesAsync(zips);
        }

        // ==================== 忙碌包装 ====================

        private async Task RunBusyAsync(string label, Func<Task> action)
        {
            if (_busy)
                return;
            _busy = true;
            _shell.SetBusy(label);
            _shell.SetStatus(label);
            BtnCheck.IsEnabled = BtnUpdateAll.IsEnabled = BtnImport.IsEnabled = false;
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Env.Log("操作失败：" + ex);
                _shell.Toast("操作失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
                _shell.SetProgress(0);
                BtnCheck.IsEnabled = BtnImport.IsEnabled = true;
                UpdateSummary();
                UpdateSelectionPanel();
            }
        }
    }
}
