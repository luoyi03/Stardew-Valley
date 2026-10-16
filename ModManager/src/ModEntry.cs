using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ModManager
{
    public class ModEntry : Mod
    {
        public const string ModId = "ly.ModManager";

        internal static IModHelper Hlp;
        internal static IMonitor Log;
        internal static ModConfig Config;
        internal static UpdateService Service;

        private static Texture2D _pixel;
        internal static Texture2D Pixel => _pixel ??= MakePixel();

        private static Rectangle _titleBtn;
        private static bool _titleHover;
        private static string _lastCheckFile;

        public override void Entry(IModHelper helper)
        {
            Hlp = helper;
            Log = Monitor;
            Config = helper.ReadConfig<ModConfig>();
            Service = new UpdateService(helper, Monitor);
            _lastCheckFile = Path.Combine(Service.DataDir, "lastcheck.txt");

            // 中英文名对照（面板里显示「中文名（英文名）」）
            try { ModNames.Load(); } catch (Exception ex) { Monitor.Log("载入中英文名对照失败：" + ex.Message, LogLevel.Warn); }

            // 上次没替换成功的更新，趁现在补上
            try { Updater.ApplyPending(); } catch (Exception ex) { Monitor.Log("应用待处理更新失败：" + ex.Message, LogLevel.Warn); }

            helper.Events.Input.ButtonsChanged += OnButtonsChanged;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.Display.Rendered += OnRendered;
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            helper.ConsoleCommands.Add("modmgr", "打开 Mod 管理器。用法：modmgr / modmgr check / modmgr update <ModID|all>", OnConsole);
        }

        internal static void SaveConfig()
        {
            try { Hlp.WriteConfig(Config); } catch (Exception ex) { Log?.Log("保存配置失败：" + ex.Message, LogLevel.Warn); }
        }

        // ---------------- 入口 ----------------
        internal static void OpenManager()
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            if (Game1.activeClickableMenu is ManagerMenu)
            {
                Game1.activeClickableMenu = null;
                Game1.playSound("bigDeSelect");
                return;
            }

            Service?.RefreshMods();
            Game1.activeClickableMenu = new ManagerMenu();
            Game1.playSound("bigSelect");
        }

        private void OnButtonsChanged(object sender, ButtonsChangedEventArgs e)
        {
            if (Config?.OpenKey == null || !Config.OpenKey.JustPressed())
                return;
            // 设置面板里正在输入时不要把快捷键吃掉
            if (Game1.activeClickableMenu is SettingsMenu)
                return;
            Hlp.Input.SuppressActiveKeybinds(Config.OpenKey);
            OpenManager();
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (e.Button != SButton.MouseLeft)
                return;
            if (!Config.ShowTitleButton)
                return;
            if (Game1.activeClickableMenu is not TitleMenu)
                return;
            if (_titleBtn.Contains(Game1.getMouseX(), Game1.getMouseY()))
            {
                Hlp.Input.Suppress(e.Button);
                OpenManager();
            }
        }

        private void OnRendered(object sender, RenderedEventArgs e)
        {
            if (!Config.ShowTitleButton)
                return;
            if (Game1.activeClickableMenu is not TitleMenu)
                return;

            _titleBtn = new Rectangle(32, Game1.uiViewport.Height - 116, 232, 52);
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            _titleHover = _titleBtn.Contains(mx, my);

            Ui.Box(e.SpriteBatch, _titleBtn, _titleHover ? Ui.Hover : Ui.PanelBg, _titleHover ? Ui.Gold : Ui.Line);
            string t = "Mod 管理器";
            var size = Ui.Measure(t, Game1.dialogueFont);
            Ui.Text(e.SpriteBatch, t, new Vector2(_titleBtn.X + 16, _titleBtn.Y + 8), Ui.Gold, Game1.dialogueFont);
            Ui.Text(e.SpriteBatch, "检查更新 / 一键更新", new Vector2(_titleBtn.X + 16, _titleBtn.Y + 32), Ui.Dim);
        }

        // ---------------- 启动检查 ----------------
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            SaveConfig();

            if (Config.AutoInstallInboxOnLaunch && !string.IsNullOrWhiteSpace(Config.InboxFolder))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        Service.RefreshMods();
                        var items = InboxInstaller.Scan(Config.InboxFolder, Service.Snapshot().Select(s => s.Mod).ToList())
                            .Where(i => i.Error == null).ToList();
                        if (items.Count == 0)
                            return;
                        var (ok, queued, errors) = InboxInstaller.InstallAll(items);
                        Monitor.Log($"下载目录自动安装：装好 {ok} 个，待重启 {queued} 个，失败 {errors.Count} 个。", LogLevel.Info);
                        if (ok + queued > 0)
                            Game1.addHUDMessage(new HUDMessage($"ModManager：已安装 {ok + queued} 个本地 Mod 包", 2));
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log("自动安装下载目录失败：" + ex.Message, LogLevel.Warn);
                    }
                });
            }

            if (!Config.AutoCheckOnLaunch)
                return;
            if (HoursSinceLastCheck() < Config.AutoCheckIntervalHours)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    Service.RefreshMods();
                    await Service.CheckAllAsync();
                    try { File.WriteAllText(_lastCheckFile, DateTime.Now.ToString("o")); } catch { }

                    int n = Service.Snapshot().Count(s => s.HasUpdate);
                    if (n > 0)
                    {
                        Game1.addHUDMessage(new HUDMessage($"ModManager：{n} 个 Mod 有新版本，按 {Config.OpenKey} 打开管理器", 2));
                        Monitor.Log($"检测到 {n} 个 Mod 有新版本。", LogLevel.Info);
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log("自动检查更新失败：" + ex.Message, LogLevel.Warn);
                }
            });
        }

        private double HoursSinceLastCheck()
        {
            try
            {
                if (!File.Exists(_lastCheckFile))
                    return 9999;
                string s = File.ReadAllText(_lastCheckFile).Trim();
                if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    return (DateTime.Now - t).TotalHours;
            }
            catch { }
            return 9999;
        }

        // ---------------- 控制台 ----------------
        private void OnConsole(string cmd, string[] args)
        {
            if (args.Length == 0)
            {
                OpenManager();
                return;
            }

            switch (args[0].ToLowerInvariant())
            {
                case "check":
                case "检查":
                    _ = Task.Run(async () =>
                    {
                        Service.RefreshMods();
                        await Service.CheckAllAsync();
                        foreach (var s in Service.Snapshot().Where(x => x.HasUpdate))
                            Monitor.Log($"可更新：{s.Mod.Name} {s.Mod.Version} → {s.LatestVersion}（{s.SourceName}）", LogLevel.Info);
                        Monitor.Log("检查完成。", LogLevel.Info);
                    });
                    Monitor.Log("开始在后台检查更新…", LogLevel.Info);
                    break;

                case "update":
                    if (args.Length < 2)
                    {
                        Monitor.Log("用法：modmgr update all 或 modmgr update <ModID>", LogLevel.Info);
                        break;
                    }
                    _ = Task.Run(async () =>
                    {
                        Service.RefreshMods();
                        await Service.CheckAllAsync();
                        if (args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                            await Service.UpdateAllAsync();
                        else
                            await Service.UpdateAsync(Service.Find(args[1]));
                    });
                    break;

                case "open":
                    OpenManager();
                    break;

                case "names":
                case "名字":
                    ModNames.Load(reload: true);
                    Monitor.Log($"已重新载入中英文名对照（{ModNames.Summary()}）。文件：{ModNames.FilePath}", LogLevel.Info);
                    break;

                default:
                    Monitor.Log("用法：modmgr / modmgr check / modmgr update <ModID|all> / modmgr names", LogLevel.Info);
                    break;
            }
        }

        private static Texture2D MakePixel()
        {
            var t = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            t.SetData(new[] { Color.White });
            return t;
        }
    }
}
