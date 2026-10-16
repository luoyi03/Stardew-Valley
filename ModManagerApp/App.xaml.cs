using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace SMM
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 隐藏的诊断模式：StardewModManager.exe --selftest [--network]
            // 把扫描 / 依赖检查 / 更新检测的结果写进 %APPDATA%\StardewModManager\selftest.txt
            if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                bool network = e.Args.Any(a => string.Equals(a, "--network", StringComparison.OrdinalIgnoreCase));
                Task.Run(() => RunSelfTest(network)).GetAwaiter().GetResult();
                Shutdown(0);
                return;
            }

            // 开发用：StardewModManager.exe --shot [输出目录]
            // 把每个页面用真实数据渲染成 PNG，方便肉眼确认界面没问题
            if (e.Args.Any(a => string.Equals(a, "--shot", StringComparison.OrdinalIgnoreCase)))
            {
                int idx = Array.FindIndex(e.Args, a => string.Equals(a, "--shot", StringComparison.OrdinalIgnoreCase));
                string dir = idx >= 0 && idx + 1 < e.Args.Length && !e.Args[idx + 1].StartsWith("--")
                    ? e.Args[idx + 1]
                    : Path.Combine(Path.GetTempPath(), "smm-shots");

                try
                {
                    RunShots(dir);
                }
                catch (Exception ex)
                {
                    Trace(dir, "失败：" + ex);
                }
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                try { AppEnv.Current?.Log("未处理异常：" + args.ExceptionObject); } catch { }
            };
            TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

            MainWindow = new SMM.MainWindow();
            MainWindow.Show();
        }

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try { AppEnv.Current?.Log("界面异常：" + e.Exception); } catch { }
            MessageBox.Show(
                "出错了：" + e.Exception.Message + "\n\n详细信息已写入日志：" +
                (AppEnv.Current?.LogFile ?? ""),
                "星露谷 Mod 管理器", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        }

        // ==================== 诊断 ====================

        private sealed class InlineProgress : IProgress<string>
        {
            private readonly Action<string> _action;
            public InlineProgress(Action<string> action) => _action = action;
            public void Report(string value) => _action(value);
        }

        private static void RunSelfTest(bool network)
        {
            var sb = new StringBuilder();
            string outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewModManager");
            Directory.CreateDirectory(outDir);

            try
            {
                var env = AppEnv.Create();
                env.EnsureDirs();

                sb.AppendLine("=== 环境 ===");
                sb.AppendLine("游戏目录 : " + env.GamePath);
                sb.AppendLine("Mods目录 : " + env.ModsPath);
                sb.AppendLine("数据目录 : " + env.DataDir);
                sb.AppendLine("备份目录 : " + env.BackupDir);
                sb.AppendLine("SMAPI    : " + (env.SmapiVersion ?? "(未检测到)"));
                string launch = GameLocator.LaunchTarget(env);
                sb.AppendLine("启动游戏 : " + (launch == null
                    ? "✗ 找不到启动程序"
                    : Path.GetFileName(launch)
                      + (launch.IndexOf("ModdingAPI", StringComparison.OrdinalIgnoreCase) >= 0
                          ? "（走 SMAPI，Mod 会加载）"
                          : "（原版，Mod 不会加载）")));
                var ips = NetInfo.LocalIPv4();
                sb.AppendLine("局域网IP : " + (ips.Count == 0
                    ? "(没找到)"
                    : string.Join("　", ips.Select(x => x.Display))));
                var room = NetInfo.Room();
                sb.AppendLine("联机房间 : " + room.Text + "　｜　" + room.Hint);
                var fw = NetInfo.CheckFirewall(env.GamePath);
                sb.AppendLine("防火墙   : " + fw.Text);
                sb.AppendLine("           " + fw.Hint);
                sb.AppendLine("邀请信息 : "
                              + (NetInfo.InviteText() ?? "(没有可用 IP)").Replace("\r\n", " / "));
                sb.AppendLine("浏览器   : " + Browser.Describe(env.Config)
                              + "　[Edge=" + (Browser.FindEdge() ?? "未找到")
                              + "  Chrome=" + (Browser.FindChrome() ?? "未找到") + "]");
                sb.AppendLine();

                var mods = ModScanner.Scan(env.ModsPath, env.DisabledDir);
                sb.AppendLine($"=== 扫描到 {mods.Count} 个 Mod ===");
                sb.AppendLine($"（界面显示名格式：中文名（English Name））");
                foreach (var m in mods)
                {
                    sb.AppendLine($"  [{m.Id}] {ModNames.Display(m)}"
                                  + (string.Equals(ModNames.Display(m), m.Name, StringComparison.Ordinal)
                                      ? "" : $"(原名 {m.Name})")
                                  + $" v{m.Version}"
                                  + (m.Disabled ? " (已禁用)" : "")
                                  + (m.IsDuplicate ? " (重复)" : "")
                                  + "  目录=" + m.DisplayPath
                                  + "  更新源=" + (m.Keys.Count == 0 ? "无" : string.Join(" | ", m.Keys.Select(k => k.Raw))));
                }
                sb.AppendLine();

                sb.AppendLine("=== 中文名（写进 manifest.json）===");
                sb.AppendLine($"配置里维护过中文名的：{env.Config.ChineseNames?.Count ?? 0} 个");
                foreach (var m in mods)
                {
                    if (!ModNamer.HasChineseName(env, m))
                        continue;
                    sb.AppendLine($"  {m.Id} → {m.Name}（原名 {m.OriginalName}，manifest 里现在 = "
                                  + (ModNamer.ReadCurrentName(m.Dir) ?? "?") + "）");
                }
                sb.AppendLine("  读写自测：" + NameWriteProbe());
                sb.AppendLine("  真实清单副本：" + RealManifestProbe(mods));
                sb.AppendLine();

                sb.AppendLine("=== 快捷键（读各 Mod 的 config.json）===");
                var hotkeys = Hotkeys.Scan(env);
                sb.AppendLine($"  读到 {hotkeys.Count} 条按键绑定"
                              + $"（能读到的前提是该 Mod 已经生成 config.json）");
                foreach (var h in hotkeys.Take(40))
                    sb.AppendLine($"  - {h.ModName}  {h.JsonPath} = {h.ValueText}{(h.IsDisabled ? "" : "")}");
                var hotConflicts = Hotkeys.Conflicts(hotkeys);
                if (hotConflicts.Count == 0)
                    sb.AppendLine("  重复检查：没发现重复");
                else
                    foreach (var c in hotConflicts)
                        sb.AppendLine($"  重复：{c.Text}");
                sb.AppendLine("  键名校验自测："
                              + $"F5={(Hotkeys.ValidateInput("F5") == null ? "OK" : "✗")} "
                              + $"LeftShift + F5={(Hotkeys.ValidateInput("LeftShift + F5") == null ? "OK" : "✗")} "
                              + $"F5, F6={(Hotkeys.ValidateInput("F5, F6") == null ? "OK" : "✗")} "
                              + $"None={(Hotkeys.ValidateInput("None") == null ? "OK" : "✗")} "
                              + $"乱填被拦={(Hotkeys.ValidateInput("随便写") != null ? "OK" : "✗")} "
                              + $"归一化 F5+LeftShift=LeftShift+F5={(Hotkeys.Normalize("F5 + LeftShift") == Hotkeys.Normalize("LeftShift + F5") ? "OK" : "✗")}");
                sb.AppendLine("  写入自测：" + HotkeyWriteProbe(hotkeys));

                // 改键拦重 + 多键写法查重（假数据，不碰真实配置）
                var ka = new HotkeyBinding { ModId = "a", ModName = "甲", JsonPath = ".A", Value = "F5" };
                var kb = new HotkeyBinding { ModId = "b", ModName = "乙", JsonPath = ".B", Value = "F5, F6" };
                var kc = new HotkeyBinding { ModId = "c", ModName = "丙", JsonPath = ".C", Value = "None" };
                var klist = new List<HotkeyBinding> { ka, kb, kc };

                var conf = Hotkeys.Conflicts(klist);
                sb.AppendLine($"  多键写法查重：「F5」与「F5, F6」→ {conf.Count} 处（应为 1）{(conf.Count == 1 && conf[0].Items.Count == 2 ? " OK" : " ✗")}"
                              + $"；禁用的不算 {(conf.Any(c => c.Items.Any(i => i.ModId == "c")) ? "✗" : "OK")}");

                var h1 = Hotkeys.ConflictsFor(klist, ka, "F5");
                var h2 = Hotkeys.ConflictsFor(klist, ka, "LeftShift + F5");
                var h3 = Hotkeys.ConflictsFor(klist, ka, "F6");
                var h4 = Hotkeys.ConflictsFor(klist, ka, "None");
                sb.AppendLine($"  改键拦重：改成 F5 → 撞 {h1.Count} 处（应 1）{(h1.Count == 1 ? " OK" : " ✗")}"
                              + $"；改成 LeftShift + F5 → 撞 {h2.Count} 处（应 0）{(h2.Count == 0 ? " OK" : " ✗")}"
                              + $"；改成 F6 → 撞 {h3.Count} 处（应 1）{(h3.Count == 1 ? " OK" : " ✗")}"
                              + $"；改成 None → 撞 {h4.Count} 处（应 0）{(h4.Count == 0 ? " OK" : " ✗")}");

                // 按键捕获的键名映射自测（按一下键 → 认得出是什么键）
                sb.AppendLine("  捕获键名映射自测："
                              + string.Join("　", new[]
                              {
                                  System.Windows.Input.Key.F5,
                                  System.Windows.Input.Key.A,
                                  System.Windows.Input.Key.D5,
                                  System.Windows.Input.Key.NumPad3,
                                  System.Windows.Input.Key.LeftCtrl,
                                  System.Windows.Input.Key.OemComma,
                                  System.Windows.Input.Key.PageUp,
                                  System.Windows.Input.Key.Space
                              }.Select(k => k + "→" + (TextInputDialog.KeyName(k) ?? "（不支持）"))));
                sb.AppendLine();

                sb.AppendLine("=== AI 设置（InGameCodex / StardewAI）===");
                foreach (var (folder, prefix, name) in new[]
                {
                    ("InGameCodex", ".Ai.", "游戏内图鉴（InGameCodex）"),
                    ("StardewAI", ".", "游戏内 AI 助手（StardewAI）")
                })
                {
                    string cfg = JsonEdit.ConfigPathOf(env, folder);
                    if (cfg == null || !File.Exists(cfg))
                    {
                        sb.AppendLine($"  {name}：还没有 config.json（先进一次游戏）");
                        continue;
                    }

                    string key = JsonEdit.Read(cfg, prefix + "ApiKey") ?? "";
                    sb.AppendLine($"  {name}：{JsonEdit.Read(cfg, prefix + "BaseUrl")}"
                                  + $"　·　模型 {JsonEdit.Read(cfg, prefix + "Model")}"
                                  + $"　·　方案 {JsonEdit.Read(cfg, prefix + "Preset")}"
                                  + $"　·　密钥 {key.Length} 位");
                }

                var merged1 = AiModels.Merge(new[] { "hy4-preview-f", "deepseek-v4.1-flash" }, "hy3");
                var merged2 = AiModels.Merge(null, "hy4-preview-f");
                sb.AppendLine("  候选合并自测：hy4 只来自接口 → "
                              + (merged1.Contains("hy4-preview-f") ? "✗ 没屏蔽掉" : "已屏蔽 OK")
                              + "；常备款齐全 → "
                              + (AiModels.Standby.All(m => merged1.Contains(m)) ? "OK" : "✗")
                              + $"；当前值不会被删 → {(merged2.Contains("hy4-preview-f") ? "OK" : "✗")}"
                              + $"（候选：{string.Join("、", merged1)}）");
                sb.AppendLine();

                sb.AppendLine("=== 互斥组 ===");
                var groupNames = ModGroups.GroupNames(env);
                if (groupNames.Count == 0)
                    sb.AppendLine("  还没分组（在「Mod 管理」里选中几个 Mod → 点「互斥组…」）");
                foreach (string g in groupNames)
                {
                    var members = mods.Where(m => string.Equals(ModGroups.GroupOf(env, m), g,
                        StringComparison.CurrentCultureIgnoreCase)).ToList();
                    int on = members.Count(m => !m.Disabled);
                    sb.AppendLine($"  「{g}」共 {members.Count} 个，启用中 {on} 个"
                                  + (on > 1 ? "  ← 同组多个启用，会冲突" : "")
                                  + "：" + string.Join("、", members.Select(m => m.Name + (m.Disabled ? "(禁用)" : ""))));
                }

                // 互斥组逻辑自测（用假数据，不碰真实配置）
                var fakeEnv = new AppEnv { Config = new AppConfig() };
                fakeEnv.Config.ExclusiveGroups["fake.horse.a"] = "马皮肤";
                fakeEnv.Config.ExclusiveGroups["fake.horse.b"] = "马皮肤";
                fakeEnv.Config.ExclusiveGroups["fake.dog.a"] = "狗皮肤";
                var fa = new LocalMod { Id = "fake.horse.a", Name = "马皮肤A", Dir = "A" };
                var fb = new LocalMod { Id = "fake.horse.b", Name = "马皮肤B", Dir = "B" };
                var fc = new LocalMod { Id = "fake.dog.a", Name = "狗皮肤A", Dir = "C" };
                var fakeList = new List<LocalMod> { fa, fb, fc };
                var wouldStop = ModGroups.ToDisable(fakeEnv, fa, fakeList);
                sb.AppendLine("  逻辑自测：启用「马皮肤A」会自动停用 "
                              + string.Join("、", wouldStop.Select(x => x.Name)) + "（应为 马皮肤B）"
                              + (wouldStop.Count == 1 && wouldStop[0].Id == "fake.horse.b" ? " ✔" : " ✘"));
                bool leak = ModGroups.Siblings(fakeEnv, fa, fakeList).Any(x => x.Id == "fake.dog.a");
                sb.AppendLine("  逻辑自测：别组（狗皮肤）不会被误停用 " + (leak ? "✘" : "✔"));
                sb.AppendLine();

                sb.AppendLine("=== 新装 Mod 会弹窗问中文名吗 ===");
                var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int askCount = 0;
                foreach (var m in mods)
                {
                    if (!NewModNamePrompt.ShouldAsk(env, m, empty))
                        continue;
                    askCount++;
                    sb.AppendLine($"  会问：{m.Id}（{m.Name}）");
                }
                sb.AppendLine($"  按「全是新装的」算会问 {askCount} 个 / 共 {mods.Count} 个"
                              + "（已有内置中文名或配过中文名的不会问）");
                var fakeNew = new LocalMod { Id = "someone.brand.new.mod", Name = "Brand New Mod" };
                sb.AppendLine($"  陌生英文名新 Mod：{(NewModNamePrompt.ShouldAsk(env, fakeNew, null) ? "会问 ✔" : "不问 ✘")}");
                var fakeCn = new LocalMod { Id = "someone.brand.new.mod", Name = "全新模组" };
                sb.AppendLine($"  manifest 已是中文的新 Mod：{(NewModNamePrompt.ShouldAsk(env, fakeCn, null) ? "会问 ✘" : "不问 ✔")}");
                var fakeKnown = new LocalMod { Id = "Pathoschild.LookupAnything", Name = "Lookup Anything" };
                sb.AppendLine($"  内置表里有中文名的新 Mod：{(NewModNamePrompt.ShouldAsk(env, fakeKnown, null) ? "会问" : "不问 ✔（界面本来就显示中文）")}");
                var fakeOld = new LocalMod { Id = "someone.brand.new.mod", Name = "Brand New Mod" };
                var snap = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fakeOld.Id };
                sb.AppendLine($"  更新已有的 Mod：{(NewModNamePrompt.ShouldAsk(env, fakeOld, snap) ? "会问 ✘" : "不问 ✔")}");
                sb.AppendLine();

                var issues = Analyzer.Analyze(env, mods);
                sb.AppendLine($"=== 问题检查：{issues.Count} 个 ===");
                foreach (var i in issues)
                    sb.AppendLine($"  [{i.LevelText}] {i.Kind} · {i.ModName} :: {i.Detail}");
                sb.AppendLine();

                var backups = Backups.List(env);
                sb.AppendLine($"=== 备份：{backups.Count} 份 ===");
                foreach (var b in backups)
                    sb.AppendLine($"  {b.Title} {b.Version} {b.TimeText} {b.SizeText} ({b.Reason})");
                sb.AppendLine();

                // ---------- 联机：清单导出 / 导入 / 比对 ----------
                sb.AppendLine("=== 联机 Mod 清单 ===");
                var manifest = ModManifest.Build(env, mods);
                sb.AppendLine($"导出：{manifest.Mods.Count} 个 Mod　游戏 {manifest.GameVersion}　SMAPI {manifest.SmapiVersion}");

                var backJson = ModManifest.Parse(manifest.ToJson(), out string jsonErr);
                var backText = ModManifest.Parse(manifest.ToText(), out string textErr);
                sb.AppendLine($"JSON 往返：{(backJson == null ? "失败 → " + jsonErr : backJson.Mods.Count + " 个")}"
                              + $"　文本往返：{(backText == null ? "失败 → " + textErr : backText.Mods.Count + " 个")}");
                sb.AppendLine("  乱输入容忍：" + (ModManifest.Parse("这不是清单", out string badErr) == null
                    ? "已拒绝（" + badErr + "）" : "⚠ 没拒绝"));

                // 造一份「好友的清单」：删掉 2 个（=我多装 2 个）、改掉 1 个版本、再加 1 个我没有的
                var friend = ModManifest.Parse(manifest.ToJson(), out _);
                var dropped = friend.Mods.Take(2).Select(m => m.Id).ToList();
                friend.Mods.RemoveAll(m => dropped.Contains(m.Id));
                var tweaked = friend.Mods.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Version));
                string tweakedOld = tweaked?.Version;
                if (tweaked != null)
                    tweaked.Version = "0.0.1";
                friend.Mods.Add(new ManifestMod
                {
                    Id = "friend.only.mod",
                    Name = "好友专属Mod",
                    Version = "1.0.0",
                    Source = "Nexus:12345",
                    Page = "https://www.nexusmods.com/stardewvalley/mods/12345"
                });

                var diff = ManifestDiff.Compare(env, friend, mods);
                sb.AppendLine($"比对：一致 {diff.SameCount} / 缺 {diff.MissingCount} / 版本不同 {diff.VersionCount} / 多装 {diff.ExtraCount}"
                              + $"　（构造：缺 1、版本不同 1、多装 2）");
                foreach (var e in diff.Entries.Take(6))
                    sb.AppendLine($"  [{e.KindText}] {e.Name}　我={e.MyVersion} 好友={e.TheirVersion}"
                                  + $"　源={e.Source ?? "-"}　可自动装={e.CanAutoInstall}");
                foreach (string w in diff.Warnings)
                    sb.AppendLine("  提醒：" + w);
                sb.AppendLine();

                sb.AppendLine("=== 下载文件夹监控 ===");
                sb.AppendLine("状态  : " + (env.Config.WatchFolderEnabled ? "已开启" : "已关闭")
                              + "，模式：" + (string.Equals(env.Config.WatchInstallMode, "auto", StringComparison.OrdinalIgnoreCase)
                                  ? "直接自动安装" : "弹窗询问"));
                var watchFolders = env.WatchFolders();
                sb.AppendLine("目录  : " + (watchFolders.Count > 0
                    ? string.Join("、", watchFolders)
                    : (env.Config.WatchFolder ?? "(自动)") + "（没找到可用的下载目录）"));
                var zips = DownloadWatcher.ScanFolder(watchFolders);
                var okZips = zips.Where(z => z.Ok).ToList();
                var trZips = zips.Where(z => z.IsTranslation).ToList();
                sb.AppendLine($"看到 {zips.Count} 个压缩包：能直接装的 {okZips.Count} 个 · 翻译包 {trZips.Count} 个");
                foreach (var zi in okZips)
                    sb.AppendLine($"  - {zi.DisplayName} {zi.VersionText}  [{zi.Id}]  包内 {zi.ModCount} 个 Mod  ← {Path.GetFileName(zi.Path)}");
                foreach (var zi in trZips)
                    sb.AppendLine($"  - 翻译包：{Path.GetFileName(zi.Path)} → 装给「{zi.TranslationTarget}」：{string.Join("、", zi.TranslationFiles)}");
                foreach (var zi in zips.Where(z => !z.Ok && !z.IsTranslation))
                    sb.AppendLine($"  - 识别失败：{Path.GetFileName(zi.Path)} → {zi.Error}");

                // 路由检查：监控路径到底会把每个包送去哪（防止再出现"翻译包被当成不是 Mod 跳过"）
                int nMod = 0, nTr = 0, nSkip = 0;
                foreach (var zi in zips)
                {
                    switch (SMM.MainWindow.RouteZip(zi))
                    {
                        case "translation": nTr++; break;
                        case "mod": nMod++; break;
                        default: nSkip++; break;
                    }
                }
                sb.AppendLine($"  路由检查：走 Mod 安装 {nMod} 个 · 走翻译安装 {nTr} 个 · 跳过 {nSkip} 个");
                sb.AppendLine();

                if (network && !string.IsNullOrWhiteSpace(env.Config.NexusApiKey))
                {
                    sb.AppendLine("=== Nexus API Key 验证 ===");
                    try
                    {
                        var acc = NexusSource.ValidateAsync(env.Config.NexusApiKey).GetAwaiter().GetResult();
                        sb.AppendLine($"  账号 {acc.Name}（id={acc.UserId}）Premium={acc.IsPremium}");

                        var hot = NexusSource.ListAsync("latest_added", env.Config.NexusApiKey).GetAwaiter().GetResult();
                        sb.AppendLine($"  Nexus 最新发布榜单：{hot.Count} 条，首条 = {hot.FirstOrDefault()?.Name}");
                        sb.AppendLine(Http.LastRequestBypassedProxy
                            ? "  网络：系统代理连不上，已自动改直连"
                            : "  网络：走系统代理正常");

                        sb.AppendLine("  --- 补写更新源的验证功能 ---");
                        foreach (string probe in new[] { "859", "https://www.nexusmods.com/stardewvalley/mods/1915", "https://github.com/Annosz/UIInfoSuite2", "这不是个编号" })
                        {
                            try
                            {
                                sb.AppendLine($"  「{probe}」 → " + SourceProbe.DescribeAsync(env, probe).GetAwaiter().GetResult());
                            }
                            catch (Exception ex)
                            {
                                sb.AppendLine($"  「{probe}」 → 拒绝：" + ex.Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("  验证失败：" + ex.Message);
                    }
                }

                if (network)
                {
                    sb.AppendLine("=== 在线检查更新 ===");
                    var states = mods.Select(m => new ModStatus { Mod = m }).ToList();
                    var progress = new InlineProgress(s => sb.AppendLine("  … " + s));
                    UpdateChecker.CheckAllAsync(env, states, progress, default).GetAwaiter().GetResult();
                    foreach (var s in states)
                        sb.AppendLine($"  [{s.Status}] {s.Name} v{s.VersionText} → {s.LatestVersion ?? "-"} "
                                      + $"来源={s.SourceText} {s.DetailText}");
                }

                sb.AppendLine();
                sb.AppendLine("=== 界面冒烟测试 ===");
                RunUiSmokeTest(sb);
            }
            catch (Exception ex)
            {
                sb.AppendLine("自检异常：" + ex);
            }

            try
            {
                File.WriteAllText(Path.Combine(outDir, "selftest.txt"), sb.ToString());
            }
            catch { }
        }

        /// <summary>
        /// 中文名读写的自测：在临时目录造一个假 manifest，改名 → 校验中文没被转义、其它字段还在 → 还原。
        /// 全程碰的是临时目录，不会动真实的 Mod 文件。
        /// </summary>
        private static string NameWriteProbe()
        {
            string dir = Path.Combine(Path.GetTempPath(), "smm-nametest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "manifest.json"),
                    "{\n  \"Name\": \"Test Mod\",\n  \"Author\": \"someone\",\n  \"Version\": \"1.0.0\",\n"
                    + "  \"UniqueID\": \"test.mod\",\n  \"UpdateKeys\": [ \"Nexus:1\" ]\n}\n",
                    new UTF8Encoding(false));

                ModNamer.WriteName(dir, "测试模组");
                string text = File.ReadAllText(Path.Combine(dir, "manifest.json"));
                bool nameOk = text.Contains("\"测试模组\"");
                bool keptOther = text.Contains("test.mod") && text.Contains("Nexus:1");
                string readBack = ModNamer.ReadCurrentName(dir);

                ModNamer.WriteName(dir, "Test Mod");
                bool restored = ModNamer.ReadCurrentName(dir) == "Test Mod";

                return $"改名={(nameOk ? "OK" : "✗")}"
                       + $"　中文没被转义={(text.Contains("\\u") ? "✗ 转义了" : "OK")}"
                       + $"　其它字段保留={(keptOther ? "OK" : "✗")}"
                       + $"　读回={readBack}　还原={(restored ? "OK" : "✗")}";
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
            finally
            {
                try { Installer.ForceDelete(dir); } catch { }
            }
        }

        /// <summary>
        /// 拿一个真实 Mod 的 manifest.json 复制到临时目录再改名，确认 UniqueID / 版本 / EntryDll /
        /// 更新源都没被写坏。只动临时副本，原文件不碰。
        /// </summary>
        private static string RealManifestProbe(List<LocalMod> mods)
        {
            var first = mods?.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Dir)
                                                  && File.Exists(ModNamer.ManifestPath(m.Dir)));
            if (first == null)
                return "跳过（没找到可用的真实 manifest）";

            string tmp = Path.Combine(Path.GetTempPath(), "smm-namecopy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmp);
                string copy = ModNamer.ManifestPath(tmp);
                File.Copy(ModNamer.ManifestPath(first.Dir), copy, true);

                var before = ModScanner.ParseManifestSafe(File.ReadAllText(copy), out _);
                ModNamer.WriteName(tmp, "改名测试 Name Test");
                string written = ModNamer.ReadCurrentName(tmp);
                var after = ModScanner.ParseManifestSafe(File.ReadAllText(copy), out string err);

                bool ok = after != null
                          && before != null
                          && written == "改名测试 Name Test"
                          && after.Id == before.Id
                          && after.Version == before.Version
                          && after.EntryDll == before.EntryDll
                          && after.Keys.Count == before.Keys.Count;

                return $"{first.Id}：「{before?.Name}」→「{written}」　"
                       + $"UniqueID={(after?.Id == before?.Id ? "OK" : "✗")}　"
                       + $"版本={(after?.Version == before?.Version ? "OK" : "✗")}　"
                       + $"EntryDll={(after?.EntryDll == before?.EntryDll ? "OK" : "✗")}　"
                       + $"更新源={after?.Keys.Count}/{before?.Keys.Count}　"
                       + $"重解析={(after != null ? "OK" : "✗ " + err)}　"
                       + (ok ? "整体 OK" : "✗ 有字段对不上");
            }
            catch (Exception ex)
            {
                return "✗ " + ex.Message;
            }
            finally
            {
                try { Installer.ForceDelete(tmp); } catch { }
            }
        }

        // ==================== 截图模式（开发用） ====================

        private static void Trace(string dir, string msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "shots.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        private static void RunShots(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            Trace(dir, "开始截图，输出目录=" + dir);

            const int W = 1280, H = 800;
            var win = new SMM.MainWindow();
            var root = (System.Windows.FrameworkElement)win.Content;
            if (root is System.Windows.Controls.Panel panel)
                panel.Background = win.Background;   // 不然截图背景是透明的

            Trace(dir, "主窗口已构造");

            Pump(win.ReloadModsAsync());
            Trace(dir, "Mod 列表已加载：" + win.Mods.Count + " 个");

            // 第三项是这一页的截图高度（设置页很长，给高一点才拍得全）
            var pages = new (string key, string file, int h)[]
            {
                ("mods", "1-mods.png", H),
                ("browse", "2-browse.png", H),
                ("backup", "3-backup.png", H),
                ("issues", "4-issues.png", H),
                ("mp", "6-mp.png", 1000),
                ("settings", "5-settings.png", 1700)
            };

            foreach (var (key, file, ph) in pages)
            {
                win.OpenPage(key);
                Layout(root, W, ph);

                if (win.CurrentPage is BrowsePage browse)
                {
                    Pump(browse.SearchAsync("latest_added", ""));
                    Layout(root, W, ph);
                    SaveShot(root, dir, file, W, ph);
                    Trace(dir, file + " 已写出");

                    // 额外验证两条新路径：按编号精确查、关键词（Nexus 搜不了）的提示
                    Pump(browse.SearchAsync("latest_added", "27517"));
                    Layout(root, W, ph);
                    SaveShot(root, dir, "2b-browse-按编号.png", W, ph);
                    Trace(dir, "按编号查询截图已写出");

                    Pump(browse.SearchAsync("latest_added", "bank"));
                    Layout(root, W, ph);
                    SaveShot(root, dir, "2c-browse-按名字.png", W, ph);
                    Trace(dir, "关键词查询截图已写出");
                    continue;
                }

                if (win.CurrentPage is IssuesPage)
                {
                    PumpFor(2500);    // 等它自己跑完依赖 / 冲突检查
                    Layout(root, W, ph);
                }

                if (win.CurrentPage is MultiplayerPage)
                {
                    PumpFor(4000);    // 等防火墙检查（要起 PowerShell）跑完，不然截到的是「正在检查…」
                    Layout(root, W, ph);
                }

                SaveShot(root, dir, file, W, ph);
                Trace(dir, file + " 已写出");
            }

            // 改中文名对话框单独拍一张
            var demoMod = new LocalMod
            {
                Id = "smm.dialog.demo",
                Name = "畜牧助手 (Better Ranching)",
                OriginalName = "畜牧助手 (Better Ranching)",
                Version = "2.0.5",
                Dir = System.IO.Path.Combine(win.Env.ModsPath ?? "(未设置)", "BetterRanching")
            };
            var nameWin = new NameDialog(win.Env, demoMod);
            var nameRoot = (System.Windows.FrameworkElement)nameWin.Content;
            Layout(nameRoot, 720, 520);
            SaveShot(nameRoot, dir, "7-改中文名.png", 720, 520);
            Trace(dir, "7-改中文名.png 已写出");
            nameWin.Close();

            win.Close();
            Trace(dir, "全部完成");
        }

        private static void SaveShot(System.Windows.FrameworkElement root, string dir, string file, int w, int h)
        {
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(root);

            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, file));
            enc.Save(fs);
        }

        private static void Layout(System.Windows.FrameworkElement el, int w, int h)
        {
            el.Measure(new System.Windows.Size(w, h));
            el.Arrange(new System.Windows.Rect(0, 0, w, h));
            el.UpdateLayout();
            PumpFor(150);
        }

        /// <summary>一边跑 Dispatcher 一边等任务完成（界面线程上不能直接 .Result，会死锁）。</summary>
        private static void Pump(Task task)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
        }

        private static void PumpFor(int ms)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        /// <summary>在独立 STA 线程上把所有页面都构造 + 排版一遍，确认 XAML 没问题。</summary>
        private static void RunUiSmokeTest(StringBuilder sb)
        {
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    var win = new SMM.MainWindow();
                    sb.AppendLine("  主窗口 + Mod 管理页：OK");
                    win.Measure(new System.Windows.Size(1200, 780));
                    win.Arrange(new System.Windows.Rect(0, 0, 1200, 780));
                    win.UpdateLayout();

                    var pages = new (string name, System.Windows.Controls.UserControl page)[]
                    {
                        ("AI 设置页", new AiPage(win)),
                        ("快捷键页", new HotkeysPage(win)),
                        ("浏览下载页", new BrowsePage(win)),
                        ("备份回滚页", new BackupPage(win)),
                        ("问题检查页", new IssuesPage(win)),
                        ("联机页", new MultiplayerPage(win)),
                        ("设置页", new SettingsPage(win))
                    };

                    foreach (var (name, page) in pages)
                    {
                        page.Measure(new System.Windows.Size(1000, 700));
                        page.Arrange(new System.Windows.Rect(0, 0, 1000, 700));
                        page.UpdateLayout();
                        sb.AppendLine("  " + name + "：OK");
                    }

                    // 补写更新源对话框
                    var fakeMod = new LocalMod
                    {
                        Id = "smm.dialog.test",
                        Name = "测试Mod (Test Mod)",
                        Version = "1.0.0"
                    };
                    var dlg = new SourceDialog(win.Env, fakeMod);
                    dlg.Measure(new System.Windows.Size(760, 470));
                    dlg.Arrange(new System.Windows.Rect(0, 0, 760, 470));
                    dlg.UpdateLayout();
                    sb.AppendLine("  补写更新源对话框：OK");
                    dlg.Close();

                    // 改中文名对话框
                    var nameDlg = new NameDialog(win.Env, fakeMod);
                    nameDlg.Measure(new System.Windows.Size(700, 460));
                    nameDlg.Arrange(new System.Windows.Rect(0, 0, 700, 460));
                    nameDlg.UpdateLayout();
                    sb.AppendLine("  改中文名对话框：OK");
                    nameDlg.Close();

                    // 互斥组对话框
                    var groupDlg = new ExclusiveGroupDialog(win.Env,
                        new List<LocalMod> { fakeMod },
                        new List<LocalMod> { fakeMod });
                    groupDlg.Measure(new System.Windows.Size(760, 560));
                    groupDlg.Arrange(new System.Windows.Rect(0, 0, 760, 560));
                    groupDlg.UpdateLayout();
                    sb.AppendLine("  互斥组对话框：OK");
                    groupDlg.Close();

                    // 通用输入框（带按键捕获）
                    var inputDlg = new TextInputDialog("改快捷键", "自检用", "新的按键", "B",
                        _ => null, null, null, null, captureKeys: true);
                    inputDlg.Measure(new System.Windows.Size(720, 460));
                    inputDlg.Arrange(new System.Windows.Rect(0, 0, 720, 460));
                    inputDlg.UpdateLayout();
                    sb.AppendLine("  改键输入框（含按键捕获）：OK");

                    // 真模拟一次"按键"：走一遍「按键 → 键名 → 填进输入框」
                    var capBox = inputDlg.FindName("TxtValue") as System.Windows.Controls.TextBox;
                    bool capOk = inputDlg.SimulateKeyPress(System.Windows.Input.Key.F5);
                    string capText = capBox?.Text ?? "?";
                    bool capCombo = inputDlg.SimulateKeyPress(System.Windows.Input.Key.F5, leftShift: true);
                    string capComboText = capBox?.Text ?? "?";

                    sb.AppendLine("  按键捕获：按 F5 → " + capText + (capOk && capText == "F5" ? " OK" : " ✗")
                                  + "；按住 LeftShift 再按 F5 → " + capComboText
                                  + (capCombo && capComboText == "LeftShift + F5" ? " OK" : " ✗")
                                  + "；键名映射 "
                                  + string.Join("　", new[]
                                  {
                                      System.Windows.Input.Key.A,
                                      System.Windows.Input.Key.D5,
                                      System.Windows.Input.Key.NumPad3,
                                      System.Windows.Input.Key.LeftCtrl,
                                      System.Windows.Input.Key.OemComma,
                                      System.Windows.Input.Key.PageUp,
                                      System.Windows.Input.Key.Space
                                  }.Select(k => k + "→" + (TextInputDialog.KeyName(k) ?? "（不支持）"))));
                    inputDlg.Close();

                    win.Close();
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  界面测试失败：" + ex.GetType().Name + " " + ex.Message);
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(30)))
                sb.AppendLine("  界面测试超时（可能有模态框卡住）");
        }

        /// <summary>
        /// 拿一个真实的 config.json 复制到临时目录，真跑一遍「改键 → 禁用 → 启用」，
        /// 确认只动那一个字段、其它设置没被写坏。只碰临时副本，原文件不动。
        /// </summary>
        private static string HotkeyWriteProbe(List<HotkeyBinding> hotkeys)
        {
            var src = hotkeys?.FirstOrDefault(h => h != null && File.Exists(h.ConfigPath));
            if (src == null)
                return "跳过（没有可用的 config.json 可以测：进游戏跑一次再来看）";

            string tmp = Path.Combine(Path.GetTempPath(), "smm-keycopy-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmp);
                string copy = Path.Combine(tmp, "config.json");
                File.Copy(src.ConfigPath, copy, true);

                string beforeLeaves = DumpLeaves(File.ReadAllText(src.ConfigPath));
                string original = src.Value;

                // ModId 用假的，避免动到真实绑定在配置里记的「原按键」
                var probe = new HotkeyBinding
                {
                    ModId = "smm.selftest.probe",
                    ModName = src.ModName,
                    ConfigPath = copy,
                    JsonPath = src.JsonPath,
                    Value = original,
                    Normalized = Hotkeys.Normalize(original)
                };

                var env = AppEnv.Current;
                bool wrote, disabled, enabled;
                try
                {
                    Hotkeys.Write(env, probe, "F9");
                    wrote = probe.Value == "F9";

                    Hotkeys.Disable(env, probe);
                    disabled = probe.Value == "None";

                    Hotkeys.Enable(env, probe);
                    enabled = probe.Value == "F9";
                }
                catch (Exception ex)
                {
                    return "失败：" + ex.Message;
                }
                finally
                {
                    env?.Config?.HotkeyOriginal?.Remove("smm.selftest.probe|" + src.JsonPath);
                    env?.Config?.Save();
                }

                string afterAll = DumpLeaves(File.ReadAllText(copy));
                string othersOkCompare = StripLine(beforeLeaves, src.JsonPath);
                bool othersOk = othersOkCompare == StripLine(afterAll, src.JsonPath);
                string diffDetail = othersOk ? "" : "　差异：" + FirstDiff(othersOkCompare, StripLine(afterAll, src.JsonPath));

                // 最后把值写回原值，确认能改回来
                bool restored;
                try
                {
                    Hotkeys.Write(env, probe, original);
                    restored = DumpLeaves(File.ReadAllText(copy)) == beforeLeaves;
                }
                catch { restored = false; }

                return $"改键={(wrote ? "OK" : "✗")}　禁用={(disabled ? "OK" : "✗")}"
                       + $"　启用={(enabled ? "OK" : "✗")}　其它字段保留={(othersOk ? "OK" : "✗")}"
                       + $"　改回原值={(restored ? "OK" : "✗")}"
                       + $"（样本：{src.ModName} {src.JsonPath} = {original}）"
                       + diffDetail;
            }
            catch (Exception ex)
            {
                return "失败：" + ex.Message;
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        /// <summary>把 JSON 里所有叶子节点拍平成「路径=值」的文本，用于比对"别的字段有没有被改"。</summary>
        private static string DumpLeaves(string json)
        {
            try
            {
                using var doc = JsonUtil.Parse(json);
                var list = new List<string>();

                void Walk(System.Text.Json.JsonElement el, string path)
                {
                    switch (el.ValueKind)
                    {
                        case System.Text.Json.JsonValueKind.Object:
                            foreach (var p in el.EnumerateObject())
                                Walk(p.Value, path + "." + p.Name);
                            break;
                        case System.Text.Json.JsonValueKind.Array:
                            int i = 0;
                            foreach (var x in el.EnumerateArray())
                                Walk(x, path + "[" + i++ + "]");
                            break;
                        default:
                            list.Add(path + "=" + el.ToString());
                            break;
                    }
                }

                Walk(doc.RootElement, "");
                list.Sort(StringComparer.Ordinal);
                return string.Join("\n", list);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>找出两份「叶子快照」的头几处不同（诊断用）。</summary>
        private static string FirstDiff(string a, string b)
        {
            var la = (a ?? "").Split('\n');
            var lb = (b ?? "").Split('\n');
            var setB = new HashSet<string>(lb, StringComparer.Ordinal);
            var setA = new HashSet<string>(la, StringComparer.Ordinal);

            var onlyA = la.Where(x => !setB.Contains(x)).Take(3).ToList();
            var onlyB = lb.Where(x => !setA.Contains(x)).Take(3).ToList();

            string s = "";
            if (onlyA.Count > 0) s += "仅原文件有 → " + string.Join(" ｜ ", onlyA);
            if (onlyB.Count > 0) s += (s.Length > 0 ? "；" : "") + "仅改写后有 → " + string.Join(" ｜ ", onlyB);
            return s.Length == 0 ? "（行集合相同，可能是顺序/空白差异）" : s;
        }

        /// <summary>去掉指定路径那一行（比对时排除被测的那个字段）。</summary>
        private static string StripLine(string leaves, string path)
            => string.Join("\n", (leaves ?? "").Split('\n')
                .Where(l => !l.StartsWith(path + "=", StringComparison.Ordinal)));

        // ==================== 通用 ====================
        /// <summary>打开网页。用哪个浏览器由设置决定（默认 Edge），失败了会退回系统默认。</summary>
        public static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            try
            {
                Browser.OpenUrl(url, AppEnv.Current?.Config);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开链接：" + ex.Message, "星露谷 Mod 管理器");
            }
        }

        /// <summary>打开本地文件（比如日志），永远用系统默认程序。</summary>
        public static void OpenFile(string path)
        {
            try
            {
                Browser.OpenFile(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打不开文件：" + ex.Message, "星露谷 Mod 管理器");
            }
        }

        public static void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    return;
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
