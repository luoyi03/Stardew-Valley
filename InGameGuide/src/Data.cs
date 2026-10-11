using System;
using System.Collections.Generic;
using System.Linq;

namespace InGameGuide
{
    public class GuideDoc
    {
        public string Title { get; set; }
        public List<Chapter> Chapters { get; set; } = new();
    }

    public class Chapter
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Sub { get; set; }
        public List<Block> Blocks { get; set; } = new();
    }

    /// <summary>由提取脚本生成的内容块。字段与 assets/guide.json 一致。</summary>
    public class Block
    {
        /** h3 / h4 / p / cap / note / li / flow / tbl / box */
        public string T { get; set; }

        /** note 的配色：tip / warn / info / "" */
        public string K { get; set; }

        /** 正文文本 */
        public string X { get; set; }

        /** li 是否有序 */
        public bool O { get; set; }

        /** flow 的步骤，或 box 的内容行 */
        public List<string> Xs { get; set; }

        /** box 的标题 */
        public string Title { get; set; }

        /** 表格：第一行为表头 */
        public List<List<string>> Rows { get; set; }
    }

    public class ItemDb
    {
        public Dictionary<string, string> Types { get; set; } = new();
        public List<string> Order { get; set; } = new();
        public List<List<string>> Items { get; set; } = new();

        private List<ItemRow> _rows;
        private Dictionary<string, int> _counts;

        public List<ItemRow> Rows => _rows ??= Build();

        public Dictionary<string, int> Counts => _counts ??= Build().GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Count());

        private List<ItemRow> Build()
        {
            var list = new List<ItemRow>(Items.Count);
            foreach (var it in Items)
            {
                if (it == null || it.Count < 4)
                    continue;
                list.Add(new ItemRow
                {
                    Type = it[0] ?? "",
                    Zh = it[1] ?? "",
                    En = it[2] ?? "",
                    Code = it[3] ?? ""
                });
            }
            // 稳定排序：先按类型顺序，再按代码数值
            list.Sort((a, b) =>
            {
                int ia = Order.IndexOf(a.Type), ib = Order.IndexOf(b.Type);
                ia = ia < 0 ? 99 : ia;
                ib = ib < 0 ? 99 : ib;
                if (ia != ib) return ia.CompareTo(ib);
                if (int.TryParse(a.Code, out int ca) && int.TryParse(b.Code, out int cb))
                    return ca.CompareTo(cb);
                return string.CompareOrdinal(a.Code, b.Code);
            });
            return list;
        }

        public string Label(string type) => Types.TryGetValue(type, out string v) ? v : type;

        public ItemRow Find(string q) => Rows.FirstOrDefault(r => r.Code == q || r.Zh == q || r.En == q);
    }

    public class ItemRow
    {
        public string Type { get; set; }
        public string Zh { get; set; }
        public string En { get; set; }
        public string Code { get; set; }

        /// <summary>物体类用于动物命名彩蛋时需要方括号。</summary>
        public bool IsSpawnable => Type == "O";

        public string DisplayName => string.IsNullOrEmpty(Zh) ? En : Zh;

        public string CopyText => IsSpawnable ? "[" + Code + "]" : Code;
    }
}
