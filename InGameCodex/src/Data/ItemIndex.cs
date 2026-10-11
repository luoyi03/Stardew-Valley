using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace InGameCodex.Data
{
    /// <summary>
    /// 运行时从游戏数据表构建的物品主索引（不读任何硬编码物品表）。
    /// 构建时机：进入存档时（SaveLoaded）一次；内容 Mod 或语言变化后重进存档即自动重建。
    /// </summary>
    public class ItemIndex
    {
        private readonly IMonitor _log;

        /// <summary>全部条目（按类别顺序）。</summary>
        public List<CodexItem> Items { get; } = new();

        private readonly Dictionary<string, CodexItem> _byId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CodexItem>> _byType = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>未限定 id → 条目（游戏数据表里几乎都是纯数字 id，见 S3 反向索引）。</summary>
        private readonly Dictionary<string, CodexItem> _byRaw = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>构建过程中的告警（不阻断，只记录）。</summary>
        public List<string> Warnings { get; } = new();

        public bool IsBuilt { get; private set; }

        /// <summary>本次构建耗时（毫秒），用于风险项 T8 实测。</summary>
        public long BuildMs { get; private set; }

        /// <summary>建不出物品实例、被跳过的 id 数。</summary>
        public int Skipped { get; private set; }

        public ItemIndex(IMonitor log)
        {
            _log = log;
        }

        public void EnsureBuilt()
        {
            if (!IsBuilt)
                Build();
        }

        public void Build()
        {
            var sw = Stopwatch.StartNew();
            Items.Clear();
            _byId.Clear();
            _byType.Clear();
            _byRaw.Clear();
            Warnings.Clear();
            Skipped = 0;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var registryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var def in ItemRegistry.ItemTypes)
            {
                string key;
                try { key = def.Identifier; }
                catch (Exception ex) { Warnings.Add("读类别标识失败：" + ex.Message); continue; }

                if (string.IsNullOrWhiteSpace(key))
                    continue;

                string bare = key.Trim('(', ')');
                registryKeys.Add(bare);

                // 只收文档约定的 13 类；将来游戏新增大类也不会混进来
                if (CodexTypes.Find(bare) == null)
                    continue;

                List<string> ids;
                try { ids = def.GetAllIds()?.ToList() ?? new List<string>(); }
                catch (Exception ex)
                {
                    Warnings.Add("类别 " + bare + " 取 id 列表失败：" + ex.Message);
                    continue;
                }

                foreach (string raw in ids)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    string qid = raw.StartsWith("(", StringComparison.Ordinal) ? raw : key + raw;
                    if (!seen.Add(qid))
                        continue;

                    CodexItem entry = CreateEntry(qid, raw, bare);
                    if (entry == null)
                    {
                        Skipped++;
                        continue;
                    }

                    Items.Add(entry);
                    _byId[qid] = entry;
                    if (!_byRaw.ContainsKey(raw))
                        _byRaw[raw] = entry;

                    if (!_byType.TryGetValue(bare, out var list))
                    {
                        list = new List<CodexItem>();
                        _byType[bare] = list;
                    }
                    list.Add(entry);
                }
            }

            foreach (var t in CodexTypes.All)
            {
                if (!registryKeys.Contains(t.Key))
                    Warnings.Add("游戏注册表里没有 " + t.Key + "（" + t.Name + "）这一类");
            }

            sw.Stop();
            BuildMs = sw.ElapsedMilliseconds;
            IsBuilt = true;

            _log?.Log($"物品索引构建完成：{Items.Count} 条 / {_byType.Count} 类，耗时 {BuildMs} ms"
                      + (Skipped > 0 ? $"，跳过 {Skipped} 条" : ""), LogLevel.Info);

            foreach (string w in Warnings)
                _log?.Log("索引告警：" + w, LogLevel.Warn);
        }

        /// <summary>把一条数据表 id 变成图鉴条目；任何一步出错都只跳过这一条，不打断整体构建。</summary>
        private CodexItem CreateEntry(string qid, string rawId, string typeKey)
        {
            try
            {
                Item item = ItemRegistry.Create(qid, 1, 0);
                if (item == null)
                {
                    Warnings.Add("建不出物品：" + qid);
                    return null;
                }

                string name = item.DisplayName;
                if (string.IsNullOrWhiteSpace(name))
                    name = item.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = qid;

                string english = item.Name;
                if (string.IsNullOrWhiteSpace(english) ||
                    string.Equals(english, name, StringComparison.OrdinalIgnoreCase))
                    english = "";

                return new CodexItem
                {
                    Id = qid,
                    RawId = rawId,
                    TypeKey = typeKey,
                    TypeName = CodexTypes.NameOf(typeKey),
                    Name = name,
                    EnglishName = english,
                    Description = Safe(() => item.getDescription(), ""),
                    Price = Safe(() => item.sellToStorePrice(), -1),
                    Category = Safe(() => item.Category, 0),
                    Pinyin = Pinyin.ToInitials(name),
                    Item = item
                };
            }
            catch (Exception ex)
            {
                Warnings.Add("处理 " + qid + " 失败：" + ex.Message);
                return null;
            }
        }

        private static T Safe<T>(Func<T> get, T fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }

        public int CountOf(string typeKey)
            => _byType.TryGetValue(typeKey, out var list) ? list.Count : 0;

        public CodexItem Find(string qualifiedId)
            => qualifiedId != null && _byId.TryGetValue(qualifiedId, out var e) ? e : null;

        /// <summary>按未限定 id 找条目（数据表里大多是 24 这种纯数字 id）。</summary>
        public CodexItem FindByRaw(string rawId)
            => rawId != null && _byRaw.TryGetValue(rawId, out var e) ? e : null;

        /// <summary>未限定 id → 显示名；找不到就原样返回 id。</summary>
        public string NameOfRaw(string rawId)
        {
            CodexItem e = FindByRaw(rawId);
            return e != null && !string.IsNullOrWhiteSpace(e.Name) ? e.Name : (rawId ?? "");
        }

        public IReadOnlyList<CodexItem> OfType(string typeKey)
            => _byType.TryGetValue(typeKey, out var list) ? list : (IReadOnlyList<CodexItem>)new List<CodexItem>();
    }
}
