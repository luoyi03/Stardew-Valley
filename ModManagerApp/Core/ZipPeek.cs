using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace SMM
{
    /// <summary>不解压就看一眼压缩包里是什么 Mod（用于下载文件夹监控）。</summary>
    public sealed class ZipPeekResult
    {
        /// <summary>包里第一个「正经 Mod」的 UniqueID（优先非内容包）。</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }

        /// <summary>包里的顶层文件夹名（用来找落点）。</summary>
        public string RootFolder { get; set; }

        /// <summary>包里包含几个 Mod（含内容包）。</summary>
        public int ModCount { get; set; }

        /// <summary>包里所有 Mod 的 UniqueID + 版本。</summary>
        public List<(string Id, string Name, string Version)> All { get; set; } = new();

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (Id ?? "(未知)") : Name;

        public string VersionText => string.IsNullOrWhiteSpace(Version) ? "?" : Version;
    }

    internal static class ZipPeek
    {
        /// <summary>
        /// 从 zip 里读出 manifest.json（只读不落地）。规则和 Installer.FindRoots 保持一致：
        /// 深度不超过 3 层，跳过点开头和 __MACOSX 的目录，找到 manifest.json 的目录算一个 Mod 根。
        /// 读不出来（不是 SMAPI Mod 包 / 损坏）返回 null。
        /// </summary>
        public static ZipPeekResult Read(string zipPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);

                // 1. 找出所有「含 manifest.json 的目录」
                var dirs = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries)
                {
                    string full = e.FullName.Replace('\\', '/').TrimStart('/');
                    if (full.Length == 0 || full.EndsWith("/"))
                        continue;
                    if (!e.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string dir = full.Contains('/') ? full.Substring(0, full.LastIndexOf('/')) : "";
                    if (dir.Length > 0 && !IsUsableDir(dir))
                        continue;
                    if (!dirs.ContainsKey(dir))
                        dirs[dir] = e;
                }
                if (dirs.Count == 0)
                    return null;

                // 2. 去掉嵌套在别的 Mod 根里面的（例如 Mod/Sub/manifest.json）
                var roots = dirs.Keys
                    .Where(d => !dirs.Keys.Any(other => other.Length > 0 && d.Length > other.Length
                                                       && d.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(d => d.Count(c => c == '/'))
                    .ToList();

                // 3. 逐个读 manifest（限制条数，防超大包）
                var details = new List<(LocalMod mod, string dir)>();
                foreach (string dir in roots.Take(50))
                {
                    try
                    {
                        using var s = dirs[dir].Open();
                        using var r = new StreamReader(s, Encoding.UTF8, true);
                        var mod = ModScanner.ParseManifestSafe(r.ReadToEnd(), out _);
                        if (mod != null)
                            details.Add((mod, dir));
                    }
                    catch { }
                }

                if (details.Count == 0)
                    return null;

                var main = details.FirstOrDefault(d => string.IsNullOrWhiteSpace(d.mod.ContentPackForId));
                if (main.mod == null)
                    main = details[0];

                var result = new ZipPeekResult
                {
                    Id = main.mod.Id,
                    Name = main.mod.Name,
                    Version = main.mod.Version,
                    Author = main.mod.Author,
                    RootFolder = main.dir,
                    ModCount = details.Count
                };
                foreach (var (m, _) in details)
                    result.All.Add((m.Id, m.Name, m.Version));
                return result;
            }
            catch
            {
                return null;   // 打不开 / 不是 zip
            }
        }

        private static bool IsUsableDir(string dir)
        {
            var parts = dir.Split('/');
            if (parts.Length > 4)          // 根 + 最多 3 层
                return false;
            foreach (string p in parts)
            {
                if (p.Length == 0)
                    continue;
                if (p.StartsWith(".") || p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
    }
}
