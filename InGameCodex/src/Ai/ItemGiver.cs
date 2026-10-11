using System;
using StardewModdingAPI;
using StardewValley;

namespace InGameCodex.Ai
{
    /// <summary>把物品真的放进玩家背包（配合 AI 输出的 [[GIVE:物品名]]）。</summary>
    public static class ItemGiver
    {
        public static string Give(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName))
                return "物品名是空的";

            if (Game1.player == null || !Context.IsWorldReady)
                return "还没进游戏，拿不到物品";

            if (!TutorialStore.TryResolveItem(itemName, out string id))
                return "没在物品表里找到「" + itemName + "」，可以在 InGameGuide 的物品代码查询里核对名字";

            Item item;

            try
            {
                item = ItemRegistry.Create(id, 1, 0);
            }
            catch (Exception ex)
            {
                return "生成「" + itemName + "」失败：" + ex.Message;
            }

            if (item == null)
                return "生成「" + itemName + "」失败（id " + id + "）";

            try
            {
                Game1.player.addItemByMenuIfNecessary(item);
            }
            catch (Exception ex)
            {
                return "放进背包失败：" + ex.Message;
            }

            string shown = item.DisplayName ?? itemName;
            bool inBag = Game1.player.Items != null && Game1.player.Items.Contains(item);

            Game1.playSound("coin");

            if (inBag)
            {
                Game1.addHUDMessage(new HUDMessage("已获得 " + shown, 2));
                return "已把「" + shown + "」放进背包";
            }

            return "背包满了，「" + shown + "」已经弹在旁边等你拿";
        }
    }
}