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

        /// <summary>UI 线程回调：新一轮回答开始（语音朗读借它打断上一段）。</summary>
        public Action OnAnswerStarted;

        /// <summary>UI 线程回调：流式回答又长出了一截。</summary>
        public Action<ChatLine> OnAnswerDelta;

        /// <summary>UI 线程回调：一条回答彻底结束。</summary>
        public Action<ChatLine> OnAnswerDone;

        /// <summary>UI 线程回调：回答被中断或出错，不要念了。</summary>
        public Action OnAnswerCancelled;

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

            // 开着语音朗读时补一句写作要求：用口语短句，念出来才像人在说话
            system = Voice.VoiceAssistant.DecorateSystem(system);

            _streaming = new ChatLine(MsgRole.Assistant, AssistantName, "");
            Lines.Add(_streaming);

            Busy = true;
            Cancelled = false;
            Version++;

            try { OnAnswerStarted?.Invoke(); } catch { /* 回调出错不能影响对话 */ }

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
            {
                Version++;
                SafeInvoke(OnAnswerDelta, _streaming);
            }

            if (!Busy && _pendingText != null)
            {
                string p = _pendingText;
                _pendingText = null;

                bool speakable = true;

                if (p.StartsWith("__ERROR__", StringComparison.Ordinal))
                {
                    speakable = false;

                    if (_streaming != null)
                    {
                        string msg = p.Substring(9);
                        _streaming.Role = MsgRole.Error;
                        _streaming.Text = string.IsNullOrWhiteSpace(_streaming.Text)
                            ? "调用失败：" + msg
                            : _streaming.Text + "\n[中断] " + msg;

                        string hint = HintFor(msg);
                        if (hint.Length > 0)
                            _streaming.Text += "\n" + hint;
                    }
                }
                else if (p != "__CANCELLED__")
                {
                    if (_streaming != null && _streaming.Text.Length == 0)
                        _streaming.Text = p;
                }
                else
                {
                    speakable = false;
                }

                ChatLine finished = _streaming;

                if (finished != null && finished.Role == MsgRole.Assistant)
                {
                    ExtractAction(finished);

                    if (speakable)
                        SafeInvoke(OnAnswerDone, finished);
                    else
                        SafeInvoke(OnAnswerCancelled);
                }
                else if (!speakable)
                {
                    SafeInvoke(OnAnswerCancelled);
                }

                _streaming = null;
                Version++;
            }
        }

        /// <summary>把常见的接口报错翻译成一句能照着做的提示。</summary>
        private static string HintFor(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "";

            if (message.Contains("未知模型", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("模型不存在", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no available channel", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("model not found", StringComparison.OrdinalIgnoreCase))
                return "（模型名已经失效了：按 Esc 关掉面板，敲 aisettings 打开设置，把「模型」换成列表里现在有的那个）";

            if (message.Contains("未登录", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("accessToken", StringComparison.OrdinalIgnoreCase))
                return "（这是本机网关上游没登录：先登录对应的桌面端，或者把接口地址/密钥换成你自己的服务商）";

            if (message.Contains("401", StringComparison.Ordinal) ||
                message.Contains("无效的令牌", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("未提供令牌", StringComparison.OrdinalIgnoreCase))
                return "（密钥不对：敲 aisettings 重新填一遍 ApiKey）";

            return "";
        }

        /// <summary>回调里出错不能把对话流程带崩。</summary>
        private static void SafeInvoke(Action action)
        {
            try { action?.Invoke(); } catch { /* ignore */ }
        }

        private static void SafeInvoke(Action<ChatLine> action, ChatLine line)
        {
            try { action?.Invoke(line); } catch { /* ignore */ }
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
