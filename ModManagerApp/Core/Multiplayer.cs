using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SMM
{
    // ========================================================================
    //  联机 Mod 清单：导出自己的、导入好友的，比出差异
    // ========================================================================

    public sealed class ManifestMod
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }

        /// <summary>"Nexus:5815" / "GitHub:owner/repo"，没有更新源就是空。</summary>
        public string Source { get; set; }

        /// <summary>内容包（有 ContentPackFor）——联机时这类**必须**和主机一致。</summary>
        public string ContentPackFor { get; set; }

        public string Page { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
        public string VersionText => string.IsNullOrWhiteSpace(Version) ? "?" : Version;
    }

    public sealed class ModManifest
    {
        public int Format { get; set; } = 1;
        public string Tool { get; set; } = "StardewModManager";
        public string CreatedAt { get; set; }
        public string GameVersion { get; set; }
        public string SmapiVersion { get; set; }
        public List<ManifestMod> Mods { get; set; } = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static ModManifest Build(AppEnv env, IEnumerable<LocalMod> mods)
        {
            var m = new ModManifest
            {
                CreatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                GameVersion = GameLocator.GameVersion(env.GamePath),
                SmapiVersion = env.SmapiVersion
            };

            foreach (var mod in mods.Where(x => !x.Disabled).OrderBy(x => x.Name ?? x.Id))
            {
                var key = mod.SupportedKeys().FirstOrDefault() ?? mod.Keys.FirstOrDefault();
                m.Mods.Add(new ManifestMod
                {
                    Id = mod.Id,
                    Name = mod.Name,
                    Version = mod.Version,
                    Author = mod.Author,
                    Source = key?.Raw,
                    Page = key?.PageUrl,
                    ContentPackFor = mod.ContentPackForId
                });
            }
            return m;
        }

        public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

        /// <summary>紧凑文本版，方便直接发到 QQ / 微信里。</summary>
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 星露谷 Mod 管理器 · 联机 Mod 清单 v1");
            sb.AppendLine($"# 游戏 {GameVersion ?? "?"} | SMAPI {SmapiVersion ?? "?"} | 生成于 {CreatedAt}");
            sb.AppendLine("# 格式：UniqueID | 名称 | 版本 | 更新源");
            foreach (var m in Mods)
                sb.AppendLine($"{m.Id} | {m.Name} | {m.Version} | {m.Source ?? "-"}");
            return sb.ToString();
        }

        /// <summary>解析清单：JSON 和上面那种文本都能认。失败返回 null 并给出原因。</summary>
        public static ModManifest Parse(string text, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "内容是空的";
                return null;
            }

            string t = text.Trim();

            // 1) JSON
            if (t.StartsWith("{"))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<ModManifest>(t, JsonOpts);
                    if (m == null || m.Mods == null || m.Mods.Count == 0)
                    {
                        error = "JSON 里没有 Mod 列表";
                        return null;
                    }
                    return m;
                }
                catch (Exception ex)
                {
                    error = "JSON 解析失败：" + ex.Message;
                    return null;
                }
            }

            // 2) 文本（一行一个：ID | 名称 | 版本 | 源）
            var mods = new List<ManifestMod>();
            foreach (string raw in t.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                var parts = line.Split('|').Select(p => p.Trim()).ToArray();
                if (parts.Length < 2 || parts[0].Length == 0)
                    continue;

                mods.Add(new ManifestMod
                {
                    Id = parts[0],
                    Name = parts.Length > 1 ? parts[1] : parts[0],
                    Version = parts.Length > 2 ? parts[2] : null,
                    Source = parts.Length > 3 && parts[3] != "-" ? Normalize(parts[3]) : null
                });
            }

            if (mods.Count == 0)
            {
                error = "没解析出任何 Mod。要选导出好的清单文件，或者把清单文本完整复制过来。";
                return null;
            }

            return new ModManifest { Mods = mods };
        }

        private static string Normalize(string source)
        {
            if (string.IsNullOrWhiteSpace(source) || source == "-")
                return null;
            var k = UpdateKey.Parse(source) ?? UpdateKey.FromInput(source);
            return k?.Raw;
        }
    }

    // ========================================================================
    //  比对结果
    // ========================================================================

    public sealed class DiffEntry
    {
        /// <summary>missing = 你缺 / older = 你的旧 / newer = 你的新 / extra = 你多装</summary>
        public string Kind { get; set; }
        public string Id { get; set; }
        public ManifestMod Theirs { get; set; }
        public LocalMod Mine { get; set; }

        public string KindText => Kind switch
        {
            "missing" => "缺这个",
            "older" => "你的版本旧",
            "newer" => "你的版本新",
            _ => "你多装了"
        };

        public string Name => Theirs?.DisplayName ?? Mine?.Name ?? Id;
        public string MyVersion => Mine?.Version ?? "—";
        public string TheirVersion => TheirVersionValue ?? "—";

        private string TheirVersionValue;

        public string Source { get; set; }
        public string PageUrl { get; set; }

        /// <summary>是否只靠工具就能装（目前只有 GitHub 源可以）。</summary>
        public bool CanAutoInstall { get; set; }

        /// <summary>内容包——联机时这类必须一致，不一致最容易出问题。</summary>
        public bool MustMatch { get; set; }

        public string Note { get; set; }

        public void SetTheirVersion(string v) => TheirVersionValue = v;
    }

    public sealed class DiffResult
    {
        public List<DiffEntry> Entries { get; } = new();
        public int SameCount { get; set; }
        public List<string> Warnings { get; } = new();

        public int MissingCount => Entries.Count(e => e.Kind == "missing");
        public int VersionCount => Entries.Count(e => e.Kind == "older" || e.Kind == "newer");
        public int ExtraCount => Entries.Count(e => e.Kind == "extra");
    }

    public static class ManifestDiff
    {
        public static DiffResult Compare(AppEnv env, ModManifest theirs, List<LocalMod> mine)
        {
            var result = new DiffResult();
            var myById = new Dictionary<string, LocalMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in mine)
            {
                if (!string.IsNullOrWhiteSpace(m.Id))
                    myById[m.Id] = m;
            }

            var theirById = new Dictionary<string, ManifestMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in theirs.Mods)
            {
                if (!string.IsNullOrWhiteSpace(m.Id))
                    theirById[m.Id] = m;
            }

            // ---- 版本 / 缺失 ----
            foreach (var t in theirs.Mods)
            {
                if (string.IsNullOrWhiteSpace(t.Id))
                    continue;

                if (!myById.TryGetValue(t.Id, out var myMod))
                {
                    var e = new DiffEntry
                    {
                        Kind = "missing",
                        Id = t.Id,
                        Theirs = t,
                        Source = t.Source,
                        PageUrl = t.Page,
                        MustMatch = !string.IsNullOrWhiteSpace(t.ContentPackFor)
                    };
                    e.SetTheirVersion(t.Version);

                    // 我的内置映射里可能有源（作者没写 UpdateKeys 的情况）
                    if (string.IsNullOrWhiteSpace(e.Source) && UpdateKey.Known.TryGetValue(t.Id, out string known))
                        e.Source = known;
                    if (string.IsNullOrWhiteSpace(e.Source)
                        && env.Config.ExtraUpdateKeys != null
                        && env.Config.ExtraUpdateKeys.TryGetValue(t.Id, out string extra))
                        e.Source = extra;

                    var key = e.Source == null ? null : (UpdateKey.Parse(e.Source) ?? UpdateKey.FromInput(e.Source));
                    if (key != null)
                    {
                        e.PageUrl ??= key.PageUrl;
                        e.CanAutoInstall = key.Site == "GitHub";
                    }

                    e.Note = string.IsNullOrWhiteSpace(e.Source)
                        ? "好友的清单里也没写更新源，只能让好友告诉你下载地址"
                        : (e.CanAutoInstall ? "GitHub 上有 Release，可以直接装" : "需要到网页手动下载 zip（下完放进下载文件夹会自动装）");
                    result.Entries.Add(e);
                    continue;
                }

                // 版本比对
                int cmp = CompareVersion(myMod.Version, t.Version);
                if (cmp == 0)
                {
                    result.SameCount++;
                    continue;
                }

                var ve = new DiffEntry
                {
                    Kind = cmp < 0 ? "older" : "newer",
                    Id = t.Id,
                    Theirs = t,
                    Mine = myMod,
                    Source = t.Source ?? myMod.SupportedKeys().FirstOrDefault()?.Raw,
                    PageUrl = t.Page ?? myMod.Keys.FirstOrDefault()?.PageUrl,
                    MustMatch = !string.IsNullOrWhiteSpace(t.ContentPackFor) || !string.IsNullOrWhiteSpace(myMod.ContentPackForId)
                };
                ve.SetTheirVersion(t.Version);
                ve.Note = ve.Kind == "older"
                    ? (ve.MustMatch ? "内容包版本不一致，联机时**必须**统一，建议更新到你俩一致" : "建议统一成同一个版本")
                    : "你的版本比好友新，可以让好友更新到你这一版";
                result.Entries.Add(ve);
            }

            // ---- 我多装的 ----
            foreach (var m in mine)
            {
                if (string.IsNullOrWhiteSpace(m.Id) || theirById.ContainsKey(m.Id))
                    continue;

                var e = new DiffEntry
                {
                    Kind = "extra",
                    Id = m.Id,
                    Mine = m,
                    MustMatch = !string.IsNullOrWhiteSpace(m.ContentPackForId)
                };
                e.SetTheirVersion(null);
                e.Note = m.ContentPackForId != null
                    ? "这是内容包，好友没装 —— 联机时可能显示异常"
                    : "好友没装。UI / 工具类一般不影响；改世界的可能会不同步";
                result.Entries.Add(e);
            }

            // ---- 版本提醒 ----
            if (!string.IsNullOrWhiteSpace(theirs.SmapiVersion)
                && !string.IsNullOrWhiteSpace(env.SmapiVersion)
                && !SameVersion(theirs.SmapiVersion, env.SmapiVersion))
            {
                result.Warnings.Add($"SMAPI 版本不一致：你 {env.SmapiVersion}，好友 {theirs.SmapiVersion} —— "
                                    + "SMAPI 版本不同直接连不上，必须换成同一个版本。");
            }

            string myGame = GameLocator.GameVersion(env.GamePath);
            if (!string.IsNullOrWhiteSpace(theirs.GameVersion)
                && !string.IsNullOrWhiteSpace(myGame)
                && !SameVersion(theirs.GameVersion, myGame))
            {
                result.Warnings.Add($"游戏版本不一致：你 {myGame}，好友 {theirs.GameVersion} —— 需要都更新到最新。");
            }

            if (result.Warnings.Count == 0)
                result.Warnings.Add("游戏和 SMAPI 版本都一致，这点没问题。");

            // 排序：内容包 > 缺失 > 版本 > 多装
            result.Entries.Sort((a, b) =>
            {
                int r = Rank(a).CompareTo(Rank(b));
                return r != 0 ? r : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        private static int Rank(DiffEntry e)
        {
            int k = e.Kind switch
            {
                "missing" => 1,
                "older" => 2,
                "newer" => 3,
                _ => 4
            };
            return (e.MustMatch ? 0 : 10) + k;
        }

        private static bool SameVersion(string a, string b)
            => CompareVersion(a, b) == 0;

        /// <summary>版本比较；有一边解析不了就退化成字符串比较。</summary>
        private static int CompareVersion(string a, string b)
        {
            a = (a ?? "").Trim().TrimStart('v', 'V');
            b = (b ?? "").Trim().TrimStart('v', 'V');
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (SemVer.TryParse(a, out var va) && SemVer.TryParse(b, out var vb))
                return va.CompareTo(vb);
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ========================================================================
    //  联机环境自检：本机 IP、端口
    // ========================================================================

    public static class NetInfo
    {
        /// <summary>Stardew 联机默认用的端口。</summary>
        public const int StardewPort = 24642;

        /// <summary>网卡名/描述里出现这些词，基本是虚拟网卡、加速器或 VPN，不是真实局域网。</summary>
        private static readonly string[] VirtualHints =
        {
            "virtual", "vmware", "vmnet", "hyper-v", "vethernet", "zerotier", "radmin", "tailscale",
            "tap", "tun", "vpn", "wireguard", "clash", "singbox", "sing-box", "docker", "wsl",
            "loopback", "bluetooth", "npcap", "proxy", "vethernet", "hamachi", "openvpn"
        };

        public sealed class LocalIp
        {
            public string Address { get; set; }
            public string Adapter { get; set; }

            /// <summary>排序后的第一个 —— 大概率是好友该填的那个。</summary>
            public bool Preferred { get; set; }

            public bool Virtual { get; set; }

            public string Display => Preferred
                ? $"{Address}（推荐，{Adapter}）"
                : $"{Address}（{Adapter}{(Virtual ? "，虚拟网卡" : "")}）";
        }

        /// <summary>
        /// 本机可用的局域网 IPv4。真网卡的 192.168 / 10 / 172.16-31 排前面，
        /// 虚拟网卡、加速器、VPN 的地址排后面（它们填给好友是连不上的）。
        /// </summary>
        public static List<LocalIp> LocalIPv4()
        {
            var list = new List<LocalIp>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    string desc = ((ni.Name ?? "") + " " + (ni.Description ?? "")).ToLowerInvariant();
                    bool virt = VirtualHints.Any(desc.Contains);

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        string ip = addr.Address.ToString();
                        if (ip.StartsWith("169.254."))     // 自动私有地址，说明没连上
                            continue;

                        list.Add(new LocalIp
                        {
                            Address = ip,
                            Adapter = ni.Name,
                            Virtual = virt
                        });
                    }
                }
            }
            catch { }

            list.Sort((a, b) => Score(a).CompareTo(Score(b)));
            if (list.Count > 0)
                list[0].Preferred = true;
            return list;
        }

        private static int Score(LocalIp x)
        {
            int s = x.Virtual ? 100 : 0;

            if (x.Address.StartsWith("192.168."))
                s += 0;
            else if (x.Address.StartsWith("10."))
                s += 1;
            else if (Is172Private(x.Address))
                s += 1;
            else if (x.Address.StartsWith("100."))
                s += 6;      // 100.64/10 是运营商级 NAT，Tailscale / ZeroTier 也在这段
            else
                s += 3;      // 看着像公网地址，多半是 VPN

            return s;
        }

        private static bool Is172Private(string ip)
        {
            var parts = ip.Split('.');
            if (parts.Length < 2 || parts[0] != "172")
                return false;
            return int.TryParse(parts[1], out int second) && second >= 16 && second <= 31;
        }

        /// <summary>24642 端口的实际情况，用来判断「房间到底开了没有」。</summary>
        public sealed class RoomStatus
        {
            public bool GameRunning { get; set; }
            public bool PortListening { get; set; }

            /// <summary>ok / warn / info —— 决定界面上那个小圆点的颜色。</summary>
            public string Level { get; set; } = "info";

            /// <summary>一句话结论。</summary>
            public string Text { get; set; } = "";

            /// <summary>该怎么做。</summary>
            public string Hint { get; set; } = "";
        }

        /// <summary>
        /// 房间开了没有：看游戏进程在不在 + 24642 端口有没有在监听。
        /// 判定是「大概率」——游戏有可能先开监听再建房，所以措辞上留了余地。
        /// </summary>
        public static RoomStatus Room()
        {
            var r = new RoomStatus { GameRunning = AppEnv.IsGameRunning() };

            try
            {
                var props = IPGlobalProperties.GetIPGlobalProperties();
                r.PortListening = props.GetActiveUdpListeners().Any(x => x.Port == StardewPort)
                                  || props.GetActiveTcpListeners().Any(x => x.Port == StardewPort);
            }
            catch (Exception ex)
            {
                r.Level = "warn";
                r.Text = "端口检测失败";
                r.Hint = "读不到端口信息（" + ex.Message + "），点「重新检测」再试一次";
                return r;
            }

            if (!r.GameRunning && !r.PortListening)
            {
                r.Level = "info";
                r.Text = "游戏没在运行 · 房间还没开";
                r.Hint = "点上面的「打开游戏」：标题界面 →「一起玩」→「主持」→ 选存档";
            }
            else if (!r.GameRunning && r.PortListening)
            {
                r.Level = "warn";
                r.Text = StardewPort + " 端口被别的程序占用了 · 这样开不了房";
                r.Hint = "关掉占用它的程序（加速器、别的游戏），或者重启电脑再开游戏";
            }
            else if (r.PortListening)
            {
                r.Level = "ok";
                r.Text = "房间已开（" + StardewPort + " 端口在监听）";
                r.Hint = "把上面的 IP 发给好友，他「一起玩 → 加入」填进去就能进来";
            }
            else
            {
                r.Level = "info";
                r.Text = "游戏开着，但房间还没开";
                r.Hint = "标题界面 →「一起玩」→「主持」→ 选存档；开好房这里会自动变成「房间已开」";
            }
            return r;
        }

        /// <summary>
        /// 防火墙检查结果。
        /// 为什么要查：好友「输了 IP 就是进不来」最常见的原因就是主机的 Windows 防火墙
        /// 没放行游戏 —— 而且很多人是用 SMAPI 启动的（进程是 StardewModdingAPI.exe），
        /// 只放行了 Stardew Valley.exe 是不够的。
        /// </summary>
        public sealed class FirewallInfo
        {
            /// <summary>开着的防火墙配置文件（Domain / Private / Public）。</summary>
            public List<string> EnabledProfiles { get; } = new List<string>();

            /// <summary>已经有入站放行规则的 exe 文件名。</summary>
            public List<string> Allowed { get; } = new List<string>();

            /// <summary>缺放行规则的 exe 完整路径（要修的就是这些）。</summary>
            public List<string> Missing { get; } = new List<string>();

            public string Level { get; set; } = "info";
            public string Text { get; set; } = "";
            public string Hint { get; set; } = "";

            public bool NeedsFix => Missing.Count > 0;
        }

        /// <summary>游戏目录里需要放行的 exe：SMAPI 在前，因为它才是开房时跑的那个。</summary>
        public static List<string> GameExes(string gamePath)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(gamePath))
                return list;
            foreach (string name in new[] { "StardewModdingAPI.exe", "Stardew Valley.exe" })
            {
                string p = Path.Combine(gamePath, name);
                if (File.Exists(p))
                    list.Add(p);
            }
            return list;
        }

        public static Task<FirewallInfo> CheckFirewallAsync(string gamePath)
            => Task.Run(() => CheckFirewall(gamePath));

        /// <summary>读防火墙状态和放行规则（只用 PowerShell 读，不需要管理员权限）。</summary>
        public static FirewallInfo CheckFirewall(string gamePath)
        {
            var info = new FirewallInfo();
            var exes = GameExes(gamePath);

            // 输出格式固定，方便解析（全程 ASCII，避免编码踩坑）：
            //   P|Domain=False,Private=True,Public=True
            //   R|Inbound|Allow|True|E:\...\Stardew Valley.exe
            const string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
                "$p=Get-NetFirewallProfile|ForEach-Object{$_.Name+'='+$_.Enabled};" +
                "'P|'+($p -join ',');" +
                "Get-NetFirewallApplicationFilter|Where-Object{$_.Program -match 'Stardew'}|ForEach-Object{" +
                "$r=$_|Get-NetFirewallRule;if($r){'R|'+$r.Direction+'|'+$r.Action+'|'+$r.Enabled+'|'+$_.Program}}";

            string output;
            try
            {
                output = RunPowershell(script);
            }
            catch (Exception ex)
            {
                info.Level = "warn";
                info.Text = "防火墙没查成";
                info.Hint = "读不到防火墙信息（" + ex.Message + "）。这不影响联机；好友真进不来时手动放行："
                            + "设置 → 网络和 Internet → Windows 防火墙 → 允许应用通过防火墙 → 勾上 StardewModdingAPI.exe";
                return info;
            }

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("P|"))
                {
                    foreach (string part in line.Substring(2).Split(','))
                    {
                        int eq = part.IndexOf('=');
                        if (eq > 0 && part.Substring(eq + 1).Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
                            info.EnabledProfiles.Add(part.Substring(0, eq).Trim());
                    }
                }
                else if (line.StartsWith("R|"))
                {
                    var f = line.Split('|');
                    if (f.Length < 5)
                        continue;
                    if (f[1].Trim().Equals("Inbound", StringComparison.OrdinalIgnoreCase)
                        && f[2].Trim().Equals("Allow", StringComparison.OrdinalIgnoreCase)
                        && f[3].Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
                        allowed.Add(Path.GetFileName(f[4].Trim()));
                }
            }

            foreach (string exe in exes)
            {
                if (allowed.Contains(Path.GetFileName(exe)))
                    info.Allowed.Add(Path.GetFileName(exe));
                else
                    info.Missing.Add(exe);
            }

            if (info.EnabledProfiles.Count == 0)
            {
                info.Level = "ok";
                info.Text = "Windows 防火墙三个配置文件都关着 · 它不会拦你";
                info.Hint = "所以好友进不来不会是防火墙的问题。（顺带一提，防火墙关着本身有风险，你自己权衡）";
                info.Missing.Clear();          // 防火墙压根没开，不用放行
            }
            else if (info.Missing.Count == 0)
            {
                info.Level = "ok";
                info.Text = "防火墙已放行 · 好友能连进来";
                info.Hint = "已放行：" + string.Join("、", info.Allowed);
            }
            else
            {
                info.Level = "warn";
                info.Text = "防火墙开着，但没放行 " + string.Join("、", info.Missing.Select(Path.GetFileName));
                info.Hint = "这是好友「输了 IP 也进不来」的头号原因 —— 点「一键放行」会弹 UAC，同意一下就好";
            }
            return info;
        }

        /// <summary>
        /// 以管理员身份给游戏的 exe 加入站放行规则（会弹 UAC）。
        /// 两个 exe 写在同一个批处理里跑，只弹一次 UAC；规则名用英文，避免批处理编码踩坑。
        /// </summary>
        public static void FixFirewall(IEnumerable<string> exes)
        {
            var list = exes.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)).ToList();
            if (list.Count == 0)
                throw new Exception("没找到要放行的 exe（游戏目录可能不对）");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            foreach (string p in list)
            {
                string name = "Stardew Multiplayer - " + Path.GetFileNameWithoutExtension(p);
                sb.AppendLine("netsh advfirewall firewall delete rule name=\"" + name + "\" > nul 2>&1");
                sb.AppendLine("netsh advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow "
                              + "program=\"" + p + "\" enable=yes profile=any protocol=any");
            }

            string bat = Path.Combine(Path.GetTempPath(), "smm-firewall.bat");
            File.WriteAllText(bat, sb.ToString(), new UTF8Encoding(false));

            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",                          // 这句触发 UAC 提权
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
        }

        /// <summary>跑一段 PowerShell 并把标准输出拿回来（不弹窗口）。</summary>
        private static string RunPowershell(string script)
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                throw new Exception("起不了 powershell");

            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(20000))
            {
                try { proc.Kill(); } catch { }
                throw new Exception("读取超时");
            }

            string stdout = outTask.GetAwaiter().GetResult();
            string stderr = errTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
                throw new Exception(string.IsNullOrWhiteSpace(stderr) ? "退出码 " + proc.ExitCode : stderr.Trim());
            return stdout;
        }

        /// <summary>拼一段可以直接粘给好友的邀请信息。没有可用 IP 时返回 null。</summary>
        public static string InviteText()
        {
            var ips = LocalIPv4();
            if (ips.Count == 0)
                return null;

            string ip = ips[0].Address;
            var room = Room();

            var sb = new StringBuilder();
            sb.AppendLine("【星露谷联机】主机地址：" + ip);
            sb.AppendLine("怎么进：标题界面 →「一起玩」→「加入」→ 地址填 " + ip
                          + "（端口 " + StardewPort + " 默认就行）");
            sb.AppendLine("前提：两边的游戏版本和 Mod 要一致，不一致会进不去或进去后贴图错乱");
            sb.Append(room.PortListening && room.GameRunning
                ? "（我这边房间已经开好了，直接进）"
                : "（我这边还没开好房，稍等我一下）");
            return sb.ToString();
        }
    }
}
