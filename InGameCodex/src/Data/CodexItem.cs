using StardewValley;

namespace InGameCodex.Data
{
    /// <summary>图鉴里的一条物品（索引期构建一次，之后只读）。</summary>
    public class CodexItem
    {
        /// <summary>限定 ID，如 (O)24。</summary>
        public string Id;

        /// <summary>数据表里的原始 id（物体类是纯数字，如 24）。</summary>
        public string RawId;

        /// <summary>类别键，如 O。</summary>
        public string TypeKey;

        /// <summary>类别中文名，如 物体。</summary>
        public string TypeName;

        /// <summary>游戏内显示名（跟随游戏语言）。</summary>
        public string Name;

        /// <summary>英文名（取得不到时为空）。</summary>
        public string EnglishName;

        /// <summary>游戏内描述。</summary>
        public string Description;

        /// <summary>直接售价（工具/无价物品为 -1）。</summary>
        public int Price;

        /// <summary>游戏内部类别号（-4 鱼、-5 蛋 等），保留给后续细分筛选用。</summary>
        public int Category;

        /// <summary>名字的拼音首字母，如 铱制洒水器 => yzssq。</summary>
        public string Pinyin;

        /// <summary>索引期创建好的物品实例，用于绘制贴图和详情展示（懒创建失败时为 null）。</summary>
        public Item Item;

        /// <summary>贴图绘制是否已确认不可用（T1/T2/T7 降级用：改用类别色块）。</summary>
        public bool IconFailed;

        public string PriceText => Price > 0 ? Price + " 金" : "—";

        public string NameOrId => string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }
}
