using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SMM
{
    /// <summary>
    /// 装完新 Mod 后弹窗问一句中文名。
    /// 填了名字会写进这个 Mod 的 manifest.json（Name 字段）并记进配置，
    /// 于是桌面版和游戏内（F9 面板）都会显示成「中文名（English）」；取消/关闭 = 跳过，以后还能改。
    /// </summary>
    internal static class NewModNamePrompt
    {
        /// <summary>安装前先拍个快照（当前已装 Mod 的 UniqueID）。</summary>
        public static HashSet<string> Snapshot(AppEnv env)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var m in ModScanner.Scan(env.ModsPath, env.DisabledDir))
                {
                    if (!string.IsNullOrWhiteSpace(m.Id))
                        set.Add(m.Id);
                }
            }
            catch { }
            return set;
        }

        /// <summary>
        /// 这个 Mod 要不要弹窗问中文名：
        /// ① 不在安装前的快照里（= 这次新装的）；② 没有配过中文名；③ manifest 里也不是中文名；
        /// ④ 内置对照表里也没有它的中文名（有的话界面已经能显示中文，没必要打扰）。
        /// </summary>
        public static bool ShouldAsk(AppEnv env, LocalMod m, HashSet<string> before)
            => m != null
               && !string.IsNullOrWhiteSpace(m.Id)
               && (before == null || !before.Contains(m.Id))
               && !ModNamer.HasChineseName(env, m)
               && !ModNames.HasChinese(m.Name)
               && ModNames.Chinese(m) == null;

        /// <summary>
        /// 和快照对比，把「这次新装进来、且还没中文名」的 Mod 逐个弹窗问名字。
        /// 返回填了几个（用户取消的不算）。
        /// 填完会立刻重扫列表——不然行上显示的、以及中文搜索用的，都还是改名前那个值。
        /// </summary>
        public static async Task<int> AskAsync(AppEnv env, HashSet<string> before, IShell shell)
        {
            if (env == null || before == null || shell == null)
                return 0;

            var owner = shell as Window;

            List<LocalMod> mods;
            try { mods = await Task.Run(() => ModScanner.Scan(env.ModsPath, env.DisabledDir)); }
            catch { return 0; }

            var fresh = mods
                .Where(m => ShouldAsk(env, m, before))
                .OrderBy(m => m.Name ?? m.Id, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (fresh.Count == 0)
                return 0;

            int named = 0;
            foreach (var m in fresh)
            {
                try
                {
                    var dlg = new NameDialog(env, m);
                    if (owner != null && owner.IsLoaded)
                        dlg.Owner = owner;

                    if (dlg.ShowDialog() == true)
                    {
                        named++;
                        env.Log("新装 Mod 填中文名：" + dlg.ResultMessage + "（" + m.Id + "）");

                        // 名字改了要重扫列表才生效：DisplayName（中文（English））与中文搜索都靠它
                        try
                        {
                            await shell.ReloadModsAsync();
                            shell.RevealMods(new[] { m.Id });
                        }
                        catch (Exception ex)
                        {
                            env.Log("改名后刷新列表失败：" + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    env.Log("问中文名时出错（" + m.Id + "）：" + ex.Message);
                }
            }
            return named;
        }
    }
}
