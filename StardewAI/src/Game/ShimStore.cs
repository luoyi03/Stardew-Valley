using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StardewModdingAPI;

namespace StardewAI.Game
{
    /// <summary>读取 dsh-connect-trio 落盘的本机回环网关信息（端口 + 密钥）。</summary>
    public static class ShimStore
    {
        public sealed class Info
        {
            public int Port;
            public string Token = "";
            public string BaseUrl = "";
            public long Ts;
            public string Source = "";
        }

        public static string PrimaryPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", ".trio-shim.json");

        public static Info Read()
        {
            return ReadFile(PrimaryPath) ?? ScanProfiles();
        }

        private static Info ScanProfiles()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles");
            if (!Directory.Exists(root))
                return null;

            var found = new List<Info>();

            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    var info = ReadFile(Path.Combine(dir, ".trio-shim.json"));
                    if (info != null)
                        found.Add(info);
                }
            }
            catch { /* ignore */ }

            return found.Count == 0 ? null : found.OrderByDescending(i => i.Ts).First();
        }

        private static Info ReadFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                int port = root.TryGetProperty("port", out var p) && p.TryGetInt32(out int pv) ? pv : 0;
                string token = root.TryGetProperty("token", out var t) ? (t.GetString() ?? "") : "";
                string baseUrl = root.TryGetProperty("baseUrl", out var b) ? (b.GetString() ?? "") : "";
                long ts = root.TryGetProperty("ts", out var s) && s.TryGetInt64(out long sv) ? sv : 0;

                if (port <= 0)
                    return null;

                if (string.IsNullOrWhiteSpace(baseUrl))
                    baseUrl = "http://127.0.0.1:" + port + "/v1";

                return new Info { Port = port, Token = token, BaseUrl = baseUrl, Ts = ts, Source = path };
            }
            catch
            {
                return null;
            }
        }

        public static string Describe(IMonitor log)
        {
            var info = Read();
            if (info == null)
                return "未找到 " + PrimaryPath + "（DSH 需要启动，且插件需带落盘补丁）";

            log?.Log("本机网关：" + info.BaseUrl + "（来源 " + info.Source + "，ts " + info.Ts + "）", LogLevel.Info);
            return info.BaseUrl + "  密钥 " + info.Token.Length + " 位  来源 " + info.Source;
        }
    }
}
