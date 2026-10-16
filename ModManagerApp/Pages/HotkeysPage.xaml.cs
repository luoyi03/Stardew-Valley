using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SMM
{
    /// <summary>
    /// 快捷键：列出各 Mod 配置里的按键绑定，找出重复，并支持改键 / 禁用 / 启用。
    ///
    /// 数据来源是各 Mod 自己的 config.json —— 只能读到「已经写进配置」的按键。
    /// 大多数 Mod 是第一次运行游戏时才生成 config.json（内容是它当时的默认键），
    /// 所以游戏至少跑过一次之后，这里才谈得上"整体检查"。
    /// </summary>
    public partial class HotkeysPage : UserControl, IPage
    {
        private readonly IShell _shell;
        private readonly ObservableCollection<HotkeyBinding> _items = new();
        private List<HotkeyBinding> _all = new();      // 完整扫描结果（筛选前）
        private readonly Dictionary<string, string> _blobs = new();   // 每条的可搜索文本（含中英对照）
        private List<HotkeyConflict> _conflicts = new();
        private string _selectedKey;                   // 记住选中的是哪一条（重建列表后恢复）
        private bool _busy;

        private AppEnv Env => _shell.Env;

        public HotkeysPage(IShell shell)
        {
            _shell = shell;
            InitializeComponent();
            List.ItemsSource = _items;
        }

        public void OnActivated() => _ = RescanAsync();

        private async Task RescanAsync(string selectKey = null)
        {
            if (_busy)
                return;

            _busy = true;
            _shell.SetBusy("正在读取各 Mod 的按键设置…");
            try
            {
                var env = Env;
                var list = await Task.Run(() => Hotkeys.Scan(env));
                var conflicts = Hotkeys.Conflicts(list);

                // 给重复的行打标记
                var marks = new Dictionary<HotkeyBinding, string>();
                foreach (var c in conflicts)
                {
                    foreach (var b in c.Items)
                        marks[b] = marks.TryGetValue(b, out string prev) ? prev + "、" + c.Combo : "重复 " + c.Combo;
                }
                foreach (var b in list)
                    b.ConflictText = marks.TryGetValue(b, out string t) ? t : "";

                // 每条预先算一份「可搜索文本」：Mod 名 / 中文名 / 配置项 / 按键，并把中英说法互相补齐
                _blobs.Clear();
                foreach (var b in list)
                    _blobs[b.Key] = BuildBlob(b);

                _all = list;
                _conflicts = conflicts;
                ApplyFilter(selectKey);

                RefreshSummary();
                _shell.SetStatus($"快捷键：{list.Count} 条，重复 {conflicts.Count} 处");
            }
            catch (Exception ex)
            {
                _shell.Toast("读取按键失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                _shell.SetBusy(null);
                UpdateSelectionPanel();
            }
        }

        /// <summary>
        /// 按右上角的下拉筛选列表内容（只看重复 / 只看启用中 / 只看已禁用），
        /// 并尽量把选中项留住（重建列表会让 WPF 丢掉选中，所以在筛选里改键才不会"跳空"）。
        /// </summary>
        private void ApplyFilter(string selectKey = null)
        {
            string keep = selectKey ?? CurrentSelectionKey() ?? _selectedKey;

            int idx = CmbFilter?.SelectedIndex ?? 0;
            IEnumerable<HotkeyBinding> q = _all;

            q = idx switch
            {
                1 => q.Where(b => !string.IsNullOrEmpty(b.ConflictText)),   // 只看重复
                2 => q.Where(b => !b.IsDisabled),                            // 只看启用中
                3 => q.Where(b => b.IsDisabled),                             // 只看已禁用
                _ => q
            };

            // 搜索框：中英文都能搜（词与词之间是"全部命中"的关系）
            if (!string.IsNullOrWhiteSpace(TxtSearch?.Text))
                q = q.Where(Matches);

            var shown = q.ToList();
            _items.Clear();
            foreach (var b in shown)
                _items.Add(b);

            RestoreSelection(keep);
            RefreshSummary();
        }

        /// <summary>当前选中的是哪一条（多选/没选返回 null）。</summary>
        private string CurrentSelectionKey()
        {
            var sel = List.SelectedItems.OfType<HotkeyBinding>().ToList();
            return sel.Count == 1 ? sel[0].Key : null;
        }

        /// <summary>重建列表后把选中项恢复回来（没被筛掉的话顺便滚到它）。</summary>
        private void RestoreSelection(string key)
        {
            _selectedKey = key;
            if (string.IsNullOrEmpty(key))
                return;

            var hit = _items.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
                return;      // 被当前筛选挡住了：先记着，切回「全部」时还能自动选回来

            List.SelectedItem = hit;
            try { List.ScrollIntoView(hit); } catch { }
        }

        /// <summary>
        /// 改完一条之后，同一个键如果还有别的功能在用，就返回那一条的 Key
        /// （界面会自动选中它，好接着改；这就是"只看重复"里一处处修过去的手感）。
        /// </summary>
        private string NextHolderKey(string editedKey, string combo)
        {
            if (string.IsNullOrEmpty(combo))
                return null;

            var other = _all.FirstOrDefault(x => !x.IsDisabled
                                                 && !string.Equals(x.Key, editedKey, StringComparison.OrdinalIgnoreCase)
                                                 && Hotkeys.Alternatives(x.Value).Contains(combo, StringComparer.Ordinal));
            return other?.Key;
        }

        private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
                return;
            ApplyFilter();
            UpdateSelectionPanel();
        }

        /// <summary>工具栏右边那行统计（搜索/筛选一变就要刷）。</summary>
        private void RefreshSummary()
        {
            var list = _all;
            var conflicts = _conflicts;

            int disabled = list.Count(b => b.IsDisabled);
            string searching = string.IsNullOrWhiteSpace(TxtSearch?.Text) ? "" : "，搜索「" + TxtSearch.Text.Trim() + "」";

            TxtSummary.Text = $"共 {list.Count} 条按键 · 重复 {conflicts.Count} 处 · 已禁用 {disabled} 条"
                              + $"　（当前显示 {_items.Count} 条{searching}）";

            if (conflicts.Count == 0)
            {
                TxtConflicts.Text = list.Count == 0
                    ? "还没读到任何按键设置（见下面说明）。"
                    : "没有发现重复的按键。";
            }
            else
            {
                TxtConflicts.Text = $"发现 {conflicts.Count} 处重复，建议只保留一个或改成别的键：\n"
                    + string.Join("\n", conflicts.Select(c => "· " + c.Text
                        + (c.SameModOnly ? "（同一个 Mod 内部）" : "（跨 Mod）")))
                    + "\n\n改法：把右上角筛选切到「只看重复」，选中一行点「改键…」就地改；"
                    + "改完会自动跳到同一个键的下一个占用者，一处处顺着改过去就行。"
                    + "（指定的键还被别人占着时，保存会被拦下并列出是谁在用）";
            }

            TxtCoverage.Text = "搜索：左上的搜索框中英文都能搜 —— Mod 名 / 中文名 / 配置项名（填 SaveKey 或「保存」）/ "
                + "按键（填 LeftControl + D2 或「左Ctrl + D2」）/ 状态（填「已禁用」或 None）。"
                + "多个词用空格分隔时要求全部命中；Esc 或「清空」按钮清掉搜索。\n\n"
                + "说明：这里只能读「已经写进各 Mod 的 config.json」的按键。"
                + "大多数 Mod 是第一次进游戏时才生成自己的 config.json（内容是它当时的默认键），"
                + "所以在游戏至少跑过一次之前，能看到的条目会很少 —— 进游戏跑一次，回来点「重新扫描」就齐了。"
                + "改键 / 禁用都是写回该 Mod 自己的 config.json（只改这一项，其它设置不动），重启游戏后生效。";
        }

        // ==================== 搜索（中英文都能搜） ====================

        /// <summary>
        /// 中英对照表：一组里的说法互相等价。
        /// 搜「保存」能找到 SaveKey，搜「左Ctrl」能找到 LeftControl + D2，反过来也一样。
        /// </summary>
        private static readonly string[][] AliasGroups =
        {
            // 修饰键 / 鼠标 / 常用键
            new[] { "leftcontrol", "左ctrl", "左control", "左控制键", "lctrl" },
            new[] { "rightcontrol", "右ctrl", "右control", "右控制键" },
            new[] { "leftshift", "左shift", "左上档键" },
            new[] { "rightshift", "右shift", "右上档键" },
            new[] { "leftalt", "左alt", "左换挡键" },
            new[] { "rightalt", "右alt", "右换挡键" },
            new[] { "control", "ctrl", "控制键", "ctrl键" },
            new[] { "shift", "上档键" },
            new[] { "alt", "换挡键" },
            new[] { "mouseleft", "鼠标左键", "左键" },
            new[] { "mouseright", "鼠标右键", "右键" },
            new[] { "mousemiddle", "鼠标中键", "中键" },
            new[] { "space", "空格", "空格键" },
            new[] { "enter", "回车", "回车键", "确认键" },
            new[] { "escape", "esc", "退出键", "取消键" },
            new[] { "tab", "制表键" },
            new[] { "back", "退格键" },
            new[] { "delete", "删除键" },
            new[] { "insert", "插入键" },
            new[] { "pageup", "上翻页", "页面上翻" },
            new[] { "pagedown", "下翻页", "页面下翻" },
            new[] { "capslock", "大写锁定" },
            new[] { "printscreen", "截图键", "截屏键" },
            new[] { "up", "上" }, new[] { "down", "下" }, new[] { "left", "左" }, new[] { "right", "右" },

            // 功能词（配置项名字）
            new[] { "save", "保存" },
            new[] { "menu", "菜单" },
            new[] { "open", "打开", "开启" },
            new[] { "close", "关闭", "关掉" },
            new[] { "toggle", "切换", "开关" },
            new[] { "chat", "聊天", "打字聊天" },
            new[] { "talk", "对话", "交谈" },
            new[] { "speak", "说话", "语音" },
            new[] { "calendar", "日历" },
            new[] { "quest", "任务", "委托" },
            new[] { "journal", "日志", "记事" },
            new[] { "map", "地图" },
            new[] { "inventory", "背包", "物品栏" },
            new[] { "screenshot", "截图", "截屏" },
            new[] { "zoom", "缩放" },
            new[] { "fishing", "钓鱼" },
            new[] { "chest", "箱子", "储物箱" },
            new[] { "auto", "自动" },
            new[] { "greet", "打招呼", "问候" },
            new[] { "pause", "暂停" },
            new[] { "fullscreen", "全屏" },
            new[] { "volume", "音量" },
            new[] { "mute", "静音" },
            new[] { "controller", "手柄" },
            new[] { "companion", "伴侣", "同伴" },
            new[] { "hotkey", "快捷键" },
            new[] { "key", "按键", "键" },
            new[] { "none", "未绑定", "已禁用", "禁用" },
            new[] { "enabled", "启用中", "启用" },
            new[] { "debug", "调试" },
            new[] { "next", "下一个" },
            new[] { "previous", "上一个", "prev" }
        };

        /// <summary>把文本里出现过的说法展开成同组的中英说法，让中英搜索互通。</summary>
        private static string Expand(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            string s = " " + text.ToLowerInvariant() + " ";
            var extra = new List<string>();

            foreach (string[] group in AliasGroups)
            {
                bool hit = false;
                foreach (string m in group)
                {
                    if (s.Contains(m))
                    {
                        hit = true;
                        break;
                    }
                }

                if (!hit)
                    continue;

                foreach (string m in group)
                {
                    if (!s.Contains(m))
                        extra.Add(m);
                }
            }

            return extra.Count == 0 ? "" : string.Join(" ", extra);
        }

        /// <summary>一条绑定的可搜索文本（原样 + 中英对照）。</summary>
        private static string BuildBlob(HotkeyBinding b)
        {
            string raw = string.Join(" ", new[]
            {
                b.ModName, b.FolderName, b.JsonPath, b.Value, b.ValueText, b.StateText, b.ConflictText
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

            return (raw + " " + Expand(raw)).ToLowerInvariant();
        }

        /// <summary>查询词的同义说法。</summary>
        private static IEnumerable<string> Variants(string term)
        {
            foreach (string[] group in AliasGroups)
            {
                bool hit = group.Any(m => m.Equals(term, StringComparison.OrdinalIgnoreCase));
                if (!hit)
                    continue;

                foreach (string m in group)
                {
                    if (!m.Equals(term, StringComparison.OrdinalIgnoreCase))
                        yield return m;
                }
            }
        }

        /// <summary>这条是否命中搜索框（多个词 = 全部都要命中）。</summary>
        private bool Matches(HotkeyBinding b)
        {
            string q = TxtSearch?.Text?.Trim();
            if (string.IsNullOrWhiteSpace(q))
                return true;

            if (!_blobs.TryGetValue(b.Key, out string blob))
                blob = BuildBlob(b);

            foreach (string term in q.Split(new[] { ' ', '　', '\t', '，', '、', ',', '+' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (blob.Contains(term, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool alias = false;
                foreach (string v in Variants(term))
                {
                    if (blob.Contains(v, StringComparison.OrdinalIgnoreCase))
                    {
                        alias = true;
                        break;
                    }
                }

                if (!alias)
                    return false;
            }

            return true;
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded)
                return;

            ApplyFilter();
            UpdateSelectionPanel();
        }

        private void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                TxtSearch.Text = "";
                e.Handled = true;
            }
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearch.Text = "";
            TxtSearch.Focus();
        }

        // ==================== 选中项 ====================

        private List<HotkeyBinding> Selected() => List.SelectedItems.OfType<HotkeyBinding>().ToList();

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionPanel();

        /// <summary>双击一行 = 直接改键（在「只看重复」里一处处修的时候最顺手）。</summary>
        private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Selected().Count == 1)
                BtnEdit_Click(sender, null);
        }

        private void UpdateSelectionPanel()
        {
            var sel = Selected();
            if (sel.Count == 0)
            {
                TxtSelTitle.Text = "在上面选一条按键设置";
                TxtSelMeta.Text = "";
                TxtSelDesc.Text = "";
                BtnEdit.IsEnabled = BtnDisable.IsEnabled = BtnEnable.IsEnabled =
                    BtnConfig.IsEnabled = BtnFolder.IsEnabled = false;
                return;
            }

            if (sel.Count > 1)
            {
                TxtSelTitle.Text = $"已选中 {sel.Count} 条按键";
                TxtSelMeta.Text = string.Join("、", sel.Take(6).Select(b => b.ValueText));
                TxtSelDesc.Text = "改键 / 禁用一次只能操作一条。";
                BtnEdit.IsEnabled = BtnDisable.IsEnabled = BtnEnable.IsEnabled = false;
                BtnConfig.IsEnabled = BtnFolder.IsEnabled = false;
                return;
            }

            var one = sel[0];
            BtnEdit.IsEnabled = BtnConfig.IsEnabled = BtnFolder.IsEnabled = true;
            BtnDisable.IsEnabled = !one.IsDisabled;
            BtnEnable.IsEnabled = one.IsDisabled;

            TxtSelTitle.Text = $"{one.ModName}　·　{one.JsonPath}";
            TxtSelMeta.Text = $"当前：{one.ValueText}　·　状态：{one.StateText}"
                              + (string.IsNullOrEmpty(one.ConflictText) ? "" : "　·　" + one.ConflictText);
            TxtSelDesc.Text = "配置文件：" + one.ConfigPath;
        }

        // ==================== 操作 ====================

        private void BtnRescan_Click(object sender, RoutedEventArgs e) => _ = RescanAsync();

        private void BtnOpenMods_Click(object sender, RoutedEventArgs e) => App.OpenFolder(Env.ModsPath);

        private async void BtnEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count != 1)
                return;

            var b = sel[0];
            string initial = b.IsDisabled ? (Remembered(b) ?? "") : b.Value;

            var dlg = new TextInputDialog(
                "改快捷键",
                $"{b.ModName}　·　{b.JsonPath}",
                "新的按键",
                initial,
                v =>
                {
                    string bad = Hotkeys.ValidateInput(v);
                    if (bad != null)
                        return bad;

                    // 撞车就不让存：同一个键给两个功能用，进游戏一按会两个一起触发
                    var hits = Hotkeys.ConflictsFor(_all, b, v);
                    if (hits.Count > 0)
                    {
                        return $"「{Hotkeys.Pretty(string.Join("", Hotkeys.Alternatives(v).Take(1)))}」已经被这些功能占用了，换一个键吧：\n"
                               + string.Join("\n", hits.Take(6).Select(h =>
                                   $"· {h.ModName}（{h.JsonPath.TrimStart('.')}：{h.ValueText}）"))
                               + (hits.Count > 6 ? $"\n… 还有 {hits.Count - 6} 个" : "")
                               + "\n\n（想停用某个功能请用「禁用」按钮；想给同一个键绑定多个功能时，可用组合键，例如 LeftShift + F5）";
                    }

                    return null;
                },
                "下面那个框点一下，直接按你要用的键就会自动识别并填进来；也可以手打：组合键用 + 连接（例如 LeftShift + F5），"
                + "一个功能绑多个备选键用逗号隔开（例如 F5, F6）。想停用这个快捷键请用「禁用」按钮。",
                "说明",
                "会写进这个 Mod 自己的 config.json（只改这一项，其它设置不动），重启游戏后生效。\n"
                + "如果这个键已经被别的功能占用，保存会被拦下并列出是谁在用。",
                captureKeys: true)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                var env = Env;
                string value = dlg.ResultText;
                string combo = Hotkeys.Alternatives(b.Value).FirstOrDefault();   // 改之前它占的键
                string editedKey = b.Key;

                await Task.Run(() => Hotkeys.Write(env, b, value));
                await RescanAsync();

                string next = NextHolderKey(editedKey, combo);
                if (next != null)
                {
                    ApplyFilter(next);      // 自动跳到同一个键的下一个占用者，方便接着改
                    var holder = _items.FirstOrDefault(x => string.Equals(x.Key, next, StringComparison.OrdinalIgnoreCase));
                    _shell.Toast(WithHint($"已把「{b.ModName}」的 {b.JsonPath} 改成 {value}；"
                                          + $"「{holder?.ModName}」的 {holder?.JsonPath} 还占着 {Hotkeys.Pretty(combo)}，已帮你选中它"));
                }
                else
                {
                    ApplyFilter(editedKey);
                    _shell.Toast(WithHint($"已把「{b.ModName}」的 {b.JsonPath} 改成 {value}"));
                }
            }
            catch (Exception ex)
            {
                _shell.Toast("改键失败：" + ex.Message);
            }
        }

        private async void BtnDisable_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count != 1 || sel[0].IsDisabled)
                return;

            var b = sel[0];
            var r = Dlg.Show(this,
                $"要停用「{b.ModName}」的 {b.JsonPath} 吗？（现在是 {b.Value}）\n\n"
                + "做法是把配置里的值写成 None（SMAPI 视为没绑定），原按键会记在管理器里，点「启用」就写回去。\n"
                + "只改这一项，其它设置不动。重启游戏后生效。",
                "禁用快捷键", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
                return;

            try
            {
                var env = Env;
                string combo = Hotkeys.Alternatives(b.Value).FirstOrDefault();
                string editedKey = b.Key;

                await Task.Run(() => Hotkeys.Disable(env, b));
                await RescanAsync();

                string next = NextHolderKey(editedKey, combo);
                if (next != null)
                {
                    ApplyFilter(next);
                    var holder = _items.FirstOrDefault(x => string.Equals(x.Key, next, StringComparison.OrdinalIgnoreCase));
                    _shell.Toast(WithHint($"已禁用「{b.ModName}」的 {b.JsonPath}；"
                                          + $"「{holder?.ModName}」的 {holder?.JsonPath} 还在用 {Hotkeys.Pretty(combo)}，已帮你选中它"));
                }
                else
                {
                    ApplyFilter(editedKey);
                    _shell.Toast(WithHint($"已禁用「{b.ModName}」的 {b.JsonPath}，这个键空出来了"));
                }
            }
            catch (Exception ex)
            {
                _shell.Toast("禁用失败：" + ex.Message);
            }
        }

        private async void BtnEnable_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;
            var sel = Selected();
            if (sel.Count != 1 || !sel[0].IsDisabled)
                return;

            var b = sel[0];

            // 启用会写回禁用前记下的那个键，先看看那个键现在是否被别人占了
            string remembered = Remembered(b);
            if (!string.IsNullOrWhiteSpace(remembered))
            {
                var hits = Hotkeys.ConflictsFor(_all, b, remembered);
                if (hits.Count > 0)
                {
                    var r = Dlg.Show(this,
                        $"启用后会写回原来的键「{remembered}」，但这个键现在已经被占用了：\n\n"
                        + string.Join("\n", hits.Take(6).Select(h => $"· {h.ModName}（{h.JsonPath.TrimStart('.')}：{h.ValueText}）"))
                        + "\n\n同时用会一按触发两个功能。还想启用吗？",
                        "这个键已被占用", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (r != MessageBoxResult.Yes)
                        return;
                }
            }

            try
            {
                var env = Env;
                await Task.Run(() => Hotkeys.Enable(env, b));
                await RescanAsync(b.Key);
                _shell.Toast(WithHint($"已启用「{b.ModName}」的 {b.JsonPath}：{b.Value}"));
            }
            catch (Exception ex)
            {
                _shell.Toast("启用失败：" + ex.Message);
            }
        }

        private void BtnConfig_Click(object sender, RoutedEventArgs e)
        {
            var sel = Selected();
            if (sel.Count != 1)
                return;
            if (!File.Exists(sel[0].ConfigPath))
            {
                _shell.Toast("配置文件不在了：" + sel[0].ConfigPath);
                return;
            }
            App.OpenFile(sel[0].ConfigPath);
        }

        private void BtnFolder_Click(object sender, RoutedEventArgs e)
        {
            var sel = Selected();
            if (sel.Count != 1)
                return;
            App.OpenFolder(Path.GetDirectoryName(sel[0].ConfigPath));
        }

        private string Remembered(HotkeyBinding b)
            => Env.Config.HotkeyOriginal != null && Env.Config.HotkeyOriginal.TryGetValue(b.Key, out string v)
                ? v
                : null;

        private static string WithHint(string msg)
            => AppEnv.IsGameRunning()
                ? msg + "（游戏正在运行，重启游戏后生效）"
                : msg + "，重启游戏后生效";
    }
}
