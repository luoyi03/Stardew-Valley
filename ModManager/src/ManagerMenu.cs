using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ModManager
{
    public sealed class ManagerMenu : IClickableMenu
    {
        private const int RowH = 64;
        private const int ScrollW = 10;

        private sealed class Chip
        {
            public string Label;
            public Rectangle Bounds;
        }

        private readonly ScrollBar _scroll = new();
        private readonly List<Chip> _chips = new()
        {
            new Chip { Label = "全部" },
            new Chip { Label = "可更新" },
            new Chip { Label = "已最新" },
            new Chip { Label = "需手动" },
            new Chip { Label = "已忽略" }
        };

        private Rectangle _content;
        private int _filter;
        private List<ModState> _rows = new();
        private int _rev = -1;
        private int _pending;
        private int _hoverRow = -1;
        private int _hoverBtn = -1;
        private string _hint = "";
        private int _hintTicks;

        private Rectangle _btnCheck;
        private Rectangle _btnSettings;
        private Rectangle _btnUpdateAll;
        private Rectangle _btnInbox;

        private List<InboxItem> _inbox = new();
        private bool _inboxBusy;
        private int _inboxRev = -1;

        public ManagerMenu()
            : base(0, 0, 100, 100, false)
        {
            Relayout();
            upperRightCloseButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 46, yPositionOnScreen + 10, 40, 40),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);
            SyncRows(true);
            RefreshInbox();
        }

        // ---------------- 本地下载目录 ----------------
        private void RefreshInbox()
        {
            _inboxBusy = true;
            _ = Task.Run(() =>
            {
                try
                {
                    var installed = ModEntry.Service?.Snapshot().Select(s => s.Mod).ToList() ?? new List<LocalMod>();
                    var items = InboxInstaller.Scan(ModEntry.Config?.InboxFolder, installed);
                    _inbox = items;
                    _inboxRev = ModEntry.Service?.Revision ?? 0;
                }
                catch (Exception ex)
                {
                    ModEntry.Log?.Log("扫描下载目录失败：" + ex.Message, StardewModdingAPI.LogLevel.Warn);
                }
                finally
                {
                    _inboxBusy = false;
                }
            });
        }

        private int InboxReady => _inbox?.Count(i => i.Error == null) ?? 0;

        private void StartInboxInstall()
        {
            string folder = ModEntry.Config?.InboxFolder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                Toast("还没设置「Mod 下载目录」，去右上角「设置」里填一下");
                return;
            }
            if (!Directory.Exists(folder))
            {
                Toast("下载目录不存在：" + folder);
                return;
            }

            var items = _inbox.Where(i => i.Error == null).ToList();
            if (items.Count == 0)
            {
                Toast("下载目录里没有可安装的包（只认 zip 或含 manifest.json 的文件夹）");
                return;
            }

            _inboxBusy = true;
            Toast($"开始安装 {items.Count} 个包…");
            _ = Task.Run(() =>
            {
                try
                {
                    var (ok, queued, errors) = InboxInstaller.InstallAll(items);
                    string msg = $"安装完成：装好 {ok} 个" + (queued > 0 ? $"，待重启 {queued} 个" : "");
                    if (errors.Count > 0)
                        msg += $"，失败 {errors.Count} 个（{errors[0]}）";
                    Toast(msg);
                    ModEntry.Log?.Log("下载目录安装结果：" + msg, StardewModdingAPI.LogLevel.Info);
                    ModEntry.Service?.RefreshMods();
                }
                finally
                {
                    RefreshInbox();
                }
            });
        }

        // ---------------- 布局 ----------------
        private void Relayout()
        {
            int uiW = Game1.uiViewport.Width;
            int uiH = Game1.uiViewport.Height;

            width = Math.Max(640, Math.Min(uiW - 40, 1200));
            height = Math.Max(400, Math.Min(uiH - 40, 780));
            xPositionOnScreen = (uiW - width) / 2;
            yPositionOnScreen = (uiH - height) / 2;

            int cx = xPositionOnScreen + 16;
            int tbY = yPositionOnScreen + 58;

            _content = new Rectangle(cx, tbY + 46, width - 34 - ScrollW, height - 58 - 46 - 60);
            _scroll.Track = new Rectangle(_content.Right + 2, _content.Y, ScrollW, _content.Height);
            _scroll.View = _content.Height;

            int bx = xPositionOnScreen + width - 52;
            _btnSettings = new Rectangle(bx - 96, yPositionOnScreen + 12, 92, 36);
            _btnCheck = new Rectangle(bx - 96 - 8 - 116, yPositionOnScreen + 12, 116, 36);

            int fx = cx;
            foreach (var c in _chips)
            {
                int w = (int)Ui.Measure(c.Label).X + 30;
                c.Bounds = new Rectangle(fx, tbY + 6, w, 34);
                fx += w + 8;
            }

            _btnInbox = new Rectangle(_content.Right - 156, tbY + 6, 156, 34);
            _btnUpdateAll = new Rectangle(_btnInbox.X - 8 - 134, tbY + 6, 134, 34);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Relayout();
            ApplyFilter();
        }

        // ---------------- 数据 ----------------
        private void SyncRows(bool force = false)
        {
            var svc = ModEntry.Service;
            if (svc == null)
                return;
            if (!force && _rev == svc.Revision)
                return;
            _rev = svc.Revision;
            _pending = Updater.PendingCount;
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var svc = ModEntry.Service;
            if (svc == null)
                return;

            IEnumerable<ModState> q = svc.Snapshot();
            q = _filter switch
            {
                1 => q.Where(s => s.HasUpdate || s.Busy || s.JobState == "done"),
                2 => q.Where(s => s.Status == "latest" && s.JobState != "done"),
                3 => q.Where(s => s.Status == "manual" || s.Status == "error" || (s.HasUpdate && !s.CanAutoUpdate)),
                4 => q.Where(s => s.Status == "ignored"),
                _ => q
            };

            _rows = q
                .OrderBy(Rank)
                .ThenBy(s => s.Mod.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _scroll.Content = _rows.Count * RowH + 8;
            _scroll.Pos = Math.Min(_scroll.Pos, _scroll.Max);
        }

        private static int Rank(ModState s)
        {
            if (s.Busy) return 0;
            if (s.JobState == "done") return 1;
            if (s.HasUpdate) return s.CanAutoUpdate ? 2 : 3;
            if (s.Status == "manual") return 3;
            if (s.Status == "error") return 4;
            if (s.Status == "ignored") return 5;
            return 6;
        }

        private Rectangle RowRect(int i)
            => new Rectangle(_content.X, _content.Y + i * RowH - _scroll.Pos, _content.Width - 4, RowH - 8);

        private void RowButtons(int i, out Rectangle web, out Rectangle act)
        {
            var r = RowRect(i);
            int by = r.Y + (r.Height - 34) / 2;
            web = new Rectangle(r.Right - 8 - 56, by, 56, 34);
            act = new Rectangle(web.X - 8 - 104, by, 104, 34);
        }

        private int HitRow(int x, int y)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = RowRect(i);
                if (r.Contains(x, y))
                    return i;
            }
            return -1;
        }

        // ---------------- 动作 ----------------
        private void Toast(string msg)
        {
            _hint = msg;
            _hintTicks = 300;
        }

        private void StartCheck()
        {
            var svc = ModEntry.Service;
            if (svc == null || svc.Checking)
                return;
            if (svc.Snapshot().Count == 0)
                svc.RefreshMods();
            Toast("开始检查更新…");
            _ = Task.Run(async () =>
            {
                try { await svc.CheckAllAsync(); }
                catch (Exception ex) { ModEntry.Log?.Log("检查更新出错：" + ex.Message, StardewModdingAPI.LogLevel.Warn); }
            });
        }

        private void StartUpdate(ModState s)
        {
            if (s == null || s.Busy)
                return;

            // 已忽略的：点按钮恢复
            if (s.Status == "ignored")
            {
                ToggleIgnore(s);
                return;
            }

            // 没有更新源的 Mod：先补源
            if (s.Mod.Keys.Count == 0 && !s.Mod.IsVirtual)
            {
                Game1.activeClickableMenu = new KeyInputDialog(s.Mod, key =>
                {
                    ModEntry.Config.ExtraUpdateKeys ??= new Dictionary<string, string>();
                    ModEntry.Config.ExtraUpdateKeys[s.Mod.Id] = key.Raw;
                    ModEntry.SaveConfig();
                    Toast("已补写更新源 " + key.Raw + "，正在检查…");

                    var svc = ModEntry.Service;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            svc.RefreshMods();
                            await svc.CheckAsync(svc.Find(s.Mod.Id));
                        }
                        catch (Exception ex)
                        {
                            ModEntry.Log?.Log("补源后检查失败：" + ex.Message, StardewModdingAPI.LogLevel.Warn);
                        }
                    });
                });
                return;
            }
            if (s.ManualDownload)
            {
                Toast("这个来源不支持自动下载，已打开下载页，下好后放到 Mod 目录覆盖即可");
                OpenPage(s);
                return;
            }
            if (!s.CanAutoUpdate && s.JobState != "error")
            {
                Toast(s.Mod.ManualHint ?? "这个 Mod 需要到网页手动更新");
                OpenPage(s);
                return;
            }
            if (s.JobState == "error" && s.Plan == null)
            {
                OpenPage(s);
                return;
            }
            Toast("开始更新：" + s.Mod.Name);
            var svc = ModEntry.Service;
            _ = Task.Run(async () =>
            {
                try { await svc.UpdateAsync(s); }
                catch (Exception ex) { ModEntry.Log?.Log("更新出错：" + ex.Message, StardewModdingAPI.LogLevel.Warn); }
            });
        }

        private void StartUpdateAll()
        {
            var svc = ModEntry.Service;
            var n = svc.Snapshot().Count(s => s.CanAutoUpdate && !s.Busy);
            if (n == 0)
            {
                Toast("没有可自动更新的 Mod");
                return;
            }
            Toast($"开始更新 {n} 个 Mod…");
            _ = Task.Run(async () =>
            {
                try { await svc.UpdateAllAsync(); }
                catch (Exception ex) { ModEntry.Log?.Log("批量更新出错：" + ex.Message, StardewModdingAPI.LogLevel.Warn); }
            });
        }

        private static void OpenPage(ModState s)
        {
            string url = s?.PageUrl;
            if (string.IsNullOrEmpty(url))
            {
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log("打开浏览器失败：" + ex.Message, StardewModdingAPI.LogLevel.Warn);
            }
        }

        private static void OpenFolder(ModState s)
        {
            string dir = s?.Mod?.Dir;
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch { }
        }

        private void ToggleIgnore(ModState s)
        {
            if (s == null)
                return;
            var cfg = ModEntry.Config;
            cfg.IgnoredModIds ??= new List<string>();
            bool removed = cfg.IgnoredModIds.RemoveAll(x => string.Equals(x, s.Mod.Id, StringComparison.OrdinalIgnoreCase)) > 0;

            if (removed)
            {
                s.Status = "unknown";
                Toast("已取消忽略：" + s.Mod.Name + "，正在重新检查…");
                if (_filter == 4)
                    _filter = 0;

                var svc = ModEntry.Service;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        svc.RefreshMods();
                        await svc.CheckAsync(svc.Find(s.Mod.Id));
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Log?.Log("取消忽略后检查失败：" + ex.Message, StardewModdingAPI.LogLevel.Warn);
                    }
                });
            }
            else
            {
                cfg.IgnoredModIds.Add(s.Mod.Id);
                s.Status = "ignored";
                Toast("已忽略：" + s.Mod.Name + "（点「取消忽略」恢复）");
            }

            ModEntry.SaveConfig();
            ModEntry.Service.BumpPublic();
        }

        // ---------------- 输入 ----------------
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                exitThisMenu(true);
                return;
            }
            if (_scroll.TryGrab(x, y))
                return;

            if (_btnCheck.Contains(x, y)) { Game1.playSound("smallSelect"); StartCheck(); return; }
            if (_btnSettings.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                Game1.activeClickableMenu = new SettingsMenu(() => Game1.activeClickableMenu = new ManagerMenu());
                return;
            }
            if (_btnUpdateAll.Contains(x, y)) { Game1.playSound("smallSelect"); StartUpdateAll(); return; }
            if (_btnInbox.Contains(x, y)) { Game1.playSound("smallSelect"); StartInboxInstall(); return; }

            for (int i = 0; i < _chips.Count; i++)
            {
                if (_chips[i].Bounds.Contains(x, y))
                {
                    _filter = i;
                    _scroll.Pos = 0;
                    ApplyFilter();
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            if (!_content.Contains(x, y))
                return;

            int idx = HitRow(x, y);
            if (idx < 0)
                return;
            var s = _rows[idx];
            RowButtons(idx, out var web, out var act);

            if (web.Contains(x, y)) { Game1.playSound("smallSelect"); OpenPage(s); return; }
            if (act.Contains(x, y)) { Game1.playSound("smallSelect"); StartUpdate(s); return; }

            OpenFolder(s);
        }

        public override void receiveRightClick(int x, int y, bool playSound = true)
        {
            int idx = HitRow(x, y);
            if (idx >= 0)
                ToggleIgnore(_rows[idx]);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _scroll.Release();
        }

        public override void receiveScrollWheelAction(int direction)
        {
            _scroll.ScrollBy(direction > 0 ? -80 : 80);
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);
            _hoverRow = -1;
            _hoverBtn = -1;
            if (!_content.Contains(x, y))
                return;
            int idx = HitRow(x, y);
            if (idx < 0)
                return;
            _hoverRow = idx;
            RowButtons(idx, out var web, out var act);
            if (web.Contains(x, y)) _hoverBtn = 0;
            else if (act.Contains(x, y)) _hoverBtn = 1;
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                exitThisMenu(true);
                return;
            }
            base.receiveKeyPress(key);
        }

        public override bool readyToClose() => true;

        public override void update(GameTime time)
        {
            base.update(time);
            if (_scroll.Dragging)
                _scroll.ApplyDrag(Game1.getMouseY());
            if (_hintTicks > 0 && --_hintTicks == 0)
                _hint = "";
            SyncRows();
        }

        // ---------------- 绘制 ----------------
        public override void draw(SpriteBatch b)
        {
            SyncRows();

            Ui.Fill(b, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), new Color(0, 0, 0, 130));
            Ui.Box(b, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), Ui.PageBg, Ui.Line);

            var svc = ModEntry.Service;
            var all = svc?.Snapshot() ?? new List<ModState>();
            int total = all.Count;
            int upd = all.Count(s => s.HasUpdate);

            // 标题
            Ui.Text(b, "Mod 管理器", new Vector2(xPositionOnScreen + 20, yPositionOnScreen + 14), Ui.Gold, Game1.dialogueFont);
            string sub = svc != null && svc.Checking
                ? $"检查中… {svc.CheckDone}/{svc.CheckTotal}"
                : $"共 {total} 个 Mod · {upd} 个可更新";
            Ui.Text(b, sub, new Vector2(xPositionOnScreen + 20, yPositionOnScreen + 40), Ui.Dim);

            Ui.Button(b, _btnCheck, svc != null && svc.Checking ? "检查中…" : "检查更新",
                svc != null && !svc.Checking, _btnCheck.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Green);
            Ui.Button(b, _btnSettings, "设置", true, _btnSettings.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Blue);

            // 工具栏
            for (int i = 0; i < _chips.Count; i++)
            {
                bool on = i == _filter;
                var r = _chips[i].Bounds;
                Ui.Box(b, r, on ? Ui.Hover : Ui.PanelBg, on ? Ui.Gold : Ui.Line);
                var size = Ui.Measure(_chips[i].Label);
                Ui.Text(b, _chips[i].Label, new Vector2(r.X + (r.Width - size.X) / 2, r.Y + (r.Height - size.Y) / 2),
                    on ? Ui.Gold : Ui.Dim);
            }

            int canAll = all.Count(s => s.CanAutoUpdate && !s.Busy);
            Ui.Button(b, _btnUpdateAll, canAll > 0 ? $"全部更新 ({canAll})" : "全部更新",
                canAll > 0, _btnUpdateAll.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Gold);

            int ready = InboxReady;
            bool noFolder = string.IsNullOrWhiteSpace(ModEntry.Config?.InboxFolder);
            string inboxLabel = _inboxBusy ? "扫描中…"
                : noFolder ? "安装下载包（未设目录）"
                : ready > 0 ? $"安装下载包 ({ready})" : "安装下载包";
            Ui.Button(b, _btnInbox, inboxLabel, ready > 0 && !_inboxBusy,
                _btnInbox.Contains(Game1.getMouseX(), Game1.getMouseY()), Ui.Green);

            // 列表
            Ui.Box(b, _content, Ui.PanelBg2, Ui.Line);
            Ui.BeginClip(b, _content);
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(b, _rows[i], i);
            Ui.EndClip(b);
            _scroll.Draw(b);

            if (_rows.Count == 0)
                Ui.Text(b, "（这个筛选条件下没有 Mod）", new Vector2(_content.X + 16, _content.Y + 14), Ui.Dim);

            // 底部
            int fy = yPositionOnScreen + height - 52;
            Ui.Fill(b, new Rectangle(xPositionOnScreen + 16, fy - 8, width - 32, 1), Ui.Line);
            string foot = _hint;
            if (string.IsNullOrEmpty(foot))
            {
                foot = svc?.LastCheckText ?? "";
                bool needKey = string.IsNullOrWhiteSpace(ModEntry.Config?.NexusApiKey)
                    && all.Any(s => s.Mod.Keys.Any(k => k.Site == "Nexus"));
                if (needKey)
                    foot = "未填写 Nexus API Key：Nexus 上的 Mod 无法检查和更新，点右上角「设置」填写（免费申请）";
            }
            int pending = _pending;
            if (pending > 0)
                foot += $" · {pending} 个更新待下次启动应用";
            Ui.Text(b, Ui.Clip(foot, Game1.smallFont, width - 220), new Vector2(xPositionOnScreen + 20, fy + 6), string.IsNullOrEmpty(_hint) ? Ui.Dim : Ui.Gold);
            string hint = "左键打开文件夹 · 右键忽略/取消 · 滚轮滚动";
            Ui.Text(b, hint, new Vector2(xPositionOnScreen + width - 20 - Ui.Measure(hint).X, fy + 6), Ui.Dim);

            upperRightCloseButton?.draw(b);
            Ui.DrawCursor(b);
        }

        private void DrawRow(SpriteBatch b, ModState s, int i)
        {
            var r = RowRect(i);
            if (r.Bottom <= _content.Y || r.Y >= _content.Bottom)
                return;

            Color accent = s.Status switch
            {
                "update" => Ui.Gold,
                "latest" => Ui.Green,
                "error" => Ui.Red,
                "manual" => Ui.Blue,
                "ignored" => Ui.Dim,
                _ => Ui.Line
            };
            if (s.JobState == "done") accent = Ui.Green;
            if (s.JobState == "error") accent = Ui.Red;

            bool hover = i == _hoverRow;
            Ui.Fill(b, r, hover ? Ui.Hover : Ui.PanelBg);
            Ui.Fill(b, new Rectangle(r.X, r.Y, 4, r.Height), accent);

            RowButtons(i, out var web, out var act);
            float textW = Math.Max(120, act.X - r.X - 28);

            Ui.Text(b, Ui.Clip(ModNames.Display(s.Mod), Game1.smallFont, textW), new Vector2(r.X + 14, r.Y + 8),
                s.HasUpdate ? Ui.Gold : Ui.Ink);
            Ui.Text(b, Ui.Clip(SubText(s), Game1.smallFont, textW), new Vector2(r.X + 14, r.Y + 32), Ui.Dim);

            Ui.Button(b, web, "网页", !string.IsNullOrEmpty(s.PageUrl), hover && _hoverBtn == 0, Ui.Blue);

            if (s.Busy)
            {
                Ui.ProgressBar(b, act, s.Progress);
            }
            else
            {
                var (label, enabled) = ActionButton(s);
                Ui.Button(b, act, label, enabled, hover && _hoverBtn == 1, enabled ? Ui.Gold : Ui.Line);
            }
        }

        private static string SubText(ModState s)
        {
            string src = s.SourceName ?? (s.Mod.Keys.Count > 0 ? s.Mod.Keys[0].Site : "无更新源");
            string head = s.Status switch
            {
                "update" => $"当前 {s.Mod.Version}  →  最新 {s.LatestVersion}   · {src}",
                "latest" => $"已是最新 {s.Mod.Version} · {src}",
                "checking" => "检查中…",
                "manual" => $"{s.Mod.Version} · {src} · 需到网页手动更新",
                "error" => $"{s.Mod.Version} · {s.Message}",
                "ignored" => $"{s.Mod.Version} · 已忽略（右键取消）",
                _ => s.Mod.Keys.Count == 0 && !s.Mod.IsVirtual
                    ? $"{s.Mod.Version} · 没有更新源，点「补源」填一个"
                    : $"{s.Mod.Version} · 未检查"
            };

            return s.JobState switch
            {
                "downloading" => $"下载中 {s.Progress}% · {src}",
                "installing" => "正在写入文件…",
                "done" => "已更新，重启游戏后生效",
                "pending" => "文件被占用，下次启动时自动完成",
                "error" => "失败：" + (s.JobError ?? "未知错误"),
                _ => head
            };
        }

        private static (string, bool) ActionButton(ModState s)
        {
            if (s.JobState == "done") return ("已更新", false);
            if (s.JobState == "pending") return ("待重启", false);
            if (s.JobState == "error") return (s.ManualDownload ? "手动下载" : "重试", true);
            if (s.Mod.Keys.Count == 0 && !s.Mod.IsVirtual) return ("补源", true);
            if (s.HasUpdate) return s.CanAutoUpdate ? ("更新", true) : ("手动", true);
            if (s.Status == "manual") return ("去网页", true);
            if (s.Status == "ignored") return ("取消忽略", true);
            if (s.Status == "error") return ("重试", true);
            return ("最新", false);
        }
    }
}
