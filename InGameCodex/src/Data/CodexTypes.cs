using System;
using System.Collections.Generic;

namespace InGameCodex.Data
{
    /// <summary>一个物品大类（键 = ItemRegistry 的前缀标识，如 O => (O)）。</summary>
    public class CodexType
    {
        /// <summary>注册表前缀，如 O / BC / WP。</summary>
        public string Key;

        /// <summary>中文类别名。</summary>
        public string Name;
    }

    /// <summary>固定 13 类，顺序与 InGameGuide 的物品代码查询一致。</summary>
    public static class CodexTypes
    {
        public static readonly CodexType[] All =
        {
            new CodexType { Key = "O",  Name = "物体" },
            new CodexType { Key = "BC", Name = "大型可制作物" },
            new CodexType { Key = "T",  Name = "工具" },
            new CodexType { Key = "W",  Name = "武器" },
            new CodexType { Key = "F",  Name = "家具" },
            new CodexType { Key = "S",  Name = "上衣" },
            new CodexType { Key = "H",  Name = "帽子" },
            new CodexType { Key = "P",  Name = "裤子" },
            new CodexType { Key = "B",  Name = "鞋子" },
            new CodexType { Key = "TR", Name = "饰品" },
            new CodexType { Key = "M",  Name = "人偶" },
            new CodexType { Key = "WP", Name = "壁纸" },
            new CodexType { Key = "FL", Name = "地板" }
        };

        private static readonly Dictionary<string, CodexType> Map = Build();

        private static Dictionary<string, CodexType> Build()
        {
            var map = new Dictionary<string, CodexType>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in All)
                map[t.Key] = t;
            return map;
        }

        public static CodexType Find(string key)
            => key != null && Map.TryGetValue(key, out var t) ? t : null;

        public static string NameOf(string key)
            => Find(key)?.Name ?? key ?? "";
    }
}
