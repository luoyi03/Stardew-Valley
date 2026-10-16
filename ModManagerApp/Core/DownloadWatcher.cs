using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;

namespace SMM
{
    /// <summary>
    /// 盯着下载文件夹（可能有多个：浏览器自己的下载目录 + 系统「下载」目录）：
    /// 一出现新 zip，就回调出去让主界面决定装不装。
    /// 只处理「启动之后」新出现 / 被改动的文件（不会去动目录里已有的旧包），
    /// 并且会等文件大小稳定、确认没被占用，才开始处理，避免装到半个文件。
    /// 除了文件系统事件，还会定时兜底自己扫一遍（事件偶尔会漏），见 Sweep。
    /// </summary>
    public sealed class DownloadWatcher : IDisposable
    {
        private sealed class Pending
        {
            public long Size;
            public int StableTicks;
            public DateTime FirstSeen;
        }

        /// <summary>每隔多少秒兜底扫一次目录。</summary>
        private const int SweepSeconds = 10;

        private readonly List<string> _folders = new();
        private readonly Action<string> _onZipReady;

        private readonly List<FileSystemWatcher> _watchers = new();
        private Timer _timer;
        private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        private readonly DateTime _startedAt = DateTime.Now;
        private int _ticks;

        public IReadOnlyList<string> Folders => _folders;
        public string Folder => string.Join("；", _folders);
        public bool IsRunning => _watchers.Count > 0;

        public DownloadWatcher(IEnumerable<string> folders, Action<string> onZipReady)
        {
            if (folders != null)
            {
                foreach (string f in folders)
                {
                    if (string.IsNullOrWhiteSpace(f))
                        continue;
                    if (_folders.Exists(x => string.Equals(x, f, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    _folders.Add(f);
                }
            }
            _onZipReady = onZipReady;
        }

        public void Start()
        {
            if (_watchers.Count > 0)
                return;

            foreach (string folder in _folders)
            {
                try
                {
                    if (!Directory.Exists(folder))
                        continue;

                    var fsw = new FileSystemWatcher(folder, "*.zip")
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        IncludeSubdirectories = false
                    };
                    fsw.Created += (_, e) => Add(e.FullPath);
                    fsw.Changed += (_, e) => Add(e.FullPath);
                    fsw.Renamed += (_, e) => Add(e.FullPath);
                    fsw.EnableRaisingEvents = true;
                    _watchers.Add(fsw);
                }
                catch { }
            }

            _timer = new Timer(1000) { AutoReset = true };
            _timer.Elapsed += (_, _) => Tick();
            _timer.Start();
        }

        private void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return;
            lock (_lock)
            {
                if (_handled.Contains(path))
                    return;
                if (_pending.ContainsKey(path))
                    return;
                _pending[path] = new Pending { Size = -1, FirstSeen = DateTime.Now };
            }
        }

        /// <summary>兜底扫描：文件系统事件偶尔会漏（缓冲溢出、刚启动就下完等），定期自己看一遍。</summary>
        private void Sweep()
        {
            foreach (string folder in _folders)
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly))
                    {
                        DateTime t;
                        try { t = File.GetLastWriteTime(f); }
                        catch { continue; }
                        if (t < _startedAt)
                            continue;      // 启动之前就躺着的老包不动（那些交给「立即检查」/ 启动补扫）
                        Add(f);
                    }
                }
                catch { }
            }
        }

        private void Tick()
        {
            if (++_ticks % SweepSeconds == 0)
                Sweep();

            List<string> ready = null;
            lock (_lock)
            {
                if (_pending.Count == 0)
                    return;

                var gone = new List<string>();
                foreach (var kv in _pending)
                {
                    string path = kv.Key;
                    Pending p = kv.Value;

                    if (!File.Exists(path))
                    {
                        gone.Add(path);
                        continue;
                    }

                    long len;
                    try { len = new FileInfo(path).Length; }
                    catch { continue; }

                    bool firstTime = p.Size < 0;
                    p.StableTicks = (!firstTime && p.Size == len) ? p.StableTicks + 1 : 0;
                    p.Size = len;

                    double waited = (DateTime.Now - p.FirstSeen).TotalSeconds;

                    // 大小为 0 或者还在变，就继续等
                    if (len <= 0)
                        continue;
                    if (p.StableTicks < 2 && waited < 15)
                        continue;
                    if (!CanOpenExclusive(path) && waited < 25)
                        continue;

                    ready ??= new List<string>();
                    ready.Add(path);
                }

                foreach (string g in gone)
                    _pending.Remove(g);

                if (ready != null)
                {
                    foreach (string r in ready)
                    {
                        _pending.Remove(r);
                        _handled.Add(r);
                    }
                }
            }

            if (ready == null)
                return;

            foreach (string path in ready)
            {
                try { _onZipReady(path); }
                catch { }
            }
        }

        /// <summary>能独占打开说明浏览器已经写完了、句柄也放开了。</summary>
        private static bool CanOpenExclusive(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 扫一遍目录里已有的 zip（用于「立即检查」按钮）。按修改时间倒序，
        /// 识别不了的也会返回（带 Error 原因），界面才能说清为什么没认出来。
        /// </summary>
        public static List<ZipPeekResult> ScanFolder(IEnumerable<string> folders, int limit = 20)
        {
            var list = new List<ZipPeekResult>();
            foreach (var fi in NewestZips(folders, null, limit))
                list.Add(ZipPeek.Read(fi.FullName));
            return list;
        }

        /// <summary>找出「某个时间点之后」新出现的 zip（启动补扫用），按修改时间倒序。</summary>
        public static List<string> FindNewZips(IEnumerable<string> folders, DateTime sinceUtc, int limit = 20)
        {
            var list = new List<string>();
            foreach (var fi in NewestZips(folders, sinceUtc, limit))
                list.Add(fi.FullName);
            return list;
        }

        private static List<FileInfo> NewestZips(IEnumerable<string> folders, DateTime? sinceUtc, int limit)
        {
            var all = new List<FileInfo>();
            if (folders == null)
                return all;

            foreach (string folder in folders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    continue;
                try
                {
                    foreach (string f in Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (sinceUtc != null && fi.LastWriteTimeUtc <= sinceUtc.Value)
                                continue;
                            all.Add(fi);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            all.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            if (all.Count > limit)
                all.RemoveRange(limit, all.Count - limit);
            return all;
        }

        public void Dispose()
        {
            try
            {
                foreach (var w in _watchers)
                {
                    try
                    {
                        w.EnableRaisingEvents = false;
                        w.Dispose();
                    }
                    catch { }
                }
                _watchers.Clear();

                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Dispose();
                    _timer = null;
                }
            }
            catch { }
        }
    }
}
