using System;
using System.Collections.Generic;
using System.IO;
using System.Timers;

namespace SMM
{
    /// <summary>
    /// 盯着下载文件夹：浏览器一下完 zip，就回调出去让主界面决定装不装。
    /// 只处理「启动之后」新出现 / 被改动的文件（不会去动目录里已有的旧包），
    /// 并且会等文件大小稳定、确认没被占用，才开始处理，避免装到半个文件。
    /// </summary>
    public sealed class DownloadWatcher : IDisposable
    {
        private sealed class Pending
        {
            public long Size;
            public int StableTicks;
            public DateTime FirstSeen;
        }

        private readonly string _folder;
        private readonly Action<string> _onZipReady;

        private FileSystemWatcher _fsw;
        private Timer _timer;
        private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public string Folder => _folder;
        public bool IsRunning => _fsw != null;

        public DownloadWatcher(string folder, Action<string> onZipReady)
        {
            _folder = folder;
            _onZipReady = onZipReady;
        }

        public void Start()
        {
            if (_fsw != null)
                return;
            if (string.IsNullOrWhiteSpace(_folder) || !Directory.Exists(_folder))
                return;

            _fsw = new FileSystemWatcher(_folder, "*.zip")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false
            };
            _fsw.Created += (_, e) => Add(e.FullPath);
            _fsw.Changed += (_, e) => Add(e.FullPath);
            _fsw.Renamed += (_, e) => Add(e.FullPath);
            _fsw.EnableRaisingEvents = true;

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

        private void Tick()
        {
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

        /// <summary>手动扫描目录里已有的 zip（用于「立即检查」按钮）。按修改时间倒序，跳过非 Mod 包。</summary>
        public static List<(string Path, ZipPeekResult Info)> ScanFolder(string folder, int limit = 20)
        {
            var list = new List<(string, ZipPeekResult)>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return list;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.zip", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return list;
            }

            var ordered = new List<FileInfo>();
            foreach (string f in files)
            {
                try { ordered.Add(new FileInfo(f)); } catch { }
            }
            ordered.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

            foreach (var fi in ordered)
            {
                if (list.Count >= limit)
                    break;
                var info = ZipPeek.Read(fi.FullName);
                if (info != null)
                    list.Add((fi.FullName, info));
            }
            return list;
        }

        public void Dispose()
        {
            try
            {
                if (_fsw != null)
                {
                    _fsw.EnableRaisingEvents = false;
                    _fsw.Dispose();
                    _fsw = null;
                }
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
