using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace ModManager
{
    public sealed class PendingItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
    }

    public static class Updater
    {
        private static string TempDir => ModEntry.Service.TempDir;
        private static string BackupDir => ModEntry.Service.BackupDir;
        private static string PendingFile => ModEntry.Service.PendingFile;

        public static async Task RunAsync(ModState s)
        {
            try
            {
                var plan = s.Plan;
                if (plan == null)
                    throw new Exception("这个来源没有可自动下载的文件");

                s.JobState = "downloading";
                s.Progress = 0;

                string url = plan.Kind == "nexus"
                    ? await NexusSource.ResolveDownloadUrlAsync(plan.NexusModId, plan.NexusFileId, ModEntry.Config.NexusApiKey)
                    : plan.Url;

                string safe = Safe(s.Mod.Id);
                string pkg = Path.Combine(TempDir, safe + ".pkg");
                string stage = Path.Combine(TempDir, "stage-" + safe);

                await Http.DownloadAsync(Http.Get(url), pkg, p =>
                {
                    s.Progress = p;
                });

                s.JobState = "installing";
                ResetDir(stage);

                string root;
                if (IsZip(pkg))
                {
                    Extract(pkg, stage);
                    root = FindRoot(stage, s.Mod.Id);
                }
                else
                {
                    Directory.CreateDirectory(stage);
                    string fname = plan.FileName;
                    if (string.IsNullOrEmpty(fname))
                        fname = s.Mod.EntryDll ?? "mod.dll";
                    File.Copy(pkg, Path.Combine(stage, Path.GetFileName(fname)), true);
                    root = stage;
                }

                if (ModEntry.Config.BackupBeforeUpdate)
                    Backup(s.Mod);

                if (TryApply(root, s.Mod.Dir, out _))
                    Finish(s, root);
                else
                {
                    AddPending(s.Mod.Id, s.Mod.Name, root, s.Mod.Dir);
                    s.JobState = "pending";
                    s.JobError = "文件被占用，已暂存，下次启动游戏时自动完成";
                }
            }
            catch (Exception ex)
            {
                s.JobState = "error";
                s.JobError = ex.Message;
                s.ManualDownload = ex.Message.Contains("手动下载") || ex.Message.Contains("Premium");
                if (s.ManualDownload && s.Plan?.Kind == "nexus")
                    s.PageUrl = NexusSource.FilesUrl(s.Plan.NexusModId);
                ModEntry.Log?.Log($"更新 {s.Mod.Name} 失败：{ex.Message}", LogLevel.Warn);
            }
        }

        private static void Finish(ModState s, string root)
        {
            if (ModScanner.TryReadVersion(root, out string ver, out _))
            {
                s.Mod.Version = ver ?? s.Mod.Version;
                s.LatestVersion = ver ?? s.LatestVersion;
            }
            s.Status = "latest";
            s.JobState = "done";
            s.Progress = 100;
            s.JobError = null;
            s.Message = "已更新，重启游戏后生效";
            try { Directory.Delete(Path.Combine(TempDir, "stage-" + Safe(s.Mod.Id)), true); } catch { }
        }

        // ---------------- 文件操作 ----------------
        public static string Safe(string id)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in id ?? "mod")
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
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

        public static void Extract(string zipPath, string dest)
        {
            string root = Path.GetFullPath(dest);
            using var fs = File.OpenRead(zipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, true);
            foreach (var e in zip.Entries)
            {
                string full = e.FullName;
                if (string.IsNullOrEmpty(full) || full.EndsWith("/"))
                    continue;
                if (full.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = full.Replace('\\', '/');
                string target = Path.GetFullPath(Path.Combine(root, rel));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target));
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

        public static string FindRoot(string stage, string expectedId)
        {
            var manifests = new List<string>();
            var queue = new Queue<(string dir, int depth)>();
            queue.Enqueue((stage, 0));

            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                string m = Path.Combine(dir, "manifest.json");
                if (File.Exists(m))
                    manifests.Add(m);
                if (depth >= 3)
                    continue;
                try
                {
                    foreach (var sub in Directory.GetDirectories(dir))
                        queue.Enqueue((sub, depth + 1));
                }
                catch { }
            }

            foreach (var m in manifests)
            {
                string id = ModScanner.UniqueIdOf(Path.GetDirectoryName(m));
                if (string.Equals(id, expectedId, StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(m);
            }
            if (manifests.Count == 1)
                return Path.GetDirectoryName(manifests[0]);

            return stage;
        }

        public static bool TryApply(string src, string dst, out string error)
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
                    if (d.RootElement.TryGetProperty("EntryDll", out var e) && e.ValueKind == JsonValueKind.String)
                        oldDll = e.GetString();
                }
            }
            catch { }

            try
            {
                CopyTree(src, dst);

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
            catch (IOException ex)
            {
                error = ex.Message;
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                CopyWithRetry(f, Path.Combine(dst, Path.GetFileName(f)));

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

        private static void Backup(LocalMod mod)
        {
            if (mod == null)
                return;
            BackupDirectory(mod.Dir, mod.Id, mod.Name);
        }

        public static void BackupDirectory(string srcDir, string id, string name = null)
        {
            if (string.IsNullOrEmpty(srcDir) || !Directory.Exists(srcDir))
                return;
            try
            {
                string baseDir = Path.Combine(BackupDir, Safe(id));
                Directory.CreateDirectory(baseDir);

                // 只保留最近 3 份
                var old = Directory.GetDirectories(baseDir).OrderBy(x => x).ToList();
                while (old.Count >= 3)
                {
                    try { Directory.Delete(old[0], true); } catch { }
                    old.RemoveAt(0);
                }

                string target = Path.Combine(baseDir, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                CopyTree(srcDir, target);
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log($"备份 {name ?? id} 失败：{ex.Message}", LogLevel.Warn);
            }
        }

        // ---------------- 待重启应用 ----------------
        private static List<PendingItem> LoadPending()
        {
            try
            {
                if (!File.Exists(PendingFile))
                    return new List<PendingItem>();
                string s = File.ReadAllText(PendingFile);
                return JsonSerializer.Deserialize<List<PendingItem>>(s) ?? new List<PendingItem>();
            }
            catch
            {
                return new List<PendingItem>();
            }
        }

        private static void SavePending(List<PendingItem> items)
        {
            try
            {
                File.WriteAllText(PendingFile, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static void AddPending(string id, string name, string source, string target)
        {
            var list = LoadPending();
            list.RemoveAll(x => x.Id == id);
            list.Add(new PendingItem { Id = id, Name = name, Source = source, Target = target });
            SavePending(list);
        }

        /// <summary>启动时把上次没替换成功的更新补上，返回成功条数。</summary>
        public static int ApplyPending()
        {
            var list = LoadPending();
            if (list.Count == 0)
                return 0;

            var left = new List<PendingItem>();
            int done = 0;
            foreach (var it in list)
            {
                if (!Directory.Exists(it.Source) || !Directory.Exists(it.Target))
                    continue;
                if (TryApply(it.Source, it.Target, out _))
                {
                    done++;
                    try { Directory.Delete(it.Source, true); } catch { }
                }
                else
                {
                    left.Add(it);
                }
            }
            SavePending(left);
            if (done > 0)
                ModEntry.Log?.Log($"ModManager：已在启动时应用 {done} 个待处理更新。", LogLevel.Info);
            return done;
        }

        public static int PendingCount => LoadPending().Count;
    }
}
