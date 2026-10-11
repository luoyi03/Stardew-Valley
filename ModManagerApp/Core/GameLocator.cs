using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SMM
{
    /// <summary>自动寻找星露谷安装目录 / SMAPI 版本。</summary>
    public static class GameLocator
    {
        public static bool IsValid(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return false;
            return File.Exists(Path.Combine(dir, "Stardew Valley.exe"))
                || Directory.Exists(Path.Combine(dir, "Mods"));
        }

        public static bool HasSmapi(string gamePath) => SmapiVersion(gamePath) != null;

        /// <summary>读 StardewModdingAPI.dll 的版本号，读不到返回 null。</summary>
        public static string SmapiVersion(string gamePath)
        {
            if (string.IsNullOrWhiteSpace(gamePath))
                return null;
            foreach (string name in new[] { "StardewModdingAPI.dll", "StardewModdingAPI.exe" })
            {
                try
                {
                    string p = Path.Combine(gamePath, name);
                    if (!File.Exists(p))
                        continue;
                    var info = FileVersionInfo.GetVersionInfo(p);
                    string v = info.ProductVersion ?? info.FileVersion;
                    if (!string.IsNullOrWhiteSpace(v))
                        return CleanVersion(v) ?? v.Trim();
                    var asm = System.Reflection.AssemblyName.GetAssemblyName(p);
                    if (asm?.Version != null)
                        return asm.Version.ToString();
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 从各种奇怪的版本字符串里抠出标准的 x.y.z。
        /// 游戏 exe 的 ProductVersion 长得像 "1.6.15, , 24356,"，SMAPI 的像 "4.5.2+821167e5"，
        /// 都不能直接拿去问接口（SMAPI 更新清单收到非法 gameVersion 会直接返回空）。
        /// </summary>
        public static string CleanVersion(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var m = Regex.Match(raw, @"\d+\.\d+(?:\.\d+)?");
            return m.Success ? m.Value : null;
        }

        /// <summary>
        /// 启动游戏。优先用 SMAPI（`StardewModdingAPI.exe`），这样 Mod 才会加载；
        /// 没装 SMAPI 就退回原版 `Stardew Valley.exe`。返回实际用的启动程序名。
        /// </summary>
        public static string Launch(AppEnv env)
        {
            if (env == null || string.IsNullOrWhiteSpace(env.GamePath))
                throw new Exception("还没设置游戏目录");

            string exe = LaunchTarget(env);
            if (exe == null)
                throw new Exception("游戏目录里找不到启动程序：" + env.GamePath);

            // 工作目录必须是游戏根目录，不然 SMAPI 找不到 Mods
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = env.GamePath,
                UseShellExecute = true
            };
            Process.Start(psi);
            env.Log("已启动游戏：" + exe);
            return Path.GetFileNameWithoutExtension(exe);
        }

        /// <summary>算出会启动哪个 exe（优先 SMAPI）；找不到返回 null。只查不启动，供自检用。</summary>
        public static string LaunchTarget(AppEnv env)
        {
            if (env == null || string.IsNullOrWhiteSpace(env.GamePath))
                return null;

            string smapi = Path.Combine(env.GamePath, "StardewModdingAPI.exe");
            if (File.Exists(smapi))
                return smapi;

            string vanilla = Path.Combine(env.GamePath, "Stardew Valley.exe");
            if (File.Exists(vanilla))
                return vanilla;

            return null;
        }

        /// <summary>读 Stardew Valley.exe 的版本号（如 1.6.15），读不到返回 null。</summary>
        public static string GameVersion(string gamePath)
        {
            if (string.IsNullOrWhiteSpace(gamePath))
                return null;
            foreach (string name in new[] { "Stardew Valley.exe", "StardewValley.exe" })
            {
                try
                {
                    string p = Path.Combine(gamePath, name);
                    if (!File.Exists(p))
                        continue;
                    string v = CleanVersion(FileVersionInfo.GetVersionInfo(p).ProductVersion);
                    if (!string.IsNullOrWhiteSpace(v))
                        return v;
                }
                catch { }
            }
            return null;
        }

        public static string Detect()
        {
            foreach (string c in Candidates())
            {
                if (IsValid(c))
                    return Normalize(c);
            }
            return null;
        }

        private static string Normalize(string dir)
        {
            try { return Path.GetFullPath(dir).TrimEnd('\\', '/'); }
            catch { return dir; }
        }

        private static IEnumerable<string> Candidates()
        {
            var list = new List<string>();

            foreach (string steam in FindSteamRoots())
            {
                foreach (string lib in LibraryFolders(steam))
                    list.Add(Path.Combine(lib, "steamapps", "common", "Stardew Valley"));
                list.Add(Path.Combine(steam, "steamapps", "common", "Stardew Valley"));
            }

            // 常见手装 / GOG 位置
            list.Add(@"C:\GOG Games\Stardew Valley");
            list.Add(@"C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley");
            list.Add(@"C:\Program Files\GOG Galaxy\Games\Stardew Valley");
            list.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Stardew Valley");
            list.Add(@"C:\Program Files\Steam\steamapps\common\Stardew Valley");

            foreach (char drive in "CDEFGH")
            {
                list.Add($@"{drive}:\Steam\steamapps\common\Stardew Valley");
                list.Add($@"{drive}:\SteamLibrary\steamapps\common\Stardew Valley");
                list.Add($@"{drive}:\Games\Steam\steamapps\common\Stardew Valley");
                list.Add($@"{drive}:\Program Files (x86)\Steam\steamapps\common\Stardew Valley");
                list.Add($@"{drive}:\GOG Games\Stardew Valley");
            }

            // 用户本机已知路径（保底）
            list.Add(@"E:\Steam\steamapps\common\Stardew Valley");

            return list.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> FindSteamRoots()
        {
            var roots = new List<string>();
            void Add(string p)
            {
                if (!string.IsNullOrWhiteSpace(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                    roots.Add(p);
            }

            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                Add(k?.GetValue("SteamPath") as string);
                Add(k?.GetValue("SteamExe") as string is string exe ? Path.GetDirectoryName(exe) : null);
            }
            catch { }
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                Add(k?.GetValue("InstallPath") as string);
            }
            catch { }
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
                Add(k?.GetValue("InstallPath") as string);
            }
            catch { }

            return roots;
        }

        /// <summary>解析 steamapps\libraryfolders.vdf 里的所有库路径。</summary>
        private static List<string> LibraryFolders(string steamRoot)
        {
            var libs = new List<string>();
            try
            {
                string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    string text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, @"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase))
                    {
                        string p = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (!string.IsNullOrWhiteSpace(p) && !libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                            libs.Add(p);
                    }
                    // 老版 vdf：直接就是数字键路径
                    foreach (Match m in Regex.Matches(text, @"""[0-9]+""\s+""([^""]+)"""))
                    {
                        string p = m.Groups[1].Value.Replace(@"\\", @"\");
                        if (!string.IsNullOrWhiteSpace(p) && !libs.Contains(p, StringComparer.OrdinalIgnoreCase))
                            libs.Add(p);
                    }
                }
            }
            catch { }
            if (!libs.Contains(steamRoot, StringComparer.OrdinalIgnoreCase))
                libs.Insert(0, steamRoot);
            return libs;
        }
    }
}
