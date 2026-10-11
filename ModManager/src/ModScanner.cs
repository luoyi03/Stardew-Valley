using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ModManager
{
    public sealed class LocalMod
    {
        public string Id;
        public string Name;
        public string Version;
        public string Dir;
        public string EntryDll;
        public string Author;
        public string Description;
        public bool IsContentPack;
        public bool Loaded;
        public string DisplayPath;
        public List<UpdateKey> Keys = new();

        /// <summary>SMAPI 本体之类的伪条目：没有目录，不能自动安装。</summary>
        public bool IsVirtual;
        public string ManualHint;
    }

    public static class ModScanner
    {
        private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

        /// <summary>扫描 Mods 目录，找到所有 manifest.json（最多下钻 4 层，跳过 . 开头的目录）。</summary>
        public static List<LocalMod> Scan(string modsPath)
        {
            var result = new List<LocalMod>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(modsPath) || !Directory.Exists(modsPath))
                return result;

            var queue = new Queue<(string dir, int depth)>();
            queue.Enqueue((modsPath, 0));

            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { continue; }

                foreach (string sub in subs)
                {
                    string folder = Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(folder) || folder.StartsWith("."))
                        continue;

                    string manifest = Path.Combine(sub, "manifest.json");
                    if (File.Exists(manifest))
                    {
                        var mod = TryRead(manifest, modsPath);
                        if (mod != null && seen.Add(mod.Id))
                            result.Add(mod);
                        continue;
                    }

                    if (depth < 3)
                        queue.Enqueue((sub, depth + 1));
                }
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        private static LocalMod TryRead(string path, string modsPath)
        {
            try
            {
                string text = File.ReadAllText(path);
                using var doc = JsonUtil.Parse(text);
                var root = doc.RootElement;

                string id = Get(root, "UniqueID") ?? Get(root, "UniqueId");
                if (string.IsNullOrWhiteSpace(id))
                    return null;

                string dir = Path.GetDirectoryName(path);
                string rel = dir;
                try
                {
                    rel = Path.GetRelativePath(modsPath, dir).Replace('\\', '/');
                }
                catch { /* 保留绝对路径 */ }

                var keys = new List<UpdateKey>();
                if (root.TryGetProperty("UpdateKeys", out var uk) && uk.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in uk.EnumerateArray())
                    {
                        if (e.ValueKind == JsonValueKind.String)
                        {
                            var k = UpdateKey.Parse(e.GetString());
                            if (k != null)
                                keys.Add(k);
                        }
                    }
                }

                return new LocalMod
                {
                    Id = id,
                    Name = Get(root, "Name") ?? id,
                    Version = Get(root, "Version") ?? "0.0.0",
                    Dir = dir,
                    EntryDll = Get(root, "EntryDll"),
                    Author = Get(root, "Author"),
                    Description = Get(root, "Description"),
                    IsContentPack = root.TryGetProperty("ContentPackFor", out _),
                    DisplayPath = rel,
                    Keys = keys
                };
            }
            catch
            {
                return null;
            }
        }

        private static string Get(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>读一个已解压更新包里的 manifest，用来确认新版本号 / 入口 DLL。</summary>
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
                version = Get(doc.RootElement, "Version");
                entryDll = Get(doc.RootElement, "EntryDll");
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
                return Get(doc.RootElement, "UniqueID");
            }
            catch
            {
                return null;
            }
        }
    }
}
