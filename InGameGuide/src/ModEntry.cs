using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace InGameGuide
{
    public class ModEntry : Mod
    {
        internal static IModHelper Hlp;
        internal static IMonitor Log;
        internal static ModConfig Config;
        internal static GuideDoc Guide;
        internal static ItemDb Db;
        internal static CommandBridge Bridge;
        internal static string LoadError;

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

            try
            {
                LoadData();
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                Monitor.Log("加载数据文件失败：" + ex.Message, LogLevel.Error);
            }

            Bridge = new CommandBridge(helper.DirectoryPath);

            helper.Events.Input.ButtonsChanged += OnButtonsChanged;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += (_, _) =>
            {
                helper.WriteConfig(Config);
            };

            helper.ConsoleCommands.Add("guide", "打开内置新手教程与物品代码查询面板。用法：guide [search]", (cmd, args) =>
            {
                OpenMenu(args.Length > 0 && args[0].Equals("search", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            });
        }

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private static void LoadData()
        {
            string dir = Hlp.DirectoryPath;
            string guidePath = Path.Combine(dir, "assets", "guide.json");
            string itemsPath = Path.Combine(dir, "assets", "items.json");

            Guide = JsonSerializer.Deserialize<GuideDoc>(File.ReadAllText(guidePath), JsonOpts);
            Db = JsonSerializer.Deserialize<ItemDb>(File.ReadAllText(itemsPath), JsonOpts);

            Log.Log($"教程载入完成：{Guide?.Chapters?.Count ?? 0} 章；物品条目：{Db?.Items?.Count ?? 0}", LogLevel.Trace);
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!e.IsMultipleOf(12))
                return;
            Bridge?.Poll();
            ItemSpawner.ProcessPendingNaming();
        }

        private void OnButtonsChanged(object sender, ButtonsChangedEventArgs e)
        {
            if (Config.TryToggleKey(out SButton key) && e.Pressed.Contains(key))
            {
                Hlp.Input.Suppress(key);
                OpenMenu(0);
                return;
            }
            if (Config.TryQueryKey(out SButton q) && e.Pressed.Contains(q))
            {
                Hlp.Input.Suppress(q);
                OpenMenu(1);
            }
        }

        internal static void OpenMenu(int tab)
        {
            if (Game1.graphics?.GraphicsDevice == null)
                return;

            // 同一个标签页再按一次 = 关闭
            if (Game1.activeClickableMenu is GuideMenu existing && existing.Tab == tab)
            {
                Game1.activeClickableMenu = null;
                Game1.playSound("bigDeSelect");
                return;
            }

            if (Guide == null || Db == null)
            {
                Game1.addHUDMessage(new HUDMessage("教程数据未加载：assets/guide.json 或 items.json 缺失", 3));
                return;
            }

            Game1.activeClickableMenu = new GuideMenu(tab);
            Game1.playSound("bigSelect");
        }
    }
}
