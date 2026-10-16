using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SMM
{
    /// <summary>
    /// 给选中的 Mod 设置「互斥组」（例如把几套马皮肤都归到「马皮肤」）。
    /// 同组里只会启用一个：启用其中一个时，管理器自动把同组其它停用。
    /// </summary>
    public partial class ExclusiveGroupDialog : Window
    {
        private readonly AppEnv _env;
        private readonly List<LocalMod> _mods;   // 列表里选中的
        private readonly List<LocalMod> _all;    // 当前已装的全部（用于列出组内成员）

        /// <summary>保存 / 移出组成功后的提示文字，给外面弹 Toast 用。</summary>
        public string ResultMessage { get; private set; }

        public ExclusiveGroupDialog(AppEnv env, List<LocalMod> mods, List<LocalMod> all)
        {
            _env = env;
            _mods = mods ?? new List<LocalMod>();
            _all = all ?? new List<LocalMod>();

            InitializeComponent();

            TxtSub.Text = _mods.Count == 1
                ? $"给「{ModNames.Display(_mods[0])}」设置互斥组"
                : $"给选中的 {_mods.Count} 个 Mod 设置同一个互斥组";

            // 组名用普通输入框（主题里的 ComboBox 模板不支持可编辑下拉），已有组名做成按钮一点即填
            BuildExistingChips(env);

            TxtGroup.Text = CommonGroup() ?? "";
            BtnRemove.IsEnabled = _mods.Any(m => ModGroups.GroupOf(env, m) != null);

            RefreshPreview();
            Loaded += (_, _) => { TxtGroup.Focus(); TxtGroup.SelectAll(); };
        }

        /// <summary>把已有的组名做成小按钮，点一下填进输入框，省得手打（也可以照样自己输新的）。</summary>
        private void BuildExistingChips(AppEnv env)
        {
            var names = ModGroups.GroupNames(env);
            PanelExisting.Visibility = names.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            foreach (string g in names)
            {
                var btn = new Button
                {
                    Content = g,
                    Margin = new Thickness(0, 0, 8, 6),
                    Padding = new Thickness(10, 3, 10, 5),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "点一下填入这个组名"
                };

                if (TryFindResource("Btn") is Style s)
                    btn.Style = s;
                else
                    btn.Foreground = Brushes.White;

                btn.Click += (_, _) =>
                {
                    TxtGroup.Text = g;
                    TxtGroup.CaretIndex = TxtGroup.Text.Length;
                    TxtGroup.Focus();
                };
                WrapExisting.Children.Add(btn);
            }
        }

        /// <summary>选中的这些 Mod 现在共同属于哪个组（不一致就返回 null）。</summary>
        private string CommonGroup()
        {
            string first = null;
            foreach (var m in _mods)
            {
                string g = ModGroups.GroupOf(_env, m);
                if (g == null)
                    return null;
                if (first == null)
                    first = g;
                else if (!string.Equals(first, g, StringComparison.CurrentCultureIgnoreCase))
                    return null;
            }
            return first;
        }

        private void RefreshPreview()
        {
            if (ListMembers == null)
                return;

            string group = (TxtGroup.Text ?? "").Trim();
            var members = new List<LocalMod>();

            if (group.Length > 0)
            {
                members.AddRange(_all.Where(m => string.Equals(ModGroups.GroupOf(_env, m), group,
                    StringComparison.CurrentCultureIgnoreCase)));
            }
            foreach (var m in _mods)
            {
                if (!members.Any(x => string.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase)))
                    members.Add(m);
            }

            ListMembers.Items.Clear();
            foreach (var m in members.OrderBy(x => x.Name ?? x.Id, StringComparer.CurrentCultureIgnoreCase))
                ListMembers.Items.Add($"{ModNames.Display(m)}　·　{(m.Disabled ? "已禁用" : "启用中")}");

            TxtCount.Text = members.Count == 0 ? "（这个组还没有成员）" : $"共 {members.Count} 个";
        }

        private void TxtGroup_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string group = (TxtGroup.Text ?? "").Trim();
            if (group.Length == 0)
            {
                ShowError("组名不能是空的。想取消分组请点「移出此组」。");
                return;
            }
            if (group.Length > 20)
            {
                ShowError("组名短一点更好认（20 个字以内）。");
                return;
            }
            if (_mods.Count == 0)
            {
                ShowError("没有选中任何 Mod。");
                return;
            }

            try
            {
                ModGroups.Assign(_env, _mods, group);
                ResultMessage = _mods.Count == 1
                    ? $"已把「{ModNames.Display(_mods[0])}」归到互斥组「{group}」"
                    : $"已把 {_mods.Count} 个 Mod 归到互斥组「{group}」";
                ResultMessage += _mods.Count(m => !m.Disabled) > 1
                    ? "（同组里现在有多个启用，建议只留一个）"
                    : "";
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("保存失败：" + ex.Message);
            }
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ModGroups.Assign(_env, _mods, null);
                ResultMessage = _mods.Count == 1
                    ? $"已把「{ModNames.Display(_mods[0])}」移出互斥组"
                    : $"已把 {_mods.Count} 个 Mod 移出互斥组";
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("操作失败：" + ex.Message);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string msg)
        {
            TxtError.Text = msg;
        }
    }
}
