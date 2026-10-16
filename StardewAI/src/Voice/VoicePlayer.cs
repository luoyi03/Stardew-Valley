using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace StardewAI.Voice
{
    /// <summary>
    /// 朗读队列：边流式回答边把「已经说完的句子」送去合成，合成好一句就播一句，
    /// 后面的句子会提前合成好排队等着，听起来就接近连续说话。
    /// 合成在后台线程做，播放由主线程每 tick 驱动（Poll）。
    /// </summary>
    internal sealed class VoicePlayer
    {
        private sealed class Job
        {
            public string Text;
            public string Speaker;
        }

        private sealed class Ready
        {
            public string Text;
            public string File;
            public string Speaker;
        }

        private readonly Queue<Job> _jobs = new();
        private readonly Queue<Ready> _ready = new();
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
        private readonly object _gate = new();

        private CancellationTokenSource _cts;
        private Task _worker;

        private ChatLine _line;
        private int _offset;
        private int _spokenChars;
        private string _speaker = "";
        private Ready _playing;

        /// <summary>音频缓存目录。</summary>
        public string OutputDir { get; set; } = "";

        /// <summary>当前正在念的句子（字幕浮层用）。</summary>
        public string Subtitle { get; private set; } = "";

        /// <summary>当前正在念的人（NPC 名 / 伴侣名）。</summary>
        public string Speaker { get; private set; } = "";

        public string LastError { get; private set; } = "";

        public bool Speaking => _playing != null;

        public bool Busy
        {
            get
            {
                lock (_gate)
                    return _playing != null || _jobs.Count > 0 || _ready.Count > 0;
            }
        }

        /// <summary>新一轮回答开始：先打断上一段朗读。</summary>
        public void Begin(string speaker)
        {
            Stop();
            _speaker = speaker ?? "";
        }

        /// <summary>流式增量：只把「已经说完的句子」入队，剩下的半句等下次。</summary>
        public void Feed(ChatLine line, string speaker, int maxChars)
        {
            if (line == null)
                return;

            Attach(line, speaker);

            string tail = Tail(line.Text ?? "");
            int cut = SpeechText.LastSentenceEnd(tail);
            if (cut <= 0)
                return;

            _offset += cut;
            Enqueue(tail.Substring(0, cut), speaker, maxChars);
        }

        /// <summary>回答结束：把剩下的话也念掉。</summary>
        public void Finish(ChatLine line, string speaker, int maxChars)
        {
            if (line == null)
                return;

            Attach(line, speaker);

            string text = line.Text ?? "";
            Enqueue(Tail(text), speaker, maxChars);
            _offset = text.Length;
            _line = null;
        }

        /// <summary>直接念一段话（试听用）。</summary>
        public void Say(string text, string speaker, int maxChars)
        {
            if (!Busy)
            {
                _spokenChars = 0;
                _speaker = speaker ?? "";
            }

            Enqueue(text, speaker, maxChars);
        }

        /// <summary>立刻闭嘴并清空队列（打断）。</summary>
        public void Stop()
        {
            var old = _cts;
            _cts = null;

            // 关键：把 _worker 也扔掉。旧线程刚被取消时 IsCompleted 可能还是 false，
            // 留着它会让下一次 EnsureWorker 以为"线程还活着"而不重启，任务就卡死在队列里了。
            _worker = null;

            try { old?.Cancel(); } catch { /* ignore */ }

            AudioOut.Stop();

            lock (_gate)
            {
                _jobs.Clear();

                while (_ready.Count > 0)
                    TryDelete(_ready.Dequeue().File);
            }

            if (_playing != null)
            {
                TryDelete(_playing.File);
                _playing = null;
            }

            Subtitle = "";
            Speaker = "";
            _line = null;
            _offset = 0;
            _spokenChars = 0;
        }

        /// <summary>主线程每 tick 调用：当前这句播完没有？播完了就播下一句。</summary>
        public void Poll()
        {
            if (_playing != null && !AudioOut.IsPlaying)
            {
                AudioOut.Stop();
                ModEntry.Log?.Log("语音播放结束（" + Path.GetFileName(_playing.File) + "）", LogLevel.Trace);
                TryDelete(_playing.File);
                _playing = null;
                Subtitle = "";
            }

            if (_playing != null)
                return;

            // 看门狗：队列里还有没合成的句子，但合成线程不在跑（被打断过或退出了），重新拉起来
            bool stuck;

            lock (_gate)
                stuck = _jobs.Count > 0;

            if (stuck && (_worker == null || _worker.IsCompleted))
            {
                EnsureWorker();

                try { _signal.Release(); }
                catch { /* 信号量满了无所谓 */ }
            }

            Ready next = null;

            lock (_gate)
            {
                if (_ready.Count > 0)
                    next = _ready.Dequeue();
            }

            if (next == null)
                return;

            if (!AudioOut.Play(next.File, ModEntry.Config?.TtsVolume ?? 100))
            {
                LastError = "播放失败：" + AudioOut.LastError;
                ModEntry.Log?.Log("语音播放失败：" + LastError + "（" + Path.GetFileName(next.File) + "）", LogLevel.Warn);
                TryDelete(next.File);
                return;
            }

            _playing = next;
            Subtitle = next.Text;
            Speaker = string.IsNullOrWhiteSpace(next.Speaker) ? _speaker : next.Speaker;

            ModEntry.Log?.Log("开始播放语音：「" + next.Text + "」通道=" + AudioOut.Backend +
                              "（" + Path.GetFileName(next.File) + "）", LogLevel.Trace);
        }

        // ---------------- 内部 ----------------

        private void Attach(ChatLine line, string speaker)
        {
            if (ReferenceEquals(line, _line))
                return;

            // 没收到 Begin（比如面板刚打开、或历史被改写）：这一条从头开始读
            Stop();
            _line = line;
            _offset = 0;
            _spokenChars = 0;
            _speaker = speaker ?? "";
        }

        private string Tail(string text)
        {
            // 回答末尾会被抽掉 [[GIVE:...]]，长度变短，这里不能回退到 0，否则会重复念一遍
            if (_offset > text.Length)
                _offset = text.Length;

            return text.Substring(_offset);
        }

        private void Enqueue(string text, string speaker, int maxChars)
        {
            string clean = SpeechText.Clean(text);
            if (clean.Length == 0)
                return;

            int cap = maxChars > 0 ? maxChars : int.MaxValue;

            foreach (string piece in SpeechText.Split(clean))
            {
                if (_spokenChars >= cap)
                    return;

                string s = piece;

                if (_spokenChars + s.Length > cap)
                    s = s.Substring(0, Math.Max(1, cap - _spokenChars));

                _spokenChars += s.Length;
                Push(new Job { Text = s, Speaker = speaker ?? "" });
            }
        }

        private void Push(Job job)
        {
            lock (_gate)
                _jobs.Enqueue(job);

            EnsureWorker();

            try { _signal.Release(); }
            catch { /* 信号量满了也无所谓 */ }
        }

        private void EnsureWorker()
        {
            if (_worker != null && !_worker.IsCompleted)
                return;

            // 被取消过的 CTS 不能复用，否则新线程一起步就退出
            if (_cts == null || _cts.IsCancellationRequested)
                _cts = new CancellationTokenSource();

            var ct = _cts.Token;
            _worker = Task.Run(() => WorkerAsync(ct));
        }

        private async Task WorkerAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await _signal.WaitAsync(ct).ConfigureAwait(false); }
                catch { return; }

                while (!ct.IsCancellationRequested)
                {
                    Job job;

                    lock (_gate)
                    {
                        if (_jobs.Count == 0)
                            break;

                        job = _jobs.Dequeue();
                    }

                    try
                    {
                        var cfg = ModEntry.Config;
                        if (cfg == null)
                            continue;

                        string file = await TtsClient.SynthesizeAsync(cfg, job.Text, OutputDir, ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(file))
                            continue;

                        long size = 0;

                        try { size = new FileInfo(file).Length; } catch { /* ignore */ }

                        ModEntry.Log?.Log("语音合成完成：「" + job.Text + "」→ " + Path.GetFileName(file) + "（" + size + " 字节）", LogLevel.Trace);

                        lock (_gate)
                            _ready.Enqueue(new Ready { Text = job.Text, Speaker = job.Speaker, File = file });
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message ?? ex.GetType().Name;
                        ModEntry.Log?.Log("语音合成失败：" + LastError, LogLevel.Warn);
                    }
                }
            }
        }

        private static void TryDelete(string file)
        {
            if (string.IsNullOrEmpty(file))
                return;

            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);

                    return;
                }
                catch
                {
                    Thread.Sleep(30);
                }
            }
        }
    }
}
