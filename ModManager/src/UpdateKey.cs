using System;
using System.Collections.Generic;

namespace ModManager
{
    /// <summary>manifest 里的 UpdateKeys 项，如 Nexus:2400@SubKey / GitHub:owner/repo。</summary>
    public sealed class UpdateKey
    {
        public string Raw;
        public string Site;      // Nexus / GitHub / ModDrop / CurseForge / Chucklefish
        public string Id;
        public string Subkey;

        /// <summary>该来源是否支持自动检测 + 自动下载。</summary>
        public bool Supported => Site == "Nexus" || Site == "GitHub";

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
                Raw = s,
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
                return new UpdateKey { Raw = $"GitHub:{gh.Groups[1].Value}/{gh.Groups[2].Value}", Site = "GitHub", Id = $"{gh.Groups[1].Value}/{gh.Groups[2].Value}" };

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
