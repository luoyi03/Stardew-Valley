using System;
using System.Collections.Generic;

namespace SMM
{
    /// <summary>manifest 里的 UpdateKeys 项，如 Nexus:2400@SubKey / GitHub:owner/repo。</summary>
    public sealed class UpdateKey
    {
        /// <summary>
        /// 作者没写（或写错）UpdateKeys 的知名 Mod，这里内置补上。
        /// 扫描 manifest 时就会应用，所以「问题检查」页也不会误报「无更新源」。
        /// 用户在设置页「手动补写更新源」里填的会覆盖这里。
        /// </summary>
        public static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            // 星露谷扩展（SVE）三个包：作者的 manifest 里没写更新源
            ["FlashShifter.SVECode"] = "Nexus:3753",
            ["FlashShifter.StardewValleyExpandedCP"] = "Nexus:3753",
            ["FlashShifter.SVE-FTM"] = "Nexus:3753",

            // 银行
            ["yangZ.Bank"] = "Nexus:27517"
        };

        public string Raw { get; set; }

        /// <summary>Nexus / GitHub / ModDrop / CurseForge / Chucklefish</summary>
        public string Site { get; set; }

        public string Id { get; set; }
        public string Subkey { get; set; }

        /// <summary>该来源是否支持自动检测 + 自动下载。</summary>
        public bool Supported => Site == "Nexus" || Site == "GitHub";

        /// <summary>SMAPI 官方更新清单 API 用的标准写法。</summary>
        public string ApiKey => Site == "Chucklefish" ? null : $"{Site}:{Id}";

        public static UpdateKey Parse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            string s = raw.Trim();
            int colon = s.IndexOf(':');
            if (colon <= 0)
                return null;

            string site = s.Substring(0, colon).Trim();
            string rest = s.Substring(colon + 1).Trim();

            string sub = null;
            int at = rest.IndexOf('@');
            if (at >= 0)
            {
                sub = rest.Substring(at + 1).Trim();
                rest = rest.Substring(0, at).Trim();
            }
            if (rest.Length == 0 || rest.Contains("?"))
                return null;

            string norm = site.ToLowerInvariant() switch
            {
                "nexus" => "Nexus",
                "github" => "GitHub",
                "moddrop" => "ModDrop",
                "curseforge" => "CurseForge",
                "chucklefish" => "Chucklefish",
                _ => null
            };
            if (norm == null)
                return null;

            return new UpdateKey
            {
                Raw = norm + ":" + rest + (string.IsNullOrEmpty(sub) ? "" : "@" + sub),
                Site = norm,
                Id = rest,
                Subkey = string.IsNullOrEmpty(sub) ? null : sub
            };
        }

        public static List<UpdateKey> ParseAll(IEnumerable<string> keys)
        {
            var list = new List<UpdateKey>();
            if (keys == null)
                return list;
            foreach (string k in keys)
            {
                var u = Parse(k);
                if (u != null)
                    list.Add(u);
            }
            return list;
        }

        /// <summary>把用户随手输入的内容猜成更新源：3753 / 页面链接 / 标准写法都行。</summary>
        public static UpdateKey FromInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;
            string s = input.Trim();

            var nexus = System.Text.RegularExpressions.Regex.Match(
                s, @"nexusmods\.com/[A-Za-z0-9_\-]+/mods/(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (nexus.Success)
                return new UpdateKey { Raw = "Nexus:" + nexus.Groups[1].Value, Site = "Nexus", Id = nexus.Groups[1].Value };

            var gh = System.Text.RegularExpressions.Regex.Match(
                s, @"github\.com/([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (gh.Success)
            {
                string repo = gh.Groups[2].Value;
                if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    repo = repo.Substring(0, repo.Length - 4);
                if (repo is "releases" or "tree" or "blob" or "issues" or "wiki")
                    return null;
                return new UpdateKey { Raw = $"GitHub:{gh.Groups[1].Value}/{repo}", Site = "GitHub", Id = $"{gh.Groups[1].Value}/{repo}" };
            }

            if (int.TryParse(s, out _))
                return new UpdateKey { Raw = "Nexus:" + s, Site = "Nexus", Id = s };

            return Parse(s);
        }

        public string PageUrl => Site switch
        {
            "Nexus" => $"https://www.nexusmods.com/stardewvalley/mods/{Id}",
            "GitHub" => $"https://github.com/{Id}",
            "ModDrop" => $"https://www.moddrop.com/stardew-valley/mods/{Id}",
            "CurseForge" => $"https://www.curseforge.com/stardewvalley/mods/{Id}",
            "Chucklefish" => $"https://community.playstarbound.com/resources/{Id}",
            _ => null
        };
    }
}
