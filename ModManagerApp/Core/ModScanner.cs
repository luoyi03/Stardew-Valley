using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SMM
{
    /// <summary>扫描 Mods 目录 + 禁用目录，解析每个 manifest.json。</summary>
    public static class ModScanner
    {
        private static readonly string[] SkipFolders = { "bin", "obj", "__MACOSX", ".git", "source", "src" };

        public static List<LocalMod> Scan(string modsPath, string disabledPath)
        {
            var result = new List<LocalMod>();
            if (!string.IsNullOrEmpty(modsPath) && Directory.Exists(modsPath))
                ScanRoot(result, modsPath, modsPath, false);
            if (!string.IsNullOrEmpty(disabledPath) && Directory.Exists(disabledPath))
                ScanRoot(result, disabledPath, disabledPath, true);

            foreach (var g in result.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (g.Count() <= 1)
                    continue;
                foreach (var m in g)
                    m.IsDuplicate = true;
            }

            result.Sort((a, b) =>
            {
                int c = string.Compare(a.SortName, b.SortName, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.Compare(a.Dir, b.Dir, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>扫描一个根目录下的一层子目录（含后面 2 层兜底下钻）。</summary>
        private static void ScanRoot(List<LocalMod> result, string root, string baseForDisplay, bool disabled)
        {
            string[] subs;
            try { subs = Directory.GetDirectories(root); }
            catch { return; }

            foreach (string sub in subs)
            {
                string folder = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(folder))
                    continue;
                if (!disabled && folder.StartsWith("."))
                    continue;                                    // .ModManager 之类的数据目录
                if (SkipFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    continue;

                var found = new List<string>();
                CollectManifests(sub, 0, found);
                if (found.Count == 0)
                    continue;

                foreach (string manifest in found)
                {
                    string dir = Path.GetDirectoryName(manifest);
                    var mod = TryRead(manifest, dir, baseForDisplay, disabled, root);
                    if (mod != null)
                    {
                        mod.FolderName = folder;
                        result.Add(mod);
                    }
                }
            }
        }

        private static void CollectManifests(string dir, int depth, List<string> found)
        {
            string m = Path.Combine(dir, "manifest.json");
            if (File.Exists(m))
            {
                found.Add(m);
                return;                                          // 这一层就是 Mod，不再往下找
            }
            if (depth >= 2)
                return;
            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".") || SkipFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;
                    CollectManifests(sub, depth + 1, found);
                }
            }
            catch { }
        }

        private static LocalMod TryRead(string path, string dir, string baseForDisplay, bool disabled, string root)
        {
            try
            {
                var mod = ParseManifest(File.ReadAllText(path));
                if (mod == null)
                    return null;

                mod.Dir = dir;
                mod.Disabled = disabled;
                mod.DisplayPath = SafeRelative(baseForDisplay, dir);

                // 用于启停的顶层文件夹
                string rootDir;
                try { rootDir = IndexRelative(root, dir); }
                catch { rootDir = null; }
                if (rootDir != null && rootDir.Length > 0)
                {
                    int slash = rootDir.IndexOf('/');
                    rootDir = slash >= 0 ? rootDir.Substring(0, slash) : rootDir;
                }
                mod.RootFolderName = string.IsNullOrEmpty(rootDir) ? mod.FolderName : rootDir;
                return mod;
            }
            catch
            {
                return null;
            }
        }

        private static string SafeRelative(string baseDir, string dir)
        {
            try { return Path.GetRelativePath(baseDir, dir).Replace('\\', '/'); }
            catch { return dir; }
        }

        private static string IndexRelative(string baseDir, string dir)
        {
            try { return Path.GetRelativePath(baseDir, dir).Replace('\\', '/'); }
            catch { return null; }
        }

        /// <summary>解析 manifest 文本为 LocalMod（供扫描与 zip 导入共用）。</summary>
        public static LocalMod ParseManifest(string text)
        {
            using var doc = JsonUtil.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string id = root.Str("UniqueID") ?? root.Str("UniqueId");
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var mod = new LocalMod
            {
                Id = id,
                Name = root.Str("Name") ?? id,
                Version = root.Str("Version") ?? "0.0.0",
                Author = root.Str("Author"),
                Description = root.Str("Description"),
                EntryDll = root.Str("EntryDll"),
                MinimumApiVersion = root.Str("MinimumApiVersion")
            };

            if (root.TryGetProperty("ContentPackFor", out var cp) && cp.ValueKind == JsonValueKind.Object)
            {
                mod.ContentPackForId = cp.Str("UniqueID") ?? cp.Str("UniqueId");
                mod.ContentPackForVersion = cp.Str("MinimumVersion");
            }

            if (root.TryGetProperty("Dependencies", out var dep) && dep.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dep.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object)
                        continue;
                    string did = d.Str("UniqueID") ?? d.Str("UniqueId");
                    if (string.IsNullOrWhiteSpace(did))
                        continue;
                    mod.Dependencies.Add(new ModDependency
                    {
                        UniqueId = did,
                        Required = d.Flag("IsRequired"),
                        MinVersion = d.Str("MinimumVersion")
                    });
                }
            }

            foreach (string k in root.StrArray("UpdateKeys"))
            {
                var parsed = UpdateKey.Parse(k);
                if (parsed != null && !mod.Keys.Any(x => string.Equals(x.Raw, parsed.Raw, StringComparison.OrdinalIgnoreCase)))
                    mod.Keys.Add(parsed);
            }

            // 更新源补写：用户在「补写更新源」/设置页里填的优先，其次才是内置的知名 Mod 映射。
            // 放在扫描阶段做（而不是只在更新检测里），界面上的「来源」列和「问题检查」页
            // 在没点过检查更新时也能正确显示。
            string extra = null;
            var userKeys = AppEnv.Current?.Config?.ExtraUpdateKeys;
            if (userKeys != null && userKeys.TryGetValue(mod.Id, out string userValue)
                && !string.IsNullOrWhiteSpace(userValue))
            {
                extra = userValue;
            }
            else if (UpdateKey.Known.TryGetValue(mod.Id, out string known))
            {
                extra = known;
            }

            if (extra != null)
            {
                var parsed = UpdateKey.Parse(extra) ?? UpdateKey.FromInput(extra);
                if (parsed != null && !mod.Keys.Any(x => string.Equals(x.Raw, parsed.Raw, StringComparison.OrdinalIgnoreCase)))
                    mod.Keys.Insert(0, parsed);
            }

            return mod;
        }

        /// <summary>一套目录里的 manifest 解析（错误信息也返回）。</summary>
        public static LocalMod ParseManifestSafe(string text, out string error)
        {
            error = null;
            try
            {
                var m = ParseManifest(text);
                if (m == null)
                    error = "manifest.json 里没有 UniqueID";
                return m;
            }
            catch (Exception ex)
            {
                error = "manifest.json 解析失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>只读一个目录里的版本号 / 入口 DLL。</summary>
        public static bool TryReadVersion(string dir, out string version, out string entryDll)
        {
            version = null;
            entryDll = null;
            try
            {
                string p = Path.Combine(dir, "manifest.json");
                if (!File.Exists(p))
                    return false;
                using var doc = JsonUtil.Parse(File.ReadAllText(p));
                version = doc.RootElement.Str("Version");
                entryDll = doc.RootElement.Str("EntryDll");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static string UniqueIdOf(string dir)
        {
            try
            {
                string p = Path.Combine(dir, "manifest.json");
                if (!File.Exists(p))
                    return null;
                using var doc = JsonUtil.Parse(File.ReadAllText(p));
                return doc.RootElement.Str("UniqueID") ?? doc.RootElement.Str("UniqueId");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>统计一个目录树下有多少个 manifest.json（用来判断这个文件夹里住了几个 Mod）。</summary>
        public static int CountManifests(string dir, int maxDepth = 3)
        {
            int count = 0;
            var queue = new Queue<(string d, int depth)>();
            queue.Enqueue((dir, 0));
            while (queue.Count > 0)
            {
                var (d, depth) = queue.Dequeue();
                if (File.Exists(Path.Combine(d, "manifest.json")))
                {
                    count++;
                    continue;
                }
                if (depth >= maxDepth)
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
            return count;
        }

        /// <summary>在目录树里找 manifest.json，返回深度最浅的那个所在目录。</summary>
        public static string FindModRoot(string dir, int maxDepth = 3)
        {
            var found = new List<(string dir, int depth)>();
            var queue = new Queue<(string d, int depth)>();
            queue.Enqueue((dir, 0));
            while (queue.Count > 0)
            {
                var (d, depth) = queue.Dequeue();
                if (File.Exists(Path.Combine(d, "manifest.json")))
                {
                    found.Add((d, depth));
                    continue;
                }
                if (depth >= maxDepth)
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
            if (found.Count == 0)
                return null;
            found.Sort((a, b) => a.depth != b.depth ? a.depth.CompareTo(b.depth) : a.dir.Length.CompareTo(b.dir.Length));
            return found[0].dir;
        }
    }
}
