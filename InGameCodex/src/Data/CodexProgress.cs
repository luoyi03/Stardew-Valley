using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace InGameCodex.Data
{
    /// <summary>
    /// S5 的存档进度层：献祭（社区中心收集包）进度 + 六项图鉴完成度 + 收藏 / 最近查看。
    /// 静态部分（包的结构、图鉴的物品集合与总数）在 Build 里做一次；存档相关部分在 Refresh 里读。
    /// 存档字段的口径全部来自对游戏程序集的反编译核实，见 docs\需求文档.md §19.2。
    /// </summary>
    public sealed class CodexProgress
    {
        /// <summary>一个包里的一个槽位（一件要求物品，或一个「任意类别」要求）。</summary>
        public sealed class Slot
        {
            public CodexItem Item;
            public string Label = "";
            public int Stack = 1;
            public int Quality;
            public bool IsCategory;
            public int Category;
            public bool Done;
            public bool OwnedByCategory;
        }

        /// <summary>一个收集包。</summary>
        public sealed class BundleView
        {
            public int Key;
            public string Room = "";
            public string RoomName = "";
            public string Name = "";
            public string Reward = "";
            public bool RewardPending;
            public int DoneSlots;
            public int Required = 1;
            public readonly List<Slot> Slots = new List<Slot>();

            public bool Complete => Required > 0 && DoneSlots >= Required;
            public int MissingSlots => Math.Max(0, Required - DoneSlots);
        }

        /// <summary>图鉴里的一项进度（博物馆 / 古物 / 矿物 / 鱼类 / 烹饪配方 / 制作配方）。</summary>
        public sealed class Section
        {
            public int Kind;                 // 0 博物馆 1 古物 2 矿物 3 鱼类 4 烹饪配方 5 制作配方
            public string Title = "";
            public int Found;
            public int Total;
            public readonly List<CodexItem> Universe = new List<CodexItem>();
            public readonly List<CodexItem> Missing = new List<CodexItem>();
            public readonly List<string> RecipeNames = new List<string>();
            public readonly List<string> MissingRecipeNames = new List<string>();
        }

        private ItemIndex _index;
        private CodexRefIndex _refs;
        private IMonitor _log;

        public bool IsBuilt { get; private set; }
        public long BuildMs { get; private set; }
        /// <summary>本次 Refresh 是否成功读到存档（false 时界面按未完成显示并标注）。</summary>
        public bool SaveReadable { get; private set; }
        public List<string> Warnings { get; } = new List<string>();
        public List<BundleView> Bundles { get; } = new List<BundleView>();
        /// <summary>房间内部名（按 Data/Bundles 出现顺序）。</summary>
        public List<string> Rooms { get; } = new List<string>();
        public List<Section> Sections { get; } = new List<Section>();

        private readonly HashSet<string> _donated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fish = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _minerals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _cooking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _crafting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CodexItem> _recipeOutput = new Dictionary<string, CodexItem>(StringComparer.OrdinalIgnoreCase);
        /// <summary>产出物品 → 能做出它的配方名（判断「有配方」角标时用，避免每帧遍历全部配方）。</summary>
        private readonly Dictionary<CodexItem, List<string>> _recipesOf = new Dictionary<CodexItem, List<string>>();
        private readonly Dictionary<int, CodexItem> _categoryRep = new Dictionary<int, CodexItem>();

        public int BundleDoneCount
        {
            get
            {
                int n = 0;
                foreach (BundleView b in Bundles)
                    if (b.Complete)
                        n++;
                return n;
            }
        }

        public void EnsureBuilt(ItemIndex index, CodexRefIndex refs, IMonitor log)
        {
            if (!IsBuilt)
                Build(index, refs, log);
        }

        // ------------------------------------------------------------------ 构建（与存档无关）

        public void Build(ItemIndex index, CodexRefIndex refs, IMonitor log)
        {
            var sw = Stopwatch.StartNew();
            _index = index;
            _refs = refs;
            _log = log;
            Warnings.Clear();
            Bundles.Clear();
            Rooms.Clear();
            Sections.Clear();
            _recipeOutput.Clear();
            _recipesOf.Clear();
            _categoryRep.Clear();
            IsBuilt = false;

            try { BuildBundles(); }
            catch (Exception ex) { Warnings.Add("献祭数据解析失败：" + ex.Message); }

            try { BuildCollection(); }
            catch (Exception ex) { Warnings.Add("图鉴数据解析失败：" + ex.Message); }

            // 构建完立刻读一次存档进度（BuildIndex 在 SaveLoaded 时调用，此时存档已就绪）
            try { Refresh(); }
            catch (Exception ex) { Warnings.Add("存档进度读取失败：" + ex.Message); }

            sw.Stop();
            BuildMs = sw.ElapsedMilliseconds;
            IsBuilt = true;

            _log?.Log($"图鉴 / 献祭进度索引构建完成：收集包 {Bundles.Count} 个 / 房间 {Rooms.Count} 个 / 图鉴 {Sections.Count} 项，耗时 {BuildMs} ms",
                LogLevel.Info);
            foreach (string w in Warnings)
                _log?.Log("进度索引告警：" + w, LogLevel.Warn);
        }

        private void BuildBundles()
        {
            var data = Load<Dictionary<string, string>>("Data/Bundles");
            if (data == null)
            {
                Warnings.Add("读不到 Data/Bundles");
                return;
            }

            var rooms = new List<string>();
            foreach (var pair in data)
            {
                string key = pair.Key ?? "";
                int slash = key.IndexOf('/');
                if (slash <= 0)
                    continue;

                string room = key.Substring(0, slash);
                if (!int.TryParse(key.Substring(slash + 1), out int idx))
                    continue;

                string[] parts = (pair.Value ?? "").Split('/');
                if (parts.Length < 5)
                    continue;

                var view = new BundleView
                {
                    Key = idx,
                    Room = room,
                    RoomName = RoomName(room),
                    Name = BundleName(parts),
                    Reward = RewardName(parts)
                };
                ParseSlots(parts[2], view);
                view.Required = ToInt(parts[4], 0);
                if (view.Required <= 0)
                    view.Required = view.Slots.Count;

                Bundles.Add(view);
                if (!rooms.Contains(room))
                    rooms.Add(room);
            }

            Rooms.AddRange(rooms);
        }

        private void ParseSlots(string spec, BundleView view)
        {
            string[] t = (spec ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                string id = t[i];
                var slot = new Slot { Stack = Math.Max(1, ToInt(t[i + 1], 1)), Quality = ToInt(t[i + 2], 0) };

                if (int.TryParse(id, out int cat) && cat < 0)
                {
                    // 负数 = 类别要求（BundleIngredientDescription：int.TryParse 成功且 < 0 即 category）
                    slot.IsCategory = true;
                    slot.Category = cat;

                    if (cat == -1)
                    {
                        // 特例：-1 是金库（CommunityCenter 区域 4）的「付钱」槽位，stack 就是金额（JunimoNoteMenu 的 purchaseButton 直接扣这笔钱）
                        slot.Item = null;
                        slot.Label = slot.Stack + " 金";
                        slot.Stack = 1;
                    }
                    else
                    {
                        slot.Item = Representative(cat);
                        slot.Label = CategoryLabel(cat, slot.Item);
                    }
                }
                else
                {
                    slot.Item = FindItem(id);
                    slot.Label = slot.Item != null ? slot.Item.Name : id;
                    if (slot.Item == null)
                        Warnings.Add("献祭槽位解析不出物品：" + id);
                }

                view.Slots.Add(slot);
            }
        }

        private string RoomName(string room)
        {
            try
            {
                string n = CodexRefIndex.BundleRoomName(room);
                if (!string.IsNullOrWhiteSpace(n))
                    return n;
            }
            catch
            {
                // 名称解析失败不影响进度显示
            }
            return room;
        }

        private string BundleName(string[] parts)
        {
            string label = parts.Length > 6 ? ResolveText(parts[6]) : "";
            if (!string.IsNullOrWhiteSpace(label))
                return label;
            string name = ResolveText(parts[0]);
            return string.IsNullOrWhiteSpace(name) ? parts[0] : name;
        }

        private string RewardName(string[] parts)
        {
            string raw = parts.Length > 1 ? parts[1] : "";
            if (string.IsNullOrWhiteSpace(raw))
                return "（无）";
            raw = raw.Trim();
            try
            {
                if (CodexRefIndex.IsItemToken(raw))
                {
                    string n0 = _index?.NameOfRaw(RawOf(raw));
                    if (!string.IsNullOrWhiteSpace(n0))
                        return n0;
                }
            }
            catch
            {
                // 落到文本解析
            }
            try
            {
                // Data/Bundles 的奖励是旧式「标准物品描述串」：类型 id [数量]。
                // 例："O 465 20" = 物体 465 的 20 个；"BO 1" = 大型可制作物 1；数量省略时按 1 计。
                string[] tok = raw.Split(new char[] { (char)32 }, StringSplitOptions.RemoveEmptyEntries);
                if (tok.Length >= 2)
                {
                    string qualifier = LegacyQualifier(tok[0]);
                    int count = tok.Length >= 3 ? ToInt(tok[2], 1) : 1;
                    if (count < 1)
                        count = 1;
                    string name = null;
                    if (qualifier != null)
                    {
                        try
                        {
                            StardewValley.Item it = StardewValley.ItemRegistry.Create(qualifier + tok[1], 1);
                            name = it == null ? null : it.DisplayName;
                        }
                        catch
                        {
                            // 落到索引
                        }
                    }
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        // 类型字母未知（如旧数据的 "R <戒指id>"）或创建失败时，直接用物品索引按原始 id 查。
                        CodexItem hit = _index?.FindByRaw(tok[1]);
                        name = hit == null ? null : hit.Name;
                    }
                    if (string.IsNullOrWhiteSpace(name))
                        name = _index?.NameOfRaw(tok[1]);
                    if (!string.IsNullOrWhiteSpace(name))
                        return count > 1 ? name + " ×" + count : name;
                }
            }
            catch
            {
                // 落到文本解析
            }
            string text = ResolveText(raw);
            return string.IsNullOrWhiteSpace(text) ? raw : text;
        }

        private static string LegacyQualifier(string type)
        {
            switch ((type ?? "").Trim().ToUpperInvariant())
            {
                case "O":
                    return "(O)";
                case "BO":
                case "BC":
                    return "(BC)";
                case "W":
                    return "(W)";
                case "H":
                    return "(H)";
                case "B":
                    return "(B)";
                case "F":
                    return "(F)";
                case "S":
                    return "(S)";
                case "P":
                    return "(P)";
                case "R":
                    // 旧数据里的戒指（1.6 的戒指是普通物体）
                    return "(O)";
                default:
                    return null;
            }
        }

        private string ResolveText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            if (raw.IndexOf('[') < 0)
                return raw;
            try { return CodexRefIndex.ResolveText(raw) ?? raw; }
            catch { return raw; }
        }

        private CodexItem FindItem(string id)
        {
            if (_index == null || string.IsNullOrWhiteSpace(id))
                return null;
            if (id[0] == '(')
                return _index.Find(id) ?? _index.FindByRaw(RawOf(id));
            return _index.FindByRaw(id) ?? _index.Find("(O)" + id);
        }

        private CodexItem Representative(int category)
        {
            if (_categoryRep.TryGetValue(category, out CodexItem cached))
                return cached;
            CodexItem found = null;
            if (_index != null)
            {
                foreach (CodexItem e in _index.Items)
                {
                    if (e?.Item == null)
                        continue;
                    try
                    {
                        if (e.Item.Category == category)
                        {
                            found = e;
                            break;
                        }
                    }
                    catch
                    {
                        // 单条读类别失败就跳过
                    }
                }
            }
            _categoryRep[category] = found;
            return found;
        }

        private string CategoryLabel(int category, CodexItem rep)
        {
            string name = "";
            if (rep?.Item != null)
            {
                try { name = rep.Item.getCategoryName(); }
                catch { name = ""; }
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                string warn = "未能本地化的献祭类别：" + category;
                if (!Warnings.Contains(warn))
                    Warnings.Add(warn);
                return "任意物品（类别 " + category + "）";
            }
            return "任意" + name;
        }

        // ------------------------------------------------------------------ 构建（图鉴六项）

        private void BuildCollection()
        {
            var museum = new Section { Kind = 0, Title = "博物馆捐赠" };
            var arch = new Section { Kind = 1, Title = "古物" };
            var mineral = new Section { Kind = 2, Title = "矿物" };
            var fish = new Section { Kind = 3, Title = "鱼类" };
            var cooking = new Section { Kind = 4, Title = "烹饪配方" };
            var crafting = new Section { Kind = 5, Title = "制作配方" };

            if (_index != null)
            {
                foreach (CodexItem e in _index.Items)
                {
                    Item it = e?.Item;
                    if (it == null)
                        continue;

                    bool isArch = Tag(it, "item_type_arch") || Tag(it, "item_type_archaeology");
                    bool isMineral = Tag(it, "item_type_minerals");
                    if (isArch)
                        arch.Universe.Add(e);
                    if (isMineral)
                        mineral.Universe.Add(e);

                    try
                    {
                        if (it.Category == -4)   // Object.FishCategory
                            fish.Universe.Add(e);
                    }
                    catch
                    {
                        // 单条读类别失败就跳过
                    }

                    try
                    {
                        if (LibraryMuseum.IsItemSuitableForDonation(e.Id, false))
                            museum.Universe.Add(e);
                    }
                    catch
                    {
                        // 单条判定失败就跳过
                    }
                }
            }

            CollectRecipes("Data/CookingRecipes", cooking);
            CollectRecipes("Data/CraftingRecipes", crafting);

            Sections.Add(museum);
            Sections.Add(arch);
            Sections.Add(mineral);
            Sections.Add(fish);
            Sections.Add(cooking);
            Sections.Add(crafting);

            foreach (Section s in Sections)
            {
                s.Total = s.Kind >= 4 ? s.RecipeNames.Count : s.Universe.Count;
                if (s.Kind == 0 && s.Total == 0)
                    Warnings.Add("博物馆可捐物品集合为空（IsItemSuitableForDonation 全部为假）");
            }
        }

        private void CollectRecipes(string asset, Section section)
        {
            var table = Load<Dictionary<string, string>>(asset);
            if (table == null)
            {
                Warnings.Add("读不到 " + asset);
                return;
            }

            foreach (var pair in table)
            {
                string recipe = pair.Key ?? "";
                if (string.IsNullOrWhiteSpace(recipe))
                    continue;

                string[] parts = (pair.Value ?? "").Split('/');
                CodexItem outItem = null;
                if (parts.Length >= 3)
                {
                    string[] outs = parts[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (outs.Length > 0)
                        outItem = FindItem(outs[0]);
                }
                _recipeOutput[recipe] = outItem;
                if (outItem != null)
                {
                    if (!_recipesOf.TryGetValue(outItem, out List<string> made))
                    {
                        made = new List<string>();
                        _recipesOf[outItem] = made;
                    }
                    made.Add(recipe);
                }
                section.RecipeNames.Add(recipe);
            }
        }

        // ------------------------------------------------------------------ 读存档

        public void Refresh()
        {
            _donated.Clear();
            _fish.Clear();
            _minerals.Clear();
            _artifacts.Clear();
            _cooking.Clear();
            _crafting.Clear();
            SaveReadable = false;

            try
            {
                var world = Game1.netWorldState?.Value;
                if (world != null)
                {
                    foreach (string id in world.MuseumPieces.Values)
                        AddKey(_donated, id);

                    foreach (BundleView b in Bundles)
                    {
                        b.DoneSlots = 0;
                        b.RewardPending = false;
                        if (world.Bundles.TryGetValue(b.Key, out bool[] flags) && flags != null)
                        {
                            for (int i = 0; i < b.Slots.Count && i < flags.Length; i++)
                            {
                                b.Slots[i].Done = flags[i];
                                if (flags[i])
                                    b.DoneSlots++;
                            }
                        }
                        if (world.BundleRewards.TryGetValue(b.Key, out bool pending))
                            b.RewardPending = pending;
                    }
                }

                Farmer player = Game1.player;
                if (player != null)
                {
                    foreach (string k in player.fishCaught.Keys)
                        AddKey(_fish, k);
                    foreach (string k in player.archaeologyFound.Keys)
                        AddKey(_artifacts, k);
                    foreach (string k in player.mineralsFound.Keys)
                        AddKey(_minerals, k);
                    foreach (string k in player.cookingRecipes.Keys)
                        _cooking.Add(k);
                    foreach (string k in player.craftingRecipes.Keys)
                        _crafting.Add(k);
                    SaveReadable = true;
                }

                foreach (BundleView b in Bundles)
                {
                    foreach (Slot s in b.Slots)
                    {
                        if (s.IsCategory)
                            s.OwnedByCategory = HasOwnedCategory(s.Category);
                    }
                }

                Recount();
            }
            catch (Exception ex)
            {
                SaveReadable = false;
                Warnings.Add("读存档进度失败：" + ex.Message);
                _log?.Log("读存档进度失败：" + ex, LogLevel.Warn);
            }
        }

        private void Recount()
        {
            foreach (Section s in Sections)
            {
                s.Missing.Clear();
                s.MissingRecipeNames.Clear();

                if (s.Kind >= 4)
                {
                    HashSet<string> known = s.Kind == 4 ? _cooking : _crafting;
                    int found = 0;
                    foreach (string recipe in s.RecipeNames)
                    {
                        if (known.Contains(recipe))
                        {
                            found++;
                            continue;
                        }
                        s.MissingRecipeNames.Add(recipe);
                        if (_recipeOutput.TryGetValue(recipe, out CodexItem item))
                            s.Missing.Add(item);
                    }
                    s.Found = found;
                    s.Total = s.RecipeNames.Count;
                    continue;
                }

                int hit = 0;
                foreach (CodexItem e in s.Universe)
                {
                    if (HasIn(s.Kind, e))
                        hit++;
                    else
                        s.Missing.Add(e);
                }
                s.Found = hit;
                s.Total = s.Universe.Count;
            }
        }

        private bool HasIn(int kind, CodexItem e)
        {
            switch (kind)
            {
                case 0: return Has(_donated, e);
                case 1: return Has(_artifacts, e);
                case 2: return Has(_minerals, e);
                case 3: return Has(_fish, e);
                default: return IsRecipeKnown(e);
            }
        }

        private bool HasOwnedCategory(int category)
        {
            var inv = Game1.player?.Items;
            if (inv == null)
                return false;
            foreach (Item it in inv)
            {
                if (it == null)
                    continue;
                try
                {
                    if (it.Category == category)
                        return true;
                }
                catch
                {
                    // 忽略单条
                }
            }
            return false;
        }

        /// <summary>把存档里的 id 键写成「限定 / 未限定」两种形态，判定时不必再区分来源。</summary>
        private static void AddKey(HashSet<string> set, string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;
            set.Add(key);
            if (key.Length > 2 && key[0] == '(' && key[2] == ')')
            {
                string prefix = key.Substring(0, 3);
                string raw = key.Substring(3);
                set.Add(raw);
                if (string.Equals(prefix, "(O)", StringComparison.OrdinalIgnoreCase))
                    set.Add("(O)" + raw);
                return;
            }
            set.Add("(O)" + key);
        }

        // ------------------------------------------------------------------ 状态查询（角标与图鉴共用）

        public bool IsDonated(CodexItem e) => Has(_donated, e);
        public bool IsFishCaught(CodexItem e) => Has(_fish, e);
        public bool IsMineralFound(CodexItem e) => Has(_minerals, e);
        public bool IsArtifactFound(CodexItem e) => Has(_artifacts, e);

        /// <summary>该物品是否已经有配方（烹饪或制作）。</summary>
        public bool IsRecipeKnown(CodexItem e)
        {
            if (e == null || !_recipesOf.TryGetValue(e, out List<string> recipes))
                return false;
            foreach (string r in recipes)
            {
                if (_cooking.Contains(r) || _crafting.Contains(r))
                    return true;
            }
            return false;
        }

        private static bool Has(HashSet<string> set, CodexItem e)
        {
            if (e == null)
                return false;
            if (!string.IsNullOrEmpty(e.Id) && set.Contains(e.Id))
                return true;
            return !string.IsNullOrEmpty(e.RawId) && set.Contains(e.RawId);
        }

        // ------------------------------------------------------------------ 收藏与最近

        public bool IsFavorite(CodexItem e)
            => e != null && ModEntry.Config?.Favorites != null
               && ModEntry.Config.Favorites.Any(x => string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase));

        public void ToggleFavorite(CodexItem e)
        {
            if (e == null || ModEntry.Config?.Favorites == null)
                return;

            var list = ModEntry.Config.Favorites;
            if (IsFavorite(e))
            {
                list.RemoveAll(x => string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                list.Insert(0, e.Id);
                while (list.Count > 50)
                    list.RemoveAt(list.Count - 1);
            }
            ModEntry.SaveConfigDeferred();
        }

        public void NoteRecent(CodexItem e)
        {
            if (e == null || ModEntry.Config?.Recents == null)
                return;
            var list = ModEntry.Config.Recents;
            int old = list.FindIndex(x => string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase));
            if (old == 0)
                return;
            if (old > 0)
                list.RemoveAt(old);
            list.Insert(0, e.Id);
            while (list.Count > 50)
                list.RemoveAt(list.Count - 1);
            ModEntry.SaveConfigDeferred();
        }

        public void ClearRecents()
        {
            if (ModEntry.Config?.Recents == null || ModEntry.Config.Recents.Count == 0)
                return;
            ModEntry.Config.Recents.Clear();
            ModEntry.SaveConfigDeferred();
        }

        /// <summary>这条配方名是否已经学会（烹饪或制作）。</summary>
        public bool IsRecipeNameKnown(string recipe)
            => !string.IsNullOrEmpty(recipe) && (_cooking.Contains(recipe) || _crafting.Contains(recipe));

        /// <summary>这条配方产出的物品（产出不在收录类型里时为 null）。</summary>
        public CodexItem RecipeOutput(string recipe)
            => recipe != null && _recipeOutput.TryGetValue(recipe, out CodexItem it) ? it : null;

        public List<CodexItem> ItemsByIds(List<string> ids)        {
            var list = new List<CodexItem>();
            if (ids == null || _index == null)
                return list;
            foreach (string id in ids)
            {
                CodexItem e = FindItem(id);
                if (e != null && !list.Contains(e))
                    list.Add(e);
            }
            return list;
        }

        // ------------------------------------------------------------------ 辅助

        private T Load<T>(string asset)
        {
            try { return Game1.content.Load<T>(asset); }
            catch (Exception ex)
            {
                Warnings.Add("读取 " + asset + " 失败：" + ex.Message);
                return default(T);
            }
        }

        private static string RawOf(string id)
        {
            if (string.IsNullOrEmpty(id))
                return id;
            return id.Length > 2 && id[0] == '(' && id[2] == ')' ? id.Substring(3) : id;
        }

        private static int ToInt(string s, int fallback)
            => int.TryParse(s, out int v) ? v : fallback;

        private static bool Tag(Item item, string tag)
        {
            try { return item.HasContextTag(tag); }
            catch { return false; }
        }
    }
}