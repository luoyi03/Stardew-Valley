using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SMM
{
    /// <summary>本程序自己的配置，独立于游戏内那个 ModManager。</summary>
    public class AppConfig
    {
        /// <summary>游戏根目录（含 Stardew Valley.exe / Mods）。</summary>
        public string GamePath { get; set; } = "";

        /// <summary>Nexus Mods 个人 API Key（免费申请），用于检测与浏览 Nexus 上的 Mod。</summary>
        public string NexusApiKey { get; set; } = "";

        /// <summary>GitHub 个人令牌（可选），只用于提高 GitHub API 的限流额度。</summary>
        public string GitHubToken { get; set; } = "";

        /// <summary>更新前把旧版本整个目录备份。</summary>
        public bool BackupBeforeUpdate { get; set; } = true;

        /// <summary>删除 Mod 前也备份一份。</summary>
        public bool BackupBeforeDelete { get; set; } = true;

        /// <summary>每个 Mod 最多保留几份备份。</summary>
        public int BackupKeep { get; set; } = 3;

        /// <summary>是否把测试版（Beta）也算作可更新版本。</summary>
        public bool IncludePrerelease { get; set; } = false;

        /// <summary>启动时自动检查一次更新。</summary>
        public bool CheckOnStart { get; set; } = false;

        /// <summary>手动补写的更新源：ModID → 更新源（如 Nexus:3753），用于作者没写的 Mod。</summary>
        public Dictionary<string, string> ExtraUpdateKeys { get; set; } = new();

        /// <summary>不参与更新检查的 Mod ID。</summary>
        public List<string> IgnoredModIds { get; set; } = new();

        /// <summary>
        /// 用户自己维护的中文名：ModID → 中文名。
        /// 保存时会同时写进该 Mod 的 manifest.json 的 Name 字段，这样游戏里（SMAPI 控制台 /
        /// 游戏内 Mod 管理器）也显示中文名。Mod 更新覆盖 manifest 后会自动重新应用。
        /// </summary>
        public Dictionary<string, string> ChineseNames { get; set; } = new();

        /// <summary>第一次改中文名时记下的 manifest 原名（英文），用于「恢复原名」。</summary>
        public Dictionary<string, string> OriginalNames { get; set; } = new();

        /// <summary>
        /// 互斥组：ModID → 组名（例如「马皮肤」）。
        /// 同组里同一时间只允许一个处于启用状态：启用其中一个时，管理器会自动把同组其它停用。
        /// 典型用途是装了好几套马皮肤 / 狗皮肤 / 家具这类互相改同一批文件的内容包，避免冲突。
        /// </summary>
        public Dictionary<string, string> ExclusiveGroups { get; set; } = new();

        /// <summary>
        /// 被「禁用」的快捷键原来是什么：&lt;ModID&gt;|&lt;配置路径&gt; → 原按键。
        /// 禁用快捷键时把配置里的值写成 None，原值记在这里；点「启用」时再写回去。
        /// </summary>
        public Dictionary<string, string> HotkeyOriginal { get; set; } = new();

        /// <summary>
        /// 「打开网页」用哪个浏览器：auto（有 Edge 用 Edge）/ default（系统默认）/
        /// edge / chrome / custom（用 BrowserPath）。
        /// </summary>
        public string BrowserMode { get; set; } = Browser.Auto;

        /// <summary>BrowserMode = custom 时用的浏览器 exe 完整路径。</summary>
        public string BrowserPath { get; set; } = "";

        /// <summary>是否监控下载文件夹（程序运行期间发现新 zip 就处理）。</summary>
        public bool WatchFolderEnabled { get; set; } = true;

        /// <summary>
        /// 监控的下载文件夹。留空 = 自动（浏览器自己的下载目录 + 系统「下载」目录）。
        /// </summary>
        public string WatchFolder { get; set; } = "";

        /// <summary>
        /// 上次运行本程序的时间（UTC）。启动时用它补扫「上次关闭之后」新下载的 zip，
        /// 不用非得开着程序才能识别。null 表示还没记录过（第一次运行不补扫，避免把旧包全弹一遍）。
        /// </summary>
        public DateTime? LastRunUtc { get; set; }

        /// <summary>发现新压缩包时怎么处理：ask = 弹窗问（默认），auto = 直接装。</summary>
        public string WatchInstallMode { get; set; } = "ask";

        [JsonIgnore]
        public string FilePath { get; set; }

        [JsonIgnore]
        public static JsonSerializerOptions JsonOpts => new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppConfig Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts) ?? new AppConfig();
                    cfg.FilePath = path;
                    cfg.ExtraUpdateKeys ??= new Dictionary<string, string>();
                    cfg.IgnoredModIds ??= new List<string>();
                    cfg.ChineseNames ??= new Dictionary<string, string>();
                    cfg.OriginalNames ??= new Dictionary<string, string>();
                    cfg.ExclusiveGroups ??= new Dictionary<string, string>();
                    cfg.HotkeyOriginal ??= new Dictionary<string, string>();
                    return cfg;
                }
            }
            catch { }
            return new AppConfig { FilePath = path };
        }

        public void Save()
        {
            try
            {
                if (string.IsNullOrEmpty(FilePath))
                    return;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch { }
        }

        public void Reset()
        {
            NexusApiKey = "";
            GitHubToken = "";
            BackupBeforeUpdate = true;
            BackupBeforeDelete = true;
            BackupKeep = 3;
            IncludePrerelease = false;
            CheckOnStart = false;
            ExtraUpdateKeys = new Dictionary<string, string>();
            IgnoredModIds = new List<string>();
            ChineseNames = new Dictionary<string, string>();
            OriginalNames = new Dictionary<string, string>();
            ExclusiveGroups = new Dictionary<string, string>();
            HotkeyOriginal = new Dictionary<string, string>();
            BrowserMode = Browser.Auto;
            BrowserPath = "";
            WatchFolderEnabled = true;
            WatchFolder = "";
            WatchInstallMode = "ask";
        }
    }
}
