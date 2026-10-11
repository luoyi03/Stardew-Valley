using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

namespace ModManager
{
    /// <summary>Nexus Mods 更新源：需要用户自己的免费 API Key。</summary>
    internal static class NexusSource
    {
        private const string Game = "stardewvalley";
        private const string Base = "https://api.nexusmods.com/v1";

        public const string KeyPageUrl = "https://next.nexusmods.com/settings/api-keys";

        private static HttpRequestMessage Req(string url, string apiKey)
        {
            var req = Http.Get(url);
            req.Headers.TryAddWithoutValidation("apikey", apiKey.Trim());
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("Application-Name", "StardewModManager");
            req.Headers.TryAddWithoutValidation("Application-Version", "1.0.0");
            return req;
        }

        public static async Task<CheckResult> CheckAsync(UpdateKey key, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return CheckResult.Fail("未填写 Nexus API Key，无法检查");
            if (!int.TryParse(key.Id, out int modId))
                return null;

            string url = $"{Base}/games/{Game}/mods/{modId}/files.json";
            string body = await Http.GetStringAsync(Req(url, apiKey));
            using var doc = JsonDocument.Parse(body);

            JsonElement arr;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                arr = doc.RootElement;
            else if (doc.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array)
                arr = f;
            else
                return CheckResult.Fail("接口返回格式异常");

            var main = new List<JsonElement>();
            var all = new List<JsonElement>();
            foreach (var e in arr.EnumerateArray())
            {
                all.Add(e);
                string cat = e.TryGetProperty("category_name", out var c) ? c.GetString() : "";
                if (cat == "MAIN" || cat == "UPDATE")
                    main.Add(e);
            }

            JsonElement best = default;
            bool found = false;
            foreach (var pool in new[] { main, all })
            {
                foreach (var e in pool)
                {
                    if (key.Subkey != null && !MatchSubkey(e, key.Subkey))
                        continue;
                    string ver = FileVersion(e);
                    if (string.IsNullOrEmpty(ver) || !SemVer.TryParse(ver, out _))
                        continue;
                    if (!found || SemVer.IsNewer(ver, FileVersion(best)))
                    {
                        best = e;
                        found = true;
                    }
                }
                if (found)
                    break;
            }

            if (!found)
                return CheckResult.Fail("没有找到带版本号的文件");

            string version = FileVersion(best);
            string fileId = GetStringOrNumber(best, "file_id");
            string fileName = best.TryGetProperty("file_name", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() : null;
            string displayName = best.TryGetProperty("name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : fileName;

            if (fileId == null)
                return CheckResult.Fail("文件缺少 file_id");

            return new CheckResult
            {
                Version = version,
                PageUrl = $"https://www.nexusmods.com/{Game}/mods/{modId}",
                SourceName = "Nexus Mods",
                Plan = new DownloadPlan
                {
                    Kind = "nexus",
                    NexusModId = modId.ToString(),
                    NexusFileId = fileId,
                    FileName = string.IsNullOrEmpty(fileName) ? displayName : fileName
                }
            };
        }

        private static string GetStringOrNumber(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v))
                return null;
            if (v.ValueKind == JsonValueKind.String)
                return v.GetString();
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetRawText();
            return null;
        }

        private static bool MatchSubkey(JsonElement e, string subkey)
        {
            string needle = "@" + subkey;
            string name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string desc = e.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            return name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || desc.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FileVersion(JsonElement e)
        {
            string v = e.TryGetProperty("mod_version", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            if (!string.IsNullOrWhiteSpace(v))
                return v;
            v = e.TryGetProperty("version", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        public static string FilesUrl(string modId)
            => $"https://www.nexusmods.com/{Game}/mods/{modId}?tab=files";

        public static async Task<string> ResolveDownloadUrlAsync(string modId, string fileId, string apiKey)
        {
            string url = $"{Base}/games/{Game}/mods/{modId}/files/{fileId}/download_link.json";
            string body;
            try
            {
                body = await Http.GetStringAsync(Req(url, apiKey));
            }
            catch (Exception ex)
            {
                // 免费账号拿不到下载直链：Nexus 只给 Premium 账号开放这个接口
                if (ex.Message.Contains("403") || ex.Message.Contains("401"))
                    throw new Exception("Nexus 免费账号不能通过接口下载（需要 Premium），请点「手动下载」到网页下");
                throw;
            }

            using var doc = JsonDocument.Parse(body);

            string fallback = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string uri = e.TryGetProperty("URI", out var u) ? u.GetString() : null;
                    if (string.IsNullOrEmpty(uri))
                        continue;
                    string shortName = e.TryGetProperty("short_name", out var s) ? s.GetString() ?? "" : "";
                    if (shortName.IndexOf("Nexus CDN", StringComparison.OrdinalIgnoreCase) >= 0
                        || shortName.IndexOf("CDN", StringComparison.OrdinalIgnoreCase) >= 0)
                        return uri;
                    fallback ??= uri;
                }
            }
            if (fallback != null)
                return fallback;
            throw new Exception("Nexus 没有返回可用的下载直链（可能是免费账号，需要 Premium）");
        }
    }
}
