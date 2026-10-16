using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SMM
{
    /// <summary>翻译包里的一批文件要装给哪个 Mod。</summary>
    public sealed class TranslationTarget
    {
        /// <summary>目标 Mod 目录（Mods 下的绝对路径，也可能是已禁用的目录）。</summary>
        public string ModDir { get; set; }

        /// <summary>目标 Mod 的显示名（manifest 里的名字，取不到就用目录名）。</summary>
        public string ModName { get; set; }

        /// <summary>包里的锚点文件夹名（识别依据，用于提示）。</summary>
        public string Anchor { get; set; }

        /// <summary>要写入的文件：源相对路径 → 目标 Mod 内的相对路径（如 i18n/zh.json）。</summary>
        public List<(string SrcRel, string DstRel)> Files { get; set; } = new();
    }

    /// <summary>一个翻译包的识别结果：可能有多个目标 Mod（例如 SVE 的中文包同时覆盖 CP 包和代码包）。</summary>
    public sealed class TranslationHit
    {
        public List<TranslationTarget> Targets { get; set; } = new();

        /// <summary>包里还有这些锚点没能对上已装 Mod（只提示，不安装）。</summary>
        public List<string> Unmatched { get; set; } = new();

        public int FileCount => Targets.Sum(t => t.Files.Count);

        /// <summary>「Mod 名、Mod 名」。</summary>
        public string TargetNames => string.Join("、", Targets.Select(t => t.ModName).Distinct());

        /// <summary>所有会写入的相对路径（去重）。</summary>
        public List<string> DestinationFiles => Targets.SelectMany(t => t.Files.Select(f => f.DstRel))
                                                      .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 翻译包（Chinese / CHS / 汉化包这类）的识别与安装。
    /// 这类包没有 manifest.json，只有 i18n/zh.json 之类的文件，正常安装流程装不了，
    /// 但可以定位到已装 Mod 的目录，把翻译文件合进去。
    ///
    /// 三种形态：
    ///   1. 带文件夹：ChestsAnywhere/i18n/zh.json
    ///   2. 发行包名套子 Mod：Stardew Valley Expanded/[CP] Stardew Valley Expanded/i18n/zh.json（可同时多个）
    ///   3. 裸文件：zh.json / es.json / default.json（目标只能从包名推断，如 "Farm Type Manager (FTM)-CHS-…"）
    /// </summary>
    public static class TranslationPack
    {
        /// <summary>能被当成本地化文件的名字：zh / zh-CN / default / es …</summary>
        private static readonly HashSet<string> Locales = new(StringComparer.OrdinalIgnoreCase)
        {
            "default", "zh", "zh-cn", "zh-tw", "ja", "ko", "ru", "de", "es", "fr", "it",
            "hu", "pt", "pt-br", "tr", "uk", "pl", "th", "vi", "nl", "id", "cs"
        };

        /// <summary>
        /// 从包内文件清单里识别是不是翻译包。
        /// relPaths：包内的相对路径（zip 条目名，或解压后的目录树）。
        /// 返回 null 表示不是能识别的翻译包；note 里带原因（给界面显示用）。
        /// </summary>
        public static TranslationHit Detect(IEnumerable<string> relPaths, string archiveName, AppEnv env, out string note)
        {
            note = null;
            var groups = new List<(string Label, List<string> Keys, List<(string SrcRel, string DstRel)> Files)>();

            foreach (string raw in relPaths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string rel = raw.Replace('\\', '/').TrimStart('/');
                if (rel.Length == 0 || rel.EndsWith("/"))
                    continue;
                if (rel.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = rel.Substring(rel.LastIndexOf('/') + 1);
                if (name.StartsWith("._", StringComparison.Ordinal))
                    continue;
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Locales.Contains(Path.GetFileNameWithoutExtension(name)))
                    continue;

                var parts = rel.Split('/');
                int i18nAt = Array.FindIndex(parts, p => p.Equals("i18n", StringComparison.OrdinalIgnoreCase));

                string label;
                string dst;
                if (i18nAt > 0)
                {
                    label = parts[i18nAt - 1];                       // 形态 1 / 2：i18n 的上一层就是目标 Mod
                    dst = string.Join("/", parts.Skip(i18nAt));
                }
                else if (i18nAt < 0 && parts.Length == 1)
                {
                    label = "";                                      // 形态 3：裸文件，目标从包名推断
                    dst = "i18n/" + name;
                }
                else if (i18nAt == 0)
                {
                    label = "";
                    dst = rel;                                       // i18n/zh.json（没有 Mod 文件夹）
                }
                else
                {
                    continue;                                        // 其它位置的 json 不当翻译
                }

                var group = groups.FirstOrDefault(g => string.Equals(g.Label, label, StringComparison.OrdinalIgnoreCase));
                if (group.Files == null)
                {
                    group = (label, new List<string>(), new List<(string, string)>());
                    groups.Add(group);
                }
                group.Files.Add((rel, dst));
            }

            if (groups.Count == 0)
            {
                note = "包里没有 manifest.json，也没找到本地化文件（i18n/zh.json 或裸的 zh.json / default.json）";
                return null;
            }

            var mods = SafeScan(env);
            var hit = new TranslationHit();

            foreach (var (label, _, files) in groups)
            {
                // 锚点：有文件夹就用文件夹名；裸文件用包名（去掉版本号 / CHS / Chinese 这类词）
                string key;
                string shown;
                if (!string.IsNullOrWhiteSpace(label))
                {
                    key = Alnum(label);
                    shown = label;
                }
                else
                {
                    string cleaned = CleanArchiveName(archiveName);
                    key = Alnum(cleaned);
                    shown = string.IsNullOrWhiteSpace(cleaned) ? (archiveName ?? "") : cleaned;
                }

                var mod = Match(mods, key, out bool ambiguous);
                if (mod == null)
                {
                    hit.Unmatched.Add(shown + (ambiguous ? "（有多个候选）" : "（Mods 里没有对应的 Mod）"));
                    continue;
                }

                hit.Targets.Add(new TranslationTarget
                {
                    ModDir = mod.Dir,
                    ModName = string.IsNullOrWhiteSpace(mod.Name) ? Path.GetFileName(mod.Dir) : mod.Name,
                    Anchor = shown,
                    Files = files
                });
            }

            if (hit.Targets.Count == 0)
            {
                note = "看起来是翻译包（" + string.Join("、", groups[0].Files.Take(3).Select(f => f.DstRel)) + "），"
                       + "但没能对上已装的 Mod：" + string.Join("、", hit.Unmatched);
                return null;
            }

            return hit;
        }

        /// <summary>按解压好的暂存目录识别（rar / 7z 用）。</summary>
        public static TranslationHit DetectFromDir(string dir, string archiveName, AppEnv env, out string note)
        {
            var list = new List<string>();
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    list.Add(Path.GetRelativePath(dir, f));
            }
            catch { }
            return Detect(list, archiveName, env, out note);
        }

        /// <summary>
        /// 把翻译文件合进目标 Mod（已存在的同名文件先备份到
        /// &lt;数据目录&gt;\i18n-backup\&lt;Mod 目录名&gt;\&lt;时间戳&gt;\…）。
        /// 返回一句给用户看的结果。
        /// </summary>
        public static string Apply(AppEnv env, string stageDir, TranslationHit hit)
        {
            if (hit == null || hit.Targets.Count == 0)
                throw new Exception("没有识别到翻译包");

            var parts = new List<string>();
            int totalWritten = 0;
            int totalBackedUp = 0;
            string backupRoot = null;

            foreach (var target in hit.Targets)
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string root = Path.GetFullPath(target.ModDir);
                string targetBackup = Path.Combine(env.DataDir, "i18n-backup",
                    Installer.Safe(Path.GetFileName(root.TrimEnd('\\', '/'))), stamp);
                backupRoot ??= Path.Combine(env.DataDir, "i18n-backup");

                int written = 0;
                int backedUp = 0;
                var done = new List<string>();

                foreach (var (srcRel, dstRel) in target.Files)
                {
                    if (string.IsNullOrWhiteSpace(dstRel) || dstRel.Contains(".."))
                        continue;

                    string src = Path.Combine(stageDir, srcRel.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(src))
                        continue;

                    // 只允许写进目标 Mod 目录内部
                    string dst = Path.GetFullPath(Path.Combine(root, dstRel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dst.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (File.Exists(dst))
                        {
                            string bak = Path.Combine(targetBackup, dstRel.Replace('/', Path.DirectorySeparatorChar));
                            Directory.CreateDirectory(Path.GetDirectoryName(bak));
                            File.Copy(dst, bak, true);
                            backedUp++;
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(src, dst, true);
                        written++;
                        done.Add(dstRel);
                    }
                    catch (Exception ex)
                    {
                        env.Log($"安装翻译失败（{dstRel} → {target.ModDir}）：{ex.Message}");
                    }
                }

                if (written == 0)
                    continue;

                totalWritten += written;
                totalBackedUp += backedUp;
                env.Log($"已为 {target.ModName} 安装翻译 {string.Join("、", done)}（{target.ModDir}）"
                        + (backedUp > 0 ? $"，{backedUp} 个旧文件已备份到 {targetBackup}" : ""));
                parts.Add($"「{target.ModName}」" + string.Join("、", done));
            }

            if (totalWritten == 0)
                throw new Exception("翻译文件没能写进任何 Mod 目录，请看日志");

            string msg = "已安装翻译：" + string.Join("；", parts)
                         + (totalBackedUp > 0 ? "（原文件已备份）" : "")
                         + "，重启游戏后生效";
            if (hit.Unmatched.Count > 0)
                msg += "。另有没能对应的：" + string.Join("、", hit.Unmatched);
            return msg;
        }

        // ------------------------------------------------------------------
        private static List<LocalMod> SafeScan(AppEnv env)
        {
            try { return ModScanner.Scan(env.ModsPath, env.DisabledDir); }
            catch { return new List<LocalMod>(); }
        }

        /// <summary>在已装 Mod 里找和 key 对得上的那个：先全等，再前缀（任一方向），都要够长。</summary>
        private static LocalMod Match(List<LocalMod> mods, string key, out bool ambiguous)
        {
            ambiguous = false;
            if (string.IsNullOrWhiteSpace(key))
                return null;

            var scored = new List<(LocalMod mod, int score)>();
            foreach (var m in mods)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Dir))
                    continue;

                int best = 0;
                foreach (string candidate in new[] { Path.GetFileName(m.Dir.TrimEnd('\\', '/')), m.Name })
                {
                    string k = Alnum(candidate);
                    if (k.Length < 4)
                        continue;

                    if (string.Equals(k, key, StringComparison.Ordinal))
                        best = Math.Max(best, 1000 + k.Length);
                    else if (key.StartsWith(k, StringComparison.Ordinal) || k.StartsWith(key, StringComparison.Ordinal))
                        best = Math.Max(best, Math.Min(k.Length, key.Length));
                }

                if (best >= 4)
                    scored.Add((m, best));
            }

            if (scored.Count == 0)
                return null;

            scored.Sort((a, b) => b.score.CompareTo(a.score));
            if (scored.Count > 1 && scored[0].score == scored[1].score)
            {
                ambiguous = true;
                return null;
            }
            return scored[0].mod;
        }

        /// <summary>把包名里的版本号、Nexus 编号、Chinese / CHS / 汉化 这些修饰去掉。</summary>
        private static string CleanArchiveName(string archiveName)
        {
            if (string.IsNullOrWhiteSpace(archiveName))
                return null;

            string s = Path.GetFileNameWithoutExtension(archiveName);
            s = s.Replace('-', ' ').Replace('_', ' ');

            var sb = new StringBuilder();
            foreach (string word in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string w = word.Trim('(', ')', '[', ']', '.');
                if (w.Length == 0)
                    continue;
                if (w.All(char.IsDigit))                              // 纯数字 = 版本 / 编号
                    continue;
                if (w.StartsWith("v") && w.Length > 1 && char.IsDigit(w[1]) && w.Skip(1).All(char.IsDigit))
                    continue;                                         // v1.2.3
                if (w.All(c => char.IsDigit(c) || c == '.'))           // 1.2.3
                    continue;
                string lower = w.ToLowerInvariant();
                if (lower is "chinese" or "chs" or "cn" or "zh" or "translation" or "translations"
                    or "spanish" or "new" or "supplement" or "compatible" or "汉化" or "中文")
                    continue;
                if (lower.StartsWith("http"))
                    continue;
                sb.Append(w).Append(' ');
            }
            return sb.ToString().Trim();
        }

        /// <summary>只留字母数字（小写），用于跨命名风格比对：
        /// 「[CP] Stardew Valley Expanded」与「_CP__Stardew_Valley_Expanded」都会归一成 cpstardewvalleyexpanded。</summary>
        private static string Alnum(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c))
                    sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
