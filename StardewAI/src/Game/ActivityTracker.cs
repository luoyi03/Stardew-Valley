using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Xna.Framework;
using StardewAI.Game;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;

namespace StardewAI.Game
{
    /// <summary>游戏里刚发生的一件事，交给 ProactiveDirector 决定要不要说。</summary>
    internal sealed class GameEvent
    {
        public string Key = "";
        public string Description = "";
        public int Priority;
    }

    /// <summary>
    /// 看清玩家在干什么：把「刚钓到鱼 / 下到新矿层 / 技能升级 / 进新地点 / 体力见底 / 站着发呆」这类事
    /// 收集成待办事件，另外提供一句「玩家此刻」的状态描述给模型看。
    /// 只读游戏状态，不改任何游戏数据。
    /// </summary>
    internal static class ActivityTracker
    {
        private static readonly object Gate = new();
        private static readonly List<GameEvent> Pending = new();
        private static readonly Dictionary<string, DateTime> LastKey = new();

        private static readonly Stopwatch Since = Stopwatch.StartNew();
        private static double _lastSampleMs;

        private static string _location = "";
        private static int _mineLevel;
        private static int _fishCount;
        private static int _money;
        private static int _health = -1;
        private static float _stamina = -1f;
        private static Vector2 _tile;
        private static DateTime _lastMove = DateTime.UtcNow;
        private static bool _idleReported;
        private static bool _inMine;

        /// <summary>血量档位（100/80/50/30）：掉到更低的一档才说一句，回血后重置。</summary>
        private static int _hpBand = 100;

        /// <summary>背包里值得报一句的矿物/宝石数量（物品 id → 总数）。</summary>
        private static readonly Dictionary<string, int> _oreCounts = new(StringComparer.Ordinal);
        private static bool _oreReady;

        /// <summary>背包里的鱼（id|品质 → 个数），用来抓「钓到有星级的鱼」。</summary>
        private static readonly Dictionary<string, int> _fishQty = new(StringComparer.Ordinal);
        private static bool _fishReady;

        /// <summary>每种鱼的历史最大尺寸，用来抓「破纪录」。</summary>
        private static readonly Dictionary<string, int> _recordSizes = new(StringComparer.Ordinal);

        /// <summary>订阅 SMAPI 事件。在 ModEntry.Entry 里调一次。</summary>
        public static void Hook(IModHelper helper)
        {
            helper.Events.GameLoop.SaveLoaded += (_, _) => Reset();
            helper.Events.GameLoop.DayStarted += (_, _) =>
            {
                Reset();
                Push("day", "刚睡醒，新的一天开始了", 45);
            };

            helper.Events.Player.LevelChanged += (_, e) =>
                Push("skill", SkillZh(e.Skill.ToString()) + "技能刚升到 " + e.NewLevel + " 级", 92);

            helper.Events.World.ObjectListChanged += (_, e) =>
            {
                if (e.Added != null)
                {
                    foreach (var kv in e.Added)
                    {
                        string n = kv.Value?.DisplayName ?? kv.Value?.Name;
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            Push("obj", "刚在「" + Display(e.Location?.Name ?? "") + "」放下/种下了「" + n + "」", 40);
                            break;
                        }
                    }
                }
            };
        }

        /// <summary>每 tick 调用（内部自己限流到大约一秒一次）。</summary>
        public static void Tick()
        {
            if (!Context.IsWorldReady || Game1.player == null)
                return;

            double now = Since.Elapsed.TotalMilliseconds;
            if (now - _lastSampleMs < 1000)
                return;

            _lastSampleMs = now;

            try
            {
                Sample();
            }
            catch
            {
                // 采样出错不影响游戏
            }
        }

        private static void Sample()
        {
            Farmer p = Game1.player;
            if (p == null)
                return;

            if (_health < 0)
            {
                _health = p.health;
                _stamina = p.stamina;
                _money = p.Money;
                _fishCount = FishCount();
                _tile = p.Tile;
            }

            // 到了新地方（走进屋、出城、下矿都算）
            string here = Game1.currentLocation?.Name ?? "";
            if (here.Length > 0 && _location.Length > 0 && here != _location)
                Push("loc", "刚来到「" + Display(here) + "」", 55);

            _location = here;

            // 血量：按 80% / 50% / 30% 三档提醒，语气一档比一档急
            int maxHp = MaxHealth(p);
            float maxStam = MaxStamina(p);

            if (p.health > 0 && maxHp > 0)
            {
                int pct = (int)Math.Round(p.health * 100.0 / maxHp);
                int band = pct <= 30 ? 30 : pct <= 50 ? 50 : pct <= 80 ? 80 : 100;

                if (band < _hpBand)
                {
                    Push("hurt" + band, HurtLine(band, p.health, maxHp),
                        band == 80 ? 72 : band == 50 ? 93 : 97);
                    _hpBand = band;
                }
                else if (band > _hpBand)
                {
                    _hpBand = band;      // 回血了：重置档位，下次再掉下来还能提醒
                }
            }

            if (p.stamina < _stamina && p.stamina <= maxStam * 0.15)
                Push("tired", "体力快见底了（" + (int)p.stamina + "/" + (int)maxStam + "）——语气心疼一点，劝他歇会儿或吃点东西", 80);

            // 钓到新鱼种
            int fish = FishCount();
            if (fish > _fishCount && _fishCount > 0)
                Push("fish", "刚钓上来一条鱼（累计钓过 " + fish + " 种了）", 80);

            _fishCount = fish;

            // 挖到矿石/宝石、钓到有星级的鱼 / 破纪录
            ScanOres(p);
            ScanFish(p);

            // 赚到钱
            if (p.Money - _money >= 500)
                Push("money", "刚刚入账了 " + (p.Money - _money) + " 金币（现在有 " + p.Money + "）", 65);

            _money = p.Money;

            // 矿洞层数（有时候直接从上往下走层数变了，没触发 LocationChanged）
            if (Game1.currentLocation is MineShaft shaft)
            {
                if (!_inMine || shaft.mineLevel != _mineLevel)
                {
                    if (_inMine)
                        Push("mine", "刚到矿洞第 " + shaft.mineLevel + " 层", 88);

                    _mineLevel = shaft.mineLevel;
                    _inMine = true;
                }
            }
            else
            {
                _inMine = false;
            }

            // 发呆
            if (Vector2.Distance(p.Tile, _tile) > 0.4f || p.isMoving())
            {
                _tile = p.Tile;
                _lastMove = DateTime.UtcNow;
                _idleReported = false;
            }

            int idleNeed = ModEntry.Config?.ProactiveIdleSeconds ?? 0;

            if (idleNeed > 0 && !_idleReported && (DateTime.UtcNow - _lastMove).TotalSeconds >= idleNeed)
            {
                _idleReported = true;
                Push("idle", "玩家在原地站了 " + (int)(DateTime.UtcNow - _lastMove).TotalSeconds + " 秒没动，好像在想事情或者在发呆", 30);
            }

            _health = p.health;
            _stamina = p.stamina;
        }

        /// <summary>取出最想说的一件事（同时清空队列，避免攒一堆旧事）。</summary>
        public static GameEvent TakeBest()
        {
            lock (Gate)
            {
                if (Pending.Count == 0)
                    return null;

                int best = 0;
                for (int i = 1; i < Pending.Count; i++)
                {
                    if (Pending[i].Priority > Pending[best].Priority)
                        best = i;
                }

                var ev = Pending[best];
                Pending.Clear();
                return ev;
            }
        }

        /// <summary>玩家此刻在做什么（塞进系统提示，让她知道你现在什么状态）。</summary>
        public static string Describe()
        {
            Farmer p = Game1.player;
            if (p == null)
                return "";

            var sb = new StringBuilder();
            sb.Append("【玩家此刻】");

            sb.Append(p.isMoving() ? "正在走动" : "站着没动");

            string tool = ToolName(p.CurrentTool);
            if (tool != null)
                sb.Append("，手里拿着").Append(tool);

            if (Game1.currentLocation is MineShaft shaft)
                sb.Append("，在矿洞第 ").Append(shaft.mineLevel).Append(" 层");
            else if (!string.IsNullOrEmpty(Game1.currentLocation?.Name))
                sb.Append("，人在「").Append(Display(Game1.currentLocation.Name)).Append("」");

            int maxHp = MaxHealth(p);
            float maxStam = MaxStamina(p);

            sb.Append("；血量 ").Append(p.health).Append("/").Append(maxHp);
            sb.Append("，体力 ").Append((int)p.stamina).Append("/").Append((int)maxStam);

            if (p.stamina <= maxStam * 0.2)
                sb.Append("（很累了）");

            sb.Append("。\n");
            return sb.ToString();
        }

        /// <summary>血量掉档时那句话（把"语气"也写进去，模型会照着说）。</summary>
        private static string HurtLine(int band, int hp, int maxHp)
        {
            string amount = hp + "/" + maxHp;

            return band switch
            {
                80 => "刚被打了一下，血量掉到 " + amount + "（八成左右）——不严重，"
                      + "语气随口一点，提一句让他小心就行",
                50 => "血量已经被打到一半以下了（" + amount + "）——语气要明显担心，"
                      + "劝他吃口东西、别硬撑，或者先退到安全的地方",
                _ => "血量只剩 " + amount + " 了，快撑不住了——语气着急一点，"
                     + "让他赶紧吃奶酪/喝咖啡、撤退或者直接用回程法杖回家"
            };
        }

        /// <summary>值得报一句的矿石/煤炭/晶球（显示名用游戏自己的本地化名字）。</summary>
        private static readonly HashSet<string> OreIds = new(StringComparer.Ordinal)
        {
            "378", "380", "384", "386", "909",      // 铜 / 铁 / 金 / 铱 / 放射性矿石
            "382",                                   // 煤炭
            "80", "338",                             // 石英 / 精炼石英
            "535", "536", "537", "749"               // 晶球 / 冰冻晶球 / 岩浆晶球 / 万能晶球
        };

        /// <summary>特别值钱的（挖到就说，优先级也高）。</summary>
        private static readonly HashSet<string> PreciousIds = new(StringComparer.Ordinal)
        {
            "72", "74",                              // 钻石 / 五彩碎片
            "384", "386", "909"                      // 金 / 铱 / 放射性矿石
        };

        /// <summary>矿物/宝石？矿物类看品类（-2），矿石看白名单。</summary>
        private static bool IsWorthTelling(Item item)
        {
            if (item == null)
                return false;

            try
            {
                if (item.Category == -2)     // -2 = 矿物/宝石
                    return true;
            }
            catch
            {
                // 拿不到品类就看白名单
            }

            return OreIds.Contains(item.ItemId);
        }

        /// <summary>背包里多了矿石/宝石就说一句。</summary>
        private static void ScanOres(Farmer p)
        {
            var now = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                foreach (Item it in p.Items)
                {
                    if (!IsWorthTelling(it))
                        continue;

                    now.TryGetValue(it.ItemId, out int c);
                    now[it.ItemId] = c + it.Stack;
                    names[it.ItemId] = it.DisplayName;
                }
            }
            catch
            {
                return;
            }

            if (_oreReady)
            {
                foreach (var kv in now)
                {
                    _oreCounts.TryGetValue(kv.Key, out int before);
                    int delta = kv.Value - before;

                    if (delta <= 0)
                        continue;

                    bool gem = !OreIds.Contains(kv.Key);
                    names.TryGetValue(kv.Key, out string name);

                    string what = delta > 1 ? delta + " 个「" + (name ?? "矿石") + "」" : "一块「" + (name ?? "矿石") + "」";

                    Push("ore" + kv.Key,
                        "刚挖到/捡到 " + what
                        + (PreciousIds.Contains(kv.Key) ? "——这个挺稀罕的，语气替他高兴一下"
                           : gem ? "——语气随口夸一句" : "——语气随口说一句就行"),
                        PreciousIds.Contains(kv.Key) ? 86 : gem ? 70 : 62);
                }
            }

            _oreCounts.Clear();
            foreach (var kv in now)
                _oreCounts[kv.Key] = kv.Value;

            _oreReady = true;
        }

        /// <summary>钓到有星级的鱼 / 破纪录。</summary>
        private static void ScanFish(Farmer p)
        {
            // (a) 背包里的鱼：只看有星级的（普通鱼由上面那条"钓到鱼"负责）
            var now = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);

            try
            {
                foreach (Item it in p.Items)
                {
                    if (it == null || it.Category != -4)     // -4 = 鱼
                        continue;

                    string key = it.ItemId + "|" + it.Quality;
                    now.TryGetValue(key, out int c);
                    now[key] = c + it.Stack;
                    names[it.ItemId] = it.DisplayName;
                }
            }
            catch
            {
                return;
            }

            if (_fishReady)
            {
                foreach (var kv in now)
                {
                    _fishQty.TryGetValue(kv.Key, out int before);
                    if (kv.Value - before <= 0)
                        continue;

                    string[] parts = kv.Key.Split('|');
                    int quality = parts.Length > 1 && int.TryParse(parts[1], out int q) ? q : 0;

                    if (quality <= 0)
                        continue;

                    names.TryGetValue(parts[0], out string nm);

                    Push("fishstar" + kv.Key,
                        "刚钓上来一条" + QualityZh(quality) + "的「" + (nm ?? "鱼") + "」"
                        + (quality >= 4 ? "——铱星，运气好得离谱，语气替他激动一下" : "——语气替他高兴"),
                        quality >= 4 ? 88 : quality >= 2 ? 84 : 82);
                }
            }

            _fishQty.Clear();
            foreach (var kv in now)
                _fishQty[kv.Key] = kv.Value;

            _fishReady = true;

            // (b) 破纪录：fishCaught[鱼][1] 是这条鱼的历史最大尺寸
            // 注意：1.6 里这个字段的类型取不到 Key/Value，所以按 IDictionary 遍历
            try
            {
                if (p.fishCaught is System.Collections.IDictionary caught)
                {
                    foreach (System.Collections.DictionaryEntry entry in caught)
                    {
                        string id = entry.Key as string;

                        if (string.IsNullOrEmpty(id))
                            continue;

                        int size = RecordSizeOf(entry.Value);

                        if (size <= 0)
                            continue;

                        if (_recordSizes.TryGetValue(id, out int before) && before > 0 && size > before)
                        {
                            names.TryGetValue(id, out string nm);

                            Push("record" + id,
                                "刚钓上来一条「" + (nm ?? "鱼") + "」，" + size + " 英寸，"
                                + "比他自己之前的纪录（" + before + " 英寸）还大——语气替他高兴、夸他一句",
                                90);
                        }

                        _recordSizes[id] = size;
                    }
                }
            }
            catch
            {
                // 读不到就算了，不影响别的
            }
        }

        /// <summary>品质 0/1/2/4 → 银星/金星/铱星。</summary>
        private static string QualityZh(int quality)
        {
            return quality switch
            {
                >= 4 => "铱星",
                2 => "金星",
                1 => "银星",
                _ => "普通"
            };
        }

        /// <summary>
        /// 读 fishCaught 里那一对 [钓到次数, 最大尺寸] 的尺寸。
        /// 1.6 里这个值的类型在不同版本可能是 int[] 也可能是 NetIntArray，所以按 object 处理。
        /// </summary>
        private static int RecordSizeOf(object value)
        {
            if (value == null)
                return 0;

            if (value is int[] arr)
                return arr.Length > 1 ? arr[1] : 0;

            try
            {
                var indexer = value.GetType().GetProperty("Item", new[] { typeof(int) });

                if (indexer != null)
                    return Convert.ToInt32(indexer.GetValue(value, new object[] { 1 }));
            }
            catch
            {
                // 换下面的枚举方式
            }

            try
            {
                if (value is System.Collections.IEnumerable list)
                {
                    int i = 0;

                    foreach (object o in list)
                    {
                        if (i++ == 1)
                            return Convert.ToInt32(o);
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return 0;
        }

        private static void Reset()
        {
            lock (Gate)
                Pending.Clear();

            LastKey.Clear();
            _location = "";
            _mineLevel = 0;
            _fishCount = 0;
            _money = 0;
            _health = -1;
            _stamina = -1f;
            _idleReported = false;
            _inMine = false;
            _lastMove = DateTime.UtcNow;

            _hpBand = 100;
            _oreCounts.Clear();
            _oreReady = false;
            _fishQty.Clear();
            _fishReady = false;
            _recordSizes.Clear();
        }

        private static void Push(string key, string description, int priority)
        {
            lock (Gate)
            {
                // 同一类事 90 秒内不重复记
                if (LastKey.TryGetValue(key, out DateTime last) && (DateTime.UtcNow - last).TotalSeconds < 90)
                    return;

                LastKey[key] = DateTime.UtcNow;
                Pending.Add(new GameEvent { Key = key, Description = description, Priority = priority });

                if (Pending.Count > 8)
                    Pending.RemoveAt(0);
            }
        }

        private static int FishCount()
        {
            Farmer p = Game1.player;
            if (p == null)
                return 0;

            try
            {
                int n = 0;

                foreach (var _ in p.fishCaught)
                    n++;

                return n;
            }
            catch
            {
                return 0;
            }
        }

        private static int MaxHealth(Farmer p)
        {
            try { return p.maxHealth; } catch { return 100; }
        }

        private static float MaxStamina(Farmer p)
        {
            try { return p.maxStamina.Value; } catch { return 270f; }
        }

        private static string ToolName(Tool tool)
        {
            if (tool == null)
                return null;

            return tool.GetType().Name switch
            {
                "Axe" => "斧头",
                "Pickaxe" => "镐子",
                "Hoe" => "锄头",
                "WateringCan" => "洒水壶",
                "FishingRod" => "鱼竿",
                "MeleeWeapon" => "武器",
                "Pan" => "淘金盘",
                "MilkPail" => "奶桶",
                "Shears" => "羊毛剪",
                "Wand" => "回城法杖",
                _ => tool.DisplayName
            };
        }

        private static string Display(string locationName)
        {
            if (string.IsNullOrWhiteSpace(locationName))
                return "未知地点";

            try
            {
                var loc = Game1.getLocationFromName(locationName);
                if (loc != null && !string.IsNullOrWhiteSpace(loc.DisplayName))
                    return loc.DisplayName;
            }
            catch
            {
                // 拿不到显示名就用内部名
            }

            return locationName;
        }

        /// <summary>技能枚举名 → 中文（用字符串匹配，避免依赖不同版本里的枚举类型名）。</summary>
        private static string SkillZh(string skill)
        {
            return skill switch
            {
                "Farming" => "耕种",
                "Fishing" => "钓鱼",
                "Foraging" => "采集",
                "Mining" => "挖矿",
                "Combat" => "战斗",
                "Luck" => "运气",
                _ => skill ?? ""
            };
        }
    }
}
