using System;
using System.Collections.Generic;
using System.Diagnostics;
using StardewModdingAPI;
using StardewValley.GameData.Characters;

namespace InGameCodex.Data
{
    /// <summary>NPC 喜好的一档（UI 展示顺序：最爱 / 喜欢 / 一般 / 不喜欢 / 厌恶）。</summary>
    public class CodexNpcTier
    {
        public string Label;

        /// <summary>类别行文本（如「所有鱼（30 种）」），不可点击。</summary>
        public readonly List<string> Categories = new List<string>();

        public readonly List<CodexItem> Items = new List<CodexItem>();

        /// <summary>截断前的件数（UI 用「还有 N 条」）。</summary>
        public int Count;
    }

    /// <summary>图鉴里的一位 NPC（索引期构建一次，之后只读）。见需求文档 §18。</summary>
    public class CodexNpc
    {
        /// <summary>内部名（Data/Characters 的键）。「全员通用」条目的 Key 为 Universal。</summary>
        public string Key;

        /// <summary>中文显示名。</summary>
        public string Name;

        /// <summary>英文内部名（详情里作对照）。</summary>
        public string EnglishName;

        /// <summary>是否「全员通用」条目（列表里固定第一）。</summary>
        public bool Universal;

        /// <summary>生日季节（春 0 / 夏 1 / 秋 2 / 冬 3），-1 表示没有生日数据。</summary>
        public int Season = -1;

        /// <summary>生日日（1..28）。</summary>
        public int Day;

        /// <summary>是否可结婚（Data/Characters 的 CanBeRomanced）。</summary>
        public bool CanMarry;

        public string Pinyin;

        /// <summary>五档，按 TierLabels 的顺序（最爱 / 喜欢 / 一般 / 不喜欢 / 厌恶）。</summary>
        public readonly List<CodexNpcTier> Tiers = new List<CodexNpcTier>();

        public bool HasBirthday => Season >= 0 && Day > 0;

        /// <summary>生日文本，如「秋 13」；没有生日数据时为空。</summary>
        public string BirthdayText => HasBirthday ? NpcIndex.SeasonNames[Season] + " " + Day : "";

        /// <summary>一年内的第几天（0..111）：季节序 * 28 + 日 - 1。</summary>
        public int DayOfYear => HasBirthday ? Season * 28 + (Day - 1) : -1;
    }

    /// <summary>
    /// NPC 喜好索引（S4）：Data/Characters（名字 / 生日 / 可结婚）+ Data/NPCGiftTastes（五档）。见需求文档 §18。
    ///
    /// Data/NPCGiftTastes 的值是「对话 + 物品」成对的 10 段（第 11 段为空），段序为：
    /// 0/1 最爱、2/3 喜欢、4/5 不喜欢、6/7 厌恶、8/9 一般。该顺序由 v1.8 实测确定（2026-10-11，见 §18.2）：
    /// Harvey 段4 的「你确定这个健康？」= wiki 的 Dislike 台词、段6「我好像对它过敏」= Hate 台词、段8「谢谢你。你人真好」= Neutral 台词；
    /// Abigail 段1 = 她全部 7 件最爱，段3 = wiki 的 Like（古剑 / 骨笛）。
    /// Universal_* 键只有 1 段（该档物品清单，没有对话）。
    /// </summary>
    public class NpcIndex
    {
        /// <summary>单档最多保留的条目数（超出显示「还有 N 条」）。</summary>
        public const int MaxPerTier = 200;

        /// <summary>五档的显示名（UI 顺序）。</summary>
        public static readonly string[] TierLabels = { "最爱", "喜欢", "一般", "不喜欢", "厌恶" };

        /// <summary>显示档位 → NPCGiftTastes 的段序号（显示顺序 最爱/喜欢/一般/不喜欢/厌恶 = 段 1/3/9/5/7）。</summary>
        public static readonly int[] TierSlot = { 1, 3, 9, 5, 7 };

        /// <summary>显示档位 → 数据档位（数据顺序：0 最爱 / 1 喜欢 / 2 不喜欢 / 3 厌恶 / 4 一般），即游戏判定优先级。</summary>
        public static readonly int[] TierData = { 0, 1, 4, 2, 3 };

        public static readonly string[] SeasonNames = { "春", "夏", "秋", "冬" };

        public readonly List<CodexNpc> Npcs = new List<CodexNpc>();

        public bool IsBuilt { get; private set; }
        public long BuildMs { get; private set; }
        public readonly List<string> Warnings = new List<string>();

        private ItemIndex _index;
        private CodexRefIndex _refs;
        private IMonitor _log;

        /// <summary>上一次读取到的 NPCGiftTastes / Characters 疑似不完整（见 §18.7），下次调用 EnsureBuilt 时重读。</summary>
        private bool _suspect;

        public void EnsureBuilt(ItemIndex index, CodexRefIndex refs, IMonitor log)
        {
            if (IsBuilt && !_suspect)
                return;

            Build(index, refs, log);
            if (_suspect)
            {
                // 实测：标题界面初次读出过只有 9 位 NPC / 没有 Universal_* 的部分数据（见 §18.7），当场重读一次。
                _log?.Log("[NPC] 数据疑似不完整，立即重读一次…", LogLevel.Info);
                Build(index, refs, log);
            }
        }

        public void Build(ItemIndex index, CodexRefIndex refs, IMonitor log)
        {
            var sw = Stopwatch.StartNew();
            _index = index;
            _refs = refs;
            _log = log;
            Npcs.Clear();
            Warnings.Clear();

            // 先让 SMAPI 丢弃这两个资产的缓存，避免读到「补丁只应用了一半」的快照（见 §18.7 实测）。
            try
            {
                ModEntry.Hlp.GameContent.InvalidateCache("Data/NPCGiftTastes");
                ModEntry.Hlp.GameContent.InvalidateCache("Data/Characters");
            }
            catch (Exception ex)
            {
                Warnings.Add("丢弃 NPC 数据缓存失败：" + ex.Message);
            }

            var tastes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in ModEntry.Hlp.GameContent.Load<Dictionary<string, string>>("Data/NPCGiftTastes"))
                    tastes[p.Key] = p.Value;
            }
            catch (Exception ex)
            {
                Warnings.Add("读取 Data/NPCGiftTastes 失败：" + ex.Message);
            }

            var chars = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in ModEntry.Hlp.GameContent.Load<Dictionary<string, CharacterData>>("Data/Characters"))
                    chars[p.Key] = p.Value;
            }
            catch (Exception ex)
            {
                Warnings.Add("读取 Data/Characters 失败：" + ex.Message);
            }

            // 名单：Data/Characters 里可社交的 + NPCGiftTastes 里出现过但 Characters 没有的（Mod 新增）。
            var keys = new List<string>();
            foreach (string k in chars.Keys)
            {
                if (IsUniversal(k) || tastes.ContainsKey(k))
                    continue;
                if (CanSocialize(chars[k]))
                    keys.Add(k);
            }
            foreach (string k in tastes.Keys)
            {
                if (IsUniversal(k) || keys.Contains(k))
                    continue;
                keys.Add(k);
            }

            int universalKeys = 0;
            foreach (string k in tastes.Keys)
            {
                if (IsUniversal(k))
                    universalKeys++;
            }

            keys.Sort((a, b) => string.Compare(DisplayName(a, CharOf(chars, a)), DisplayName(b, CharOf(chars, b)), StringComparison.CurrentCulture));

            foreach (string k in keys)
            {
                try
                {
                    Npcs.Add(BuildNpc(k, CharOf(chars, k), tastes));
                }
                catch (Exception ex)
                {
                    Warnings.Add("NPC「" + k + "」构建失败：" + ex);   // 临时：带堆栈，定位后改回 ex.Message
                }
            }

            try
            {
                Npcs.Insert(0, BuildUniversal(tastes));
            }
            catch (Exception ex)
            {
                Warnings.Add("全员通用构建失败：" + ex.Message);
            }

            // 完整性判据：原版 + SVE 至少有 40 位可社交 NPC、5 个 Universal_* 键；明显偏小说明读到了半成品数据。
            _suspect = keys.Count < 12 || universalKeys < 5;
            if (_suspect)
            {
                Warnings.Add("NPCGiftTastes / Characters 疑似不完整（NPC " + keys.Count + " 位、Universal_* " + universalKeys + " 个）");
            }

            IsBuilt = true;
            BuildMs = sw.ElapsedMilliseconds;
            _log?.Log("NPC 索引构建完成：" + Npcs.Count + " 位（含全员通用），耗时 " + BuildMs + " ms", LogLevel.Info);
            foreach (string w in Warnings)
                _log?.Log("NPC 索引告警：" + w, LogLevel.Warn);

        }

        private CodexNpc BuildNpc(string key, object cd, Dictionary<string, string> tastes)
        {
            string name = DisplayName(key, cd);

            var npc = new CodexNpc
            {
                Key = key,
                Name = name,
                EnglishName = key,
                Pinyin = Pinyin.ToInitials(name),
                Season = SeasonIndex(CodexRefIndex.Prop(cd, "BirthSeason")),
                Day = ToInt(CodexRefIndex.Prop(cd, "BirthDay")),
                CanMarry = IsTrue(CodexRefIndex.Prop(cd, "CanBeRomanced"))
            };
            for (int d = 0; d < 5; d++)
                npc.Tiers.Add(new CodexNpcTier { Label = TierLabels[d] });

            string raw = tastes.TryGetValue(key, out string r) ? r : "";
            string[] segs = raw.Split(new[] { '/' }, StringSplitOptions.None);

            var explicitIds = new List<string>[5];
            var cats = new List<int>[5];
            // 先按下标 0..4 全部初始化，再按 TierData 映射访问：显示档位→数据档位不是恒等映射，
            // 若在同一次循环里边初始化 d 边取 [TierData[d]]，会读到尚未初始化的 null（曾导致 43 位 NPC 构建失败）。
            for (int d = 0; d < 5; d++)
            {
                explicitIds[d] = new List<string>();
                cats[d] = new List<int>();
            }
            for (int d = 0; d < 5; d++)
            {
                int data = TierData[d];
                int slot = TierSlot[d];
                if (slot < segs.Length)
                    ParseTokens(segs[slot], explicitIds[data], cats[data]);
            }
            FillTiers(npc, explicitIds, cats);
            return npc;
        }

        private CodexNpc BuildUniversal(Dictionary<string, string> tastes)
        {
            var npc = new CodexNpc { Key = "Universal", Name = "全员通用", EnglishName = "", Universal = true, Pinyin = "qyty" };
            for (int d = 0; d < 5; d++)
                npc.Tiers.Add(new CodexNpcTier { Label = TierLabels[d] });

            var explicitIds = new List<string>[5];
            var cats = new List<int>[5];
            for (int d = 0; d < 5; d++)
            {
                explicitIds[d] = new List<string>();
                cats[d] = new List<int>();
            }
            for (int d = 0; d < 5; d++)
            {
                int data = TierData[d];
                string key = UniversalKey(data);
                if (key == null || !tastes.TryGetValue(key, out string raw))
                    continue;
                string[] segs = raw.Split(new[] { '/' }, StringSplitOptions.None);
                if (segs.Length > 0)
                    ParseTokens(segs[0], explicitIds[data], cats[data]);
            }
            FillTiers(npc, explicitIds, cats);
            return npc;
        }

        /// <summary>把一段里的 token 分成「物品 id」与「负数的物品类别 id」。</summary>
        private static void ParseTokens(string seg, List<string> explicitIds, List<int> cats)
        {
            foreach (string tok in seg.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(tok, out int cat) && cat < 0)
                    cats.Add(cat);
                else
                    explicitIds.Add(tok);
            }
        }

        /// <summary>
        /// 按游戏的判定优先级摊平五档：先各档的「点名物品」（数据顺序 最爱 → 喜欢 → 不喜欢 → 厌恶 → 一般），
        /// 再各档的「类别」，同一件物品只进最先命中的那一档（与游戏里「点名优先于类别」一致，
        /// 例如 Abigail 最爱南瓜、同时 -75 蔬菜在不喜欢里，显示时南瓜只出现在最爱）。
        /// </summary>
        private void FillTiers(CodexNpc npc, List<string>[] explicitIds, List<int>[] cats)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int data = 0; data < 5; data++)
            {
                CodexNpcTier tier = npc.Tiers[DisplayOfData(data)];
                foreach (string tok in explicitIds[data])
                {
                    CodexItem it = Resolve(tok);
                    if (it != null && claimed.Add(it.Id))
                        tier.Items.Add(it);
                }
            }

            // 第二趟：各档的「类别」（同一件物品只进最先命中的那一档）
            for (int data = 0; data < 5; data++)
            {
                CodexNpcTier tier = npc.Tiers[DisplayOfData(data)];
                foreach (int cat in cats[data])
                {
                    IReadOnlyList<CodexItem> list = _refs?.ItemsInCategory(cat);
                    if (list == null || list.Count == 0)
                        continue;   // 当前数据里该类别没有任何物品（如 -25），不显示空类别行

                    string cn = null;
                    for (int i = 0; i < list.Count && i < 6 && string.IsNullOrWhiteSpace(cn); i++)
                        cn = CategoryName(list[i]);

                    int added = 0;
                    foreach (CodexItem it in list)
                    {
                        if (claimed.Add(it.Id))
                        {
                            tier.Items.Add(it);
                            added++;
                        }
                    }
                    if (added == 0)
                        continue;   // 该类别全部物品已被更高档位收走，不显示空行
                    if (string.IsNullOrWhiteSpace(cn))
                    {
                        tier.Categories.Add("一类物品（" + added + " 种）");
                    }
                    else
                    {
                        tier.Categories.Add("所有" + cn + "（" + added + " 种）");
                    }
                }
            }

            foreach (CodexNpcTier t in npc.Tiers)
            {
                t.Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
                t.Count = t.Items.Count;
                if (t.Items.Count > MaxPerTier)
                    t.Items.RemoveRange(MaxPerTier, t.Items.Count - MaxPerTier);
            }
        }

        /// <summary>显示档位 → 数据档位。</summary>
        private static int DisplayOfData(int data)
        {
            for (int d = 0; d < 5; d++)
            {
                if (TierData[d] == data)
                    return d;
            }
            return 0;
        }

        private CodexItem Resolve(string tok)
        {
            if (int.TryParse(tok, out int n) && n < 0)
                return null;
            if (!CodexRefIndex.IsItemToken(tok))
                return null;
            string rawId = CodexRefIndex.Unqualify(tok);
            CodexItem it = _index?.FindByRaw(rawId);
            if (it == null)
                return null;
            return it;
        }

        private static string CategoryName(CodexItem sample)
        {
            try
            {
                if (sample?.Item != null)
                {
                    string n = sample.Item.getCategoryName();
                    if (!string.IsNullOrWhiteSpace(n))
                        return n;
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>数据档位 → Universal_* 的键。</summary>
        private static string UniversalKey(int data)
        {
            switch (data)
            {
                case 0: return "Universal_Love";
                case 1: return "Universal_Like";
                case 2: return "Universal_Dislike";
                case 3: return "Universal_Hate";
                case 4: return "Universal_Neutral";
            }
            return null;
        }

        private static bool IsUniversal(string key)
            => key != null && key.StartsWith("Universal_", StringComparison.OrdinalIgnoreCase);

        /// <summary>Data/Characters 里明确标为不可社交、也不可收礼的角色不进列表。</summary>
        private static bool CanSocialize(object cd)
        {
            if (cd == null)
                return true;
            if (IsFalse(CodexRefIndex.Prop(cd, "CanSocialize")))
                return false;
            if (IsFalse(CodexRefIndex.Prop(cd, "CanReceiveGifts")))
                return false;
            return true;
        }

        private string DisplayName(string key, object cd)
        {
            if (cd != null)
            {
                string dn = CodexRefIndex.ResolveText(CodexRefIndex.PropStr(cd, "DisplayName"));
                if (!string.IsNullOrWhiteSpace(dn))
                    return dn;
            }
            return key;
        }

        private static object CharOf(Dictionary<string, object> chars, string key)
            => chars.TryGetValue(key, out object v) ? v : null;

        private static int SeasonIndex(object v)
        {
            if (v == null)
                return -1;
            if (v is string s)
            {
                s = s.Trim();
                string low = s.ToLowerInvariant();
                if (low.StartsWith("spring")) return 0;
                if (low.StartsWith("summer")) return 1;
                if (low.StartsWith("fall") || low.StartsWith("autumn")) return 2;
                if (low.StartsWith("winter")) return 3;
                if (s.Contains("春")) return 0;
                if (s.Contains("夏")) return 1;
                if (s.Contains("秋")) return 2;
                if (s.Contains("冬")) return 3;
                return int.TryParse(s, out int i) && i >= 0 && i < 4 ? i : -1;
            }
            try
            {
                int n = Convert.ToInt32(v);
                return n >= 0 && n < 4 ? n : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static int ToInt(object v)
        {
            if (v == null)
                return 0;
            try
            {
                return Convert.ToInt32(v);
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsTrue(object v)
            => v != null && string.Equals(Convert.ToString(v)?.Trim(), "True", StringComparison.OrdinalIgnoreCase);

        private static bool IsFalse(object v)
            => v != null && string.Equals(Convert.ToString(v)?.Trim(), "False", StringComparison.OrdinalIgnoreCase);

        /// <summary>按内部名取一位 NPC（找不到返回 null）。</summary>
        public CodexNpc Find(string key)
        {
            foreach (CodexNpc n in Npcs)
            {
                if (string.Equals(n.Key, key, StringComparison.OrdinalIgnoreCase))
                    return n;
            }
            return null;
        }
    }
}