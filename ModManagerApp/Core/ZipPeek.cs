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
        /// <summary>这个 zip 的完整路径。</summary>
        public string Path { get; set; }

        /// <summary>包里有没有 manifest.json（没有 = 根本不是能直接安装的 Mod 包）。</summary>
        public bool HasManifest { get; set; }

        /// <summary>识别失败的原因；识别成功时为 null。</summary>
        public string Error { get; set; }

        /// <summary>是否识别成功。</summary>
        public bool Ok => Error == null;

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
        /// 永远返回结果对象、不返回 null：识别不了的原因放在 Error 里，界面才能告诉用户为什么。
        /// </summary>
        public static ZipPeekResult Read(string zipPath)
        {
            var result = new ZipPeekResult { Path = zipPath };
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);

                // 1. 找出所有「含 manifest.json 的目录」
                bool tooDeep = false;
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
                    {
                        tooDeep = true;      // 有 manifest，只是藏得太深 / 在被忽略的目录里
                        continue;
                    }
                    if (!dirs.ContainsKey(dir))
                        dirs[dir] = e;
                }

                if (dirs.Count == 0)
                {
                    if (tooDeep)
                    {
                        result.HasManifest = true;
                        result.Error = "manifest.json 藏得太深（超过 3 层目录），没法确定从哪一层装";
                    }
                    else
                    {
                        result.Error = "包里没有 manifest.json，不是能直接安装的 SMAPI Mod 包"
                                       + "（翻译包 / 补丁包这类要手动放进对应 Mod 目录）";
                    }
                    return result;
                }
                result.HasManifest = true;

                // 2. 去掉嵌套在别的 Mod 根里面的（例如 Mod/Sub/manifest.json）
                var roots = dirs.Keys
                    .Where(d => !dirs.Keys.Any(other => other.Length > 0 && d.Length > other.Length
                                                       && d.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(d => d.Count(c => c == '/'))
                    .ToList();

                // 3. 逐个读 manifest（限制条数，防超大包）
                var details = new List<(LocalMod mod, string dir)>();
                var errors = new List<string>();
                foreach (string dir in roots.Take(50))
                {
                    try
                    {
                        using var s = dirs[dir].Open();
                        using var r = new StreamReader(s, Encoding.UTF8, true);
                        var mod = ModScanner.ParseManifestSafe(r.ReadToEnd(), out string err);
                        if (mod != null)
                            details.Add((mod, dir));
                        else
                            errors.Add(Label(dir) + "：" + (err ?? "读不出来"));
                    }
                    catch (Exception ex)
                    {
                        errors.Add(Label(dir) + "：" + ex.Message);
                    }
                }

                if (details.Count == 0)
                {
                    result.Error = errors.Count > 0 ? string.Join("；", errors) : "manifest.json 读不出来";
                    return result;
                }

                var main = details.FirstOrDefault(d => string.IsNullOrWhiteSpace(d.mod.ContentPackForId));
                if (main.mod == null)
                    main = details[0];

                result.Id = main.mod.Id;
                result.Name = main.mod.Name;
                result.Version = main.mod.Version;
                result.Author = main.mod.Author;
                result.RootFolder = main.dir;
                result.ModCount = details.Count;
                foreach (var (m, _) in details)
                    result.All.Add((m.Id, m.Name, m.Version));

                // 包里个别 Mod 读不出来不影响整体识别（errors 只在全部失败时才用得上）
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "打不开这个压缩包：" + ex.Message;
                return result;
            }
        }

        private static string Label(string dir)
            => string.IsNullOrEmpty(dir) ? "(压缩包根目录)" : dir;

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
