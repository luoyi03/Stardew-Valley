using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SMM
{
    /// <summary>决定「打开网页」时用哪个浏览器。</summary>
    internal static class Browser
    {
        public const string Auto = "auto";          // 有 Edge 就用 Edge，没有就系统默认
        public const string SystemDefault = "default";
        public const string Edge = "edge";
        public const string Chrome = "chrome";
        public const string Custom = "custom";

        /// <summary>找 msedge.exe：先查注册表 App Paths，再查常见安装位置。</summary>
        public static string FindEdge() => FindApp("msedge.exe", new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
        });

        public static string FindChrome() => FindApp("chrome.exe", new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe")
        });

        private static string FindApp(string exeName, string[] fallbacks)
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = root.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                    if (key?.GetValue(null) is string p && File.Exists(p))
                        return p;
                }
                catch { }
            }

            foreach (string p in fallbacks)
            {
                if (File.Exists(p))
                    return p;
            }
            return null;
        }

        /// <summary>按设置算出用哪个浏览器 exe；返回 null 表示交给系统默认打开。</summary>
        public static string Resolve(AppConfig cfg)
        {
            string mode = cfg?.BrowserMode;
            if (string.IsNullOrWhiteSpace(mode))
                mode = Auto;

            return mode switch
            {
                Edge => FindEdge() ?? FindChrome(),
                Chrome => FindChrome() ?? FindEdge(),
                Custom => !string.IsNullOrWhiteSpace(cfg.BrowserPath) && File.Exists(cfg.BrowserPath)
                    ? cfg.BrowserPath
                    : null,
                SystemDefault => null,
                _ => FindEdge()                                  // auto
            };
        }

        /// <summary>给界面显示用的一句话。</summary>
        public static string Describe(AppConfig cfg)
        {
            string mode = cfg?.BrowserMode ?? Auto;
            string exe = Resolve(cfg);

            if (exe != null)
            {
                string name = Path.GetFileNameWithoutExtension(exe);
                return mode == Custom ? $"自定义浏览器：{exe}" : $"{name}（{exe}）";
            }

            if (mode == Custom)
                return "自定义路径无效或不存在，会退回系统默认浏览器";
            return "系统默认浏览器";
        }

        // ==================== 打开 ====================

        /// <summary>用设置的浏览器打开网址；浏览器起不来就退回系统默认，保证一定能打开。</summary>
        public static void OpenUrl(string url, AppConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            string exe = Resolve(cfg);
            if (exe != null)
            {
                try
                {
                    var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
                    // 用 ArgumentList 而不是拼字符串，网址里的 & ? 之类不会被搞坏
                    psi.ArgumentList.Add(url);
                    Process.Start(psi);
                    return;
                }
                catch
                {
                    // 落到下面用系统默认再试一次
                }
            }

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        /// <summary>本地文件 / 文件夹：永远交给系统默认程序，不套浏览器设置。</summary>
        public static void OpenFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }
}
