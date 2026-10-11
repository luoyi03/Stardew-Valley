using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using StardewValley.GameData.Characters;
using StardewValley.GameData.Crops;
using StardewValley.GameData.FishPonds;
using StardewValley.GameData.FruitTrees;
using StardewValley.GameData.Locations;
using StardewValley.GameData.Machines;
using StardewValley.GameData.Movies;
using StardewValley.GameData.Museum;
using StardewValley.GameData.Objects;
using StardewValley.GameData.Shops;
using StardewValley.GameData.SpecialOrders;
using StardewValley.GameData.Tools;
using StardewValley.GameData.WildTrees;
using StardewValley.TokenizableStrings;

namespace InGameCodex.Data
{
    /// <summary>获取来源的种类（用于「来源」筛选与详情分组，见需求文档 §3.3 / §17.2）。</summary>
    public enum SrcKind { Shop, Craft, Cook, Fish, Forage, Artifact, Crop, FruitTree, WildTree, Monster, Machine, Quest, Museum, FishPond, Tool }

    public static class SrcKindInfo
    {
        public static readonly SrcKind[] All = (SrcKind[])Enum.GetValues(typeof(SrcKind));

        public static string Name(SrcKind k)
        {
            switch (k)
            {
                case SrcKind.Shop: return "商店";
                case SrcKind.Craft: return "合成";
                case SrcKind.Cook: return "烹饪";
                case SrcKind.Fish: return "钓鱼";
                case SrcKind.Forage: return "采集";
                case SrcKind.Artifact: return "远古斑点";
                case SrcKind.Crop: return "作物";
                case SrcKind.FruitTree: return "果树";
                case SrcKind.WildTree: return "野生树";
                case SrcKind.Monster: return "怪物掉落";
                case SrcKind.Machine: return "机器";
                case SrcKind.Quest: return "任务";
                case SrcKind.Museum: return "博物馆";
                case SrcKind.FishPond: return "鱼塘";
                case SrcKind.Tool: return "工具升级";
            }
            return k.ToString();
        }
    }

    /// <summary>一条获取来源。</summary>
    public class SrcLine
    {
        public SrcKind Kind { get; }
        public string Text { get; }

        public SrcLine(SrcKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    /// <summary>一条用途。</summary>
    public class UseLine
    {
        public string Text { get; }

        public UseLine(string text)
        {
            Text = text;
        }
    }

    /// <summary>
    /// 用途 / 获取来源的反向索引：进入存档后遍历游戏数据表一次，按「未限定物品 id」建索引。
    /// 为了跨 SDV 1.6 小版本与 Mod 数据保持兼容，读取成员统一走反射（见 Prop/AsEnumerable/AsStrings），
    /// 单个来源失败只跳过该来源；某来源一条都没解析出来会在日志里给出警示。
    /// </summary>
    public class CodexRefIndex
    {
        private readonly Dictionary<string, List<SrcLine>> _obtain = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<UseLine>> _use = new(StringComparer.OrdinalIgnoreCase);

        private ItemIndex _index;
        private IMonitor _log;
        private Dictionary<int, List<CodexItem>> _byCategory = new();
        private int _srcAdded;
        private int _srcSkipped;

        public bool IsBuilt { get; private set; }
        public long BuildMs { get; private set; }
        public int ObtainLines { get; private set; }
        public int UseLines { get; private set; }
        public List<string> Warnings { get; } = new();
        public Dictionary<SrcKind, int> Counts { get; } = new();

        private static readonly string[] TasteNames = { "最爱", "喜欢", "不喜欢", "讨厌", "中立" };

        /// <summary>NPC 内部名 → 显示名（读 Data/Characters，见 S3 需求文档 §17）。</summary>
        private Dictionary<string, string> _npcNames;

        public void EnsureBuilt(ItemIndex index, IMonitor log)
        {
            if (!IsBuilt)
                Build(index, log);
        }

        public void Build(ItemIndex index, IMonitor log)
        {
            var sw = Stopwatch.StartNew();
            _index = index;
            _log = log;
            _obtain.Clear();
            _use.Clear();
            Warnings.Clear();
            Counts.Clear();
            ObtainLines = 0;
            UseLines = 0;

            EnsureNpcNames();

            _byCategory = new Dictionary<int, List<CodexItem>>();
            if (index?.Items != null)
            {
                foreach (CodexItem e in index.Items)
                {
                    if (!_byCategory.TryGetValue(e.Category, out var list))
                    {
                        list = new List<CodexItem>();
                        _byCategory[e.Category] = list;
                    }
                    list.Add(e);
                }
            }

            RunSource("商店", BuildShops);
            RunSource("合成 / 烹饪配方", BuildRecipes);
            RunSource("机器", BuildMachines);
            RunSource("钓鱼 / 采集 / 远古斑点", BuildLocations);
            RunSource("作物 / 果树 / 野生树", BuildFarming);
            RunSource("怪物掉落", BuildMonsters);
            RunSource("任务奖励", BuildQuests);
            RunSource("博物馆奖励", BuildMuseum);
            RunSource("鱼塘", BuildFishPonds);
            RunSource("工具升级", BuildTools);
            RunSource("献祭需求", BuildBundles);
            RunSource("物品基础数据（食用 / 晶球）", BuildObjects);
            RunSource("送礼喜好", BuildGiftTastes);
            RunSource("电影零食", BuildConcessions);

            sw.Stop();
            BuildMs = sw.ElapsedMilliseconds;
            IsBuilt = true;

            _log?.Log($"反向索引构建完成：来源 {ObtainLines} 条 / 用途 {UseLines} 条，"
                      + $"覆盖物品 有来源 {_obtain.Count} 个、有用途 {_use.Count} 个，耗时 {BuildMs} ms", LogLevel.Info);
            foreach (string w in Warnings)
                _log?.Log("反向索引告警：" + w, LogLevel.Warn);
        }

        /// <summary>跑一个来源；异常只跳过该来源，不影响其它来源（需求文档 §17.7）。</summary>
        private void RunSource(string name, Action body)
        {
            _srcAdded = 0;
            _srcSkipped = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                body();
                string skip = _srcSkipped > 0 ? $"，跳过 {_srcSkipped} 条" : "";
                _log?.Log($"[来源] {name} OK {_srcAdded} 条{skip}，耗时 {sw.ElapsedMilliseconds} ms", LogLevel.Info);
                if (_srcAdded == 0)
                    Warnings.Add(name + "：0 条（数据表结构可能已变化，或该表当前无内容）");
            }
            catch (Exception ex)
            {
                Warnings.Add(name + " 构建失败：" + ex.Message);
                _log?.Log($"[来源] {name} 失败：{ex.Message}", LogLevel.Warn);
            }
        }

        // ---------- 对外查询 ----------

        public IReadOnlyList<SrcLine> Obtain(CodexItem e)
            => e != null && _obtain.TryGetValue(e.RawId, out var list) ? list : (IReadOnlyList<SrcLine>)Array.Empty<SrcLine>();

        public IReadOnlyList<UseLine> Use(CodexItem e)
            => e != null && _use.TryGetValue(e.RawId, out var list) ? list : (IReadOnlyList<UseLine>)Array.Empty<UseLine>();

        public bool HasSource(CodexItem e, SrcKind kind)
        {
            if (e == null || !_obtain.TryGetValue(e.RawId, out var list))
                return false;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Kind == kind)
                    return true;
            return false;
        }

        public bool HasAnySource(CodexItem e)
            => e != null && _obtain.TryGetValue(e.RawId, out var list) && list.Count > 0;

        // ---------- 写入 ----------

        private void AddObtain(string rawId, SrcKind kind, string text)
        {
            if (string.IsNullOrWhiteSpace(rawId))
                return;
            if (!_obtain.TryGetValue(rawId, out var list))
            {
                list = new List<SrcLine>();
                _obtain[rawId] = list;
            }
            // 同一条来源只登记一次（例：鱼塘多档产出的文案相同，见需求文档 §17.16）
            foreach (SrcLine exists in list)
            {
                if (exists.Kind == kind && string.Equals(exists.Text, text, StringComparison.Ordinal))
                    return;
            }
            list.Add(new SrcLine(kind, text));
            ObtainLines++;
            _srcAdded++;
            Counts[kind] = Counts.TryGetValue(kind, out int c) ? c + 1 : 1;
        }

        private void AddUse(string rawId, string text)
        {
            if (string.IsNullOrWhiteSpace(rawId) || string.IsNullOrWhiteSpace(text))
                return;
            if (!_use.TryGetValue(rawId, out var list))
            {
                list = new List<UseLine>();
                _use[rawId] = list;
            }
            foreach (UseLine exists in list)
            {
                if (string.Equals(exists.Text, text, StringComparison.Ordinal))
                    return;
            }
            list.Add(new UseLine(text));
            UseLines++;
            _srcAdded++;
        }

        // ---------- 各来源 ----------

        private void BuildShops()
        {
            var shops = Load<Dictionary<string, ShopData>>("Data/Shops");
            foreach (var pair in shops)
            {
                string label = ShopLabel(pair.Key, pair.Value);
                foreach (object it in AsEnumerable(Prop(pair.Value, "Items")))
                {
                    string id = PropStr(it, "ItemId");
                    if (!IsItemToken(id))
                    {
                        // TOOL_UPGRADES [tool ID]：工具升级由第 15 项来源（Data/Tools）接管，不算解析失败（§17.11）
                        if (id != null && id.TrimStart().StartsWith("TOOL_UPGRADES", StringComparison.OrdinalIgnoreCase))
                            continue;
                        _srcSkipped++;
                        continue;
                    }
                    int price = Int(Prop(it, "Price"));
                    int stock = Int(Prop(it, "AvailableStock"));
                    string priceText = price > 0 ? $"（{price} 金）" : "";
                    string stockText = stock > 0 ? $"，库存 {stock}" : "";
                    AddObtain(Unqualify(id), SrcKind.Shop, $"商店：{label}{priceText}{stockText}");
                }
            }
        }

        private string ShopLabel(string shopId, ShopData shop)
        {
            string key = ShortShopKey(shopId);
            foreach (object o in AsEnumerable(Prop(shop, "Owners")))
            {
                string n = PropStr(o, "Name");
                if (IsPlaceholderOwner(n))
                    continue;
                string owner = ResolveText(NpcName(n));
                if (!string.IsNullOrWhiteSpace(owner))
                    return owner;
            }
            string guess = NpcFromShopKey(shopId);
            if (guess != null)
                return key.EndsWith("Ledger", StringComparison.OrdinalIgnoreCase) ? guess + "的账本" : guess + "的商店";
            // 无店主的商店（赌场 / 沙漠商人 / 节日摊位等）：游戏没有商店名字段，只能按 key 试本地化资产
            string name = LoadShopName(key) ?? FestivalShopName(key) ?? FallbackName(key);
            if (name == null)
            {
                // SVE 的商店 key 去掉 Mod 前缀仍是 StardewValleyExpandedCP_XXX，按分段再试人工对照表
                foreach (string seg in key.Split('_'))
                {
                    name = FallbackName(seg);
                    if (name != null)
                        break;
                }
            }
            if (name != null)
                return name;
            return "未收录商店（" + key + "）";
        }

        /// <summary>店主名占位符（ShopOwnerType 的枚举名，见需求文档 §17.10）。</summary>
        private static bool IsPlaceholderOwner(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return true;
            string s = name.Trim();
            return s.Equals("AnyOrNone", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Any", StringComparison.OrdinalIgnoreCase)
                || s.Equals("None", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>商店 key 去掉 Mod id 前缀（只保留最后一个 '.' 之后的部分）。</summary>
        private static string ShortShopKey(string shopId)
        {
            if (string.IsNullOrWhiteSpace(shopId))
                return "";
            int i = shopId.LastIndexOf('.');
            return i >= 0 && i < shopId.Length - 1 ? shopId.Substring(i + 1) : shopId;
        }

        /// <summary>无店主商店的显示名：按 key 依次试 Strings\1_6_Strings / Strings\Locations / Strings\StringsFromMaps；都不命中返回 null（不编造）。</summary>
        private static string LoadShopName(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return null;
            foreach (string asset in new[] { "Strings\\1_6_Strings:", "Strings\\Locations:", "Strings\\StringsFromMaps:" })
            {
                string s = LoadLocalizedString(asset + key);
                if (IsDisplayName(s, key))
                    return s;
            }
            return null;
        }

        /// <summary>节日摊位（key 形如 Festival_&lt;节日 id&gt;_&lt;店主&gt;，如 Festival_DanceOfTheMoonlightJellies_Pierre）：用节日名做显示名；取不到返回 null。</summary>
        private static string FestivalShopName(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("Festival_", StringComparison.OrdinalIgnoreCase))
                return null;
            foreach (string seg in key.Split('_'))
            {
                if (seg.Length < 4 || seg.Equals("Festival", StringComparison.OrdinalIgnoreCase))
                    continue;
                string s = LoadLocalizedString("Strings\\1_6_Strings:" + seg);
                if (IsDisplayName(s, seg))
                    return s + "（节日摊位）";
                if (FestivalNames.TryGetValue(seg, out string fn))
                    return fn + "（节日摊位）";
            }
            return null;
        }

        /// <summary>节日内部 id → 中文名（游戏数据里没有这些键，v1.7.3 探针实测，见需求文档 §17.16）。</summary>
        private static readonly Dictionary<string, string> FestivalNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["DanceOfTheMoonlightJellies"] = "月光水母之舞",
            ["EggFestival"] = "彩蛋节",
            ["FeastOfTheWinterStar"] = "冬日星盛宴",
            ["FestivalOfIce"] = "冰雪节",
            ["FlowerDance"] = "花舞节",
            ["Luau"] = "夏威夷宴会",
            ["NightMarket"] = "夜市",
            ["SpiritsEve"] = "万灵节",
            ["StardewValleyFair"] = "星露谷集市",
            ["DesertFestival"] = "沙漠节",
        };

        /// <summary>献祭房间的官方中文名（S3 探针实测键名）：Strings\Locations:CommunityCenter_AreaName_&lt;房间名去空格&gt;（Pantry → 茶水间）。</summary>
        internal static string BundleRoomName(string room)
        {
            if (string.IsNullOrWhiteSpace(room))
                return null;
            string s = LoadLocalizedString("Strings\\Locations:CommunityCenter_AreaName_" + room.Replace(" ", ""));
            return IsDisplayName(s, room) ? s : null;
        }

        /// <summary>游戏数据里没有任何中文名的少数条目的人工对照表（S3 探针实测，见需求文档 §17.15）。</summary>
        private static readonly Dictionary<string, string> FallbackNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // 地点：Data/Locations 无 DisplayName，Strings\Locations 与 StringsFromMaps 也没有对应条目（v1.7.3/1.7.4 探针实测）
            ["Default"] = "默认地点",
            ["FarmHouse"] = "农舍",
            ["FarmCave"] = "农场洞穴",
            ["HarveyRoom"] = "哈维的诊所",
            ["SebastianRoom"] = "塞巴斯蒂安的房间",
            ["BeachNightMarket"] = "夜市",
            ["Club"] = "赌场",
            ["SandyHouse"] = "桑迪的家",
            ["WitchSwamp"] = "女巫沼泽",
            ["WitchHut"] = "女巫小屋",
            ["WitchWarpCave"] = "女巫的传送洞穴",
            ["Summit"] = "山顶",
            ["Greenhouse"] = "温室",
            ["SkullCave"] = "骷髅洞穴",
            ["Cellar"] = "地窖",
            ["MermaidHouse"] = "美人鱼小屋",
            ["Submarine"] = "潜水艇",
            ["Sunroom"] = "阳光房",
            ["BoatTunnel"] = "船只隧道",
            ["BugLand"] = "变异虫巢穴",
            ["UndergroundMine"] = "地下矿井",
            ["Caldera"] = "火山口",
            ["LeoTreeHouse"] = "雷欧的树屋",
            ["QiNutRoom"] = "齐先生的金核桃房间",
            ["MasteryCave"] = "精通洞穴",
            ["DesertFestival"] = "沙漠节",
            ["fishingGame"] = "钓鱼小游戏",
            ["Temp"] = "临时地图",
            ["LewisBasement"] = "刘易斯的地下室",
            ["IslandSouth"] = "姜岛南部",
            ["IslandSouthEast"] = "姜岛东南部",
            ["IslandSouthEastCave"] = "姜岛东南洞穴",
            ["IslandEast"] = "姜岛东部",
            ["IslandWest"] = "姜岛西部",
            ["IslandNorth"] = "姜岛北部",
            ["IslandHut"] = "姜岛小屋",
            ["IslandWestCave1"] = "姜岛西部洞穴",
            ["IslandNorthCave1"] = "姜岛北部洞穴",
            ["IslandFieldOffice"] = "姜岛野外办公处",
            ["IslandFarmHouse"] = "姜岛农舍",
            ["IslandFarmCave"] = "姜岛农场洞穴",
            ["CaptainRoom"] = "船长室",
            ["IslandShrine"] = "姜岛神殿",
            // SVE 自定义地图（Custom_ 前缀；人工对照，非官方译名）
            ["Custom_ZuzuCityStreet"] = "祖祖市街道",
            ["Custom_Atlantis"] = "亚特兰蒂斯",
            ["Custom_Void"] = "虚空",
            ["Custom_VoidDen"] = "虚空巢穴",
            ["Custom_DesertRailway"] = "沙漠铁路",
            ["Custom_GrampletonFields_Small"] = "格兰普尔顿原野",
            ["Custom_GrampletonCoast"] = "格兰普尔顿海岸",
            ["Custom_GrampletonTrainStation"] = "格兰普尔顿火车站",
            ["Custom_GrenvilleFalls"] = "格伦维尔瀑布",
            ["Custom_IridiumQuarry"] = "铱矿采石场",
            ["Custom_SpriteSpringCave"] = "精灵泉洞穴",
            ["Custom_TreasureCave"] = "宝藏洞穴",
            ["Custom_CrimsonBadlandsMap"] = "绯红荒原",
            ["Custom_DinoIsland_South"] = "恐龙岛南部",
            ["Custom_DinoIsland_North"] = "恐龙岛北部",
            ["Custom_FirstSlashGuild"] = "第一刀锋公会",
            ["Custom_FirstSlashHallway"] = "第一刀锋走廊",
            ["Custom_FirstSlashGuestRoom"] = "第一刀锋客房",
            ["Custom_HighlandsCavernPrison"] = "高地洞穴监狱",
            ["Custom_HighlandsCavernPrisonEvent"] = "高地洞穴监狱（剧情）",
            ["Custom_HighlandsOutpost"] = "高地前哨",
            ["Custom_WizardBasement"] = "巫师的地下室",
            ["Custom_CastleVillageOutpost"] = "城堡村前哨",
            ["Custom_JojaEmporium"] = "Joja 商场",
            ["Custom_JojaPetStore"] = "Joja 宠物店",
            ["Custom_MorrisHouse"] = "莫里斯的家",
            ["Custom_ScarlettHouse"] = "斯卡莱特的家",
            ["Custom_ClaireHouse"] = "克莱尔的家",
            ["Custom_AndyHouse"] = "安迪的家",
            ["Custom_AndyCellar"] = "安迪的地窖",
            ["Custom_SusanHouse"] = "苏珊的家",
            ["Custom_JenkinsHouse"] = "詹金斯的家",
            ["Custom_OliviaCellar"] = "奥利维亚的地窖",
            ["Custom_SophiaHouse"] = "索菲亚的家",
            ["Custom_SophiaCellar"] = "索菲亚的地窖",
            ["Custom_GrandpasShed"] = "爷爷的棚屋",
            ["Custom_GrandpasShedRuins"] = "爷爷的棚屋废墟",
            ["Custom_MarnieShed"] = "玛妮的棚屋",
            ["Custom_WillyRoom"] = "威利的房间",
            ["Custom_AuroraVineyard"] = "奥罗拉葡萄园",
            ["Custom_AuroraVineyardRefurbished"] = "奥罗拉葡萄园",
            ["Custom_AuroraVineyardCellarRefurbished"] = "奥罗拉葡萄园酒窖",
            ["Custom_VictorSummit"] = "维克多的山顶",
            ["Custom_ForestWestOld"] = "西森林（旧）",
            ["Custom_WoodsCompat"] = "森林（兼容地图）",
            ["Custom_EnchantedGrove"] = "魔法树林",
            ["Custom_FakeEnchantedGrove"] = "魔法树林（伪装）",
            ["Custom_FakeEnchantedGrove2"] = "魔法树林（伪装 2）",
            ["Custom_ZCCC_Entrance"] = "祖祖市会展中心·正门",
            ["Custom_ZCCC_MainHall"] = "祖祖市会展中心·主厅",
            ["Custom_ZCCC_FoodCourt"] = "祖祖市会展中心·美食广场",
            ["Custom_ZCCC_Stage"] = "祖祖市会展中心·舞台",
            ["Custom_ZCCC_VendorFloor"] = "祖祖市会展中心·摊位层",
            ["Custom_ZCCC_Courtyard"] = "祖祖市会展中心·庭院",
            ["Custom_SakuraVineyard"] = "樱花葡萄园",
            ["Custom_SakuraVineyardCellar"] = "樱花葡萄园酒窖",
            ["Custom_SakuraVineyardCottage"] = "樱花葡萄园小屋",
            ["Custom_SakuraVineyardCottageInterior"] = "樱花葡萄园小屋内",
            ["Custom_SakuraVineyardDiningRoom"] = "樱花葡萄园餐厅",
            ["Custom_Countryside_ClaireHouse"] = "乡村·克莱尔的家",
            ["Custom_Countryside_BusStop_Bus"] = "乡村·公交站",
            ["Custom_Countryside_BusStop_NoBus"] = "乡村·公交站（无巴士）",
            ["Custom_BusStop_NoBus"] = "公交站（无巴士）",
            ["Custom_Highway"] = "高速公路",
            ["Custom_MovieTheaterScreen_ClaireEvent"] = "电影院银幕",
            ["Custom_Martin_WarpRoom"] = "马丁的传送房间",
            ["Custom_GaldoranVoid"] = "加尔多的虚空",
            ["Custom_TownEast"] = "东镇",
            ["Custom_ApplesRoom"] = "阿普尔斯的房间",
            ["Custom_GunthersRoom"] = "冈瑟的房间",
            ["Custom_GuntherExpedition1"] = "冈瑟的探险 1",
            ["Custom_GuntherExpedition2"] = "冈瑟的探险 2",
            ["Custom_LanceSecretWarp"] = "兰斯的密室",
            ["Custom_HanksGarage"] = "汉克的修车厂",
            ["Custom_HenchmanHouse"] = "亨奇曼的家",
            // 商店：Data/Shops 的 Owners 全为 AnyOrNone、ShopData 无名称字段
            ["Casino"] = "赌场",
            ["DesertTrade"] = "沙漠商人",
            ["BoxOffice"] = "电影院售票处",
            ["Concessions"] = "电影院小卖部",
            ["Catalogue"] = "家具目录",
            ["Furniture Catalogue"] = "家具目录",
            ["FurnitureCatalogue2"] = "家具目录 2",
            ["BooksellerTrade"] = "书摊",
            ["HatMouse"] = "帽子鼠",
            ["Hospital"] = "哈维的诊所",
            ["IceCreamStand"] = "冰淇淋摊",
            ["IslandTrade"] = "姜岛商人",
            ["JojaFurnitureCatalogue"] = "Joja 家具目录",
            ["JunimoFurnitureCatalogue"] = "祝尼魔家具目录",
            ["LostItems"] = "失物招领",
            ["QiGemShop"] = "齐先生宝石商店",
            ["Raccoon"] = "浣熊商店",
            ["RetroFurnitureCatalogue"] = "复古家具目录",
            ["TrashFurnitureCatalogue"] = "垃圾家具目录",
            ["Traveler"] = "旅行商人",
            ["VolcanoShop"] = "火山商店",
        };

        private static string FallbackName(string key)
            => !string.IsNullOrWhiteSpace(key) && FallbackNames.TryGetValue(key, out string v) ? v : null;

        /// <summary>Mod 新增 NPC 的显示名：SVE 的部分 NPC 在 Data/Characters 里 DisplayName 为空（v1.7.4 探针实测，见需求文档 §17.16），
        /// 这里人工补名；祝尼魔摊位的颜色也走这张表（BlueJunimo → 蓝色祝尼魔）。</summary>
        private static readonly Dictionary<string, string> ExtraShopOwners = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Chloe"] = "克洛伊",
            ["Axel"] = "阿克塞尔",
            ["Jace"] = "杰斯",
            ["Zoey"] = "佐伊",
            ["Brooklyn"] = "布鲁克林",
            ["Aurora"] = "奥罗拉",
            ["Apples"] = "阿普尔斯",
            ["Charlie"] = "查理",
            ["Adventurer"] = "冒险家",
            ["BlueJunimo"] = "蓝色祝尼魔",
            ["GreyJunimo"] = "灰色祝尼魔",
            ["RedJunimo"] = "红色祝尼魔",
            ["YellowJunimo"] = "黄色祝尼魔",
            ["OrangeJunimo"] = "橙色祝尼魔",
            ["PurpleJunimo"] = "紫色祝尼魔",
        };

        /// <summary>店主名候选：Data/Characters 的显示名 + 人工补充表（ExtraShopOwners）。</summary>
        private IEnumerable<KeyValuePair<string, string>> OwnerNamePairs()
        {
            if (_npcNames != null)
            {
                foreach (var pair in _npcNames)
                    yield return pair;
            }
            foreach (var pair in ExtraShopOwners)
                yield return pair;
        }

        /// <summary>尽力从商店 key 前缀猜店主（如 ChloeVendor4 → Chloe）；猜不到返回 null，不编造。</summary>
        private string NpcFromShopKey(string shopId)
        {
            if (string.IsNullOrWhiteSpace(shopId))
                return null;
            string key = ShortShopKey(shopId);
            // ① 整段前缀（如 ChloeVendor4 → Chloe）
            string best = null;
            int bestLen = 0;
            foreach (var pair in OwnerNamePairs())
            {
                if (pair.Key.Length >= 4 && key.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase) && pair.Key.Length > bestLen)
                {
                    best = pair.Value;
                    bestLen = pair.Key.Length;
                }
            }
            if (best != null)
                return best;
            // ② 按 '_' 分段再试前缀：SVE 的商店 key 形如 StardewValleyExpandedCP_ChloeVendor4（见需求文档 v1.7.4）
            foreach (string seg in key.Split('_'))
            {
                if (seg.Length < 4)
                    continue;
                foreach (var pair in OwnerNamePairs())
                {
                    if (pair.Key.Length >= 4 && seg.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                        return pair.Value;
                }
            }
            return null;
        }

        private void BuildRecipes()
        {
            BuildRecipeTable("Data/CraftingRecipes", SrcKind.Craft, "合成");
            BuildRecipeTable("Data/CookingRecipes", SrcKind.Cook, "烹饪");
        }

        private void BuildRecipeTable(string asset, SrcKind kind, string verb)
        {
            var table = Load<Dictionary<string, string>>(asset);
            foreach (var pair in table)
            {
                string[] parts = (pair.Value ?? "").Split('/');
                if (parts.Length < 3)
                {
                    _srcSkipped++;
                    continue;
                }

                string[] outs = Split(parts[2]);
                if (outs.Length == 0 || !IsItemToken(outs[0]))
                {
                    _srcSkipped++;
                    continue;
                }

                string outRaw = Unqualify(outs[0]);
                int outCount = outs.Length > 1 && int.TryParse(outs[1], out int oc) ? oc : 1;
                string outName = NameOfRaw(outRaw);
                string materials = DescribeIngredients(parts[0], 3);

                AddObtain(outRaw, kind, $"{verb}：{materials}");
                AddUse(outRaw, $"{verb}产出：{outName}" + (outCount > 1 ? $" ×{outCount}" : "") + $"（材料 {materials}）");

                foreach (Ingredient ing in ParseIngredients(parts[0]))
                    foreach (string raw in ResolveIngredient(ing.Id))
                        AddUse(raw, $"{verb}材料：{outName}（需要 {ing.Count} 个）");
            }
        }

        private void BuildMachines()
        {
            var machines = Load<Dictionary<string, MachineData>>("Data/Machines");
            foreach (var pair in machines)
            {
                string machineName = NameOfRaw(Unqualify(pair.Key));
                foreach (object rule in AsEnumerable(Prop(pair.Value, "OutputRules")))
                {
                    var outs = new List<string>();
                    foreach (object outItem in AsEnumerable(Prop(rule, "OutputItem")))
                    {
                        string oid = PropStr(outItem, "ItemId");
                        if (!IsItemToken(oid))
                            continue;
                        string on = NameOfRaw(Unqualify(oid));
                        int min = Int(Prop(outItem, "MinStack"));
                        int max = Int(Prop(outItem, "MaxStack"));
                        if (min <= 0) min = 1;
                        if (max < min) max = min;
                        string qty = min == max ? "×" + min : $"×{min}~{max}";
                        outs.Add(on + qty);
                        AddObtain(Unqualify(oid), SrcKind.Machine, $"机器：{machineName}（产出 {on}{qty}）");
                    }

                    string outText = outs.Count > 0 ? string.Join("、", outs.Take(3)) : "按规则产出";
                    foreach (object trg in AsEnumerable(Prop(rule, "Triggers")))
                    {
                        string req = PropStr(trg, "RequiredItemId");
                        if (!IsItemToken(req))
                            continue;
                        AddUse(Unqualify(req), $"机器加工：放入 {machineName} → {outText}");
                    }
                }
            }
        }

        private void BuildLocations()
        {
            var locs = Load<Dictionary<string, LocationData>>("Data/Locations");
            foreach (var pair in locs)
            {
                LocationData loc = pair.Value;
                string locName = LocationName(pair.Key, loc);

                foreach (object f in AsEnumerable(Prop(loc, "Fish")))
                {
                    string id = PropStr(f, "ItemId");
                    if (!IsItemToken(id))
                    {
                        _srcSkipped++;
                        continue;
                    }
                    string area = FishAreaName(loc, PropStr(f, "FishAreaId"));
                    int lvl = Int(Prop(f, "MinFishingLevel"));
                    string lvlText = lvl > 0 ? $"，需钓鱼等级 {lvl}" : "";
                    AddObtain(Unqualify(id), SrcKind.Fish,
                        $"钓鱼：{locName}{area}{SeasonText(PropStr(f, "Season"))}{lvlText}{PctText(Num(Prop(f, "Chance")))}");
                }

                foreach (object g in AsEnumerable(Prop(loc, "Forage")))
                {
                    string id = PropStr(g, "ItemId");
                    if (!IsItemToken(id))
                    {
                        _srcSkipped++;
                        continue;
                    }
                    AddObtain(Unqualify(id), SrcKind.Forage,
                        $"采集：{locName}{SeasonText(PropStr(g, "Season"))}{PctText(Num(Prop(g, "Chance")))}");
                }

                foreach (object a in AsEnumerable(Prop(loc, "ArtifactSpots")))
                {
                    string id = PropStr(a, "ItemId");
                    if (!IsItemToken(id))
                    {
                        _srcSkipped++;
                        continue;
                    }
                    AddObtain(Unqualify(id), SrcKind.Artifact, $"远古斑点：{locName}{PctText(Num(Prop(a, "Chance")))}");
                }
            }
        }

        private void BuildFarming()
        {
            var crops = Load<Dictionary<string, CropData>>("Data/Crops");
            foreach (var pair in crops)
            {
                CropData c = pair.Value;
                string seedRaw = Unqualify(pair.Key);
                string seedName = NameOfRaw(seedRaw);
                string hid = PropStr(c, "HarvestItemId");
                if (!IsItemToken(hid))
                {
                    _srcSkipped++;
                    continue;
                }

                int days = 0;
                foreach (object d in AsEnumerable(Prop(c, "DaysInPhase")))
                    days += Int(d);
                int regrow = Int(Prop(c, "RegrowDays"));
                string regrowText = regrow > 0 ? $"，{regrow} 天后再收" : "";
                string seasons = SeasonsText(Prop(c, "Seasons"));
                string harvest = NameOfRaw(Unqualify(hid));

                AddObtain(Unqualify(hid), SrcKind.Crop, $"种植：{seedName}（{days} 天成熟{regrowText}{seasons}）");
                AddUse(seedRaw, $"种植：可收获 {harvest}（{days} 天成熟{regrowText}）");
            }

            var fruitTrees = Load<Dictionary<string, FruitTreeData>>("Data/FruitTrees");
            foreach (var pair in fruitTrees)
            {
                FruitTreeData t = pair.Value;
                string seedRaw = Unqualify(pair.Key);
                string treeName = PropStr(t, "DisplayName");
                if (string.IsNullOrWhiteSpace(treeName))
                    treeName = NameOfRaw(seedRaw);
                foreach (object f in AsEnumerable(Prop(t, "Fruit")))
                {
                    string id = PropStr(f, "ItemId");
                    if (!IsItemToken(id))
                    {
                        _srcSkipped++;
                        continue;
                    }
                    AddObtain(Unqualify(id), SrcKind.FruitTree,
                        $"果树：{treeName}{SeasonText(PropStr(f, "Season"))}{PctText(Num(Prop(f, "Chance")))}");
                }
            }

            var wildTrees = Load<Dictionary<string, WildTreeData>>("Data/WildTrees");
            foreach (var pair in wildTrees)
            {
                WildTreeData t = pair.Value;
                string seed = PropStr(t, "SeedItemId");
                string treeName = NameOfRaw(Unqualify(seed));
                AddDropList(SrcKind.WildTree, Prop(t, "SeedDropItems"), $"野生树：{treeName} 落种");
                AddDropList(SrcKind.WildTree, Prop(t, "ChopItems"), $"野生树：砍伐 {treeName}");
                AddDropList(SrcKind.WildTree, Prop(t, "TapItems"), $"野生树：{treeName} 挂采集器");
                AddDropList(SrcKind.WildTree, Prop(t, "ShakeItems"), $"野生树：摇晃 {treeName}");
                if (IsItemToken(seed))
                    AddObtain(Unqualify(seed), SrcKind.WildTree, $"野生树：{treeName} 的种子");
            }
        }

        private void AddDropList(SrcKind kind, object list, string label)
        {
            foreach (object d in AsEnumerable(list))
            {
                string id = PropStr(d, "ItemId");
                if (!IsItemToken(id))
                {
                    _srcSkipped++;
                    continue;
                }
                AddObtain(Unqualify(id), kind, $"{label}{PctText(Num(Prop(d, "Chance")))}");
            }
        }

        private void BuildMonsters()
        {
            var monsters = Load<Dictionary<string, string>>("Data/Monsters");
            foreach (var pair in monsters)
            {
                string[] parts = (pair.Value ?? "").Split('/');
                if (parts.Length < 7)
                {
                    _srcSkipped++;
                    continue;
                }

                string name = parts.Length > 14 && !string.IsNullOrWhiteSpace(parts[14]) ? parts[14] : pair.Key;
                string[] drop = Split(parts[6]);
                for (int i = 0; i + 1 < drop.Length; i += 2)
                {
                    if (!IsItemToken(drop[i]))
                    {
                        _srcSkipped++;
                        continue;
                    }
                    double chance = 0;
                    try { chance = double.Parse(drop[i + 1], System.Globalization.CultureInfo.InvariantCulture); }
                    catch { }
                    AddObtain(Unqualify(drop[i]), SrcKind.Monster, $"怪物掉落：{name}{PctText(chance)}");
                }
            }
        }

        private void BuildQuests()
        {
            var slayer = Load<Dictionary<string, MonsterSlayerQuestData>>("Data/MonsterSlayerQuests");
            foreach (var pair in slayer)
            {
                string rid = PropStr(pair.Value, "RewardItemId");
                if (!IsItemToken(rid))
                    continue;
                string qname = ResolveText(PropStr(pair.Value, "DisplayName"));
                if (!IsDisplayName(qname, pair.Key))
                {
                    qname = pair.Key;
                }
                AddObtain(Unqualify(rid), SrcKind.Quest, $"任务：怪物狩猎任务「{qname}」奖励");
            }

            var orders = Load<Dictionary<string, SpecialOrderData>>("Data/SpecialOrders");
            foreach (var pair in orders)
            {
                string orderName = ResolveText(PropStr(pair.Value, "Name"));
                if (!IsDisplayName(orderName, pair.Key))
                {
                    orderName = pair.Key;
                }
                foreach (object rw in AsEnumerable(Prop(pair.Value, "Rewards")))
                {
                    string type = Convert.ToString(Prop(rw, "Type"));
                    if (!string.Equals(type, "Object", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string[] tok = Split(PropStr(rw, "Data"));
                    if (tok.Length == 0 || !IsItemToken(tok[0]))
                    {
                        _srcSkipped++;
                        continue;
                    }
                    int n = tok.Length > 1 && int.TryParse(tok[1], out int q) ? q : 1;
                    string raw = Unqualify(tok[0]);
                    AddObtain(raw, SrcKind.Quest,
                        $"任务：特别订单「{orderName}」奖励 {NameOfRaw(raw)}" + (n > 1 ? $"×{n}" : ""));
                }
            }
        }

        /// <summary>博物馆捐赠要求的类别标签 → 中文（Data/MuseumRewards 的 TargetContextTags，v1.7.4）。
        /// 未收录的标签按「指定类别物品」显示并通过诊断日志报出，便于后续补表。</summary>
        private string TagName(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return tag;
            // 形如 id_o_96：要求捐赠某个具体物品，取它的显示名
            if (tag.StartsWith("id_", StringComparison.OrdinalIgnoreCase))
            {
                string rest = tag.Substring(3);
                int us = rest.IndexOf('_');
                string raw = us >= 0 ? rest.Substring(us + 1) : rest;
                string nm = NameOfRaw(Unqualify(raw));
                if (!string.IsNullOrWhiteSpace(nm) && !string.Equals(nm, raw, StringComparison.OrdinalIgnoreCase))
                    return nm;
            }
            switch (tag.Trim().ToLowerInvariant())
            {
                case "item_type_arch": return "古物";
                case "item_type_minerals": return "矿物";
                case "item_type_artifacts": return "古物";
                case "item_type_archaeology": return "古物";
                case "item_type_fish": return "鱼";
                case "item_type_seeds": return "种子";
                case "item_type_trinkets": return "饰品";
                default:
                    return "指定类别物品";
            }
        }

        private void BuildMuseum()
        {
            var rewards = Load<Dictionary<string, MuseumRewards>>("Data/MuseumRewards");
            foreach (var pair in rewards)
            {
                // 1.6 的 TargetContextTags 是「模型列表」（每项 Tag + Count），不是字符串列表（wiki：Modding:Museum）
                var reqs = new List<TagCount>();
                foreach (object t in AsEnumerable(Prop(pair.Value, "TargetContextTags")))
                {
                    string tag = PropStr(t, "Tag");
                    int need = Int(Prop(t, "Count"));
                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        if (need < 0)
                            reqs.Add(new TagCount { Tag = "", Count = need, MuseumComplete = true });
                        continue;
                    }
                    reqs.Add(new TagCount { Tag = tag, Count = need });
                }
                if (reqs.Count == 0)
                {
                    _srcSkipped++;
                    continue;
                }

                string rid = PropStr(pair.Value, "RewardItemId");
                int cnt = Int(Prop(pair.Value, "RewardItemCount"));
                string rewardName = IsItemToken(rid) ? NameOfRaw(Unqualify(rid)) : "";
                string rewardText = string.IsNullOrWhiteSpace(rewardName)
                    ? "（无物品奖励）"
                    : rewardName + (cnt > 1 ? "×" + cnt : "");

                var reqTexts = new List<string>();
                bool completeOnly = false;
                foreach (TagCount req in reqs)
                {
                    if (req.MuseumComplete)
                    {
                        completeOnly = true;
                        reqTexts.Add("博物馆全部捐赠完成");
                    }
                    else
                    {
                        reqTexts.Add(req.Count > 1 ? $"任意 {req.Count} 个{TagName(req.Tag)}" : TagName(req.Tag));
                    }
                }
                string reqText = string.Join("、", reqTexts);

                if (!completeOnly)
                {
                    foreach (CodexItem e in _index.Items)
                    {
                        if (e?.Item == null)
                            continue;
                        bool all = true;
                        foreach (TagCount req in reqs)
                        {
                            if (!HasTag(e.Item, req.Tag))
                            {
                                all = false;
                                break;
                            }
                        }
                        if (all)
                            AddUse(e.RawId, $"博物馆捐赠：{reqText} 后可得奖励 {rewardText}");
                    }
                }

                if (IsItemToken(rid))
                    AddObtain(Unqualify(rid), SrcKind.Museum, $"博物馆奖励：{reqText}");
            }
        }

        private void BuildFishPonds()
        {
            // 1.6 的 Data/FishPondData 是「元素列表」（每项 Id / RequiredTags / ProducedItems），不是字典（wiki：Modding:Fish ponds）
            var ponds = Load<List<FishPondData>>("Data/FishPondData");
            var candidates = new List<CodexItem>();
            if (_byCategory.TryGetValue(-4, out var fish))
                candidates.AddRange(fish);

            foreach (FishPondData pond in ponds)
            {
                var tags = AsStrings(Prop(pond, "RequiredTags"));
                if (tags.Count == 0)
                {
                    _srcSkipped++;
                    continue;
                }

                var producedIds = new List<string>();
                var producedNames = new List<string>();
                foreach (object p in AsEnumerable(Prop(pond, "ProducedItems")))
                {
                    string pid = PropStr(p, "ItemId");
                    if (!IsItemToken(pid))
                        continue;
                    producedIds.Add(Unqualify(pid));
                    producedNames.Add(NameOfRaw(Unqualify(pid)));
                }

                // 命中鱼：所有正向 tag 都命中，且带 ! 前缀的 tag 都不命中（wiki：Modding:Fish ponds）
                var matched = new List<CodexItem>();
                foreach (CodexItem e in candidates)
                {
                    if (e.Item == null)
                        continue;
                    bool ok = true;
                    foreach (string tag in tags)
                    {
                        if (tag.StartsWith("!"))
                        {
                            if (HasTag(e.Item, tag.Substring(1)))
                            {
                                ok = false;
                                break;
                            }
                        }
                        else if (!HasTag(e.Item, tag))
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                        matched.Add(e);
                }

                string who = matched.Count > 0
                    ? string.Join("、", matched.Take(3).Select(x => x.Name)) + (matched.Count > 3 ? $" 等 {matched.Count} 种鱼" : "")
                    : string.Join("、", tags);
                string prodText = producedNames.Count > 0
                    ? "（产出 " + string.Join("、", producedNames.Take(3)) + "）"
                    : "";

                for (int i = 0; i < producedIds.Count; i++)
                    AddObtain(producedIds[i], SrcKind.FishPond, $"鱼塘：{who} 产出");

                foreach (CodexItem e in matched)
                    AddUse(e.RawId, $"鱼塘：可放入鱼塘养殖{prodText}");
            }
        }

        /// <summary>工具升级（第 15 项来源，见需求文档 §17.11）：读 Data/Tools 的 UpgradeFrom / ConventionalUpgradeFrom。</summary>
        private void BuildTools()
        {
            var tools = Load<Dictionary<string, ToolData>>("Data/Tools");
            var materials = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in tools)
            {
                object td = pair.Value;
                string rawId = Unqualify(pair.Key);
                string toolName = NameOfRaw(rawId);
                if (string.IsNullOrWhiteSpace(toolName))
                    toolName = rawId;

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool any = false;
                int salePrice = Int(Prop(td, "SalePrice"));

                foreach (object u in AsEnumerable(Prop(td, "UpgradeFrom")))
                {
                    int price = Int(Prop(u, "Price"));
                    if (price <= 0)
                        price = salePrice;
                    any |= AddToolUpgrade(rawId, toolName, PropStr(u, "RequireToolId"), price,
                                          PropStr(u, "TradeItemId"), TradeAmount(Prop(u, "TradeItemAmount")), seen, materials);
                }

                string conv = PropStr(td, "ConventionalUpgradeFrom");
                int level = Int(Prop(td, "UpgradeLevel"));
                if (!string.IsNullOrWhiteSpace(conv) && level >= 1 && level <= 4)
                {
                    any |= AddToolUpgrade(rawId, toolName, conv, ConventionalUpgradePrice(level),
                                          ConventionalUpgradeTradeItem(level), 5, seen, materials);
                }

                // 基础工具（原版 5 件）：开局自带，不在任何商店
                if (!any && IsStarterTool(rawId))
                    AddObtain(rawId, SrcKind.Tool, "工具升级：初始装备（游戏开局自动获得）");
            }

            // 材料侧用途：每种材料聚合一行，避免详情刷屏
            foreach (var pair in materials)
            {
                List<string> names = pair.Value;
                string shown = string.Join("、", names.Take(3).ToArray());
                string more = names.Count > 3 ? " 等 " + names.Count + " 件工具" : (names.Count > 1 ? " 共 " + names.Count + " 件工具" : "");
                AddUse(pair.Key, "工具升级：可用于升级 " + shown + more);
            }
        }

        /// <summary>写入一条工具升级来源，并给上一阶工具 / 材料补用途；返回是否写入（false = 无内容或重复）。</summary>
        private bool AddToolUpgrade(string rawId, string toolName, string fromTool, int price, string trade, int amount,
                                    HashSet<string> seen, Dictionary<string, List<string>> materials)
        {
            string from = Unqualify(fromTool);
            string tradeId = Unqualify(trade);
            if (!seen.Add(from + "|" + tradeId + "|" + amount))
                return false;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(from))
            {
                parts.Add("交出 " + NameOfRaw(from));
                AddUse(from, "工具升级：可在铁匠铺升级为 " + toolName);
            }
            if (!string.IsNullOrWhiteSpace(tradeId) && amount > 0)
            {
                parts.Add(amount + "×" + NameOfRaw(tradeId));
                if (!materials.TryGetValue(tradeId, out List<string> list))
                {
                    list = new List<string>();
                    materials[tradeId] = list;
                }
                if (!list.Contains(toolName))
                    list.Add(toolName);
            }
            if (price > 0)
                parts.Add(price + " 金");
            if (parts.Count == 0)
                return false;

            AddObtain(rawId, SrcKind.Tool, "工具升级：铁匠铺（" + NpcName("Clint") + "）· " + string.Join(" + ", parts.ToArray()));
            return true;
        }

        private static int TradeAmount(object v)
        {
            int n = Int(v);
            return n > 0 ? n : 1;
        }

        private static readonly HashSet<string> StarterTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Axe", "Pickaxe", "Hoe", "WateringCan", "TrashCan" };

        /// <summary>原版基础工具（开局自带、没有任何升级来源）。</summary>
        private static bool IsStarterTool(string rawId)
        {
            return !string.IsNullOrWhiteSpace(rawId) && StarterTools.Contains(rawId);
        }

        /// <summary>约定升级的金币价格（优先游戏自身规则，失败退化到默认表）。</summary>
        private static int ConventionalUpgradePrice(int level)
        {
            try
            {
                int p = StardewValley.Internal.ShopBuilder.GetToolUpgradeConventionalPrice(level);
                if (p > 0)
                    return p;
            }
            catch (Exception)
            {
            }
            switch (level)
            {
                case 1: return 2000;
                case 2: return 5000;
                case 3: return 10000;
                case 4: return 25000;
            }
            return 0;
        }

        /// <summary>约定升级所需的金属锭（未限定 id）。游戏内等价方法 GetToolUpgradeConventionalTradeItem 为 internal，无法调用，故用 §17.11 文档表。</summary>
        private static string ConventionalUpgradeTradeItem(int level)
        {
            switch (level)
            {
                case 1: return "334";
                case 2: return "335";
                case 3: return "336";
                case 4: return "337";
            }
            return "";
        }

        private void BuildBundles()
        {
            var bundles = Load<Dictionary<string, string>>("Data/Bundles");
            foreach (var pair in bundles)
            {
                string[] parts = (pair.Value ?? "").Split('/');
                if (parts.Length < 3)
                {
                    _srcSkipped++;
                    continue;
                }

                string bundleName = parts[0];
                string room = pair.Key;
                int slash = room.IndexOf('/');
                if (slash > 0)
                    room = room.Substring(0, slash);
                string roomName = BundleRoomName(room) ?? FallbackName(room);
                if (roomName == null)
                {
                    roomName = room;
                }

                string[] req = Split(parts[2]);
                for (int i = 0; i + 2 < req.Length; i += 3)
                {
                    string id = req[i].Trim('[', ']');
                    if (!IsItemToken(id))
                    {
                        _srcSkipped++;
                        continue;
                    }
                    int count = 1;
                    int.TryParse(req[i + 1].Trim('[', ']'), out count);
                    if (count < 1)
                        count = 1;
                    AddUse(Unqualify(id), $"献祭：{roomName} · {bundleName}（需要 {count} 个）");
                }
            }
        }

        private void BuildObjects()
        {
            var objects = Load<Dictionary<string, ObjectData>>("Data/Objects");
            foreach (var pair in objects)
            {
                ObjectData od = pair.Value;
                string raw = Unqualify(pair.Key);

                int ed = Int(Prop(od, "Edibility"));
                if (ed > 0)
                {
                    bool drink = Prop(od, "IsDrink") is bool d && d;
                    AddUse(raw, $"{(drink ? "饮用" : "食用")}：基础 +{(int)(ed * 2.5)} 能量 / +{(int)(ed * 1.125)} 生命");
                }

                foreach (object g in AsEnumerable(Prop(od, "GeodeDrops")))
                {
                    string gid = PropStr(g, "ItemId");
                    if (!IsItemToken(gid))
                        continue;
                    AddObtain(Unqualify(gid), SrcKind.Artifact,
                        $"晶球：{NameOfRaw(raw)} 开出{PctText(Num(Prop(g, "Chance")))}");
                }

                if (Prop(od, "ArtifactSpotChances") is System.Collections.IDictionary dict)
                {
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        double p = Num(de.Value);
                        if (p <= 0)
                            continue;
                        AddObtain(raw, SrcKind.Artifact, $"远古斑点：{de.Key}（{p * 100:0.#}%）");
                    }
                }
            }
        }

        private void BuildGiftTastes()
        {
            var tastes = Load<Dictionary<string, string>>("Data/NPCGiftTastes");
            var acc = new Dictionary<string, Dictionary<int, List<string>>>(StringComparer.OrdinalIgnoreCase);

            void Mark(string raw, int slot, string who)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return;
                if (!acc.TryGetValue(raw, out var slots))
                {
                    slots = new Dictionary<int, List<string>>();
                    acc[raw] = slots;
                }
                if (!slots.TryGetValue(slot, out var names))
                {
                    names = new List<string>();
                    slots[slot] = names;
                }
                if (!names.Contains(who))
                    names.Add(who);
            }

            void MarkId(string id, int slot, string who)
            {
                if (string.IsNullOrWhiteSpace(id))
                    return;
                string token = id.Trim();
                if (int.TryParse(token, out int cat) && cat < 0)
                {
                    if (_byCategory.TryGetValue(cat, out var items))
                        foreach (CodexItem e in items)
                            Mark(e.RawId, slot, who);
                    return;
                }
                if (!IsItemToken(token))
                    return;
                Mark(Unqualify(token), slot, who);
            }

            foreach (var pair in tastes)
            {
                string key = pair.Key ?? "";
                string[] seg = (pair.Value ?? "").Split('/');

                if (key.StartsWith("Universal_", StringComparison.OrdinalIgnoreCase))
                {
                    int slot = UniversalSlot(key);
                    if (slot < 0 || seg.Length == 0)
                        continue;
                    string who = "所有人（普遍" + TasteNames[slot] + "）";
                    foreach (string tok in Split(seg[0]))
                        MarkId(tok, slot, who);
                    continue;
                }

                string npc = NpcName(key);
                for (int slot = 0; slot < 5 && slot < seg.Length; slot++)
                    foreach (string tok in Split(seg[slot]))
                        MarkId(tok, slot, npc);
            }

            foreach (var pair in acc)
            {
                var parts = new List<string>();
                for (int slot = 0; slot < 5; slot++)
                {
                    if (!pair.Value.TryGetValue(slot, out var names) || names.Count == 0)
                        continue;
                    string list = names.Count <= 6
                        ? string.Join("、", names)
                        : string.Join("、", names.Take(6)) + $" 等 {names.Count} 人";
                    parts.Add(TasteNames[slot] + " " + list);
                }
                if (parts.Count > 0)
                    AddUse(pair.Key, "送礼：" + string.Join("；", parts));
            }
        }

        private static int UniversalSlot(string key)
        {
            string k = key.Substring("Universal_".Length).ToLowerInvariant();
            switch (k)
            {
                case "love": return 0;
                case "like": return 1;
                case "dislike": return 2;
                case "hate": return 3;
                case "neutral": return 4;
            }
            return -1;
        }

        private void BuildConcessions()
        {
            // 1.6 的 Data/Concessions 与 Data/ConcessionTastes 都是「元素列表」（wiki：Modding:Movie theater data）；
            // 零食的 Id 不是物品 id，故用内部名 Name 依次对「英文名 / 显示名 / 未限定 id / 归一化名」匹配。
            var conc = Load<List<ConcessionItemData>>("Data/Concessions");
            var tastes = Load<List<ConcessionTaste>>("Data/ConcessionTastes");

            var byName = new Dictionary<string, CodexItem>(StringComparer.OrdinalIgnoreCase);
            var byNorm = new Dictionary<string, CodexItem>(StringComparer.OrdinalIgnoreCase);
            if (_index?.Items != null)
            {
                foreach (CodexItem e in _index.Items)
                {
                    PutRef(byName, e.EnglishName, e);
                    PutRef(byName, e.Name, e);
                    PutRef(byName, e.RawId, e);
                    PutRef(byNorm, NormKey(e.EnglishName), e);
                    PutRef(byNorm, NormKey(e.Name), e);
                    PutRef(byNorm, NormKey(e.RawId), e);
                }
            }

            var missing = new List<string>();
            foreach (ConcessionItemData c in conc)
            {
                string name = PropStr(c, "Name");
                CodexItem entry = null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (!byName.TryGetValue(name, out entry) || entry == null)
                        byNorm.TryGetValue(NormKey(name), out entry);
                    if (entry == null)
                        entry = _index?.FindByRaw(name) ?? _index?.FindByRaw("(O)" + name);
                }
                if (entry?.Item == null)
                {
                    _srcSkipped++;
                    if (!string.IsNullOrWhiteSpace(name) && missing.Count < 8)
                        missing.Add(name);
                    continue;
                }

                var parts = new List<string>();
                int price = Int(Prop(c, "Price"));
                if (price > 0)
                    parts.Add($"售价 {price} 金");

                var loved = new List<string>();
                var liked = new List<string>();
                var disliked = new List<string>();
                foreach (ConcessionTaste t in tastes)
                {
                    string who = NpcName(PropStr(t, "Name"));
                    if (MatchConcessionTaste(entry.Item, name, AsStrings(Prop(t, "LovedTags"))))
                        loved.Add(who);
                    else if (MatchConcessionTaste(entry.Item, name, AsStrings(Prop(t, "LikedTags"))))
                        liked.Add(who);
                    else if (MatchConcessionTaste(entry.Item, name, AsStrings(Prop(t, "DislikedTags"))))
                        disliked.Add(who);
                }
                if (loved.Count > 0)
                    parts.Add("喜欢 " + ShortList(loved));
                if (liked.Count > 0)
                    parts.Add("还不错 " + ShortList(liked));
                if (disliked.Count > 0)
                    parts.Add("不喜欢 " + ShortList(disliked));

                if (parts.Count > 0)
                    AddUse(entry.RawId, "电影零食：" + string.Join("；", parts));
            }

            if (missing.Count > 0)
                ModEntry.Log?.Log($"[来源] 电影零食 未匹配到图鉴条目的零食名：{string.Join("、", missing)}", LogLevel.Info);
        }

        /// <summary>ConcessionTastes 的一条值既可能是零食名（如 Cotton Candy），也可能是物品 tag（如 Sweet）。</summary>
        private static bool MatchConcessionTaste(Item item, string concessionName, List<string> tags)
        {
            foreach (string tag in tags)
            {
                if (string.Equals(tag, concessionName, StringComparison.OrdinalIgnoreCase) || HasTag(item, tag))
                    return true;
            }
            return false;
        }

        /// <summary>往名字→条目表里放一条（首个命中优先，空键跳过）。</summary>
        private static void PutRef(Dictionary<string, CodexItem> map, string key, CodexItem entry)
        {
            if (!string.IsNullOrWhiteSpace(key) && entry != null && !map.ContainsKey(key))
                map[key] = entry;
        }

        /// <summary>名字归一化：忽略大小写与空格、连字符、撇号等非字母数字字符（Cotton Candy ≡ CottonCandy）。</summary>
        private static string NormKey(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        // ---------- 通用工具 ----------

        private static T Load<T>(string asset) => ModEntry.Hlp.GameContent.Load<T>(asset);

        private string NameOfRaw(string rawId) => _index?.NameOfRaw(rawId) ?? rawId;
        /// <summary>某个游戏内部类别号（如 -4 鱼）下的全部物品（S4 的「所有鱼」类别行用）。</summary>
        internal IReadOnlyList<CodexItem> ItemsInCategory(int category)
            => _byCategory.TryGetValue(category, out var list) ? list : (IReadOnlyList<CodexItem>)Array.Empty<CodexItem>();

        private static string ShortList(List<string> names)
            => names.Count <= 6 ? string.Join("、", names) : string.Join("、", names.Take(6)) + $" 等 {names.Count} 人";

        private static bool HasTag(Item item, string tag)
        {
            try { return item.HasContextTag(tag); }
            catch { return false; }
        }

        private static bool HasAnyTag(Item item, List<string> tags)
        {
            foreach (string t in tags)
                if (HasTag(item, t))
                    return true;
            return false;
        }

        /// <summary>地点显示名四级降级（v1.7.3，见需求文档 §17.14）：DisplayName → Strings\StringsFromMaps → Strings\Locations → 内部名。</summary>
        private string LocationName(string locId, LocationData loc)
        {
            string name = ResolveText(PropStr(loc, "DisplayName"));
            if (IsDisplayName(name, locId))
                return name;
            name = LoadLocalizedString("Strings\\StringsFromMaps:" + locId);
            if (IsDisplayName(name, locId))
                return name;
            name = LoadLocalizedString("Strings\\Locations:" + locId);
            if (IsDisplayName(name, locId))
                return name;
            name = FallbackName(locId);
            if (name != null)
                return name;
            // 面板里不出现英文内部名：SVE 自定义地图按 id 结构给中文兜底（v1.7.4）
            if (locId != null && locId.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
                return CustomLocationName(locId);
            return "其他地点";
        }

        /// <summary>SVE 自定义地图的中文兜底（游戏数据里这些地图没有名字，v1.7.4 探针实测）：
        /// Custom_&lt;NPC&gt;_WarpRoom → 「XX 的传送点」；Custom_&lt;NPC&gt;PictureMap → 「XX 的风景画」；其余「SVE 自定义地图」。</summary>
        private string CustomLocationName(string locId)
        {
            string mid = locId.Substring("Custom_".Length);
            string label;
            if (mid.EndsWith("_WarpRoom", StringComparison.OrdinalIgnoreCase))
            {
                string who = mid.Substring(0, mid.Length - "_WarpRoom".Length);
                if (string.Equals(who, "DayEnd", StringComparison.OrdinalIgnoreCase))
                    label = "当日结算传送点";
                else if (string.Equals(who, "CastleVillage_DayEnd", StringComparison.OrdinalIgnoreCase))
                    label = "城堡村当日结算传送点";
                else
                {
                    string npc = NpcDisplayOrNull(who);
                    label = npc != null ? npc + "的传送点" : "传送点";
                }
            }
            else if (mid.EndsWith("PictureMap", StringComparison.OrdinalIgnoreCase))
            {
                string who = mid.Substring(0, mid.Length - "PictureMap".Length).TrimEnd('_');
                string npc = NpcDisplayOrNull(who);
                label = npc != null ? npc + "的风景画" : "风景画";
            }
            else
                label = "SVE 自定义地图";
            return label;
        }

        /// <summary>NPC 中文显示名（Data/Characters 优先，其次人工补充表 ExtraShopOwners）；查不到返回 null。</summary>
        private string NpcDisplayOrNull(string internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName))
                return null;
            if (_npcNames != null && _npcNames.TryGetValue(internalName, out string dn) && !string.IsNullOrWhiteSpace(dn))
                return dn;
            return ExtraShopOwners.TryGetValue(internalName, out string extra) && !string.IsNullOrWhiteSpace(extra) ? extra : null;
        }

        /// <summary>是不是一个可用的显示名：非空、不含未解析 token、不等于内部名（否则说明该资产没有对应条目）。</summary>
        private static bool IsDisplayName(string s, string rawKey)
            => !string.IsNullOrWhiteSpace(s)
               && s.IndexOf("[LocalizedText", StringComparison.OrdinalIgnoreCase) < 0
               && !string.Equals(s.Trim(), rawKey, StringComparison.OrdinalIgnoreCase);

        /// <summary>取本地化字符串；资产或键不存在时返回 null（不算错误）。
        /// 注意：SDV 的 LoadString 在键不存在时不抛异常，而是**原样返回传入的 path**（实测：Strings\Locations:IslandSouth），必须判为未命中。</summary>
        private static string LoadLocalizedString(string path)
        {
            try
            {
                string s = Game1.content.LoadString(path);
                if (string.IsNullOrWhiteSpace(s) || string.Equals(s, path, StringComparison.OrdinalIgnoreCase))
                    return null;
                return s;
            }
            catch (Exception) { return null; }
        }

        private string FishAreaName(LocationData loc, string areaId)
        {
            if (string.IsNullOrWhiteSpace(areaId))
                return "";
            try
            {
                if (Prop(loc, "FishAreas") is System.Collections.IDictionary dict && dict.Contains(areaId))
                {
                    string n = ResolveText(PropStr(dict[areaId], "DisplayName"));
                    if (IsDisplayName(n, areaId))
                        return "· " + n;
                }
            }
            catch { }
            return "";
        }

        private void EnsureNpcNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var chars = Load<Dictionary<string, CharacterData>>("Data/Characters");
                foreach (var pair in chars)
                {
                    string dn = ResolveText(PropStr(pair.Value, "DisplayName"));
                    if (!string.IsNullOrWhiteSpace(dn))
                        map[pair.Key] = dn;
                }
            }
            catch (Exception ex)
            {
                Warnings.Add("Data/Characters 读取失败：" + ex.Message);
            }
            _npcNames = map;
        }

        private string NpcName(string internalName)
        {
            if (string.IsNullOrWhiteSpace(internalName))
                return "";
            if (_npcNames != null && _npcNames.TryGetValue(internalName, out string dn) && !string.IsNullOrWhiteSpace(dn))
                return dn;
            if (ExtraShopOwners.TryGetValue(internalName, out string extra) && !string.IsNullOrWhiteSpace(extra))
                return extra;
            return internalName;
        }

        /// <summary>解析 1.6 本地化 token（见需求文档 §17.10）：优先游戏自带 TokenParser，失败时按 key 兜底。</summary>
        internal static string ResolveText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.IndexOf('[') < 0)
                return text;
            try
            {
                string parsed = TokenParser.ParseText(text, null, null, null);
                if (!string.IsNullOrWhiteSpace(parsed) && parsed.IndexOf("[LocalizedText", StringComparison.OrdinalIgnoreCase) < 0)
                    return parsed;
            }
            catch (Exception)
            {
            }

            var sb = new System.Text.StringBuilder();
            int at = 0;
            foreach (System.Text.RegularExpressions.Match m in TokenRx.Matches(text))
            {
                sb.Append(text, at, m.Index - at);
                string asset = m.Groups[1].Value.Replace('\\', '/');
                string key = m.Groups[2].Value;
                string s = null;
                string full = asset + ":" + key;
                try { s = Game1.content.LoadString(full); }
                catch (Exception) { }
                if (string.IsNullOrWhiteSpace(s) || string.Equals(s, full, StringComparison.OrdinalIgnoreCase))
                    s = LastKeyPart(key);
                sb.Append(s);
                at = m.Index + m.Length;
            }
            sb.Append(text, at, text.Length - at);
            return sb.ToString();
        }

        private static readonly System.Text.RegularExpressions.Regex TokenRx =
            new System.Text.RegularExpressions.Regex(@"\[LocalizedText\s+([^:\]\s]+):([^\]\s]+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string LastKeyPart(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";
            int i = key.LastIndexOfAny(new[] { '\\', '/', '.', ':' });
            return i >= 0 && i < key.Length - 1 ? key.Substring(i + 1) : key;
        }


        private static string SeasonsText(object seasons)
        {
            var list = AsStrings(seasons);
            return list.Count == 0 ? "" : "（" + string.Join("/", list.Select(SeasonWord)) + "）";
        }

        private static string SeasonText(string season)
            => string.IsNullOrWhiteSpace(season) ? "" : "（" + SeasonWord(season) + "）";

        private static string SeasonWord(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "spring": return "春季";
                case "summer": return "夏季";
                case "fall": return "秋季";
                case "winter": return "冬季";
            }
            return s;
        }

        private static string PctText(double v)
            => v > 0 ? $"（{v * 100:0.#}%）" : "";

        /// <summary>物品 id 标记：纯数字，或带括号的限定 id（(O)24）。</summary>
        internal static bool IsItemToken(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;
            string s = id.Trim();
            if (s.StartsWith("(", StringComparison.Ordinal))
                return s.Length > 3 && s.IndexOf(')') > 0;
            foreach (char c in s)
                if (!char.IsDigit(c))
                    return false;
            return true;
        }

        internal static string Unqualify(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return "";
            string s = id.Trim();
            if (s.StartsWith("(", StringComparison.Ordinal))
            {
                int i = s.IndexOf(')');
                if (i > 0)
                    s = s.Substring(i + 1);
            }
            return s.Trim();
        }

        private static string[] Split(string s)
            => (s ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        private static List<string> AsStrings(object o)
        {
            var list = new List<string>();
            if (o == null)
                return list;
            if (o is string s)
            {
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
                return list;
            }
            if (o is System.Collections.IEnumerable en)
            {
                foreach (object x in en)
                {
                    string t = x as string;
                    if (!string.IsNullOrWhiteSpace(t))
                        list.Add(t);
                }
                return list;
            }
            string v = Convert.ToString(o);
            if (!string.IsNullOrWhiteSpace(v))
                list.Add(v);
            return list;
        }

        private static System.Collections.IEnumerable AsEnumerable(object o)
        {
            if (o == null)
                yield break;
            if (o is string || o is System.Collections.IDictionary)
            {
                yield return o;
                yield break;
            }
            if (o is System.Collections.IEnumerable en)
            {
                foreach (object x in en)
                    yield return x;
                yield break;
            }
            yield return o;
        }

        internal static object Prop(object o, string name)
        {
            if (o == null)
                return null;
            try
            {
                Type t = o.GetType();
                var p = t.GetProperty(name);
                if (p != null)
                    return p.GetValue(o);
                var f = t.GetField(name);
                if (f != null)
                    return f.GetValue(o);
            }
            catch { }
            return null;
        }

        internal static string PropStr(object o, string name) => Prop(o, name) as string ?? "";

        private static int Int(object v)
        {
            try { return v == null ? -1 : Convert.ToInt32(v); }
            catch { return -1; }
        }

        private static double Num(object v)
        {
            try { return v == null ? 0 : Convert.ToDouble(v); }
            catch { return 0; }
        }

        private struct Ingredient
        {
            public string Id;
            public int Count;
        }

        /// <summary>博物馆奖励的一项捐赠要求（1.6 的 TargetContextTags 元素）。</summary>
        private struct TagCount
        {
            public string Tag;
            public int Count;
            public bool MuseumComplete;
        }

        private static List<Ingredient> ParseIngredients(string field)
        {
            var list = new List<Ingredient>();
            string[] tok = Split(field);
            for (int i = 0; i + 1 < tok.Length; i += 2)
            {
                string id = tok[i].Trim('[', ']');
                if (!int.TryParse(tok[i + 1].Trim('[', ']'), out int n))
                    continue;
                list.Add(new Ingredient { Id = id, Count = n });
            }
            return list;
        }

        private IEnumerable<string> ResolveIngredient(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                yield break;
            string s = id.Trim().Trim('[', ']');
            if (int.TryParse(s, out int cat) && cat < 0)
            {
                if (_byCategory.TryGetValue(cat, out var items))
                    foreach (CodexItem e in items)
                        yield return e.RawId;
                yield break;
            }
            if (!IsItemToken(s))
                yield break;
            yield return Unqualify(s);
        }

        private string DescribeIngredients(string field, int max)
        {
            var ing = ParseIngredients(field);
            if (ing.Count == 0)
                return "无材料";
            var parts = new List<string>();
            for (int i = 0; i < ing.Count && i < max; i++)
            {
                string id = ing[i].Id;
                string nm = int.TryParse(id, out int cat) && cat < 0
                    ? "任意同类（#" + cat + "）"
                    : NameOfRaw(Unqualify(id));
                parts.Add(nm + "×" + ing[i].Count);
            }
            if (ing.Count > max)
                parts.Add("…");
            return string.Join("、", parts);
        }
    }
}
