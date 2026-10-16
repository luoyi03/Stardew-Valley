using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SMM
{
    /// <summary>依赖 / 重复 / 兼容性检查。</summary>
    public static class Analyzer
    {
        private static readonly string[] BuiltinIds = { "Pathoschild.SMAPI", "Pathoschild.ContentPatcher" };

        public static List<ModIssue> Analyze(AppEnv env, List<LocalMod> mods)
        {
            var issues = new List<ModIssue>();
            if (mods == null)
                return issues;

            var byId = new Dictionary<string, LocalMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods)
                byId.TryAdd(m.Id, m);

            // ---------- 1. 重复安装 / UniqueID 冲突 ----------
            foreach (var g in mods.Where(m => m.IsDuplicate).GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
            {
                var list = g.ToList();
                string paths = string.Join("、", list.Select(x => x.DisplayPath));
                var issue = new ModIssue
                {
                    Level = IssueLevel.Error,
                    Kind = "重复安装",
                    ModId = g.Key,
                    ModName = ModNames.Display(list[0]),
                    Detail = $"同一个 Mod 装了 {list.Count} 份：{paths}。SMAPI 只会加载其中一份，建议删掉多余的。"
                };

                var live = list.Where(x => !x.Disabled).ToList();
                if (live.Count > 1)
                {
                    var keep = live[0];
                    var drop = live.Skip(1).ToList();
                    issue.FixLabel = "删除多余的副本";
                    issue.Fix = () =>
                    {
                        foreach (var d in drop)
                            MoveToTrash(env, d, "重复副本");
                    };
                }
                issues.Add(issue);
            }

            // ---------- 2. 入口 DLL 重名（多半是同一 Mod 的不同版本） ----------
            foreach (var g in mods.Where(m => !m.Disabled && !string.IsNullOrEmpty(m.EntryDll))
                                  .GroupBy(m => m.EntryDll, StringComparer.OrdinalIgnoreCase))
            {
                var list = g.ToList();
                if (list.Count < 2 || list.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2)
                    continue;
                issues.Add(new ModIssue
                {
                    Level = IssueLevel.Warning,
                    Kind = "入口文件冲突",
                    ModId = string.Join(" / ", list.Select(x => x.Id)),
                    ModName = string.Join(" / ", list.Select(ModNames.Display)),
                    Detail = $"这些 Mod 都用同一个 {g.Key} 作为入口文件，通常意味着同一个 Mod 装了两份，建议只保留一个。"
                });
            }

            var enabledIds = new HashSet<string>(
                mods.Where(m => !m.Disabled).Select(m => m.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var m in mods.Where(x => !x.Disabled))
            {
                // ---------- 3. 前置依赖 ----------
                foreach (var dep in m.Dependencies)
                {
                    if (string.IsNullOrEmpty(dep.UniqueId) || BuiltinIds.Contains(dep.UniqueId, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (!byId.TryGetValue(dep.UniqueId, out var target))
                        continue;    // 游戏本体 / SMAPI 内置的东西，不报

                    if (target.Disabled)
                    {
                        var t = target;
                        issues.Add(new ModIssue
                        {
                            Level = IssueLevel.Error,
                            Kind = "依赖被禁用",
                            ModId = m.Id,
                            ModName = ModNames.Display(m),
                            Detail = $"「{m.Name}」需要前置「{t.Name}」，但它现在是禁用状态，进游戏会报错。",
                            FixLabel = $"重新启用 {t.RootFolderName}",
                            Fix = () => ModControl.Enable(env, t)
                        });
                    }
                    else if (dep.Required && !string.IsNullOrEmpty(dep.MinVersion)
                             && SemVer.TryParse(target.Version, out var tv)
                             && SemVer.TryParse(dep.MinVersion, out var mv)
                             && tv.CompareTo(mv) < 0)
                    {
                        issues.Add(new ModIssue
                        {
                            Level = IssueLevel.Warning,
                            Kind = "依赖版本过低",
                            ModId = m.Id,
                            ModName = ModNames.Display(m),
                            Detail = $"「{m.Name}」要求前置「{target.Name}」至少 {dep.MinVersion}，当前是 {target.Version}，请先更新前置。"
                        });
                    }
                }

                // 缺失的必需前置：manifest 里写了，但目录里根本没有
                foreach (var dep in m.Dependencies.Where(d => d.Required))
                {
                    if (string.IsNullOrEmpty(dep.UniqueId) || BuiltinIds.Contains(dep.UniqueId, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (byId.ContainsKey(dep.UniqueId))
                        continue;

                    issues.Add(new ModIssue
                    {
                        Level = IssueLevel.Error,
                        Kind = "缺少前置 Mod",
                        ModId = m.Id,
                        ModName = ModNames.Display(m),
                        Detail = $"「{m.Name}」需要前置「{dep.UniqueId}」{VersionText(dep.MinVersion)}，但它没有安装。"
                    });
                }

                // ---------- 4. Content Pack 的目标框架 ----------
                if (!string.IsNullOrEmpty(m.ContentPackForId))
                {
                    if (!byId.TryGetValue(m.ContentPackForId, out var framework))
                    {
                        issues.Add(new ModIssue
                        {
                            Level = IssueLevel.Error,
                            Kind = "缺少内容框架",
                            ModId = m.Id,
                            ModName = ModNames.Display(m),
                            Detail = $"内容包「{m.Name}」需要框架「{m.ContentPackForId}」{VersionText(m.ContentPackForVersion)}，但它没有安装。"
                        });
                    }
                    else if (framework.Disabled)
                    {
                        var fw = framework;
                        issues.Add(new ModIssue
                        {
                            Level = IssueLevel.Error,
                            Kind = "依赖被禁用",
                            ModId = m.Id,
                            ModName = ModNames.Display(m),
                            Detail = $"内容包「{m.Name}」需要的框架「{fw.Name}」被禁用了。",
                            FixLabel = $"重新启用 {fw.RootFolderName}",
                            Fix = () => ModControl.Enable(env, fw)
                        });
                    }
                }

                // ---------- 5. SMAPI 版本要求 ----------
                if (!string.IsNullOrEmpty(m.MinimumApiVersion) && !string.IsNullOrEmpty(env.SmapiVersion)
                    && SemVer.TryParse(m.MinimumApiVersion, out var need)
                    && SemVer.TryParse(NormalizeSmapi(env.SmapiVersion), out var have)
                    && have.CompareTo(need) < 0)
                {
                    issues.Add(new ModIssue
                    {
                        Level = IssueLevel.Error,
                        Kind = "SMAPI 版本过低",
                        ModId = m.Id,
                        ModName = ModNames.Display(m),
                        Detail = $"「{m.Name}」要求 SMAPI ≥ {m.MinimumApiVersion}，当前是 {env.SmapiVersion}，请去 smapi.io 下载新版安装器更新。"
                    });
                }

                // ---------- 6. 更新源缺失（SMAPI 自带的 Mod 不提醒） ----------
                if (m.Keys.Count == 0 && !m.Id.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ModIssue
                    {
                        Level = IssueLevel.Info,
                        Kind = "无更新源",
                        ModId = m.Id,
                        ModName = ModNames.Display(m),
                        Detail = $"「{m.Name}」的 manifest 里没写 UpdateKeys，没法自动检查更新。在 Mod 管理页选中它，点「补写更新源」，填上它的 Nexus 编号或 GitHub 仓库就行。"
                    });
                }
            }

            // ---------- 7. 被依赖却处于禁用状态（反向检查，覆盖上面的漏网） ----------
            var disabledIds = new HashSet<string>(
                mods.Where(x => x.Disabled).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var m in mods.Where(x => x.Disabled))
            {
                bool needed = mods.Any(other => !other.Disabled && !string.Equals(other.Id, m.Id, StringComparison.OrdinalIgnoreCase)
                    && (other.Dependencies.Any(d => string.Equals(d.UniqueId, m.Id, StringComparison.OrdinalIgnoreCase))
                        || string.Equals(other.ContentPackForId, m.Id, StringComparison.OrdinalIgnoreCase)));
                if (!needed)
                    continue;
                if (issues.Any(i => i.Kind == "依赖被禁用" && string.Equals(i.ModId, m.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                issues.Add(new ModIssue
                {
                    Level = IssueLevel.Warning,
                    Kind = "被依赖的禁用项",
                    ModId = m.Id,
                    ModName = ModNames.Display(m),
                    Detail = $"「{m.Name}」目前是禁用状态，但有别的 Mod 需要它。"
                });
            }

            // ---------- 8. 互斥组里有多个还启用着 ----------
            // 互斥组（例如「马皮肤」）里同一时间只该有一个启用，否则它们会互改同一批文件。
            foreach (var g in mods.Where(m => !m.Disabled)
                                  .GroupBy(m => ModGroups.GroupOf(env, m), StringComparer.CurrentCultureIgnoreCase)
                                  .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Count() > 1))
            {
                var list = g.OrderBy(m => m.Name ?? m.Id, StringComparer.CurrentCultureIgnoreCase).ToList();
                var keep = list[0];
                var drop = list.Skip(1).ToList();

                var issue = new ModIssue
                {
                    Level = IssueLevel.Warning,
                    Kind = "互斥组冲突",
                    ModId = string.Join(" / ", list.Select(x => x.Id)),
                    ModName = $"互斥组「{g.Key}」",
                    Detail = $"互斥组「{g.Key}」里有 {list.Count} 个同时启用着："
                             + string.Join("、", list.Select(x => x.Name))
                             + "。它们通常改同一批游戏文件，同时启用会互相冲突，建议只留一个。",
                    FixLabel = $"只留「{keep.Name}」"
                };
                issue.Fix = () =>
                {
                    foreach (var d in drop)
                        ModControl.Disable(env, d, mods);
                };
                issues.Add(issue);
            }

            return issues
                .OrderBy(i => i.Level)
                .ThenBy(i => i.Kind)
                .ThenBy(i => i.ModName)
                .ToList();
        }

        public static string NormalizeSmapi(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                return v;
            v = v.Trim();
            int plus = v.IndexOf('+');
            if (plus > 0)
                v = v.Substring(0, plus);
            return v;
        }

        private static string VersionText(string v) => string.IsNullOrEmpty(v) ? "" : $"（≥ {v}）";

        /// <summary>删除 Mod 前先备份到备份区，再删除原目录。</summary>
        public static void MoveToTrash(AppEnv env, LocalMod mod, string reason)
        {
            if (mod == null || string.IsNullOrEmpty(mod.Dir) || !Directory.Exists(mod.Dir))
                return;

            if (env.Config.BackupBeforeDelete)
                Backups.Create(env, mod.Dir, mod.Id, mod.Name, mod.Version, Path.GetFileName(mod.Dir), reason);

            string del = mod.Dir;

            // 如果这个 Mod 独占一个顶层文件夹，就把整个顶层文件夹删掉（含它的资源文件）
            string rootDir = mod.Disabled ? env.DisabledDir : env.ModsPath;
            string top = TopFolderUnder(rootDir, mod.Dir);
            if (top != null && ModScanner.CountManifests(top) <= 1)
                del = top;

            Installer.ForceDelete(del);
        }

        /// <summary>找到 dir 在 root 下的一级子目录。</summary>
        private static string TopFolderUnder(string root, string dir)
        {
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                string r = Path.GetFullPath(root).TrimEnd('\\', '/');
                if (!full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return null;
                string rel = full.Substring(r.Length + 1);
                int slash = rel.IndexOf(Path.DirectorySeparatorChar);
                string top = slash >= 0 ? rel.Substring(0, slash) : rel;
                return string.IsNullOrEmpty(top) ? null : Path.Combine(r, top);
            }
            catch
            {
                return null;
            }
        }
    }
}
