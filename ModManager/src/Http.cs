using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ModManager
{
    internal static class Http
    {
        private static readonly HttpClient Client = Create();

        public const string UserAgent = "StardewModManager/1.0";

        private static HttpClient Create()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            return c;
        }

        public static HttpRequestMessage Get(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            return req;
        }

        public static async Task<string> GetStringAsync(HttpRequestMessage req)
        {
            using var resp = await Client.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)resp.StatusCode}：{Brief(body)}");
            return body;
        }

        public static async Task DownloadAsync(HttpRequestMessage req, string dest, Action<int> progress)
        {
            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                string b = await resp.Content.ReadAsStringAsync();
                throw new Exception($"HTTP {(int)resp.StatusCode}：{Brief(b)}");
            }

            long total = resp.Content.Headers.ContentLength ?? 0;
            string dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var src = await resp.Content.ReadAsStreamAsync();
            using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var buf = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                fs.Write(buf, 0, n);
                read += n;
                if (total > 0)
                    progress?.Invoke((int)Math.Min(99, read * 100 / total));
            }
            progress?.Invoke(100);
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
}
