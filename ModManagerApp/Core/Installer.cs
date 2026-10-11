using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace SMM
{
    /// <summary>文件层面的安装 / 解压 / 复制工具。</summary>
    public static class Installer
    {
        public static string Safe(string id)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in id ?? "mod")
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            string s = sb.ToString().Trim('.', ' ');
            return string.IsNullOrEmpty(s) ? "mod" : s;
        }

        public static void ResetDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { }
            Directory.CreateDirectory(dir);
        }

        public static bool IsZip(string file)
        {
            try
            {
                using var fs = File.OpenRead(file);
                return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
            }
            catch
            {
                return false;
            }
        }

        /// <summary>解压 zip（带 Zip Slip 防护，跳过 __MACOSX）。</summary>
        public static void Extract(string zipPath, string dest)
        {
            string root = Path.GetFullPath(dest);
            using var fs = File.OpenRead(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, true);
            foreach (var e in zip.Entries)
            {
                string full = e.FullName;
                if (string.IsNullOrEmpty(full) || full.EndsWith("/") || full.EndsWith("\\"))
                    continue;
                if (full.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = full.Replace('\\', '/');
                string target = Path.GetFullPath(Path.Combine(root, rel));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    continue;

                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                try
                {
                    e.ExtractToFile(target, true);
                }
                catch
                {
                    // 个别条目解压失败不阻断整体
                }
            }
        }

        /// <summary>在一个已解压的暂存目录里找出所有 Mod 根目录。</summary>
        public static List<string> FindRoots(string stage)
        {
            var all = new List<(string dir, int depth)>();
            var queue = new Queue<(string d, int depth)>();
            queue.Enqueue((stage, 0));
            while (queue.Count > 0)
            {
                var (d, depth) = queue.Dequeue();
                if (File.Exists(Path.Combine(d, "manifest.json")))
                {
                    all.Add((d, depth));
                    continue;
                }
                if (depth >= 3)
                    continue;
                try
                {
                    foreach (string sub in Directory.GetDirectories(d))
                    {
                        string n = Path.GetFileName(sub);
                        if (n.StartsWith(".") || n.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
                            continue;
                        queue.Enqueue((sub, depth + 1));
                    }
                }
                catch { }
            }

            all.Sort((a, b) => a.depth != b.depth ? a.depth.CompareTo(b.depth) : a.dir.Length.CompareTo(b.dir.Length));

            var roots = new List<string>();
            foreach (var (dir, _) in all)
            {
                if (roots.Any(r => IsInside(dir, r)))
                    continue;
                roots.Add(dir);
            }
            return roots;
        }

        private static bool IsInside(string dir, string ancestor)
        {
            string a = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string d = Path.GetFullPath(dir);
            return d.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyWithRetry(f, Path.Combine(dst, name));
            }

            foreach (string d in Directory.GetDirectories(src))
            {
                string name = Path.GetFileName(d);
                if (name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
                    continue;
                CopyTree(d, Path.Combine(dst, name));
            }
        }

        private static void CopyWithRetry(string src, string dst)
        {
            const int tries = 4;
            for (int i = 0; i < tries; i++)
            {
                try
                {
                    File.Copy(src, dst, true);
                    return;
                }
                catch (IOException) when (i < tries - 1) { System.Threading.Thread.Sleep(400); }
                catch (UnauthorizedAccessException) when (i < tries - 1) { System.Threading.Thread.Sleep(400); }
            }
        }

        /// <summary>把解压好的内容覆盖到目标 Mod 目录；返回失败原因。</summary>
        public static bool Apply(string src, string dst, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(dst) || !Directory.Exists(dst))
            {
                error = "目标目录不存在";
                return false;
            }

            string oldDll = null;
            try
            {
                string mp = Path.Combine(dst, "manifest.json");
                if (File.Exists(mp))
                {
                    using var d = JsonUtil.Parse(File.ReadAllText(mp));
                    oldDll = d.RootElement.Str("EntryDll");
                }
            }
            catch { }

            try
            {
                CopyTree(src, dst);

                // 新版本换了入口 DLL 时，清掉旧的 dll / pdb，避免 SMAPI 加载到两个
                if (!string.IsNullOrEmpty(oldDll) && !File.Exists(Path.Combine(src, oldDll)))
                {
                    string gone = Path.Combine(dst, oldDll);
                    if (File.Exists(gone))
                    {
                        try { File.Delete(gone); } catch { }
                    }
                    string pdb = Path.ChangeExtension(gone, ".pdb");
                    if (File.Exists(pdb))
                    {
                        try { File.Delete(pdb); } catch { }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                return false;
            }
        }

        public static string Describe(Exception ex)
        {
            string msg = ex.Message;
            if (ex is UnauthorizedAccessException
                || msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("正由另一进程使用", StringComparison.OrdinalIgnoreCase) >= 0)
                return "文件被占用，请先关闭游戏再试（" + msg + "）";
            return msg;
        }

        public static long DirSize(string dir)
        {
            long total = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
            }
            catch { }
            return total;
        }

        /// <summary>把一个目录删干净（先尝试直接删，失败则逐文件删）。</summary>
        public static void ForceDelete(string dir)
        {
            try { Directory.Delete(dir, true); return; } catch { }
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                }
                Directory.Delete(dir, true);
            }
            catch { }
        }
    }

    // ========================================================================
    //  备份 / 回滚
    // ========================================================================
    public static class Backups
    {
        private sealed class BackupMeta
        {
            public string UniqueId { get; set; }
            public string Name { get; set; }
            public string Version { get; set; }
            public string FolderName { get; set; }
            public string Reason { get; set; }
            public string SourceDir { get; set; }
            public DateTime Time { get; set; }
        }

        /// <summary>把某个 Mod 目录整份备份下来，返回备份条目。</summary>
        public static BackupEntry Create(AppEnv env, string srcDir, string uniqueId, string name, string version, string folderName, string reason)
        {
            if (string.IsNullOrEmpty(srcDir) || !Directory.Exists(srcDir))
                return null;

            string idPart = Installer.Safe(uniqueId ?? folderName ?? "mod");
            string baseDir = Path.Combine(env.BackupDir, idPart);
            Directory.CreateDirectory(baseDir);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string target = Path.Combine(baseDir, stamp);
            Installer.CopyTree(srcDir, target);

            var meta = new BackupMeta
            {
                UniqueId = uniqueId,
                Name = name,
                Version = version,
                FolderName = folderName,
                Reason = reason,
                SourceDir = srcDir,
                Time = DateTime.Now
            };
            try
            {
                File.WriteAllText(Path.Combine(baseDir, stamp + ".json"),
                    JsonSerializer.Serialize(meta, AppConfig.JsonOpts));
            }
            catch { }

            Prune(env, idPart);

            return new BackupEntry
            {
                UniqueId = uniqueId,
                Name = name,
                Version = version,
                FolderName = folderName,
                Reason = reason,
                Time = meta.Time,
                Path = target,
                SizeBytes = Installer.DirSize(target)
            };
        }

        private static void Prune(AppEnv env, string idPart)
        {
            int keep = Math.Max(1, env.Config.BackupKeep);
            try
            {
                string baseDir = Path.Combine(env.BackupDir, idPart);
                if (!Directory.Exists(baseDir))
                    return;
                var dirs = Directory.GetDirectories(baseDir)
                    .OrderByDescending(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                for (int i = keep; i < dirs.Count; i++)
                {
                    Installer.ForceDelete(dirs[i]);
                    try { File.Delete(Path.Combine(baseDir, Path.GetFileName(dirs[i]) + ".json")); } catch { }
                }
            }
            catch { }
        }

        /// <summary>列出所有备份（新→旧）。</summary>
        public static List<BackupEntry> List(AppEnv env)
        {
            var list = new List<BackupEntry>();
            try
            {
                if (!Directory.Exists(env.BackupDir))
                    return list;
                foreach (string modDir in Directory.GetDirectories(env.BackupDir))
                {
                    foreach (string snap in Directory.GetDirectories(modDir))
                    {
                        string name = Path.GetFileName(snap);
                        var entry = new BackupEntry
                        {
                            Path = snap,
                            Time = ParseTime(name),
                            FolderName = name
                        };

                        string metaPath = Path.Combine(modDir, name + ".json");
                        if (File.Exists(metaPath))
                        {
                            try
                            {
                                var meta = JsonSerializer.Deserialize<BackupMeta>(File.ReadAllText(metaPath), AppConfig.JsonOpts);
                                if (meta != null)
                                {
                                    entry.UniqueId = meta.UniqueId;
                                    entry.Name = meta.Name;
                                    entry.Version = meta.Version;
                                    entry.FolderName = meta.FolderName ?? name;
                                    entry.Reason = meta.Reason;
                                    if (meta.Time != default)
                                        entry.Time = meta.Time;
                                }
                            }
                            catch { }
                        }

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            var m = ModScanner.FindModRoot(snap, 2);
                            if (m != null)
                            {
                                ModScanner.TryReadVersion(m, out string ver, out _);
                                entry.Version = ver;
                                entry.Name = ModScanner.UniqueIdOf(m);
                            }
                        }
                        if (string.IsNullOrEmpty(entry.UniqueId))
                            entry.UniqueId = Path.GetFileName(modDir);

                        entry.SizeBytes = Installer.DirSize(snap);
                        list.Add(entry);
                    }
                }
            }
            catch { }
            return list.OrderByDescending(x => x.Time).ToList();
        }

        private static DateTime ParseTime(string stamp)
        {
            if (DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss-fff", null,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            return Directory.Exists(stamp) ? Directory.GetCreationTime(stamp) : DateTime.MinValue;
        }

        public static void Delete(BackupEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Path))
                return;
            Installer.ForceDelete(entry.Path);
            try
            {
                string modDir = Path.GetDirectoryName(entry.Path);
                string stamp = Path.GetFileName(entry.Path);
                File.Delete(Path.Combine(modDir, stamp + ".json"));
                if (!Directory.EnumerateFileSystemEntries(modDir).Any())
                    Directory.Delete(modDir);
            }
            catch { }
        }

        /// <summary>把备份还原回 Mods 目录。</summary>
        public static void Restore(AppEnv env, BackupEntry entry, List<LocalMod> installed, out string message)
        {
            message = null;
            if (entry == null || !entry.Exists)
                throw new Exception("备份文件已不存在");

            string root = ModScanner.FindModRoot(entry.Path, 2) ?? entry.Path;

            var existing = installed?.FirstOrDefault(m =>
                !string.IsNullOrEmpty(entry.UniqueId)
                && string.Equals(m.Id, entry.UniqueId, StringComparison.OrdinalIgnoreCase)
                && !m.Disabled);

            string target;
            if (existing != null)
                target = existing.Dir;
            else
            {
                string folder = string.IsNullOrEmpty(entry.FolderName) ? Installer.Safe(entry.UniqueId) : entry.FolderName;
                if (folder.StartsWith("disabled", StringComparison.OrdinalIgnoreCase) || folder.StartsWith("."))
                    folder = Installer.Safe(entry.UniqueId);
                target = Path.Combine(env.ModsPath, folder);
            }

            if (Directory.Exists(target) && env.Config.BackupBeforeUpdate)
                Create(env, target, entry.UniqueId, entry.Name, entry.Version, Path.GetFileName(target), "还原前备份");

            Directory.CreateDirectory(target);
            if (!Installer.Apply(root, target, out string err))
                throw new Exception(err);

            message = $"已还原 {entry.Title}（{entry.Version ?? "?"}）→ {Path.GetFileName(target)}";
        }
    }

    // ========================================================================
    //  启用 / 禁用
    // ========================================================================
    public static class ModControl
    {
        /// <summary>禁用：把顶层文件夹挪到 .ModManagerApp\disabled 下（同盘移动，瞬间完成）。</summary>
        public static string Disable(AppEnv env, LocalMod mod, List<LocalMod> all)
        {
            if (mod == null || mod.Disabled)
                return null;
            string folder = string.IsNullOrEmpty(mod.RootFolderName) ? mod.FolderName : mod.RootFolderName;
            string src = Path.Combine(env.ModsPath, folder);
            if (!Directory.Exists(src))
                src = mod.Dir;
            if (!Directory.Exists(src))
                throw new Exception("找不到 Mod 目录：" + src);

            Directory.CreateDirectory(env.DisabledDir);
            string dest = Path.Combine(env.DisabledDir, folder);
            if (Directory.Exists(dest))
                dest = Path.Combine(env.DisabledDir, folder + "_" + DateTime.Now.ToString("MMddHHmmss"));

            Directory.Move(src, dest);

            int affected = all?.Count(m => string.Equals(m.RootFolderName, folder, StringComparison.OrdinalIgnoreCase)
                                           && !m.Disabled) ?? 1;
            return affected > 1
                ? $"已禁用「{folder}」（该文件夹里共 {affected} 个 Mod 一起停用）"
                : $"已禁用「{folder}」";
        }

        /// <summary>启用：把文件夹挪回 Mods。</summary>
        public static string Enable(AppEnv env, LocalMod mod)
        {
            if (mod == null || !mod.Disabled)
                return null;
            string folder = string.IsNullOrEmpty(mod.RootFolderName) ? mod.FolderName : mod.RootFolderName;
            string src = Path.Combine(env.DisabledDir, folder);
            if (!Directory.Exists(src))
                src = mod.Dir;
            if (!Directory.Exists(src))
                throw new Exception("找不到已禁用的目录：" + src);

            string dest = Path.Combine(env.ModsPath, folder);
            if (Directory.Exists(dest))
                throw new Exception($"Mods 下已经有同名文件夹「{folder}」，请先重命名或删除其中一个");

            Directory.Move(src, dest);
            return $"已启用「{folder}」";
        }
    }
}
