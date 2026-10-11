using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewValley;

namespace InGameGuide
{
    public class BridgeRequest
    {
        public long Id { get; set; }
        public string Action { get; set; }
        public string Item { get; set; }
        public int Count { get; set; } = 1;
        public int Quality { get; set; }
    }

    public class BridgeResult
    {
        public long Id { get; set; }
        public bool Ok { get; set; }
        public string Message { get; set; }
        public string Item { get; set; }
        public string Code { get; set; }
        public string QualifiedId { get; set; }
        public int Count { get; set; }
        public bool WorldReady { get; set; }
        public string Time { get; set; }
        /// <summary>匹配到多个物品，需要用户指定。</summary>
        public bool NeedConfirm { get; set; }
        public List<CandidateInfo> Candidates { get; set; }
    }

    public class CandidateInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string En { get; set; }
        public string Code { get; set; }
        public string QualifiedId { get; set; }
        public string Type { get; set; }
        public bool Exact { get; set; }
    }

    /// <summary>
    /// 和游戏外的“AI”通信的桥：外部往 bridge/request.json 写一条命令，
    /// Mod 每隔几帧检查一次，执行完把结果写回 bridge/result.json。
    /// 不用开端口，避免防火墙和权限问题。
    /// </summary>
    public class CommandBridge
    {
        private readonly string _dir;
        private readonly string _reqPath;
        private readonly string _resPath;
        private DateTime _lastWrite = DateTime.MinValue;
        private long _lastId;

        /// <summary>上一次因为歧义而挂起的请求，等 pick 来指定。</summary>
        private PendingRequest _pending;

        private class PendingRequest
        {
            public string Action;
            public int Count;
            public int Quality;
            public List<ItemRow> Rows;
        }

        private static readonly JsonSerializerOptions Opts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public CommandBridge(string modDir)
        {
            _dir = Path.Combine(modDir, "bridge");
            Directory.CreateDirectory(_dir);
            _reqPath = Path.Combine(_dir, "request.json");
            _resPath = Path.Combine(_dir, "result.json");

            if (File.Exists(_resPath))
            {
                try
                {
                    var old = JsonSerializer.Deserialize<BridgeResult>(File.ReadAllText(_resPath), Opts);
                    if (old != null)
                        _lastId = old.Id;
                }
                catch { /* 忽略 */ }
            }
            // 启动时已存在的 request 不当成新命令
            if (File.Exists(_reqPath))
                _lastWrite = File.GetLastWriteTimeUtc(_reqPath);
        }

        public void Poll()
        {
            try
            {
                if (!File.Exists(_reqPath))
                    return;

                var wt = File.GetLastWriteTimeUtc(_reqPath);
                if (wt == _lastWrite)
                    return;

                string text;
                try
                {
                    text = File.ReadAllText(_reqPath);
                }
                catch
                {
                    return; // 还在写，等下一轮
                }
                _lastWrite = wt;

                BridgeRequest req;
                try
                {
                    req = JsonSerializer.Deserialize<BridgeRequest>(text, Opts);
                }
                catch (Exception ex)
                {
                    Write(new BridgeResult { Id = _lastId, Ok = false, Message = "请求 JSON 解析失败：" + ex.Message });
                    return;
                }

                if (req == null)
                    return;
                if (req.Id <= _lastId)
                    return; // 已执行过

                _lastId = req.Id;
                Write(Execute(req));
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log("命令桥处理出错：" + ex.Message, StardewModdingAPI.LogLevel.Warn);
            }
        }

        private BridgeResult Execute(BridgeRequest req)
        {
            string action = (req.Action ?? "add").Trim().ToLowerInvariant();
            var res = new BridgeResult
            {
                Id = req.Id,
                WorldReady = StardewModdingAPI.Context.IsWorldReady,
                Time = DateTime.Now.ToString("HH:mm:ss")
            };

            if (action is "ping" or "status")
            {
                res.Ok = true;
                res.Message = StardewModdingAPI.Context.IsWorldReady
                    ? "已连接，存档已加载"
                    : "已连接，但还没进入存档（加载存档后才能发物品）";
                return res;
            }

            // 待确认的选择：用 pick 指定
            if (action is "pick" or "choose")
                return Pick(req, res);

            var cands = ItemSpawner.Candidates(req.Item, 8);
            if (cands.Count == 0)
            {
                res.Ok = false;
                res.Message = "没找到物品「" + req.Item + "」";
                return res;
            }

            if (cands.Count > 1)
            {
                // 有歧义，不要替用户猜，先把候选列出来
                _pending = new PendingRequest
                {
                    Action = action,
                    Count = req.Count <= 0 ? 1 : req.Count,
                    Quality = req.Quality,
                    Rows = cands
                };

                res.Ok = false;
                res.NeedConfirm = true;
                res.Message = $"「{req.Item}」有 {cands.Count} 个匹配，请指定要哪个（回复序号或完整名字）";
                res.Candidates = new List<CandidateInfo>();
                for (int i = 0; i < cands.Count; i++)
                {
                    var c = cands[i];
                    res.Candidates.Add(new CandidateInfo
                    {
                        Index = i + 1,
                        Name = c.DisplayName,
                        En = c.En,
                        Code = c.Code,
                        QualifiedId = ItemSpawner.QualifiedId(c),
                        Type = ModEntry.Db.Label(c.Type),
                        Exact = c.DisplayName.Equals(req.Item?.Trim(), StringComparison.OrdinalIgnoreCase)
                    });
                }
                return res;
            }

            var row = cands[0];
            res.Item = row.DisplayName;
            res.Code = row.Code;
            res.QualifiedId = ItemSpawner.QualifiedId(row);
            res.Count = req.Count;

            switch (action)
            {
                case "add":
                case "give":
                case "spawn":
                    res.Message = ItemSpawner.Give(row, req.Count <= 0 ? 1 : req.Count, req.Quality);
                    res.Ok = !res.Message.Contains("失败") && !res.Message.Contains("没找到") && !res.Message.Contains("还没进入");
                    break;

                case "name":
                case "egg":
                    res.Message = ItemSpawner.PrepareName(row);
                    res.Ok = !res.Message.Contains("不是物体类");
                    break;

                case "search":
                case "find":
                    res.Ok = true;
                    res.Message = $"{row.DisplayName} / {row.En} → {res.QualifiedId}（{ModEntry.Db.Label(row.Type)}）";
                    break;

                case "check":
                case "validate":
                {
                    // 只创建不入库，用来确认游戏里确实存在这个东西
                    try
                    {
                        var probe = ItemRegistry.Create(res.QualifiedId, 1, 0);
                        if (probe == null)
                        {
                            res.Ok = false;
                            res.Message = $"游戏里没有这个物品：{res.QualifiedId}";
                        }
                        else
                        {
                            res.Ok = true;
                            res.Message = $"可以生成：{probe.DisplayName}（{res.QualifiedId}）"
                                        + $" 类型={probe.GetType().Name} 上限={probe.maximumStackSize()}";
                        }
                    }
                    catch (Exception ex)
                    {
                        res.Ok = false;
                        res.Message = "创建探测失败：" + ex.Message;
                    }
                    break;
                }

                default:
                    res.Ok = false;
                    res.Message = "未知动作：" + action + "（可用：add / name / search / check / pick / ping）";
                    break;
            }

            return res;
        }

        /// <summary>从上一次歧义候选里挑一个执行，item 可以是序号(从 1 开始)或完整名字。</summary>
        private BridgeResult Pick(BridgeRequest req, BridgeResult res)
        {
            if (_pending == null || _pending.Rows == null || _pending.Rows.Count == 0)
            {
                res.Ok = false;
                res.Message = "当前没有待选择的物品，请先发一次有歧义的 add/name";
                return res;
            }

            ItemRow row = null;
            string key = (req.Item ?? "").Trim();

            if (int.TryParse(key, out int idx) && idx >= 1 && idx <= _pending.Rows.Count)
                row = _pending.Rows[idx - 1];
            else
                row = _pending.Rows.FirstOrDefault(r =>
                    r.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                    (r.Zh != null && r.Zh.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
                    (r.En != null && r.En.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
                    ItemSpawner.QualifiedId(r).Equals(key, StringComparison.OrdinalIgnoreCase));

            if (row == null)
            {
                res.Ok = false;
                res.Message = $"选不到「{key}」，可用序号 1~{_pending.Rows.Count} 或完整名字";
                res.Candidates = _pending.Rows.Select((r, i) => new CandidateInfo
                {
                    Index = i + 1,
                    Name = r.DisplayName,
                    QualifiedId = ItemSpawner.QualifiedId(r)
                }).ToList();
                return res;
            }

            var done = new PendingRequest
            {
                Action = _pending.Action,
                Count = _pending.Count,
                Quality = _pending.Quality,
                Rows = new List<ItemRow> { row }
            };
            _pending = null;

            res.Item = row.DisplayName;
            res.Code = row.Code;
            res.QualifiedId = ItemSpawner.QualifiedId(row);
            res.Count = done.Count;

            res.Message = done.Action == "name"
                ? ItemSpawner.PrepareName(row)
                : ItemSpawner.Give(row, done.Count, done.Quality);

            res.Ok = !res.Message.Contains("失败") && !res.Message.Contains("没找到")
                     && !res.Message.Contains("还没进入") && !res.Message.Contains("不是物体类");
            return res;
        }

        private void Write(BridgeResult res)
        {
            try
            {
                string tmp = _resPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(res, Opts));
                File.Copy(tmp, _resPath, true);
                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                ModEntry.Log?.Log("写回 bridge/result.json 失败：" + ex.Message, StardewModdingAPI.LogLevel.Warn);
            }
        }
    }
}
