using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SMM
{
    /// <summary>全局共享的 HTTP 客户端，带下载进度回调。</summary>
    internal static class Http
    {
        public const string UserAgent = "StardewModManager/1.0";

        /// <summary>走系统代理的客户端（默认）。</summary>
        private static readonly HttpClient Client = Create(useSystemProxy: true);

        /// <summary>绕开代理直连的客户端（代理挂掉时兜底）。</summary>
        private static readonly HttpClient DirectClient = Create(useSystemProxy: false);

        /// <summary>上一次请求是不是靠「绕开代理」才成功的（用于诊断日志）。</summary>
        public static bool LastRequestBypassedProxy { get; private set; }

        private static HttpClient Create(bool useSystemProxy)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseProxy = useSystemProxy
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return c;
        }

        public static HttpRequestMessage Get(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return req;
        }

        private static HttpRequestMessage BuildPost(string url, string json)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        // ==================== 请求快照 ====================
        // HttpClient 发完一次就会把请求内容释放掉，所以重试不能复用原请求对象，
        // 必须先把「方法 + 地址 + 头 + body」抄下来，需要时再造一份新的。

        private sealed class RequestSnapshot
        {
            private HttpMethod _method;
            private Uri _uri;
            private readonly Dictionary<string, string[]> _headers = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string[]> _contentHeaders = new(StringComparer.OrdinalIgnoreCase);
            private string _body;

            public static async Task<RequestSnapshot> CaptureAsync(HttpRequestMessage req)
            {
                var s = new RequestSnapshot { _method = req.Method, _uri = req.RequestUri };

                foreach (var h in req.Headers)
                    s._headers[h.Key] = new List<string>(h.Value).ToArray();

                if (req.Content != null)
                {
                    s._body = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
                    foreach (var h in req.Content.Headers)
                        s._contentHeaders[h.Key] = new List<string>(h.Value).ToArray();
                }
                return s;
            }

            public HttpRequestMessage Create()
            {
                var req = new HttpRequestMessage(_method, _uri);
                foreach (var kv in _headers)
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                if (_body != null)
                {
                    var content = new ByteArrayContent(Encoding.UTF8.GetBytes(_body));
                    content.Headers.Clear();
                    foreach (var kv in _contentHeaders)
                        content.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    req.Content = content;
                }
                return req;
            }
        }

        // ==================== 发送 ====================

        /// <summary>网络层失败：连不上 / TLS 握手失败 / 超时。（HTTP 4xx、5xx 不算）</summary>
        private static bool IsNetworkFailure(Exception ex)
            => ex is HttpRequestException
               || ex is System.IO.IOException
               || ex is TaskCanceledException
               || (ex.InnerException != null && IsNetworkFailure(ex.InnerException));

        /// <summary>
        /// 优先走系统代理；代理挂了（本机常见：代理软件关了但注册表里代理还开着）
        /// 就改直连重试一次，避免整个程序因为代理抽风而全部报错。
        /// </summary>
        private static async Task<HttpResponseMessage> SendWithRetryAsync(RequestSnapshot snap, CancellationToken ct)
        {
            try
            {
                var resp = await Client.SendAsync(snap.Create(), HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                LastRequestBypassedProxy = false;
                return resp;
            }
            catch (Exception ex) when (IsNetworkFailure(ex))
            {
                var resp = await DirectClient.SendAsync(snap.Create(), HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                LastRequestBypassedProxy = true;
                return resp;
            }
        }

        private static async Task<string> SendStringAsync(RequestSnapshot snap)
        {
            using var resp = await SendWithRetryAsync(snap, CancellationToken.None).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new HttpError(resp.StatusCode, $"HTTP {(int)resp.StatusCode}：{Brief(body)}", body);
            return body;
        }

        public static async Task<string> GetStringAsync(HttpRequestMessage req)
            => await SendStringAsync(await RequestSnapshot.CaptureAsync(req).ConfigureAwait(false)).ConfigureAwait(false);

        public static async Task<string> GetStringAsync(string url, params string[] headers)
        {
            var req = Get(url);
            for (int i = 0; i + 1 < headers.Length; i += 2)
                req.Headers.TryAddWithoutValidation(headers[i], headers[i + 1]);
            using (req)
                return await GetStringAsync(req).ConfigureAwait(false);
        }

        public static async Task<string> PostJsonAsync(string url, string json)
        {
            using var req = BuildPost(url, json);
            return await GetStringAsync(req).ConfigureAwait(false);
        }

        // ==================== 下载 ====================

        public static Task DownloadAsync(string url, string dest, IProgress<int> progress, CancellationToken ct)
            => DownloadAsync(Get(url), dest, progress, ct);

        public static async Task DownloadAsync(HttpRequestMessage req, string dest, IProgress<int> progress, CancellationToken ct)
        {
            var snap = await RequestSnapshot.CaptureAsync(req).ConfigureAwait(false);
            using var resp = await SendWithRetryAsync(snap, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                string b = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpError(resp.StatusCode, $"HTTP {(int)resp.StatusCode}：{Brief(b)}", b);
            }

            long total = resp.Content.Headers.ContentLength ?? 0;
            string dir = System.IO.Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);

            using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var fs = new System.IO.FileStream(dest, System.IO.FileMode.Create, System.IO.FileAccess.Write,
                System.IO.FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                fs.Write(buf, 0, n);
                read += n;
                if (total > 0)
                    progress?.Report((int)Math.Min(99, read * 100 / total));
            }
            progress?.Report(100);
        }

        private static string Brief(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "(空响应)";
            s = s.Trim();
            if (s.StartsWith("<"))
                return "(服务器返回了非 JSON 内容)";
            return s.Length > 180 ? s.Substring(0, 180) + "…" : s;
        }
    }

    /// <summary>带状态码的 HTTP 异常，方便上层区分 401/403/404。</summary>
    public sealed class HttpError : Exception
    {
        public HttpStatusCode Status { get; }

        public HttpError(HttpStatusCode status, string message, string body) : base(message)
        {
            Status = status;
            Body = body ?? "";
        }

        public string Body { get; }

        public bool IsAuthProblem => Status == HttpStatusCode.Unauthorized || Status == HttpStatusCode.Forbidden;
    }
}
