using System;
using System.Collections.Generic;
using System.IO;

namespace SMM
{
    // 注意：这些模型都会被 XAML 绑定，所以一律用「属性」而不是字段。
    // WPF 的绑定只认属性，写成 public string Name; 这种字段会静默失败（界面显示空白）。

    /// <summary>一次更新检测得到的「下载计划」。</summary>
    public sealed class DownloadPlan
    {
        /// <summary>"url" = 直接下载；"nexus" = 需要先用 API 换下载直链。</summary>
        public string Kind { get; set; }
        public string Url { get; set; }
        public string NexusModId { get; set; }
        public string NexusFileId { get; set; }
        public string FileName { get; set; }
    }

    public sealed class CheckResult
    {
        public string Version { get; set; }
        public string PageUrl { get; set; }
        public string SourceName { get; set; }
        public DownloadPlan Plan { get; set; }
        public string Error { get; set; }

        public bool HasVersion => !string.IsNullOrWhiteSpace(Version) && Error == null;

        public static CheckResult Fail(string error) => new() { Error = error };
    }

    public sealed class ModDependency
    {
        public string UniqueId { get; set; }
        public bool Required { get; set; }
        public string MinVersion { get; set; }
    }

    /// <summary>Mods 目录（或禁用目录）里扫出来的一个 Mod。</summary>
    public sealed class LocalMod
    {
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>manifest 里的原始名字（改过中文名时就是改之前的英文名）。</summary>
        public string OriginalName { get; set; }

        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public string Dir { get; set; }
        public string FolderName { get; set; }

        /// <summary>相对 Mods / 禁用目录的顶层文件夹名，启停时移动它。</summary>
        public string RootFolderName { get; set; }

        public string EntryDll { get; set; }
        public string MinimumApiVersion { get; set; }
        public string ContentPackForId { get; set; }
        public string ContentPackForVersion { get; set; }
        public List<ModDependency> Dependencies { get; set; } = new();
        public List<UpdateKey> Keys { get; set; } = new();

        /// <summary>被本管理器禁用（放在 .ModManagerApp\disabled 里）。</summary>
        public bool Disabled { get; set; }

        /// <summary>同一个 UniqueID 出现了多次（重复安装）。</summary>
        public bool IsDuplicate { get; set; }

        /// <summary>相对于 Mods 目录的显示路径。</summary>
        public string DisplayPath { get; set; }

        public string SortName => Name ?? Id ?? "";

        /// <summary>显示用名字：「中文名（English Name）」。</summary>
        public string DisplayName => ModNames.Display(this);

        public IEnumerable<UpdateKey> SupportedKeys()
        {
            foreach (var k in Keys)
                if (k.Supported)
                    yield return k;
        }
    }

    /// <summary>备份记录。</summary>
    public sealed class BackupEntry
    {
        public string UniqueId { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string FolderName { get; set; }
        public string Reason { get; set; }
        public DateTime Time { get; set; }

        /// <summary>备份所在目录。</summary>
        public string Path { get; set; }

        public long SizeBytes { get; set; }

        public bool Exists => Path != null && Directory.Exists(Path);

        public string TimeText => Time.ToString("yyyy-MM-dd HH:mm");
        public string SizeText => FormatSize(SizeBytes);
        public string Title => ModNames.Display(UniqueId, Name, null);

        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)
                return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1)
            {
                v /= 1024;
                i++;
            }
            return i == 0 ? $"{v:0} B" : $"{v:0.0} {units[i]}";
        }
    }

    public enum IssueLevel { Error, Warning, Info }

    public sealed class ModIssue
    {
        public IssueLevel Level { get; set; }
        public string Kind { get; set; }
        public string ModName { get; set; }
        public string ModId { get; set; }
        public string Detail { get; set; }

        /// <summary>可一键处理时按钮上的文字（null 表示只能手动处理）。</summary>
        public string FixLabel { get; set; }

        public Action Fix { get; set; }

        public string LevelText => Level switch
        {
            IssueLevel.Error => "错误",
            IssueLevel.Warning => "警告",
            _ => "提示"
        };
    }

    /// <summary>浏览页里的一条可下载条目。</summary>
    public sealed class BrowseItem
    {
        /// <summary>Nexus / GitHub</summary>
        public string Source { get; set; }

        /// <summary>Nexus mod id 或 GitHub 的 owner/repo。</summary>
        public string Id { get; set; }

        public string Name { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public string Summary { get; set; }
        public string PageUrl { get; set; }
        public string PictureUrl { get; set; }
        public long Downloads { get; set; }
        public int Endorsements { get; set; }
        public DateTime? Updated { get; set; }

        /// <summary>已装版本（null = 未安装）。</summary>
        public string InstalledVersion { get; set; }

        public bool Installed => InstalledVersion != null;

        public string InstallStateText => InstalledVersion == null ? "未安装" : "已装 v" + InstalledVersion;

        public string UpdatedText => Updated?.ToString("yyyy-MM-dd") ?? "";
        public string DownloadsText => Downloads <= 0 ? "" : FormatCount(Downloads) + " 次下载";

        /// <summary>名字兜底，避免出现整行空白。</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (Source + " " + Id) : Name;

        private static string FormatCount(long n)
            => n >= 1000000 ? $"{n / 1000000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}K" : n.ToString();
    }
}
