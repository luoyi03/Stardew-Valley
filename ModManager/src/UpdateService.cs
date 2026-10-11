using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace ModManager
{
    public sealed class ModState
    {
        public LocalMod Mod;

        /// <summary>unknown / checking / latest / update / manual / error / ignored</summary>
        public string Status = "unknown";

        public string LatestVersion;
        public string SourceName;
        public string PageUrl;
        public string Message;
        public DownloadPlan Plan;

        /// <summary>下载 / 安装进度 0~100。</summary>
        public int Progress;

        /// <summary>空 / downloading / installing / done / pending / error</summary>
        public string JobState = "";
        public string JobError;

        /// <summary>能检出新版本，但来源不允许自动下载（如 Nexus 免费账号）。</summary>
        public bool ManualDownload;

        public bool HasUpdate => Status == "update";
        public bool CanAutoUpdate => HasUpdate && Plan != null && !Mod.IsVirtual && !ManualDownload;
        public bool Busy => JobState is "downloading" or "installing";
    }

    public sealed class UpdateService
    {
        /// <summary>作者没写（或写错）更新源的知名 Mod，这里补上。</summary>
        private static readonly Dictionary<string, string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            ["FlashShifter.SVECode"] = "Nexus:3753",
            ["FlashShifter.StardewValleyExpandedCP"] = "Nexus:3753",
            ["FlashShifter.SVE-FTM"] = "Nexus:3753"
        };
        private readonly IModHelper _helper;
        private readonly IMonitor _log;
        private readonly object _lock = new();
        private readonly List<ModState> _states = new();

        public readonly string DataDir;
        public readonly string TempDir;
        public readonly string BackupDir;
        public readonly string PendingFile;
        public readonly string ModsPath;

        public int Revision;
        public volatile bool Checking;
        public volatile int CheckDone;
        public volatile int CheckTotal;
        public string LastCheckText = "尚未检查";
        public DateTime? LastCheck;

        public UpdateService(IModHelper helper, IMonitor log)
        {
            _helper = helper;
            _log = log;

            // Mods 目录 = 本 Mod 目录的上一级
            ModsPath = Path.GetFullPath(Path.Combine(helper.DirectoryPath, ".."));
            string mods = ModsPath;
            DataDir = Path.GetFullPath(Path.Combine(mods, ".ModManager"));
            TempDir = Path.Combine(DataDir, "temp");
            BackupDir = Path.Combine(DataDir, "backup");
            PendingFile = Path.Combine(DataDir, "pending.json");

            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(TempDir);
            Directory.CreateDirectory(BackupDir);
        }

        private void Bump() => Revision++;

        /// <summary>外部改动了状态后手动通知界面刷新。</summary>
        public void BumpPublic() => Bump();

        public List<ModState> Snapshot()
        {
            lock (_lock)
                return new List<ModState>(_states);
        }

        public ModState Find(string id) => Snapshot().FirstOrDefault(s => s.Mod.Id == id);

        /// <summary>只检查单个 Mod（补写更新源后用）。</summary>
        public async Task CheckAsync(ModState state)
        {
            if (state == null)
                return;
            await CheckOneAsync(state);
        }

        // ---------------- 扫描 ----------------
        public void RefreshMods()
        {
            var mods = ModScanner.Scan(ModsPath);
            var ignored = new HashSet<string>(ModEntry.Config.IgnoredModIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var m in mods)
            {
                if (m.Id == ModEntry.ModId)
                    m.Name = m.Name + "（本管理器）";
                m.Loaded = _helper.ModRegistry.Get(m.Id) != null;
            }

            // 合并内置补丁 + 用户在界面里手动补写的更新源
            var extra = ModEntry.Config?.ExtraUpdateKeys;
            foreach (var m in mods)
            {
                string key = null;
                if (extra != null && extra.TryGetValue(m.Id, out var user))
                    key = user;
                else if (KnownKeys.TryGetValue(m.Id, out var known))
                    key = known;
                if (key == null)
                    continue;
                var parsed = UpdateKey.Parse(key);
                if (parsed != null && !m.Keys.Any(x => string.Equals(x.Raw, parsed.Raw, StringComparison.OrdinalIgnoreCase)))
                    m.Keys.Insert(0, parsed);
            }

            // SMAPI 本体：只能提示，不能自动替换
            mods.Add(new LocalMod
            {
                Id = "SMAPI",
                Name = "SMAPI 本体",
                Version = Constants.ApiVersion?.ToString() ?? "?",
                Dir = null,
                IsVirtual = true,
                ManualHint = "SMAPI 需要运行官方安装程序更新，无法在游戏内替换",
                Keys = UpdateKey.ParseAll(new[] { "GitHub:Pathoschild/SMAPI" })
            });

            lock (_lock)
            {
                var old = _states.ToDictionary(s => s.Mod.Id, StringComparer.OrdinalIgnoreCase);
                _states.Clear();
                foreach (var m in mods)
                {
                    if (old.TryGetValue(m.Id, out var prev)
                        && prev.Mod.Version == m.Version
                        && prev.Status != "unknown"
                        && prev.Status != "checking")
                    {
                        prev.Mod = m;
                        _states.Add(prev);
                        continue;
                    }
                    _states.Add(new ModState
                    {
                        Mod = m,
                        Status = ignored.Contains(m.Id) ? "ignored" : "unknown",
                        PageUrl = m.Keys.Count > 0 ? m.Keys[0].PageUrl : null,
                        SourceName = m.Keys.Count > 0 ? m.Keys[0].Site : null
                    });
                }
            }
            Bump();
        }

        // ---------------- 检查 ----------------
        public async Task CheckAllAsync()
        {
            if (Checking)
                return;
            Checking = true;
            CheckDone = 0;
            Bump();

            RefreshMods();
            var todo = Snapshot().Where(s => s.Status != "ignored" && s.Mod.Keys.Count > 0).ToList();
            CheckTotal = todo.Count;

            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(todo.Select(async s =>
            {
                await gate.WaitAsync();
                try
                {
                    await CheckOneAsync(s);
                }
                finally
                {
                    CheckDone++;
                    gate.Release();
                    Bump();
                }
            }));

            int updates = Snapshot().Count(s => s.HasUpdate);
            LastCheck = DateTime.Now;
            LastCheckText = updates > 0
                ? $"最近检查：{DateTime.Now:MM-dd HH:mm} · {updates} 个可更新"
                : $"最近检查：{DateTime.Now:MM-dd HH:mm} · 全部最新";
            Checking = false;
            Bump();
        }

        private async Task CheckOneAsync(ModState s)
        {
            lock (_lock)
            {
                s.Status = "checking";
                s.Message = null;
            }
            Bump();

            try
            {
                CheckResult best = null;
                string firstError = null;

                foreach (var key in s.Mod.Keys)
                {
                    CheckResult r;
                    switch (key.Site)
                    {
                        case "GitHub":
                            r = await GitHubSource.CheckAsync(key, ModEntry.Config.GitHubToken,
                                ModEntry.Config.IncludePrerelease, s.Mod.Version, s.Mod.EntryDll);
                            break;
                        case "Nexus":
                            r = await NexusSource.CheckAsync(key, ModEntry.Config.NexusApiKey);
                            break;
                        default:
                            continue;
                    }
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

                lock (_lock)
                {
                    if (best == null)
                    {
                        // 有可自动检查的来源（Nexus/GitHub）就别整体降级成“手动”
                        bool hasSupported = s.Mod.Keys.Any(k => k.Supported);
                        s.Status = hasSupported ? "error" : "manual";
                        s.Message = firstError
                            ?? (s.Mod.Keys.Count == 0 ? "manifest 未写 UpdateKeys"
                                : hasSupported ? "没有拿到可用的检查结果" : "来源暂不支持自动检查");
                        if (s.Mod.Keys.Count > 0)
                        {
                            // 页面优先给可自动检查的来源，方便手动兜底
                            var preferred = s.Mod.Keys.FirstOrDefault(k => k.Supported) ?? s.Mod.Keys[0];
                            s.SourceName = preferred.Site;
                            s.PageUrl = preferred.PageUrl;
                        }
                    }
                    else
                    {
                        s.LatestVersion = best.Version;
                        s.SourceName = best.SourceName;
                        s.PageUrl = best.PageUrl;
                        s.Plan = s.Mod.IsVirtual ? null : best.Plan;
                        s.Status = SemVer.IsNewer(best.Version, s.Mod.Version) ? "update" : "latest";
                        s.Message = null;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    s.Status = "error";
                    s.Message = ex.Message;
                }
                _log.Log($"检查 {s.Mod.Name} 失败：{ex.Message}", LogLevel.Debug);
            }

            Bump();
        }

        // ---------------- 更新 ----------------
        public async Task UpdateAsync(ModState state)
        {
            if (state == null || !state.CanAutoUpdate || state.Busy)
                return;

            state.JobState = "downloading";
            state.Progress = 0;
            state.JobError = null;
            Bump();

            await Updater.RunAsync(state);
            Bump();
        }

        public async Task UpdateAllAsync()
        {
            var targets = Snapshot().Where(s => s.CanAutoUpdate && !s.Busy).ToList();
            if (targets.Count == 0)
                return;
            using var gate = new SemaphoreSlim(2);
            await Task.WhenAll(targets.Select(async s =>
            {
                await gate.WaitAsync();
                try
                {
                    await UpdateAsync(s);
                }
                finally
                {
                    gate.Release();
                }
            }));
            RefreshMods();
        }
    }
}
