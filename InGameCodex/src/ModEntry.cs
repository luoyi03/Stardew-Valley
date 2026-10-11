using System;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using InGameCodex.Ai;
using InGameCodex.Data;
using InGameCodex.Ui;

namespace InGameCodex
{
    public class ModEntry : Mod
    {
        internal static IModHelper Hlp;
        internal static IMonitor Log;
        internal static ModConfig Config;
        internal static ItemIndex Index;
        internal static CodexRefIndex Refs;
        internal static NpcIndex Npcs;
        internal static CodexProgress Progress;

        private static Texture2D _pixel;
        internal static Texture2D Pixel => _pixel ??= MakePixel();

        private static Texture2D MakePixel()
        {
            var t = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            t.SetData(new[] { Color.White });
            return t;
        }

        public override void Entry(IModHelper helper)
        {
            Hlp = helper;
            Log = Monitor;
            Config = helper.ReadConfig<ModConfig>();
            Cx.S = Math.Clamp(Config.UiScale, 1f, 2f);
            Index = new ItemIndex(Monitor);
            Refs = new CodexRefIndex();
            Npcs = new NpcIndex();
            Progress = new CodexProgress();

            try
            {
                AiRuntime.Init();   // S9：AI 问答（首次启动会从 StardewAI 的 config.json 复制默认配置）
            }
            catch (Exception ex)
            {
                Monitor.Log("AI 问答初始化失败：" + ex, LogLevel.Error);
            }

            Pinyin.Load(Path.Combine(helper.DirectoryPath, "assets", "pinyin.json"), Monitor);

            helper.Events.GameLoop.GameLaunched += (_, _) =>
            {
                helper.WriteConfig(Config);
                // 用了本机网关方案时，DSH 每次启动端口和密钥都会变，进游戏先自动刷一次
                AiRuntime.RefreshLocalGateway(true);
            };
            helper.Events.GameLoop.SaveLoaded += (_, _) =>
            {
                BuildIndex();
                AiRuntime.RefreshLocalGateway(false);
            };
            helper.Events.GameLoop.UpdateTicked += (_, _) => FlushConfigIfDirty();
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            helper.ConsoleCommands.Add("codex",
                "打开星露谷图鉴面板。用法：codex [物品|npc|献祭|图鉴|收藏|ai]；codex index 打印索引统计",
                OnCommand);
        }

        private void BuildIndex()
        {
            try
            {
                Index.Build();
            }
            catch (Exception ex)
            {
                Monitor.Log("物品索引构建失败：" + ex, LogLevel.Error);
            }

            try
            {
                Refs.Build(Index, Monitor);
            }
            catch (Exception ex)
            {
                Monitor.Log("用途 / 来源反向索引构建失败：" + ex, LogLevel.Error);
            }

            try
            {
                Npcs.Build(Index, Refs, Monitor);
            }
            catch (Exception ex)
            {
                Monitor.Log("NPC 喜好索引构建失败：" + ex, LogLevel.Error);
            }

            try
            {
                Progress.Build(Index, Refs, Monitor);
            }
            catch (Exception ex)
            {
                Monitor.Log("献祭 / 图鉴进度索引构建失败：" + ex, LogLevel.Error);
            }
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (Config.Ai != null && Config.Ai.TryAskKey(out SButton aiKey) && e.Button == aiKey)
            {
                Hlp.Input.Suppress(aiKey);
                ToggleAi();
                return;
            }

            if (!Config.TryToggleKey(out SButton key) || e.Button != key)
                return;

            Hlp.Input.Suppress(key);
            ToggleMenu();
        }

        /// <summary>S9：F5 打开 / 关闭 AI 问答页（已在 AI 页时再按一次关闭面板）。</summary>
        internal static void ToggleAi()
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            if (Game1.activeClickableMenu is CodexMenu m && m.CurrentTab == 5)
            {
                Game1.activeClickableMenu = null;
                Game1.playSound("bigDeSelect");
                return;
            }

            if (!TryOpen(5))
                return;

            Game1.playSound("bigSelect");
        }

        /// <summary>同一个按键再按一次 = 关闭（与 InGameGuide 的手感保持一致）。</summary>
        internal static void ToggleMenu()
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            if (Game1.activeClickableMenu is CodexMenu)
            {
                Game1.activeClickableMenu = null;
                Game1.playSound("bigDeSelect");
                return;
            }

            if (!TryOpen(0))
                return;

            Game1.playSound("bigSelect");
        }

        /// <summary>打开指定标签页；索引还没建好时给出提示而不是白屏。</summary>
        internal static bool TryOpen(int tab)
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return false;

            try
            {
                Index.EnsureBuilt();
                Refs.EnsureBuilt(Index, Log);
                Npcs.EnsureBuilt(Index, Refs, Log);
                Progress.EnsureBuilt(Index, Refs, Log);
            }
            catch (Exception ex)
            {
                Log.Log("建索引时出错：" + ex, LogLevel.Error);
            }

            if (!Index.IsBuilt || Index.Items.Count == 0)
            {
                Game1.addHUDMessage(new HUDMessage("图鉴数据还没准备好，先进一次存档再按 " + Config.ToggleKey, 3));
                return false;
            }

            Game1.activeClickableMenu = new CodexMenu(tab);
            return true;
        }

        private static bool _configDirty;
        private static double _configWriteAt = -999;

        /// <summary>收藏 / 最近查看这类高频小改动：先攒着，隔一小会儿或面板关闭时再写盘（避免每次点击都写 config.json）。</summary>
        internal static void SaveConfigDeferred()
        {
            _configDirty = true;
        }

        /// <summary>由 GameLoop.UpdateTicked 每帧调用：把攒下的配置改动落盘（最多 3 秒一次）。</summary>
        internal static void FlushConfigIfDirty()
        {
            if (!_configDirty || Config == null || Hlp == null)
                return;

            double now = Game1.currentGameTime?.TotalGameTime.TotalSeconds ?? 0;
            if (now - _configWriteAt < 3.0)
                return;

            _configDirty = false;
            _configWriteAt = now;
            try { Hlp.WriteConfig(Config); }
            catch (Exception ex) { Log?.Log("保存配置失败：" + ex.Message, LogLevel.Warn); }
        }

        private void OnCommand(string cmd, string[] args)        {
            if (args.Length > 0 && args[0].Equals("index", StringComparison.OrdinalIgnoreCase))
            {
                Monitor.Log("=== InGameCodex 索引统计 ===", LogLevel.Info);
                Monitor.Log($"总条目 {Index.Items.Count} 条，构建耗时 {Index.BuildMs} ms，跳过 {Index.Skipped} 条"
                            + (Index.IsBuilt ? "" : "（尚未构建：请先进一次存档）"), LogLevel.Info);
                foreach (var t in CodexTypes.All)
                    Monitor.Log($"  {t.Key,-3} {t.Name,-12} {Index.CountOf(t.Key)} 条", LogLevel.Info);

                Monitor.Log($"反向索引：来源 {Refs.ObtainLines} 条 / 用途 {Refs.UseLines} 条，构建耗时 {Refs.BuildMs} ms"
                            + (Refs.IsBuilt ? "" : "（尚未构建：请先进一次存档）"), LogLevel.Info);
                foreach (SrcKind k in SrcKindInfo.All)
                {
                    int sc = Refs.Counts.TryGetValue(k, out int c) ? c : 0;
                    Monitor.Log($"  {SrcKindInfo.Name(k),-6} {sc} 条", LogLevel.Info);
                }
                Monitor.Log($"NPC 索引：{Npcs.Npcs.Count} 位（含全员通用），构建耗时 {Npcs.BuildMs} ms"
                            + (Npcs.IsBuilt ? "" : "（尚未构建）"), LogLevel.Info);
                Monitor.Log($"进度索引：收集包 {Progress.Bundles.Count} 个 / 房间 {Progress.Rooms.Count} 个 / 图鉴 {Progress.Sections.Count} 项，"
                            + $"构建耗时 {Progress.BuildMs} ms，存档已读 {(Progress.SaveReadable ? "是" : "否")}", LogLevel.Info);
                foreach (CodexProgress.Section sec in Progress.Sections)
                    Monitor.Log($"  {sec.Title,-8} {sec.Found} / {sec.Total}", LogLevel.Info);
                foreach (string w in Progress.Warnings.Take(20))
                    Monitor.Log("  进度索引告警：" + w, LogLevel.Warn);
                foreach (string w in Npcs.Warnings.Take(20))
                    Monitor.Log("  NPC 索引告警：" + w, LogLevel.Warn);
                foreach (string w in Refs.Warnings.Take(20))
                    Monitor.Log("  反向索引告警：" + w, LogLevel.Warn);
                if (Index.Warnings.Count > 0)
                {
                    Monitor.Log("告警：", LogLevel.Warn);
                    foreach (string w in Index.Warnings.Take(20))
                        Monitor.Log("  " + w, LogLevel.Warn);
                }
                return;
            }

            int tab = 0;
            if (args.Length > 0)
            {
                string a = args[0];
                if (a.Equals("npc", StringComparison.OrdinalIgnoreCase)) tab = 1;
                else if (a.Equals("献祭", StringComparison.OrdinalIgnoreCase) || a.Equals("bundle", StringComparison.OrdinalIgnoreCase)) tab = 2;
                else if (a.Equals("图鉴", StringComparison.OrdinalIgnoreCase) || a.Equals("collection", StringComparison.OrdinalIgnoreCase)) tab = 3;
                else if (a.Equals("收藏", StringComparison.OrdinalIgnoreCase) || a.Equals("fav", StringComparison.OrdinalIgnoreCase)) tab = 4;
                else if (a.Equals("ai", StringComparison.OrdinalIgnoreCase) || a.Equals("AI", StringComparison.OrdinalIgnoreCase)) tab = 5;
            }

            if (TryOpen(tab))
                Game1.playSound("bigSelect");
        }
    }
}
