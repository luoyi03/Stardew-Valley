using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace ModManager
{
    public enum InboxResult
    {
        Ok,
        Queued,   // 文件被占用，已排队到下次启动
        Failed
    }

    public sealed class InboxItem
    {
        public string Path;
        public string Display;
        public bool IsFolder;
        public string UniqueId;
        public string Name;
        public string Version;
        public string TargetDir;
        public bool Known;      // 能匹配到已安装的 Mod
        public string Error;
    }

    /// <summary>把「下载目录」里放好的 zip / 已解压 Mod 文件夹装进游戏。</summary>
    public static class InboxInstaller
    {
        public static List<InboxItem> Scan(string folder, List<LocalMod> installed)
        {
            var list = new List<InboxItem>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return list;

            var entries = new List<(string path, bool isFolder)>();
            try
            {
                foreach (string f in Directory.GetFiles(folder))
                {
                    if (f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                        entries.Add((f, false));
                }
                foreach (string d in Directory.GetDirectories(folder))
                {
                    string n = Path.GetFileName(d);
                    if (!n.StartsWith("_"))
                        entries.Add((d, true));
                }
            }
            catch
            {
                return list;
            }

            foreach (var (p, isFolder) in entries)
            {
                var item = new InboxItem
                {
                    Path = p,
                    Display = Path.GetFileName(p),
                    IsFolder = isFolder
                };

                if (isFolder && Directory.GetFiles(p).Length == 0 && Directory.GetDirectories(p).Length == 0)
                {
                    item.Error = "空目录";
                    list.Add(item);
                    continue;
                }

                ReadInfo(item);
                item.Known = false;

                if (item.UniqueId != null)
                {
                    var match = installed?.FirstOrDefault(m =>
                        string.Equals(m.Id, item.UniqueId, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        item.Known = true;
                        item.TargetDir = match.Dir;
                    }
                    else
                    {
                        item.TargetDir = Path.Combine(ModEntry.Service.ModsPath, Updater.Safe(item.UniqueId));
                    }
                }
                else
                {
                    item.TargetDir = Path.Combine(ModEntry.Service.ModsPath, Updater.Safe(Path.GetFileNameWithoutExtension(p)));
                }

                list.Add(item);
            }

            return list.OrderBy(x => x.Error != null).ThenBy(x => x.Display).ToList();
        }

        private static void ReadInfo(InboxItem item)
        {
            try
            {
                if (item.IsFolder)
                {
                    string m = FindManifestInDir(item.Path);
                    if (m == null)
                    {
                        item.Error = "目录里没找到 manifest.json";
                        return;
                    }
                    Parse(item, File.ReadAllText(m));
                    return;
                }

                if (item.Path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
                {
                    item.Error = "7z 需要你先解压，我只认 zip";
                    return;
                }

                using var fs = File.OpenRead(item.Path);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, true);
                var cand = zip.Entries
                    .Where(e => !e.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)
                                && e.FullName.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName.Count(c => c == '/' || c == '\\'))
                    .ThenBy(e => e.FullName.Length)
                    .FirstOrDefault();

                if (cand == null)
                {
                    item.Error = "压缩包里没找到 manifest.json，不是 SMAPI Mod 包";
                    return;
                }

                using var sr = new StreamReader(cand.Open());
                Parse(item, sr.ReadToEnd());
            }
            catch (Exception ex)
            {
                item.Error = ex.Message;
            }
        }

        private static string FindManifestInDir(string dir)
        {
            var queue = new Queue<(string d, int depth)>();
            queue.Enqueue((dir, 0));
            while (queue.Count > 0)
            {
                var (d, depth) = queue.Dequeue();
                string m = Path.Combine(d, "manifest.json");
                if (File.Exists(m))
                    return m;
                if (depth >= 3)
                    continue;
                try
                {
                    foreach (var sub in Directory.GetDirectories(d))
                        queue.Enqueue((sub, depth + 1));
                }
                catch { }
            }
            return null;
        }

        private static void Parse(InboxItem item, string json)
        {
            using var doc = JsonUtil.Parse(json);
            var root = doc.RootElement;
            item.UniqueId = Str(root, "UniqueID") ?? Str(root, "UniqueId");
            item.Name = Str(root, "Name") ?? item.UniqueId;
            item.Version = Str(root, "Version");
            if (item.UniqueId == null)
                item.Error = "manifest.json 里没有 UniqueID";
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static InboxResult InstallOne(InboxItem item, out string message)
        {
            message = null;
            if (item == null || item.Error != null)
            {
                message = item?.Error ?? "空条目";
                return InboxResult.Failed;
            }

            try
            {
                string stage = Path.Combine(ModEntry.Service.TempDir, "inbox-" + Updater.Safe(item.Display));
                string root;
                if (item.IsFolder)
                {
                    root = Updater.FindRoot(item.Path, item.UniqueId);
                    stage = null;
                }
                else
                {
                    Updater.ResetDir(stage);
                    Updater.Extract(item.Path, stage);
                    root = Updater.FindRoot(stage, item.UniqueId);
                }

                bool existed = Directory.Exists(item.TargetDir);
                if (existed && ModEntry.Config.BackupBeforeUpdate)
                    Updater.BackupDirectory(item.TargetDir, item.UniqueId ?? item.Display, item.Name);

                if (!existed)
                    Directory.CreateDirectory(item.TargetDir);

                if (Updater.TryApply(root, item.TargetDir, out string err))
                {
                    Archive(item);
                    ModEntry.Log?.Log($"已从下载目录安装：{item.Name ?? item.Display} → {item.TargetDir}", StardewModdingAPI.LogLevel.Info);
                    return InboxResult.Ok;
                }

                // 被占用：暂存，下次启动补装（root 必须留在临时目录里）
                if (stage == null)
                {
                    stage = Path.Combine(ModEntry.Service.TempDir, "inbox-copy-" + Updater.Safe(item.Display));
                    Updater.ResetDir(stage);
                    CopyDir(root, stage);
                    root = stage;
                }
                Updater.AddPending(item.UniqueId ?? item.Display, item.Name ?? item.Display, root, item.TargetDir);
                Archive(item);
                message = "已排队，下次启动时完成";
                return InboxResult.Queued;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return InboxResult.Failed;
            }
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        /// <summary>装好的包挪到下载目录下的 _已安装 子目录，避免重复安装。</summary>
        private static void Archive(InboxItem item)
        {
            try
            {
                string folder = Path.GetDirectoryName(item.Path);
                string destDir = Path.Combine(folder, "_已安装");
                Directory.CreateDirectory(destDir);
                string dest = Path.Combine(destDir, item.Display);
                if (File.Exists(dest) || Directory.Exists(dest))
                    dest = Path.Combine(destDir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + item.Display);

                if (item.IsFolder)
                    Directory.Move(item.Path, dest);
                else
                    File.Move(item.Path, dest);
            }
            catch { }
        }

        public static (int ok, int queued, List<string> errors) InstallAll(List<InboxItem> items)
        {
            int ok = 0, queued = 0;
            var errors = new List<string>();
            foreach (var it in items)
            {
                var r = InboxInstaller.InstallOne(it, out string msg);
                if (r == InboxResult.Ok) ok++;
                else if (r == InboxResult.Queued) queued++;
                else errors.Add($"{it.Display}：{msg}");
            }
            return (ok, queued, errors);
        }
    }
}
