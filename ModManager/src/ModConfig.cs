using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace ModManager
{
    public class ModConfig
    {
        /// <summary>Nexus Mods 个人 API Key（免费申请），用于检测与下载 Nexus 上的 Mod。</summary>
        public string NexusApiKey { get; set; } = "";

        /// <summary>GitHub 个人令牌（可选），只用于提高 GitHub API 的限流额度。</summary>
        public string GitHubToken { get; set; } = "";

        /// <summary>打开管理界面的快捷键。</summary>
        public KeybindList OpenKey { get; set; } = KeybindList.ForSingle(SButton.F9);

        /// <summary>启动游戏时自动检查更新。</summary>
        public bool AutoCheckOnLaunch { get; set; } = true;

        /// <summary>自动检查的最小间隔（小时），避免频繁请求。</summary>
        public int AutoCheckIntervalHours { get; set; } = 12;

        /// <summary>更新前把旧版本整个目录备份到 Mods/.ModManager/backup。</summary>
        public bool BackupBeforeUpdate { get; set; } = true;

        /// <summary>是否把测试版（Beta）也算作可更新版本。</summary>
        public bool IncludePrerelease { get; set; } = false;

        /// <summary>你自己下载 Mod 压缩包的目录：里面的 zip / 已解压目录会被装进游戏。</summary>
        public string InboxFolder { get; set; } = "";

        /// <summary>启动游戏时自动把下载目录里的包装上。</summary>
        public bool AutoInstallInboxOnLaunch { get; set; } = false;

        /// <summary>在标题界面右下角显示「Mod 管理器」入口按钮。</summary>
        public bool ShowTitleButton { get; set; } = false;

        /// <summary>手动补写的更新源：ModID → 更新源（如 Nexus:3753），用于作者没写的 Mod。</summary>
        public Dictionary<string, string> ExtraUpdateKeys { get; set; } = new();

        /// <summary>不参与更新检查的 Mod ID。</summary>
        public List<string> IgnoredModIds { get; set; } = new();

        public void Reset()
        {
            NexusApiKey = "";
            GitHubToken = "";
            OpenKey = KeybindList.ForSingle(SButton.F9);
            AutoCheckOnLaunch = true;
            AutoCheckIntervalHours = 12;
            BackupBeforeUpdate = true;
            IncludePrerelease = false;
            ShowTitleButton = false;
        }
    }
}
