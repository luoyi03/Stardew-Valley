using System;
using System.Threading.Tasks;

namespace SMM
{
    /// <summary>解析并验证用户手填的「更新源」，顺便告诉他是哪个 Mod，避免填错编号。</summary>
    internal static class SourceProbe
    {
        /// <summary>把随手输入的内容（3753 / 网页链接 / Nexus:3753 / GitHub 链接）解析成更新源。</summary>
        public static UpdateKey Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;
            string s = input.Trim();
            return UpdateKey.Parse(s) ?? UpdateKey.FromInput(s);
        }

        /// <summary>验证一个更新源，返回一句人话。解析不了或者查不到就抛异常。</summary>
        public static async Task<string> DescribeAsync(AppEnv env, string input)
        {
            var key = Parse(input);
            if (key == null)
                throw new Exception("看不懂这个写法。可以填：Nexus 编号（例如 3753）、Nexus 网页链接、"
                                    + "GitHub 仓库链接（例如 https://github.com/作者/仓库）");

            if (key.Site == "Nexus")
            {
                if (string.IsNullOrWhiteSpace(env?.Config?.NexusApiKey))
                    return $"Nexus:{key.Id}（没填 Nexus API Key，没法帮你确认这个编号对不对）";

                var info = await NexusSource.ModInfoAsync(key.Id, env.Config.NexusApiKey).ConfigureAwait(false);
                if (info == null)
                    throw new Exception($"Nexus 上找不到编号 {key.Id} 这个 Mod，编号可能填错了");

                string msg = $"✓ Nexus:{key.Id} → 「{info.Name}」";
                if (!string.IsNullOrWhiteSpace(info.Author))
                    msg += $"（作者 {info.Author}）";
                if (!string.IsNullOrWhiteSpace(info.Version))
                    msg += $"，最新版 {info.Version}";
                return msg;
            }

            if (key.Site == "GitHub")
            {
                CheckResult r;
                try
                {
                    r = await GitHubSource.CheckAsync(key, env?.Config?.GitHubToken,
                        env?.Config?.IncludePrerelease == true, null, null).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new Exception("GitHub 查不到这个仓库：" + ex.Message);
                }

                if (r == null)
                    throw new Exception($"GitHub 上没有 {key.Id} 这个仓库");
                if (r.Error != null)
                    throw new Exception($"GitHub:{key.Id} → " + r.Error);

                string msg = $"✓ GitHub:{key.Id}";
                if (!string.IsNullOrWhiteSpace(r.Version))
                    msg += $"，最新 Release {r.Version}";
                msg += r.Plan == null
                    ? "（这个 Release 里没挂 zip，只能用来检查版本，装不了）"
                    : "（有可下载的 zip，可以自动更新）";
                return msg;
            }

            return $"✓ {key.Raw}（这个来源不支持自动检查，只当作记录）";
        }

        /// <summary>
        /// 从 Mod 名字里挑出适合拿去 Nexus 搜索的部分：
        /// "更多背包升级 (More Backpack Upgrades)" → "More Backpack Upgrades"。
        /// 纯中文名就原样返回（Nexus 上用中文搜不到，但用户可能拿去找汉化版）。
        /// </summary>
        public static string SearchText(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "";

            var m = System.Text.RegularExpressions.Regex.Match(name, @"[（(]([^）)]*[A-Za-z][^）)]*)[）)]");
            if (m.Success)
            {
                string inner = m.Groups[1].Value.Trim();
                if (inner.Length >= 2)
                    return inner;
            }
            return name.Trim();
        }
    }
}
