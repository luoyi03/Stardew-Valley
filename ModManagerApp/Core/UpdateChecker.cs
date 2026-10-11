using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SMM
{
    public static class UpdateChecker
    {
        /// <summary>批量检查全部 Mod 的更新。</summary>
        public static async Task CheckAllAsync(AppEnv env, List<ModStatus> states,
            IProgress<string> phase, CancellationToken ct)
        {
            var cfg = env.Config;
            var ignored = new HashSet<string>(cfg.IgnoredModIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // 应用用户手动补写的更新源（内置映射已经在扫描阶段补好了）
            foreach (var s in states)
            {
                if (cfg.ExtraUpdateKeys != null && cfg.ExtraUpdateKeys.TryGetValue(s.Mod.Id, out string raw)
                    && !string.IsNullOrWhiteSpace(raw))
                {
                    var k = UpdateKey.Parse(raw) ?? UpdateKey.FromInput(raw);
                    if (k != null && !s.Mod.Keys.Any(x => string.Equals(x.Raw, k.Raw, StringComparison.OrdinalIgnoreCase)))
                        s.Mod.Keys.Insert(0, k);
                }
            }

            var targets = states.Where(s => !s.Mod.Disabled && !ignored.Contains(s.Mod.Id)).ToList();
            foreach (var s in targets)
            {
                s.Status = "checking";
                s.Message = null;
                s.LatestVersion = null;
                s.Plan = null;
                s.ManualDownload = false;
            }
            foreach (var s in states.Where(x => x.Mod.Disabled))
                s.Status = "disabled";
            foreach (var s in states.Where(x => ignored.Contains(x.Mod.Id)))
                s.Status = "ignored";

            // ---- 第一步：一次 POST 问 SMAPI 官方更新清单（免费、免 Key，一次能问几十个） ----
            var requests = new List<SmapiModRequest>();
            var reqByState = new Dictionary<ModStatus, SmapiModRequest>();
            foreach (var s in targets)
            {
                var keys = s.Mod.SupportedKeys()
                    .Select(k => k.ApiKey)
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (keys.Count == 0)
                    continue;

                var req = new SmapiModRequest
                {
                    Id = s.Mod.Id,
                    UpdateKeys = keys,
                    InstalledVersion = s.Mod.Version
                };
                requests.Add(req);
                reqByState[s] = req;
            }

            Dictionary<string, SmapiResult> smapi = new();
            if (requests.Count > 0)
            {
                phase?.Report($"正在查询 SMAPI 官方更新清单（{requests.Count} 个 Mod）…");
                smapi = await SmapiApi.CheckAsync(GameLocator.GameVersion(env.GamePath), requests).ConfigureAwait(false);
            }

            foreach (var s in targets)
            {
                if (reqByState.TryGetValue(s, out var req) && smapi.TryGetValue(req.Id, out var r))
                {
                    if (!string.IsNullOrWhiteSpace(r.Version))
                    {
                        Apply(s, new CheckResult
                        {
                            Version = r.Version,
                            PageUrl = r.Url,
                            SourceName = "SMAPI 更新清单"
                        });
                    }
                    else if (r.Known)
                    {
                        // 接口认识这个 Mod 又没给建议更新 → 已经是最新版
                        s.Status = "latest";
                        s.Message = null;
                        s.Plan = null;
                        FillSource(s);
                    }
                    else if (r.Error != null)
                    {
                        s.Message = r.Error;      // 记下来，第二步也失败时给用户看
                    }
                }
                else if (!s.Mod.Keys.Any(k => k.Supported))
                {
                    MarkNoSource(s);
                }
                // 其余留给第二步兜底
            }

            // ---- 第二步：清单里查不到的，直接问源头 API ----
            var fallback = targets.Where(s => s.Status == "checking").ToList();
            if (fallback.Count > 0)
            {
                phase?.Report($"正在逐个查询 {fallback.Count} 个 Mod 的更新源…");
                using var gate = new SemaphoreSlim(4);
                int done = 0;
                await Task.WhenAll(fallback.Select(async s =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        await DirectCheckAsync(env, s).ConfigureAwait(false);
                    }
                    finally
                    {
                        done++;
                        phase?.Report($"正在逐个查询更新源…（{done}/{fallback.Count}）");
                        gate.Release();
                    }
                })).ConfigureAwait(false);
            }
        }

        /// <summary>查不到更新源时怎么标注（SMAPI 自带的 Mod 单独说，别让用户以为漏了）。</summary>
        private static void MarkNoSource(ModStatus s)
        {
            if (s.Mod.Id.StartsWith("SMAPI.", StringComparison.OrdinalIgnoreCase))
            {
                s.Status = "smapi";
                s.Message = null;
                FillSource(s);
                return;
            }

            s.Status = "manual";
            s.Message = s.Mod.Keys.Count == 0
                ? "manifest 没写 UpdateKeys。可以点「补写更新源」，填上它的 Nexus 编号或 GitHub 仓库。"
                : "作者写的更新源暂不支持自动检查";
            FillSource(s);
        }

        /// <summary>给状态补一个来源名 / 页面地址，方便界面显示「打开网页」。</summary>
        private static void FillSource(ModStatus s)
        {
            if (!string.IsNullOrWhiteSpace(s.SourceName) && !string.IsNullOrWhiteSpace(s.PageUrl))
                return;
            var k = s.Mod.Keys.FirstOrDefault(x => x.Supported) ?? s.Mod.Keys.FirstOrDefault();
            if (k == null)
                return;
            if (string.IsNullOrWhiteSpace(s.SourceName))
                s.SourceName = k.Site;
            if (string.IsNullOrWhiteSpace(s.PageUrl))
                s.PageUrl = k.PageUrl;
        }

        private static void Apply(ModStatus s, CheckResult best)
        {
            s.LatestVersion = best.Version;
            s.SourceName = best.SourceName;
            if (!string.IsNullOrWhiteSpace(best.PageUrl))
                s.PageUrl = best.PageUrl;
            s.Plan = best.Plan;
            s.Status = SemVer.IsNewer(best.Version, s.Mod.Version) ? "update" : "latest";
            s.Message = null;
        }

        /// <summary>直接调用 GitHub / Nexus 接口检查单个 Mod（同时拿到下载计划）。</summary>
        public static async Task DirectCheckAsync(AppEnv env, ModStatus s)
        {
            CheckResult best = null;
            string firstError = null;

            foreach (var key in s.Mod.Keys)
            {
                try
                {
                    CheckResult r = key.Site switch
                    {
                        "GitHub" => await GitHubSource.CheckAsync(key, env.Config.GitHubToken,
                            env.Config.IncludePrerelease, s.Mod.Version, s.Mod.EntryDll).ConfigureAwait(false),
                        "Nexus" => await NexusSource.CheckAsync(key, env.Config.NexusApiKey).ConfigureAwait(false),
                        _ => null
                    };
                    if (r == null)
                        continue;
                    if (r.Error != null)
                    {
                        firstError ??= r.Error;
                        continue;
                    }
                    if (best == null || SemVer.IsNewer(r.Version, best.Version))
                        best = r;
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                }
            }

            if (best != null)
            {
                Apply(s, best);
                return;
            }

            bool hasSupported = s.Mod.Keys.Any(k => k.Supported);
            if (!hasSupported)
            {
                MarkNoSource(s);
                return;
            }

            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(s.Message))
                reasons.Add(s.Message);
            if (!string.IsNullOrWhiteSpace(firstError))
                reasons.Add(firstError);

            s.Status = "error";
            s.Message = reasons.Count > 0 ? string.Join("；", reasons.Distinct()) : "没有拿到可用的检查结果";
            FillSource(s);
        }

        /// <summary>更新前解析下载计划（Nexus 需要再问一次文件列表）。</summary>
        public static async Task<DownloadPlan> ResolvePlanAsync(AppEnv env, ModStatus s)
        {
            if (s?.Plan != null)
                return s.Plan;
            if (s == null || s.Mod == null)
                return null;

            foreach (var key in s.Mod.Keys.Where(k => k.Supported))
            {
                try
                {
                    CheckResult r = key.Site == "GitHub"
                        ? await GitHubSource.CheckAsync(key, env.Config.GitHubToken, env.Config.IncludePrerelease, s.Mod.Version, s.Mod.EntryDll).ConfigureAwait(false)
                        : await NexusSource.CheckAsync(key, env.Config.NexusApiKey).ConfigureAwait(false);
                    if (r?.Plan != null)
                        return r.Plan;
                }
                catch { }
            }
            return null;
        }
    }
}
