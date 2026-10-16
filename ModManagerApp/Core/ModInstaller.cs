using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMM
{
    public sealed class InstallProgress
    {
        public string Phase = "";
        public int Percent;
    }

    /// <summary>下载 → 解压 → 备份 → 覆盖安装 的完整流程。</summary>
    public static class ModInstaller
    {
        /// <summary>更新一个已知 Mod（plan 由更新检测得到）。</summary>
        public static async Task<string> UpdateAsync(AppEnv env, LocalMod mod, DownloadPlan plan,
            IProgress<InstallProgress> progress, CancellationToken ct)
        {
            if (plan == null)
                throw new Exception("这个来源没有可自动下载的文件，请用「打开网页」手动下载后导入");

            RequireGameClosed();

            string safe = Installer.Safe(mod.Id);
            string pkg = Path.Combine(env.TempDir, safe + ".pkg");
            string stage = Path.Combine(env.TempDir, "stage-" + safe);

            try
            {
                await DownloadAsync(env, plan, pkg, progress, ct).ConfigureAwait(false);

                progress?.Report(new InstallProgress { Phase = "正在解压…", Percent = 100 });
                Installer.ResetDir(stage);

                if (Installer.IsArchive(pkg))
                {
                    Installer.Extract(pkg, stage);
                    string root = PickRoot(stage, mod.Id);
                    if (root == null)
                        throw new Exception("更新包里没找到 manifest.json");
                    return Commit(env, root, mod, plan.FileName, progress);
                }

                // 裸 DLL：直接覆盖到原来的 Mod 目录
                string dllName = Path.GetFileName(plan.FileName ?? mod.EntryDll ?? "mod.dll");
                File.Copy(pkg, Path.Combine(stage, dllName), true);
                return Commit(env, stage, mod, dllName, progress);
            }
            finally
            {
                TryDelete(pkg);
                TryDelete(stage);
            }
        }

        /// <summary>从本地 zip / 已解压目录安装（拖进来那种）。</summary>
        public static async Task<string> ImportAsync(AppEnv env, string path, bool isFolder,
            IProgress<InstallProgress> progress, CancellationToken ct)
        {
            RequireGameClosed();
            progress?.Report(new InstallProgress { Phase = "正在读取安装包…", Percent = 0 });

            string stage = null;
            try
            {
                if (isFolder)
                {
                    return await Task.Run(() =>
                    {
                        string root = ModScanner.FindModRoot(path) ?? path;
                        var installed = ScanInstalled(env);
                        return CommitPackage(env, root, Path.GetFileName(path.TrimEnd('\\', '/')), installed, progress);
                    }, ct).ConfigureAwait(false);
                }

                if (!Installer.IsArchive(path))
                    throw new Exception("只支持 zip / rar / 7z 压缩包（也可以直接拖已解压的 Mod 文件夹）");

                stage = Path.Combine(env.TempDir, "import-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Installer.ResetDir(stage);
                Installer.Extract(path, stage);

                var list = ScanInstalled(env);
                return CommitPackage(env, stage, Path.GetFileNameWithoutExtension(path), list, progress);
            }
            finally
            {
                if (stage != null)
                    TryDelete(stage);
            }
        }

        /// <summary>从浏览页的条目直接安装（先解析出下载计划）。</summary>
        public static async Task<string> InstallBrowseAsync(AppEnv env, BrowseItem item,
            IProgress<InstallProgress> progress, CancellationToken ct)
        {
            progress?.Report(new InstallProgress { Phase = "正在解析下载地址…", Percent = 0 });

            DownloadPlan plan;
            if (item.Source == "GitHub")
            {
                var r = await GitHubSource.CheckAsync(
                    new UpdateKey { Site = "GitHub", Id = item.Id, Raw = "GitHub:" + item.Id },
                    env.Config.GitHubToken, env.Config.IncludePrerelease, item.InstalledVersion, null).ConfigureAwait(false);
                if (r == null || r.Error != null)
                    throw new Exception(r?.Error ?? "没有拿到 GitHub Release 信息");
                if (r.Plan == null)
                    throw new Exception("这个仓库的 Release 里没有可下载的 zip / dll，请到网页手动下载");
                plan = r.Plan;
            }
            else
            {
                var r = await NexusSource.CheckAsync(
                    new UpdateKey { Site = "Nexus", Id = item.Id, Raw = "Nexus:" + item.Id },
                    env.Config.NexusApiKey).ConfigureAwait(false);
                if (r == null || r.Error != null)
                    throw new Exception(r?.Error ?? "没有拿到 Nexus 文件信息");
                plan = r.Plan;
            }

            if (plan == null)
                throw new Exception("没有可自动下载的文件");

            string safe = Installer.Safe(item.Source + "-" + item.Id);
            string pkg = Path.Combine(env.TempDir, safe + ".pkg");
            string stage = Path.Combine(env.TempDir, "browse-" + safe);

            RequireGameClosed();
            try
            {
                await DownloadAsync(env, plan, pkg, progress, ct).ConfigureAwait(false);
                progress?.Report(new InstallProgress { Phase = "正在解压…", Percent = 100 });
                Installer.ResetDir(stage);

                if (!Installer.IsArchive(pkg))
                {
                    // 裸文件包：只能按 UniqueID 未知的方式落一个目录
                    string dll = Path.GetFileName(plan.FileName ?? "mod.dll");
                    File.Copy(pkg, Path.Combine(stage, dll), true);
                }
                else
                {
                    Installer.Extract(pkg, stage);
                }

                var installed = ScanInstalled(env);
                return CommitPackage(env, stage, item.Name, installed, progress);
            }
            finally
            {
                TryDelete(pkg);
                TryDelete(stage);
            }
        }

        /// <summary>从直链 URL 安装（用户自己贴链接）。</summary>
        public static async Task<string> InstallFromUrlAsync(AppEnv env, string url,
            IProgress<InstallProgress> progress, CancellationToken ct)
        {
            RequireGameClosed();
            string name = "download";
            try
            {
                string path = new Uri(url).AbsolutePath;
                name = Path.GetFileNameWithoutExtension(path);
            }
            catch { }

            string safe = Installer.Safe(name);
            string pkg = Path.Combine(env.TempDir, safe + ".pkg");
            string stage = Path.Combine(env.TempDir, "url-" + safe);

            try
            {
                var plan = new DownloadPlan { Kind = "url", Url = url, FileName = Path.GetFileName(url) };
                await DownloadAsync(env, plan, pkg, progress, ct).ConfigureAwait(false);
                progress?.Report(new InstallProgress { Phase = "正在解压…", Percent = 100 });

                Installer.ResetDir(stage);
                if (Installer.IsArchive(pkg))
                    Installer.Extract(pkg, stage);
                else
                    File.Copy(pkg, Path.Combine(stage, Path.GetFileName(url)), true);

                var installed = ScanInstalled(env);
                return CommitPackage(env, stage, name, installed, progress);
            }
            finally
            {
                TryDelete(pkg);
                TryDelete(stage);
            }
        }

        // ------------------------------------------------------------------
        private static async Task DownloadAsync(AppEnv env, DownloadPlan plan, string dest,
            IProgress<InstallProgress> progress, CancellationToken ct)
        {
            progress?.Report(new InstallProgress { Phase = "正在解析下载直链…", Percent = 0 });

            string url = plan.Url;
            if (plan.Kind == "nexus")
                url = await NexusSource.ResolveDownloadUrlAsync(plan.NexusModId, plan.NexusFileId, env.Config.NexusApiKey).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(url))
                throw new Exception("没有可用的下载地址");

            var inner = new Progress<int>(p => progress?.Report(new InstallProgress { Phase = "正在下载…", Percent = p }));
            await Http.DownloadAsync(url, dest, inner, ct).ConfigureAwait(false);
        }

        /// <summary>把一个已知 Mod 的新内容覆盖上去（含备份）。</summary>
        private static string Commit(AppEnv env, string root, LocalMod mod, string fileName, IProgress<InstallProgress> progress)
        {
            progress?.Report(new InstallProgress { Phase = "正在备份旧版本…", Percent = 100 });
            if (env.Config.BackupBeforeUpdate && Directory.Exists(mod.Dir))
                Backups.Create(env, mod.Dir, mod.Id, mod.Name, mod.Version, Path.GetFileName(mod.Dir), "更新前备份");

            progress?.Report(new InstallProgress { Phase = "正在写入文件…", Percent = 100 });
            Directory.CreateDirectory(mod.Dir);
            if (!Installer.Apply(root, mod.Dir, out string err))
                throw new Exception(err);

            ModScanner.TryReadVersion(mod.Dir, out string ver, out _);
            ModNamer.ReapplyAll(env);        // 新包的 manifest 是英文名，把配过的中文名写回去
            env.Log($"已更新 {mod.Name} → {ver ?? "?"}（{Path.GetFileName(mod.Dir)}）");
            return $"已更新「{mod.Name}」到 {ver ?? "新版本"}，重启游戏后生效";
        }

        /// <summary>处理一个包含（可能多个）Mod 的解压目录。</summary>
        private static string CommitPackage(AppEnv env, string stage, string fallbackName,
            List<LocalMod> installed, IProgress<InstallProgress> progress)
        {
            var roots = Installer.FindRoots(stage);
            if (roots.Count == 0)
                throw new Exception("压缩包里没有找到 manifest.json，看起来不是 SMAPI Mod 包");

            var done = new List<string>();
            var failed = new List<string>();

            foreach (string root in roots)
            {
                string manifestPath = Path.Combine(root, "manifest.json");
                string text;
                try { text = File.ReadAllText(manifestPath); }
                catch (Exception ex) { failed.Add(Path.GetFileName(root) + "：" + ex.Message); continue; }

                var parsed = ModScanner.ParseManifestSafe(text, out string err);
                if (parsed == null)
                {
                    failed.Add(Path.GetFileName(root) + "：" + err);
                    continue;
                }

                var existing = installed.FirstOrDefault(m =>
                    string.Equals(m.Id, parsed.Id, StringComparison.OrdinalIgnoreCase));

                string targetDir;
                string label;
                if (existing != null)
                {
                    targetDir = existing.Dir;
                    label = existing.Name;
                }
                else
                {
                    string folder = Path.GetFileName(root.TrimEnd('\\', '/'));
                    if (string.IsNullOrWhiteSpace(folder) || folder == Path.GetFileName(stage.TrimEnd('\\', '/')))
                        folder = fallbackName;
                    folder = Installer.Safe(folder);
                    targetDir = Path.Combine(env.ModsPath, folder);
                    label = parsed.Name;
                }

                try
                {
                    if (Directory.Exists(targetDir) && env.Config.BackupBeforeUpdate)
                    {
                        progress?.Report(new InstallProgress { Phase = $"正在备份 {label}…", Percent = 100 });
                        Backups.Create(env, targetDir, parsed.Id, parsed.Name,
                            ModScanner.UniqueIdOf(targetDir), Path.GetFileName(targetDir), "覆盖前备份");
                    }

                    string stageCopy = Path.Combine(env.TempDir, "copy-" + Installer.Safe(parsed.Id) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    Installer.ResetDir(stageCopy);
                    Installer.CopyTree(root, stageCopy);

                    progress?.Report(new InstallProgress { Phase = $"正在安装 {label}…", Percent = 100 });
                    Directory.CreateDirectory(targetDir);
                    if (!Installer.Apply(stageCopy, targetDir, out string applyErr))
                        throw new Exception(applyErr);

                    TryDelete(stageCopy);

                    ModScanner.TryReadVersion(targetDir, out string ver, out _);
                    done.Add($"{parsed.Name} {ver ?? ""}".Trim());
                }
                catch (Exception ex)
                {
                    failed.Add(label + "：" + ex.Message);
                }
            }

            ModNamer.ReapplyAll(env);        // 覆盖安装会把 manifest 里的中文名冲掉，装完写回来
            env.Log($"安装包处理完成：成功 {done.Count} 个，失败 {failed.Count} 个");

            if (done.Count == 0)
                throw new Exception(failed.Count > 0 ? string.Join("；", failed) : "没有安装任何内容");

            string msg = done.Count == 1
                ? $"已安装「{done[0]}」，重启游戏后生效"
                : $"已安装 {done.Count} 个 Mod：{string.Join("、", done)}";
            if (failed.Count > 0)
                msg += $"。有 {failed.Count} 个失败：" + string.Join("；", failed);
            return msg;
        }

        private static string PickRoot(string stage, string expectedId)
        {
            var roots = Installer.FindRoots(stage);
            foreach (string r in roots)
            {
                if (string.Equals(ModScanner.UniqueIdOf(r), expectedId, StringComparison.OrdinalIgnoreCase))
                    return r;
            }
            return roots.Count == 1 ? roots[0] : roots.FirstOrDefault();
        }

        private static List<LocalMod> ScanInstalled(AppEnv env)
        {
            try { return ModScanner.Scan(env.ModsPath, env.DisabledDir); }
            catch { return new List<LocalMod>(); }
        }

        private static void RequireGameClosed()
        {
            if (AppEnv.IsGameRunning())
                throw new Exception("游戏正在运行，请先关闭游戏再安装 / 更新 Mod");
        }

        private static void TryDelete(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;
            Installer.ForceDelete(dir);
        }
    }
}
