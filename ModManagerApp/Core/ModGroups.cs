using System;
using System.Collections.Generic;
using System.Linq;

namespace SMM
{
    /// <summary>
    /// 互斥组：同一个组里，同一时间只允许一个 Mod 处于启用状态。
    ///
    /// 典型用途：装了好几套马皮肤 / 狗皮肤 / 家具这类内容包，它们改的是同一批文件，
    /// 同时启用会互相冲突（SMAPI 报 conflict、或者干脆显示成乱的那一套）。
    /// 分组之后，想看哪套就启用哪套，管理器自动把同组其它停用，不用手动一个个关。
    ///
    /// 组名存在 AppConfig.ExclusiveGroups（Mod 的 UniqueID → 组名），一个 Mod 只属于一个组。
    /// </summary>
    internal static class ModGroups
    {
        /// <summary>这个 Mod 属于哪个互斥组（没分组返回 null）。</summary>
        public static string GroupOf(AppEnv env, LocalMod mod) => GroupOf(env, mod?.Id);

        public static string GroupOf(AppEnv env, string modId)
        {
            var map = env?.Config?.ExclusiveGroups;
            if (map == null || string.IsNullOrWhiteSpace(modId))
                return null;
            return map.TryGetValue(modId, out string g) && !string.IsNullOrWhiteSpace(g) ? g : null;
        }

        /// <summary>现有组名（去重、排序），给设置对话框的下拉用。</summary>
        public static List<string> GroupNames(AppEnv env)
            => (env?.Config?.ExclusiveGroups?.Values ?? Enumerable.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>同组的其它 Mod（不含自己）。</summary>
        public static List<LocalMod> Siblings(AppEnv env, LocalMod mod, IEnumerable<LocalMod> all)
        {
            string group = GroupOf(env, mod);
            if (group == null || mod == null || string.IsNullOrWhiteSpace(mod.Id))
                return new List<LocalMod>();

            return (all ?? Enumerable.Empty<LocalMod>())
                .Where(m => m != null
                            && !string.IsNullOrWhiteSpace(m.Id)
                            && !string.IsNullOrWhiteSpace(m.Dir)
                            && !string.Equals(m.Id, mod.Id, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(GroupOf(env, m), group, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }

        /// <summary>启用某个 Mod 时，同组里需要被自动停用的那些（还处于启用状态的兄弟）。</summary>
        public static List<LocalMod> ToDisable(AppEnv env, LocalMod enabling, IEnumerable<LocalMod> all)
            => Siblings(env, enabling, all).Where(m => !m.Disabled).ToList();

        /// <summary>把一组 Mod 归到某个组（group 为空 = 移出组）。</summary>
        public static void Assign(AppEnv env, IEnumerable<LocalMod> mods, string group)
        {
            if (env?.Config == null)
                return;

            var map = env.Config.ExclusiveGroups ??= new Dictionary<string, string>();
            group = (group ?? "").Trim();

            foreach (var m in mods ?? Enumerable.Empty<LocalMod>())
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Id))
                    continue;
                if (group.Length == 0)
                    map.Remove(m.Id);
                else
                    map[m.Id] = group;
            }
            env.Config.Save();
        }

        /// <summary>组里当前启用着的那些（用于界面上提示）。</summary>
        public static List<LocalMod> EnabledIn(AppEnv env, string group, IEnumerable<LocalMod> all)
            => string.IsNullOrWhiteSpace(group)
                ? new List<LocalMod>()
                : (all ?? Enumerable.Empty<LocalMod>())
                    .Where(m => m != null && !m.Disabled
                                && string.Equals(GroupOf(env, m), group, StringComparison.CurrentCultureIgnoreCase))
                    .ToList();
    }
}
