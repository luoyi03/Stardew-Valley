using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace InGameGuide
{
    /// <summary>把物品塞进背包 / 生成小鸡命名码。</summary>
    public static class ItemSpawner
    {
        /// <summary>物体类在数据里只存数字代码，需要补上 (O) 前缀。</summary>
        public static string QualifiedId(ItemRow row)
            => row.IsSpawnable ? "(O)" + row.Code : row.Code;

        /// <summary>
        /// 找出所有候选。返回 0 个＝没找到；返回 1 个＝明确；返回多个＝有歧义，必须先让用户选。
        /// 限定 ID（(O)645）和物体数字代码是唯一的，命中就直接返回 1 个。
        /// </summary>
        public static List<ItemRow> Candidates(string query, int max = 8)
        {
            var result = new List<ItemRow>();
            if (string.IsNullOrWhiteSpace(query))
                return result;

            string q = query.Trim();
            string ql = q.ToLowerInvariant();
            var rows = ModEntry.Db.Rows;

            // 1) 限定 ID：唯一
            var byQid = rows.FirstOrDefault(r => QualifiedId(r).Equals(q, StringComparison.OrdinalIgnoreCase));
            if (byQid != null)
            {
                result.Add(byQid);
                return result;
            }

            // 2) 物体类数字代码：唯一
            var byCode = rows.FirstOrDefault(r => r.Type == "O" && r.Code == q);
            if (byCode != null)
            {
                result.Add(byCode);
                return result;
            }

            // 3) 名称匹配：完全同名 > 前缀 > 包含
            var matched = rows.Where(r =>
                (r.Zh != null && r.Zh.IndexOf(ql, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (r.En != null && r.En.IndexOf(ql, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

            return matched
                .OrderBy(r => NameRank(r, q))
                .ThenBy(r => r.DisplayName.Length)
                .ThenBy(r => r.DisplayName, StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }

        private static int NameRank(ItemRow r, string q)
        {
            bool zhExact = r.Zh != null && r.Zh.Equals(q, StringComparison.OrdinalIgnoreCase);
            bool enExact = r.En != null && r.En.Equals(q, StringComparison.OrdinalIgnoreCase);
            if (zhExact || enExact)
                return 0;
            bool zhStart = r.Zh != null && r.Zh.StartsWith(q, StringComparison.OrdinalIgnoreCase);
            bool enStart = r.En != null && r.En.StartsWith(q, StringComparison.OrdinalIgnoreCase);
            if (zhStart || enStart)
                return 1;
            if (r.Zh != null && r.Zh.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            return 3;
        }

        /// <summary>给 UI 的提示用：这个查询是否有歧义。</summary>
        public static bool IsAmbiguous(string query) => Candidates(query, 8).Count > 1;

        public static string Give(ItemRow row, int count, int quality)
        {
            if (row == null)
                return "没找到这个物品";
            if (!Context.IsWorldReady || Game1.player == null)
                return "还没进入存档，先加载一个存档再用";

            count = Math.Max(1, Math.Min(9999, count));
            quality = Math.Max(0, Math.Min(4, quality));

            string qid = QualifiedId(row);
            Item item;
            try
            {
                item = ItemRegistry.Create(qid, count, quality);
            }
            catch (Exception ex)
            {
                return "创建物品失败（" + qid + "）：" + ex.Message;
            }
            if (item == null)
                return "游戏里没有这个物品：" + qid + "（" + row.DisplayName + "）";

            item.resetState();

            bool placed = Game1.player.addItemToInventoryBool(item);
            if (!placed)
            {
                // 背包满了就掉在脚下，别丢东西
                Game1.createItemDebris(item, Game1.player.getStandingPosition(), 1, Game1.player.currentLocation);
                Hud($"背包已满：{row.DisplayName} ×{count} 掉在了脚下");
                return $"背包已满，{row.DisplayName} ×{count} 已掉在脚下（{qid}）";
            }

            Hud($"获得 {row.DisplayName} ×{count}");
            return $"已放入背包：{row.DisplayName} ×{count}（{qid}）";
        }

        /// <summary>小鸡代码流：生成 [74] 形式的命名码，并设为买动物时的待用名字。</summary>
        public static string PrepareName(ItemRow row)
        {
            if (row == null)
                return "没找到这个物品";
            if (!row.IsSpawnable)
                return $"{row.DisplayName} 不是物体类，动物命名彩蛋只对 (O) 物体生效";

            string text = row.CopyText;
            bool copied = WinClipboard.SetText(text);
            PendingName = text;
            Hud($"命名码 {text} 已就绪，去玛妮处买动物时会自动填好");
            return $"命名码 {text}（{row.DisplayName}）" + (copied ? "，已复制到剪贴板" : "")
                   + "，已设为待用命名：在玛妮牧场买鸡/牛时会自动填入名字框";
        }

        private static void Hud(string msg)
        {
            try
            {
                Game1.addHUDMessage(new HUDMessage(msg, 2));
                Game1.playSound("coin");
            }
            catch { /* 忽略 */ }
        }

        /// <summary>待用的动物命名码，买动物时自动填入。</summary>
        public static string PendingName { get; set; }

        private static int _namingTicks;

        /// <summary>每帧调用：买动物弹出命名界面时，自动把待用命名码填进去。</summary>
        public static void ProcessPendingNaming()
        {
            if (PendingName == null)
                return;

            var menu = Game1.activeClickableMenu;
            if (menu == null)
            {
                _namingTicks = 0;
                return;
            }

            bool isNaming = menu is PurchaseAnimalsMenu
                || menu.GetType().Name.IndexOf("Naming", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isNaming)
            {
                _namingTicks = 0;
                return;
            }

            if (++_namingTicks < 3)
                return; // 等菜单初始化完

            string name = PendingName;
            PendingName = null;
            _namingTicks = 0;

            var box = FindTextBox(menu);
            if (box == null)
            {
                Hud("没找到命名框，请手动输入： " + name);
                return;
            }

            box.Text = name;
            Hud("已自动填入命名码 " + name + "，确认即可领取物品");
        }

        private static TextBox FindTextBox(object menu)
        {
            var type = menu.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;

            while (type != null && type != typeof(object))
            {
                foreach (var f in type.GetFields(flags))
                    if (f.FieldType == typeof(TextBox))
                        return f.GetValue(menu) as TextBox;
                foreach (var p in type.GetProperties(flags))
                    if (p.PropertyType == typeof(TextBox) && p.CanRead)
                        return p.GetValue(menu) as TextBox;
                type = type.BaseType;
            }
            return null;
        }
    }
}
