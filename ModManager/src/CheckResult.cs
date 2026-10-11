using System;

namespace ModManager
{
    /// <summary>一次更新检测的结果。</summary>
    public sealed class DownloadPlan
    {
        /// <summary>"url" = 直接下载；"nexus" = 需要先用 API 换下载直链。</summary>
        public string Kind;
        public string Url;
        public string NexusModId;
        public string NexusFileId;
        public string FileName;
    }

    public sealed class CheckResult
    {
        public string Version;
        public string PageUrl;
        public string SourceName;
        public DownloadPlan Plan;
        public string Error;

        public bool HasVersion => !string.IsNullOrWhiteSpace(Version) && Error == null;

        public static CheckResult Fail(string error) => new() { Error = error };
    }
}
