using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StardewAI.Llm;

namespace StardewAI
{
    public enum MsgRole
    {
        User,
        Assistant,
        Error,
        Info
    }

    public sealed class ChatLine
    {
        public MsgRole Role;
        public string Name;
        public string Text = "";

        /// <summary>这一条回答里附带的可执行动作（目前只有「给物品」，存物品名）。</summary>
        public string Action;

        public ChatLine(MsgRole role, string name, string text)
        {
            Role = role;
            Name = name;
            Text = text ?? "";
        }
    }

    /// <summary>一次对话会话：负责历史维护、后台请求、流式增量缓冲。</summary>
    public sealed class ChatSession
    {
        public readonly List<ChatLine> Lines = new();
        private readonly ConcurrentQueue<string> _deltas = new();
        private readonly List<ChatMsg> _history = new();

        private CancellationTokenSource _cts;
        private ChatLine _streaming;
        private string _pendingText;

        public bool Busy { get; private set; }
        public bool Cancelled { get; private set; }

        /// <summary>内容版本号，UI 据此判断是否重新排版。</summary>
        public int Version { get; private set; } = 1;

        public Func<string> SystemPromptBuilder { get; set; }
        public string AssistantName { get; set; } = "AI";
        public string UserName { get; set; } = "玩家";

        public void Say(MsgRole role, string name, string text)
        {
            Lines.Add(new ChatLine(role, name, text));
            Version++;
        }

        /// <summary>用存档里的历史重建一次会话（读取记忆时用）。</summary>
        public void Restore(List<ChatMsg> messages)
        {
            Lines.Clear();
            _history.Clear();

            foreach (var m in messages ?? new List<ChatMsg>())
            {
                if (m == null)
                    continue;

                bool mine = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase);
                Lines.Add(new ChatLine(mine ? MsgRole.User : MsgRole.Assistant, mine ? UserName : AssistantName, m.Content ?? ""));
                _history.Add(new ChatMsg(m.Role, m.Content));
            }

            Version++;
        }

        /// <summary>导出用于存档的历史（只含 user / assistant）。</summary>
        public List<ChatMsg> ExportHistory() => new(_history);

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignore */ }
        }

        public void Clear()
        {
            Stop();
            Lines.Clear();
            _history.Clear();
            Busy = false;
            Version++;
        }

        public void Ask(string text)
        {
            if (Busy)
                return;

            string q = (text ?? "").Trim();
            if (q.Length == 0)
                return;

            Lines.Add(new ChatLine(MsgRole.User, UserName, q));
            _history.Add(new ChatMsg("user", q));

            Start(null);
        }

        /// <summary>隐藏的一次系统触发（例如让 NPC 主动打招呼）。</summary>
        public void Trigger(string hiddenInstruction)
        {
            if (Busy || string.IsNullOrWhiteSpace(hiddenInstruction))
                return;
            Start(hiddenInstruction);
        }

        private void Start(string hidden)
        {
            string system = SystemPromptBuilder?.Invoke() ?? "";
            _streaming = new ChatLine(MsgRole.Assistant, AssistantName, "");
            Lines.Add(_streaming);

            Busy = true;
            Cancelled = false;
            Version++;

            var messages = new List<ChatMsg> { new("system", system) };
            messages.AddRange(_history);
            if (!string.IsNullOrWhiteSpace(hidden))
                messages.Add(new ChatMsg("user", hidden));

            var cfg = ModEntry.Config;
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(15, cfg.TimeoutSeconds)));
            var token = _cts.Token;

            int pairLimit = Math.Max(1, cfg.MaxHistory);
            _pendingText = null;

            Task.Run(async () =>
            {
                string error = null;
                string answer = null;

                try
                {
                    answer = await LlmClient.ChatAsync(
                        cfg.BaseUrl, cfg.ApiKey, cfg.Model, cfg.Temperature, cfg.MaxTokens, cfg.Stream,
                        messages, piece => Enqueue(piece), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _pendingText = "__CANCELLED__";
                }
                catch (Exception ex)
                {
                    error = ex.Message ?? ex.GetType().Name;
                }

                if (error == null && !token.IsCancellationRequested)
                {
                    string final = answer ?? "";
                    _pendingText = final;
                    _history.Add(new ChatMsg("assistant", final));

                    int max = pairLimit * 2;
                    while (_history.Count > max)
                        _history.RemoveRange(0, Math.Min(2, _history.Count - max));
                }
                else if (error != null)
                {
                    _pendingText = "__ERROR__" + error;
                }

                Busy = false;
            });
        }

        /// <summary>把后台线程的结果搬到主线程的安全位置。必须在 UI 线程调用。</summary>
        public void PollUI()
        {
            int got = 0;
            while (_deltas.TryDequeue(out string piece))
            {
                if (_streaming != null)
                    _streaming.Text += piece;
                got++;
            }

            if (got > 0)
                Version++;

            if (!Busy && _pendingText != null)
            {
                string p = _pendingText;
                _pendingText = null;

                if (p.StartsWith("__ERROR__", StringComparison.Ordinal))
                {
                    if (_streaming != null)
                    {
                        string msg = p.Substring(9);
                        _streaming.Role = MsgRole.Error;
                        _streaming.Text = string.IsNullOrWhiteSpace(_streaming.Text)
                            ? "调用失败：" + msg
                            : _streaming.Text + "\n[中断] " + msg;
                    }
                }
                else if (p != "__CANCELLED__")
                {
                    if (_streaming != null && _streaming.Text.Length == 0)
                        _streaming.Text = p;
                }

                if (_streaming != null)
                {
                    if (_streaming.Role == MsgRole.Assistant)
                        ExtractAction(_streaming);
                }

                _streaming = null;
                Version++;
            }
        }

        /// <summary>把回答里的 [[GIVE:物品名]] 标记抽出来，正文里不显示它。</summary>
        private static void ExtractAction(ChatLine line)
        {
            if (line?.Text == null || line.Text.IndexOf("GIVE", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            var match = GiveTag.Match(line.Text);
            if (!match.Success)
                return;

            line.Action = match.Groups[1].Value.Trim();
            line.Text = GiveTag.Replace(line.Text, "").Trim();
        }

        private static readonly System.Text.RegularExpressions.Regex GiveTag =
            new(@"\[\[\s*GIVE\s*:\s*([^\]\r\n]+?)\s*\]\]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>后台线程回调：把流式增量先塞进队列。</summary>
        internal void Enqueue(string piece)
        {
            if (!string.IsNullOrEmpty(piece))
                _deltas.Enqueue(piece);
        }
    }
}
