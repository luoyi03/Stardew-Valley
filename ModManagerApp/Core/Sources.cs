using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SMM
{
    // ========================================================================
    //  GitHub
    // ========================================================================
    /// <summary>GitHub 更新源：读 releases 的 tag 当作版本号，挑 zip 资源作为下载包。</summary>
    internal static class GitHubSource
    {
        public static async Task<CheckResult> CheckAsync(UpdateKey key, string token, bool includePrerelease, string installedVersion, string entryDll)
        {
            string[] parts = (key.Id ?? "").Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                return null;

            var req = Http.Get($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases?per_page=10");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());

            string body = await Http.GetStringAsync(req).ConfigureAwait(false);
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
                if (rel.Flag("draft"))
                    continue;
                bool pre = rel.Flag("prerelease");
                if (pre && !includePrerelease && !installedIsPre)
                    continue;

                string tag = rel.Str("tag_name");
                if (string.IsNullOrWhiteSpace(tag) || !SemVer.TryParse(tag, out _))
                    continue;

                if (bestVersion == null || SemVer.IsNewer(tag, bestVersion))
                {
                    bestVersion = tag;
                    pageUrl = rel.Str("html_url") ?? pageUrl;
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
                string an = a.Str("name");
                string au = a.Str("browser_download_url");
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

        /// <summary>GitHub 关键词搜索（免费，未登录时限流 10 次/分钟）。</summary>
        public static async Task<List<BrowseItem>> SearchAsync(string keyword, string token)
        {
            string q = string.IsNullOrWhiteSpace(keyword)
                ? "stardew valley mod in:name,description,readme"
                : $"{keyword} stardew valley in:name,description,readme";
            string url = "https://api.github.com/search/repositories?q=" + Uri.EscapeDataString(q) + "&sort=stars&order=desc&per_page=30";

            var req = Http.Get(url);
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());

            string body = await Http.GetStringAsync(req).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var list = new List<BrowseItem>();
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var r in items.EnumerateArray())
            {
                string full = r.Str("full_name");
                if (string.IsNullOrWhiteSpace(full))
                    continue;
                string desc = r.Str("description") ?? "";
                string name = r.Str("name") ?? full;
                // 排除明显不是 Mod 的仓库
                string hay = (name + " " + desc).ToLowerInvariant();
                if (!hay.Contains("stardew") && !hay.Contains("smapi") && !hay.Contains("mod"))
                    continue;

                var item = new BrowseItem
                {
                    Source = "GitHub",
                    Id = full,
                    Name = string.IsNullOrWhiteSpace(r.Str("name")) ? full : r.Str("name"),
                    Author = r.TryGetProperty("owner", out var ow) ? ow.Str("login") : null,
                    Summary = desc,
                    PageUrl = r.Str("html_url") ?? $"https://github.com/{full}",
                    Version = null
                };
                if (r.TryGetProperty("stargazers_count", out var st) && st.ValueKind == JsonValueKind.Number)
                    item.Downloads = st.GetInt64();
                if (r.TryGetProperty("updated_at", out var up) && up.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(up.GetString(), out var dt))
                    item.Updated = dt;
                list.Add(item);
            }
            return list;
        }
    }

    // ========================================================================
    //  Nexus Mods
    // ========================================================================
    internal sealed class NexusAccount
    {
        public string Name;
        public long UserId;
        public bool IsPremium;
    }

    /// <summary>Nexus Mods 官方 v1 API：需要用户自己的免费 API Key。</summary>
    internal static class NexusSource
    {
        public const string Game = "stardewvalley";
        public const string Base = "https://api.nexusmods.com/v1";
        public const string KeyPageUrl = "https://www.nexusmods.com/users/myaccount?tab=api";

        private static HttpRequestMessage Req(string url, string apiKey)
        {
            var req = Http.Get(url);
            req.Headers.TryAddWithoutValidation("apikey", apiKey.Trim());
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("Application-Name", "StardewModManager");
            req.Headers.TryAddWithoutValidation("Application-Version", "1.0.0");
            return req;
        }

        /// <summary>检查某个 Nexus Mod 的最新文件版本，并给出下载计划（file_id）。</summary>
        public static async Task<CheckResult> CheckAsync(UpdateKey key, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return CheckResult.Fail("未填写 Nexus API Key，无法检查");
            if (!int.TryParse(key.Id, out int modId))
                return null;

            string url = $"{Base}/games/{Game}/mods/{modId}/files.json";
            string body = await Http.GetStringAsync(Req(url, apiKey)).ConfigureAwait(false);
            using var doc = JsonUtil.Parse(body);

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
                string cat = e.Str("category_name") ?? "";
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
            string fileId = best.StrOrNum("file_id");
            string fileName = best.Str("file_name");
            string displayName = best.Str("name") ?? fileName;

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

        private static bool MatchSubkey(JsonElement e, string subkey)
        {
            string needle = "@" + subkey;
            string name = e.Str("name") ?? "";
            string desc = e.Str("description") ?? "";
            return name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                || desc.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FileVersion(JsonElement e)
        {
            string v = e.Str("mod_version");
            if (!string.IsNullOrWhiteSpace(v))
                return v;
            return e.Str("version");
        }

        public static string FilesUrl(string modId)
            => $"https://www.nexusmods.com/{Game}/mods/{modId}?tab=files";

        /// <summary>用 validate 接口验证 Key，能顺便问出账号名和是不是 Premium。失败时抛异常。</summary>
        public static async Task<NexusAccount> ValidateAsync(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("还没填 API Key");

            string body;
            try
            {
                body = await Http.GetStringAsync(Req(Base + "/users/validate.json", apiKey)).ConfigureAwait(false);
            }
            catch (HttpError ex) when (ex.IsAuthProblem)
            {
                throw new Exception("这个 Key 无效或已被吊销（Nexus 返回鉴权失败）");
            }
            catch (Exception ex)
            {
                throw new Exception("连不上 Nexus：" + ex.Message);
            }

            using var doc = JsonUtil.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                throw new Exception("Nexus 返回格式异常");

            var account = new NexusAccount
            {
                Name = r.Str("name"),
                IsPremium = r.TryGetProperty("is_premium", out var p) && p.ValueKind == JsonValueKind.True
            };
            if (r.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.Number)
                account.UserId = uid.GetInt64();
            return account;
        }

        /// <summary>换取下载直链：仅 Premium 账号可用，免费账号会拿到 403。</summary>
        public static async Task<string> ResolveDownloadUrlAsync(string modId, string fileId, string apiKey)
        {
            string url = $"{Base}/games/{Game}/mods/{modId}/files/{fileId}/download_link.json";
            string body;
            try
            {
                body = await Http.GetStringAsync(Req(url, apiKey)).ConfigureAwait(false);
            }
            catch (HttpError ex) when (ex.IsAuthProblem)
            {
                throw new Exception("Nexus 免费账号不能通过接口直接下载（需要 Premium），请点「打开网页」手动下载 zip 后再导入");
            }

            using var doc = JsonUtil.Parse(body);

            string fallback = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string uri = e.Str("URI");
                    if (string.IsNullOrEmpty(uri))
                        continue;
                    string shortName = e.Str("short_name") ?? "";
                    if (shortName.IndexOf("CDN", StringComparison.OrdinalIgnoreCase) >= 0)
                        return uri;
                    fallback ??= uri;
                }
            }
            return fallback ?? throw new Exception("Nexus 没有返回可用的下载直链（免费账号需要 Premium）");
        }

        /// <summary>读取单个 Mod 的详情。</summary>
        public static async Task<BrowseItem> ModInfoAsync(string modId, string apiKey)
        {
            string url = $"{Base}/games/{Game}/mods/{modId}.json";
            string body = await Http.GetStringAsync(Req(url, apiKey)).ConfigureAwait(false);
            using var doc = JsonUtil.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return null;
            return new BrowseItem
            {
                Source = "Nexus",
                Id = modId,
                Name = r.Str("name") ?? ("Nexus Mod " + modId),
                Author = r.Str("author"),
                Version = r.Str("version"),
                Summary = r.Str("summary"),
                PageUrl = $"https://www.nexusmods.com/{Game}/mods/{modId}",
                PictureUrl = r.Str("picture_url"),
                Downloads = r.TryGetProperty("mod_downloads", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0,
                Endorsements = r.TryGetProperty("endorsement_count", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0
            };
        }

        /// <summary>Nexus 榜单：latest_added / latest_updated / trending。</summary>
        public static async Task<List<BrowseItem>> ListAsync(string kind, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("浏览 Nexus 榜单需要先填写 API Key（免费申请）");
            string url = $"{Base}/games/{Game}/mods/{kind}.json";
            string body = await Http.GetStringAsync(Req(url, apiKey)).ConfigureAwait(false);
            using var doc = JsonUtil.Parse(body);
            var list = new List<BrowseItem>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.StrOrNum("mod_id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var item = new BrowseItem
                {
                    Source = "Nexus",
                    Id = id,
                    Name = r.Str("name") ?? ("Nexus Mod " + id),
                    Author = r.Str("author"),
                    Version = r.Str("version"),
                    Summary = r.Str("summary"),
                    PageUrl = $"https://www.nexusmods.com/{Game}/mods/{id}",
                    PictureUrl = r.Str("picture_url")
                };
                if (r.TryGetProperty("mod_downloads", out var d) && d.ValueKind == JsonValueKind.Number)
                    item.Downloads = d.GetInt64();
                list.Add(item);
            }
            return list;
        }
    }

    // ========================================================================
    //  SMAPI 官方更新清单 API（免费、无需 Key，一次性批量检查）
    // ========================================================================
    internal sealed class SmapiModRequest
    {
        public string Id;
        public List<string> UpdateKeys = new();
        public string InstalledVersion;
    }

    internal sealed class SmapiResult
    {
        /// <summary>接口认识这个 Mod（返回了带 id 的条目）。</summary>
        public bool Known;

        /// <summary>有更新时才不为 null。</summary>
        public string Version;
        public string Url;

        /// <summary>接口报的错（比如更新源无效）。</summary>
        public string Error;
    }

    internal static class SmapiApi
    {
        private const string Endpoint = "https://smapi.io/api/v3.0/mods";
        private const string ApiVersion = "3.0.0";
        private const int ChunkSize = 30;

        /// <summary>
        /// 批量查询更新（POST，一次最多 30 个 Mod），返回「ModID → 结果」。
        /// 官方接口：https://smapi.io/api/v3.0/mods
        /// 注意：接口自己会拿 installedVersion 和最新版比较，只有当有更新时才返回 suggestedUpdate；
        /// 返回了条目但没有 suggestedUpdate，说明「已是最新」。
        /// </summary>
        public static async Task<Dictionary<string, SmapiResult>> CheckAsync(string gameVersion, List<SmapiModRequest> mods)
        {
            var result = new Dictionary<string, SmapiResult>(StringComparer.OrdinalIgnoreCase);
            if (mods == null || mods.Count == 0)
                return result;

            string gv = string.IsNullOrWhiteSpace(gameVersion) ? "1.6.15" : gameVersion;

            for (int i = 0; i < mods.Count; i += ChunkSize)
            {
                var chunk = mods.Skip(i).Take(ChunkSize).ToList();
                string body;
                try
                {
                    body = JsonSerializer.Serialize(new
                    {
                        mods = chunk.Select(m => new
                        {
                            id = m.Id,
                            updateKeys = m.UpdateKeys,
                            installedVersion = m.InstalledVersion,
                            isBroken = false
                        }),
                        apiVersion = ApiVersion,
                        gameVersion = gv
                    });
                }
                catch (Exception ex)
                {
                    foreach (var m in chunk)
                        result[m.Id] = new SmapiResult { Error = "SMAPI 更新清单组装失败：" + ex.Message };
                    continue;
                }

                string resp;
                try
                {
                    resp = await Http.PostJsonAsync(Endpoint, body).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 这一批失败不影响其他批次，但要如实告诉用户
                    foreach (var m in chunk)
                        result[m.Id] = new SmapiResult { Error = "SMAPI 更新清单请求失败：" + ex.Message };
                    continue;
                }

                int parsed = 0;
                try
                {
                    using var doc = JsonUtil.Parse(resp);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        throw new Exception("返回的不是数组");

                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        string id = e.Str("id");
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        var r = new SmapiResult();
                        if (e.TryGetProperty("suggestedUpdate", out var su) && su.ValueKind == JsonValueKind.Object)
                        {
                            r.Version = su.Str("version");
                            r.Url = su.Str("url");
                        }

                        var errs = e.StrArray("errors");
                        if (errs.Count > 0)
                            r.Error = "SMAPI 清单：" + string.Join("；", errs.Take(2));
                        else
                            r.Known = true;      // 没报错就说明接口认识这个 Mod

                        result[id] = r;
                        parsed++;
                    }
                }
                catch (Exception ex)
                {
                    // 单批解析失败不影响其他批次，但要留下线索
                    foreach (var m in chunk)
                        result[m.Id] = new SmapiResult { Error = $"SMAPI 更新清单返回格式异常（{ex.Message}）：{Preview(resp)}" };
                    continue;
                }

                if (parsed == 0)
                {
                    foreach (var m in chunk)
                        result[m.Id] = new SmapiResult { Error = "SMAPI 更新清单没有返回这批 Mod 的记录：" + Preview(resp) };
                }
            }
            return result;
        }

        private static string Preview(string text)
        {
            if (text == null)
                return "(null)";
            text = text.Trim();
            return text.Length <= 200 ? text : text.Substring(0, 200) + "…";
        }
    }
}
