using System;
using System.Collections.Generic;
using System.IO;

namespace SMM
{
    /// <summary>程序运行环境：配置、路径、日志。</summary>
    public sealed class AppEnv
    {
        public static AppEnv Current { get; private set; }

        public AppConfig Config;

        public string GamePath;
        public string ModsPath;

        /// <summary>本程序自己的数据目录：&lt;游戏目录&gt;\.ModManagerApp。</summary>
        public string DataDir;
        public string BackupDir;
        public string TempDir;
        public string DisabledDir;
        public string LogFile;
        public string SmapiVersion;

        public bool GameReady => !string.IsNullOrEmpty(ModsPath) && Directory.Exists(ModsPath);

        public static AppEnv Create()
        {
            var env = new AppEnv();
            string roaming = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "StardewModManager");
            Directory.CreateDirectory(roaming);

            env.Config = AppConfig.Load(Path.Combine(roaming, "config.json"));

            string game = env.Config.GamePath;
            if (!GameLocator.IsValid(game))
                game = GameLocator.Detect() ?? game;

            env.ApplyGamePath(game, save: false);
            Current = env;
            return env;
        }

        /// <summary>切换游戏目录并重算所有子路径。</summary>
        public void ApplyGamePath(string gamePath, bool save = true)
        {
            GamePath = string.IsNullOrWhiteSpace(gamePath) ? "" : gamePath.Trim().TrimEnd('\\', '/');
            ModsPath = string.IsNullOrEmpty(GamePath) ? null : Path.Combine(GamePath, "Mods");

            string data = null;
            if (!string.IsNullOrEmpty(GamePath) && Directory.Exists(GamePath))
            {
                string candidate = Path.Combine(GamePath, ".ModManagerApp");
                if (EnsureWritable(candidate))
                    data = candidate;
            }
            if (data == null)
            {
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "StardewModManager", "data");
                Directory.CreateDirectory(data);
            }

            DataDir = data;
            BackupDir = Path.Combine(data, "backup");
            TempDir = Path.Combine(data, "temp");
            DisabledDir = Path.Combine(data, "disabled");
            LogFile = Path.Combine(data, "log.txt");

            SmapiVersion = GameLocator.SmapiVersion(GamePath);

            if (save)
            {
                Config.GamePath = GamePath;
                Config.Save();
            }
        }

        private static bool EnsureWritable(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void EnsureDirs()
        {
            try { Directory.CreateDirectory(BackupDir); } catch { }
            try { Directory.CreateDirectory(TempDir); } catch { }
            try { Directory.CreateDirectory(DisabledDir); } catch { }
        }

        public void Log(string message)
        {
            try
            {
                if (string.IsNullOrEmpty(LogFile))
                    return;
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.Delete(LogFile);
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        /// <summary>
        /// 实际要监控的下载目录（可能有多个）。
        /// 设置里填了就用它；没填就自动：浏览器（Edge / Chrome / Firefox）自己的下载目录 + 系统「下载」目录。
        /// 之所以要读浏览器设置，是因为很多人在浏览器里把下载位置改成了别处（例如 D:\下载），
        /// 系统「下载」目录就空了，只盯系统目录的话新包永远识别不到。
        /// </summary>
        public List<string> WatchFolders()
        {
            if (!Config.WatchFolderEnabled)
                return new List<string>();

            string manual = Config.WatchFolder;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                manual = manual.Trim().TrimEnd('\\', '/');
                return Directory.Exists(manual) ? new List<string> { manual } : new List<string>();
            }

            return AutoWatchFolders();
        }

        /// <summary>自动推断的下载目录：浏览器实际下载目录 + 系统「下载」目录。</summary>
        public static List<string> AutoWatchFolders()
        {
            var list = new List<string>();

            foreach (string d in Browser.DownloadFolders())
                AddFolder(list, d);
            AddFolder(list, DefaultDownloadFolder());

            return list;
        }

        private static void AddFolder(List<string> list, string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    return;
                folder = folder.Trim().TrimEnd('\\', '/');
                if (!list.Exists(x => string.Equals(x, folder, StringComparison.OrdinalIgnoreCase)))
                    list.Add(folder);
            }
            catch { }
        }

        /// <summary>系统「下载」目录。注册表里的最准（用户可能改过位置），读不到就用 %USERPROFILE%\Downloads。</summary>
        public static string DefaultDownloadFolder()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string p
                    && !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    return p;
            }
            catch { }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        /// <summary>游戏是否正在运行（更新文件会被占用）。</summary>
        public static bool IsGameRunning()
        {
            try
            {
                foreach (string name in new[] { "Stardew Valley", "StardewModdingAPI" })
                    if (System.Diagnostics.Process.GetProcessesByName(name).Length > 0)
                        return true;
            }
            catch { }
            return false;
        }
    }
}
