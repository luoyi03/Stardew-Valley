using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace StardewAI.Voice
{
    /// <summary>
    /// 语音总管：她说话（TTS 朗读回答）+ 听你说话（按住按键录音 → 转写 → 当成你的提问发出去）。
    /// 用法：打字问她 → 她的回答会被念出来；按住 PttKey（默认 V）说话 → 松开后自动转写成文字发给她。
    /// </summary>
    internal static class VoiceAssistant
    {
        public static VoicePlayer Player { get; } = new();
        public static MicRecorder Mic { get; } = new();

        /// <summary>音频缓存目录（Mods/StardewAI/voice）。</summary>
        public static string Dir { get; private set; } = "";

        private static string _wavPath = "";

        /// <summary>由 ModEntry 注入：把识别出来的话送进对话，返回是否送出去了。</summary>
        public static Func<string, bool> SendTranscript;

        /// <summary>当前状态（「正在识别…」之类），给字幕浮层用。</summary>
        public static string Status { get; private set; } = "";

        public static string LastError { get; private set; } = "";

        private static string _pendingText;
        private static string _pendingError;
        private static volatile bool _transcribing;
        private static CancellationTokenSource _sttCts;

        public static bool Enabled => ModEntry.Config?.VoiceEnabled == true;

        public static bool Speaks => Enabled && ModEntry.Config.SpeakReplies;

        /// <summary>下一条回答不要念（主动搭话按配置静音时用）。</summary>
        public static bool MuteNextAnswer;

        public static void Init(string modDir)
        {
            try
            {
                Dir = Path.Combine(modDir, "voice");
                Directory.CreateDirectory(Dir);
                _wavPath = Path.Combine(Dir, "mic.wav");
                Player.OutputDir = Dir;
                Cleanup();
            }
            catch (Exception ex)
            {
                LastError = "创建语音目录失败：" + ex.Message;
                ModEntry.Log?.Log(LastError, LogLevel.Warn);
            }
        }

        /// <summary>删掉一天前的缓存音频（录音和语音片段都只临时用一次）。</summary>
        private static void Cleanup()
        {
            try
            {
                foreach (string file in Directory.GetFiles(Dir))
                {
                    try
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                            File.Delete(file);
                    }
                    catch
                    {
                        // 正在被占用就留着
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }

        public static void Shutdown()
        {
            try { _sttCts?.Cancel(); } catch { /* ignore */ }
            Mic.Cancel();
            Player.Stop();
            EdgeWorker.Shutdown();
        }

        /// <summary>
        /// 给合成接口做一次预热。
        /// Fish 免费模型第一次调用可能要等几十秒（模型冷启动），提前念个字把它叫醒，
        /// 免得你第一句就卡住。只对 fish 后端做（其它后端没这个问题，还省得白花钱）。
        /// </summary>
        public static void WarmUpTts()
        {
            var cfg = ModEntry.Config;

            if (cfg == null || !cfg.VoiceEnabled || string.IsNullOrEmpty(Dir))
                return;

            if (!string.Equals(cfg.TtsBackend?.Trim(), "fish", StringComparison.OrdinalIgnoreCase))
                return;

            Task.Run(async () =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    string file = await TtsClient.SynthesizeAsync(cfg, "你好", Dir, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();

                    ModEntry.Log?.Log("语音预热完成（" + sw.ElapsedMilliseconds + " ms）：" + (file ?? "无文件"), LogLevel.Trace);

                    try
                    {
                        if (!string.IsNullOrEmpty(file) && File.Exists(file))
                            File.Delete(file);
                    }
                    catch
                    {
                        // 忽略
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Log?.Log("语音预热失败（不影响后面使用）：" + ex.Message, LogLevel.Trace);
                }
            });
        }

        /// <summary>主线程每 tick 调用。</summary>
        public static void Tick()
        {
            Player.Poll();

            // 收到识别结果 / 出错，在 UI 线程发出去
            if (!_transcribing && (_pendingText != null || _pendingError != null))
            {
                string text = _pendingText;
                string err = _pendingError;
                _pendingText = null;
                _pendingError = null;
                Status = "";

                if (err != null)
                    Notify("语音识别失败：" + err);
                else if (text != null)
                {
                    bool ok = SendTranscript?.Invoke(text) == true;
                    if (!ok)
                        Notify("没有找到可以对话的对象（先去和某个 NPC 说话，或按 " + (ModEntry.Config?.PttKey ?? "V") + " 说话会自动打开伴侣面板）");
                }
            }

            // 录音超时上限
            if (Mic.Active)
            {
                int max = Math.Max(5, ModEntry.Config?.SttMaxSeconds ?? 60);
                if (Mic.Seconds >= max)
                {
                    ModEntry.Log?.Log("录音已达 " + max + " 秒上限，自动结束。", LogLevel.Info);
                    StopRecordingAndSend();
                }
            }
        }

        // ---------------- 她说话（朗读回答） ----------------

        public static void OnAnswerStarted(string speaker)
        {
            if (!Speaks)
                return;

            Player.Begin(speaker);
        }

        public static void OnAnswerDelta(ChatLine line, string speaker)
        {
            if (MuteNextAnswer || !Speaks)
                return;

            Player.Feed(line, speaker, MaxChars());
        }

        public static void OnAnswerDone(ChatLine line, string speaker)
        {
            bool mute = MuteNextAnswer;
            MuteNextAnswer = false;

            if (mute || !Speaks)
                return;

            Player.Finish(line, speaker, MaxChars());
        }

        public static void OnAnswerCancelled()
        {
            MuteNextAnswer = false;
            Player.Stop();
        }

        /// <summary>打断当前朗读。</summary>
        public static void Interrupt() => Player.Stop();

        /// <summary>试听一段话（控制台 aitts 用）。</summary>
        public static void SpeakTest(string text)
        {
            var cfg = ModEntry.Config;
            if (cfg == null || string.IsNullOrWhiteSpace(text))
                return;

            if (string.Equals(cfg.TtsBackend?.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                Notify("语音合成已关闭（TtsBackend = none）");
                return;
            }

            string name = string.IsNullOrWhiteSpace(cfg.CompanionName) ? "AI" : cfg.CompanionName.Trim();
            Player.Begin(name);
            Player.Say(text, name, MaxChars());
        }

        /// <summary>给系统提示补一句「回答会被念出来」的写作要求。</summary>
        public static string DecorateSystem(string system)
        {
            var cfg = ModEntry.Config;

            if (cfg == null || !cfg.VoiceEnabled || !cfg.SpeakReplies || !cfg.VoiceShortReplies)
                return system;

            return (system ?? "") +
                   "\n【语音朗读】你的回答会被念出来给玩家听：请用口语短句，尽量 3 句以内说完，" +
                   "不要用列表、编号、Markdown、表情符号和括号注解，总长度控制在 120 字以内。\n";
        }

        private static int MaxChars()
        {
            int v = ModEntry.Config?.SpeakMaxChars ?? 400;
            return v <= 0 ? 0 : Math.Clamp(v, 30, 4000);
        }

        // ---------------- 听你说话 ----------------

        public static void StartRecording()
        {
            var cfg = ModEntry.Config;
            if (cfg == null || !cfg.VoiceEnabled || !cfg.SttEnabled)
                return;

            Interrupt();      // 先让她闭嘴，免得录到自己的声音
            Status = "";
            LastError = "";

            if (!Mic.Start(Math.Clamp(cfg.SttSampleRate, 8000, 48000)))
            {
                LastError = Mic.LastError;
                Notify(Mic.LastError);
            }
            else
            {
                ModEntry.Log?.Log("开始录音，格式：" + MciAudio.LastRecordFormat, LogLevel.Trace);
            }
        }

        public static void StopRecordingAndSend()
        {
            if (!Mic.Active)
                return;

            var cfg = ModEntry.Config;
            string wav = Mic.Stop(_wavPath, Math.Max(0.2f, cfg?.SttMinSeconds ?? 0.6f));

            if (wav == null)
            {
                Status = "";

                if (!string.IsNullOrWhiteSpace(Mic.LastError))
                    Notify(Mic.LastError);

                return;
            }

            if (cfg == null || !cfg.SttEnabled)
            {
                Status = "";
                return;
            }

            _transcribing = true;
            Status = "正在识别你刚才说的话…";

            try { _sttCts?.Cancel(); } catch { /* ignore */ }
            _sttCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, cfg.TimeoutSeconds)));
            var ct = _sttCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    // 录音格式由声卡决定（可能是 44.1k 立体声），先统一转成 16k 单声道再识别：
                    // 云端 whisper 更省流量，Windows 自带识别引擎也只吃常见 PCM 格式
                    string src = wav;

                    try
                    {
                        string conv = await AudioConvert.ToAsrWavAsync(cfg, wav, ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(conv))
                            src = conv;
                    }
                    catch
                    {
                        // 转不了就用原文件，让识别后端自己处理
                    }

                    string text = await SttClient.TranscribeAsync(cfg, src, ct).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        ModEntry.Log?.Log("语音识别结果为空（" + Path.GetFileName(src) + "，" + new FileInfo(src).Length + " 字节）", LogLevel.Trace);
                        _pendingError = "没听清（识别结果为空）";
                    }
                    else
                    {
                        ModEntry.Log?.Log("语音识别结果：" + text.Trim(), LogLevel.Trace);
                        _pendingText = text.Trim();
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.Log?.Log("语音识别失败：" + ex.Message, LogLevel.Warn);
                    _pendingError = ex.Message ?? ex.GetType().Name;
                }
                finally
                {
                    _transcribing = false;
                }
            });
        }

        public static void CancelRecording() => Mic.Cancel();

        private static void Notify(string message)
        {
            try { Game1.addHUDMessage(new HUDMessage("语音：" + message, 3)); }
            catch { /* 忽略 */ }
        }

        /// <summary>SMAPI 控制台里打印一段状态。</summary>
        public static string Describe()
        {
            var cfg = ModEntry.Config;
            if (cfg == null)
                return "配置没读到";

            return "语音：" + (cfg.VoiceEnabled ? "开" : "关") +
                   "；朗读回答：" + (cfg.SpeakReplies ? "开" : "关") +
                   "；合成后端：" + cfg.TtsBackend + (cfg.TtsBackend == "edge" ? "（" + cfg.EdgeTtsVoice + "）" : "（" + cfg.TtsModel + " / " + cfg.TtsVoice + "）") +
                   "；按住说话：" + (cfg.SttEnabled ? cfg.PttKey : "关") +
                   "；识别模型：" + cfg.SttModel +
                   "；缓存目录：" + Dir +
                   (string.IsNullOrWhiteSpace(LastError) ? "" : "；最近错误：" + LastError);
        }
    }
}
