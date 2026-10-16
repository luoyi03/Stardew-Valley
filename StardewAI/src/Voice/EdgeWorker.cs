using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace StardewAI.Voice
{
    /// <summary>
    /// 常驻的 Python 合成助手（tts_worker.py）。
    /// 一次启动，之后每句只等 edge 服务那 1 秒左右；不再每句都新起进程 import 一遍
    /// edge_tts / miniaudio（那两次进程启动本身就要 2 秒多，是"声音出来慢"的主因）。
    /// 助手直接吐 wav，mp3 解码在它进程内完成（实测只要 2ms）。
    /// </summary>
    internal static class EdgeWorker
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly object ProcGate = new();

        private static Process _proc;
        private static List<string> _candidates = new();

        /// <summary>tts_worker.py 的完整路径。</summary>
        public static string ScriptPath { get; set; } = "";

        public static string LastError { get; private set; } = "";

        /// <summary>上一句实际耗时（毫秒），写日志用。</summary>
        public static int LastMs { get; private set; }

        public static bool Available => !string.IsNullOrEmpty(ScriptPath) && File.Exists(ScriptPath);

        /// <summary>提前把进程拉起来（进游戏时调一次，第一句就不用等冷启动）。</summary>
        public static void Warmup()
        {
            if (!Available)
                return;

            Task.Run(() =>
            {
                try
                {
                    EnsureStarted(ModEntry.Config);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    ModEntry.Log?.Log("语音合成助手预热失败：" + ex.Message, LogLevel.Trace);
                }
            });
        }

        /// <summary>合成为 wav，返回文件路径。</summary>
        public static async Task<string> SynthesizeAsync(ModConfig cfg, string text, string outDir, CancellationToken ct)
        {
            if (!Available)
                throw new Exception("没找到 tts_worker.py（应在 Mods/StardewAI/ 下）");

            await Gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                Process proc = EnsureStarted(cfg);
                string outPath = Path.Combine(outDir, "tts_" + Guid.NewGuid().ToString("N") + ".wav");

                var req = new Dictionary<string, object>
                {
                    ["text_b64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                    ["out"] = outPath,
                    ["voice"] = string.IsNullOrWhiteSpace(cfg.EdgeTtsVoice) ? "zh-CN-XiaoxiaoNeural" : cfg.EdgeTtsVoice.Trim(),
                    ["rate"] = string.IsNullOrWhiteSpace(cfg.EdgeTtsRate) ? "+0%" : cfg.EdgeTtsRate.Trim()
                };

                await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(req)).ConfigureAwait(false);
                await proc.StandardInput.FlushAsync().ConfigureAwait(false);

                string reply = await ReadReplyAsync(proc, ct).ConfigureAwait(false);

                if (reply == null)
                    throw new Exception("合成助手没有回应（进程退出了？" + (string.IsNullOrWhiteSpace(LastError) ? "" : " " + LastError) + "）");

                using var doc = JsonDocument.Parse(reply);
                var root = doc.RootElement;

                if (root.TryGetProperty("ms", out var msEl) && msEl.TryGetInt32(out int ms))
                    LastMs = ms;

                bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;

                if (!ok)
                {
                    string err = root.TryGetProperty("error", out var errEl) ? errEl.ToString() : "未知错误";
                    throw new Exception(err);
                }

                if (!File.Exists(outPath) || new FileInfo(outPath).Length <= 1000)
                    throw new Exception("合成助手没写出有效的 wav（" + outPath + "）");

                LastError = "";
                return outPath;
            }
            finally
            {
                Gate.Release();
            }
        }

        public static void Shutdown()
        {
            lock (ProcGate)
            {
                if (_proc != null)
                {
                    try { if (!_proc.HasExited) _proc.StandardInput.Close(); } catch { /* ignore */ }
                    try { if (!_proc.HasExited) _proc.Kill(true); } catch { /* ignore */ }
                    try { _proc.Dispose(); } catch { /* ignore */ }
                    _proc = null;
                }
            }
        }

        // ---------------- 内部 ----------------

        private static async Task<string> ReadReplyAsync(Process proc, CancellationToken ct)
        {
            // net6.0 的 ReadLineAsync 不吃 CancellationToken，用「取消就杀进程」来打断
            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { /* ignore */ }
            });

            try
            {
                return await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Process EnsureStarted(ModConfig cfg)
        {
            lock (ProcGate)
            {
                if (_proc != null && !_proc.HasExited)
                    return _proc;

                if (_proc != null)
                {
                    try { _proc.Dispose(); } catch { /* ignore */ }
                    _proc = null;
                }

                if (_candidates.Count == 0)
                    BuildCandidates(cfg);

                string lastError = null;

                foreach (string py in _candidates)
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = py,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,

                            // 关键：不能带 BOM，否则助手第一行会解析失败
                            StandardInputEncoding = new UTF8Encoding(false),
                            StandardOutputEncoding = Encoding.UTF8
                        };

                        psi.ArgumentList.Add("-u");
                        psi.ArgumentList.Add(ScriptPath);

                        var proc = Process.Start(psi);
                        if (proc == null)
                        {
                            lastError = "无法启动 " + py;
                            continue;
                        }

                        proc.ErrorDataReceived += (_, e) =>
                        {
                            if (!string.IsNullOrWhiteSpace(e.Data))
                                LastError = e.Data.Trim();
                        };

                        proc.BeginErrorReadLine();

                        _proc = proc;
                        return proc;
                    }
                    catch (Exception ex)
                    {
                        lastError = py + "：" + ex.Message;
                    }
                }

                throw new Exception("起不了语音合成助手（" + lastError + "）");
            }
        }

        private static void BuildCandidates(ModConfig cfg)
        {
            _candidates = new List<string>();
            string cmd = (cfg?.EdgeTtsCommand ?? "").Trim();

            if (cmd.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0)
                _candidates.Add(cmd);

            _candidates.Add("python");
            _candidates.Add("py");
        }
    }
}
