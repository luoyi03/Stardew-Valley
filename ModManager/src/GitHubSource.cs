using System;
using System.Threading.Tasks;
using System.Text.Json;

namespace ModManager
{
    /// <summary>GitHub 更新源：读 releases 的 tag 当作版本号，挑 zip 资源作为下载包。</summary>
    internal static class GitHubSource
    {
        public static async Task<CheckResult> CheckAsync(UpdateKey key, string token, bool includePrerelease, string installedVersion, string entryDll)
        {
            string[] parts = key.Id.Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return null;

            var req = Http.Get($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases?per_page=10");
            req.Headers.Accept.Clear();
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());

            string body = await Http.GetStringAsync(req);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            bool installedIsPre = SemVer.TryParse(installedVersion, out var iv) && iv.IsPrerelease;

            string bestVersion = null;
            string pageUrl = $"https://github.com/{parts[0]}/{parts[1]}";
            string assetUrl = null;
            string assetName = null;

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
                    continue;
                bool pre = rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
                if (pre && !includePrerelease && !installedIsPre)
                    continue;

                string tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag))
                    continue;
                if (!SemVer.TryParse(tag, out var v))
                    continue;

                if (bestVersion == null || SemVer.IsNewer(tag, bestVersion))
                {
                    bestVersion = tag;
                    if (rel.TryGetProperty("html_url", out var h))
                        pageUrl = h.GetString();
                    PickAsset(rel, entryDll, out assetUrl, out assetName);
                }
            }

            if (bestVersion == null)
                return CheckResult.Fail("该仓库还没有带版本号的 Release");

            var plan = assetUrl == null ? null : new DownloadPlan
            {
                Kind = "url",
                Url = assetUrl,
                FileName = assetName
            };

            return new CheckResult
            {
                Version = bestVersion,
                PageUrl = pageUrl,
                SourceName = "GitHub",
                Plan = plan
            };
        }

        private static void PickAsset(JsonElement rel, string entryDll, out string url, out string name)
        {
            url = null;
            name = null;
            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return;

            string dllUrl = null, dllName = null;
            foreach (var a in assets.EnumerateArray())
            {
                string an = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string au = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(an) || string.IsNullOrEmpty(au))
                    continue;

                string lower = an.ToLowerInvariant();
                if (lower.EndsWith(".zip") && !lower.Contains("source") && !lower.Contains("src"))
                {
                    url = au;
                    name = an;
                    return;
                }
                if (lower.EndsWith(".dll") && dllUrl == null)
                {
                    dllUrl = au;
                    dllName = an;
                }
            }

            // 只有裸 DLL 的情况：仅在该 Mod 本来就是单 DLL + manifest 时才敢覆盖
            if (dllUrl != null && !string.IsNullOrEmpty(entryDll))
            {
                url = dllUrl;
                name = dllName;
            }
        }
    }
}
