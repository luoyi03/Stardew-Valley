using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace InGameCodex.Ai
{
    /// <summary>
    /// 联网搜索：本地图鉴和教程都答不上来时，去 AnySearch 现查一次网页资料塞给模型。
    /// 默认走匿名额度（不需要密钥、每天有免费次数），也可以在设置里填 AnySearch 的 key 提高配额。
    /// </summary>
    internal static class WebSearch
    {
        private const string Endpoint = "https://api.anysearch.com/v1/search";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

        private static readonly JsonSerializerOptions WriteOpts = new();

        private static readonly JsonSerializerOptions ReadOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// 查一次网页。成功返回拼好的参考文本；查不到 / 出错都返回空字符串，
        /// 让聊天照常进行（联网只是锦上添花，不该把提问搞失败）。
        /// </summary>
        public static async Task<string> FetchAsync(string query, int maxResults, string apiKey, int maxChars, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "";

            int want = maxResults <= 0 ? 3 : Math.Min(maxResults, 5);
            var body = new SearchRequest { Query = query.Trim(), MaxResults = want, Language = "zh" };

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                req.Content = new StringContent(JsonSerializer.Serialize(body, WriteOpts), Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");

                string key = (apiKey ?? "").Trim();
                if (key.Length > 0)
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

                using HttpResponseMessage resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    AiRuntime.Log?.Log("联网搜索失败：HTTP " + (int)resp.StatusCode + " " + Trim(text, 160), LogLevel.Warn);
                    return "";
                }

                string formatted = Format(text, maxChars);
                AiRuntime.Log?.Log("联网搜索「" + Trim(query, 40) + "」→ " + (formatted.Length > 0 ? formatted.Length + " 字资料" : "没有结果"), LogLevel.Trace);
                return formatted;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return "";
            }
            catch (Exception ex)
            {
                AiRuntime.Log?.Log("联网搜索失败：" + ex.Message, LogLevel.Warn);
                return "";
            }
        }

        /// <summary>把接口返回的 JSON 变成一段可以直接塞进系统提示的参考文本。</summary>
        private static string Format(string json, int maxChars)
        {
            SearchResponse res;
            try
            {
                res = JsonSerializer.Deserialize<SearchResponse>(json, ReadOpts);
            }
            catch (JsonException)
            {
                AiRuntime.Log?.Log("联网搜索返回的不是预期格式：" + Trim(json, 160), LogLevel.Warn);
                return "";
            }

            List<SearchItem> rows = res?.Data?.Results;
            if (rows == null || rows.Count == 0)
                return "";

            int cap = maxChars > 0 ? maxChars : 1200;
            var sb = new StringBuilder();
            sb.Append("\n【联网搜到的网页资料（刚刚实时查的；和你的记忆冲突时以这里为准，回答里顺带提一句来源网站）】\n");

            int n = 0;
            foreach (SearchItem r in rows)
            {
                if (r == null || sb.Length >= cap)
                    break;

                string title = (r.Title ?? "").Trim();
                string body = FirstNonEmpty(r.Snippet, r.Content);
                if (title.Length == 0 && body.Length == 0)
                    continue;

                string host = HostOf(r.Url);

                n++;
                sb.Append(n).Append(". ").Append(title.Length > 0 ? title : host);
                if (host.Length > 0)
                    sb.Append("（").Append(host).Append("）");
                sb.Append("\n   ").Append(Trim(body, 300)).Append("\n");
            }

            if (n == 0)
                return "";

            string text = sb.ToString();
            return text.Length > cap ? text.Substring(0, cap) : text;
        }

        private static string HostOf(string url)
        {
            try
            {
                return Uri.TryCreate(url, UriKind.Absolute, out Uri u) ? u.Host : "";
            }
            catch
            {
                return "";
            }
        }

        private static string FirstNonEmpty(string a, string b)
            => !string.IsNullOrWhiteSpace(a) ? a.Trim() : (b ?? "").Trim();

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ---------------- 接口结构（AnySearch POST /v1/search） ----------------

        private sealed class SearchRequest
        {
            [JsonPropertyName("query")]
            public string Query { get; set; }

            [JsonPropertyName("max_results")]
            public int MaxResults { get; set; }

            [JsonPropertyName("language")]
            public string Language { get; set; }
        }

        private sealed class SearchResponse
        {
            [JsonPropertyName("code")]
            public int Code { get; set; }

            [JsonPropertyName("message")]
            public string Message { get; set; }

            [JsonPropertyName("data")]
            public SearchData Data { get; set; }
        }

        private sealed class SearchData
        {
            [JsonPropertyName("results")]
            public List<SearchItem> Results { get; set; }
        }

        private sealed class SearchItem
        {
            [JsonPropertyName("title")]
            public string Title { get; set; }

            [JsonPropertyName("url")]
            public string Url { get; set; }

            [JsonPropertyName("snippet")]
            public string Snippet { get; set; }

            [JsonPropertyName("content")]
            public string Content { get; set; }
        }
    }
}
