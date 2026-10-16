using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace StardewAI.Voice
{
    /// <summary>
    /// 文字转语音。默认走「OpenAI 兼容」的 /audio/speech，
    /// 也可以改用本机 edge-tts（免费、免密钥、中文自然）。
    /// </summary>
    internal static class TtsClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

        /// <summary>合成一段文字，返回音频文件路径（失败抛异常）。</summary>
        public static async Task<string> SynthesizeAsync(ModConfig cfg, string text, string outDir, CancellationToken ct)
        {
            string file = await SynthesizeRawAsync(cfg, text, outDir, ct).ConfigureAwait(false);

            // mp3 在 Windows 上只能靠 MCI 的 MPEGVideo（DirectShow + COM）播，而游戏主线程不是 STA 单元，
            // 那个设备在游戏进程里起不来；转成 wav 就能交给游戏自己的音频引擎，所以默认转一下。
            if (cfg.TtsConvertToWav && !string.IsNullOrEmpty(file) &&
                file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                string wav = await AudioConvert.ToWavAsync(cfg, file, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(wav))
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                    return wav;
                }
            }

            return file;
        }

        private static async Task<string> SynthesizeRawAsync(ModConfig cfg, string text, string outDir, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new Exception("要念的文字是空的");

            if (string.IsNullOrWhiteSpace(outDir))
                outDir = Path.Combine(Path.GetTempPath(), "StardewAI_voice");

            Directory.CreateDirectory(outDir);

            switch ((cfg?.TtsBackend ?? "").Trim().ToLowerInvariant())
            {
                case "none":
                    throw new Exception("语音合成已关闭（TtsBackend = none）");

                case "sapi":
                case "windows":
                    return await SapiAsync(cfg, text, outDir, ct).ConfigureAwait(false);

                case "fish":
                case "fish-audio":
                case "fishaudio":
                    try
                    {
                        return await FishAsync(cfg, text, outDir, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (cfg.TtsFallbackSapi && ex is not OperationCanceledException)
                    {
                        ModEntry.Log?.Log("Fish 合成失败（" + ex.Message + "），这一句改用 Windows 自带语音。", LogLevel.Warn);
                        return await SapiAsync(cfg, text, outDir, ct).ConfigureAwait(false);
                    }

                case "edge":
                case "edge-tts":
                case "edgetts":
                    // 先走常驻助手：省掉每句两次 Python 冷启动（约 2.5 秒）
                    if (cfg.EdgeUseWorker && EdgeWorker.Available)
                    {
                        try
                        {
                            return await EdgeWorker.SynthesizeAsync(cfg, text, outDir, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            ModEntry.Log?.Log("常驻合成助手不可用（" + ex.Message + "），改用命令行 edge-tts。", LogLevel.Warn);
                        }
                    }

                    try
                    {
                        return await EdgeAsync(cfg, text, outDir, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (cfg.TtsFallbackSapi && ex is not OperationCanceledException)
                    {
                        // edge-tts 是在线合成，断网/被限流时别让她哑巴
                        ModEntry.Log?.Log("edge-tts 合成失败（" + ex.Message + "），这一句改用 Windows 自带语音。", LogLevel.Warn);
                        return await SapiAsync(cfg, text, outDir, ct).ConfigureAwait(false);
                    }

                default:
                    try
                    {
                        return await OpenAiAsync(cfg, text, outDir, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (cfg.TtsFallbackSapi && RouteMissing(ex.Message))
                    {
                        // 接口没开 /audio/speech（很多中转站都是这样）→ 直接用 Windows 自带语音顶上
                        ModEntry.Log?.Log("语音接口不支持 /audio/speech（" + ex.Message + "），已自动改用 Windows 自带语音。", LogLevel.Warn);
                        return await SapiAsync(cfg, text, outDir, ct).ConfigureAwait(false);
                    }
            }
        }

        /// <summary>看起来是「这个接口没有这条路由」而不是密钥/网络问题。</summary>
        private static bool RouteMissing(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            return message.Contains("404", StringComparison.Ordinal)
                   || message.Contains("405", StringComparison.Ordinal)
                   || message.Contains("no route", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("数据太小", StringComparison.Ordinal);
        }

        // ---------------- Fish Audio（api.fish.audio）----------------

        /// <summary>
        /// Fish Audio 的 /v1/tts：模型名放在请求头、body 是 text/format/prosody，
        /// 返回原始音频（要 wav 就直接给 wav，游戏音频引擎能播）。
        /// 免费模型 s2.1-pro-free（官方定价 $0 / 百万字节）。
        /// </summary>
        private static async Task<string> FishAsync(ModConfig cfg, string text, string outDir, CancellationToken ct)
        {
            string root = (string.IsNullOrWhiteSpace(cfg.FishBaseUrl) ? cfg.EffectiveTtsUrl : cfg.FishBaseUrl).Trim().TrimEnd('/');
            if (root.Length == 0)
                throw new Exception("没填 Fish 的地址（FishBaseUrl 填 https://api.fish.audio/v1）");

            string url = root.EndsWith("/tts", StringComparison.OrdinalIgnoreCase) ? root : root + "/tts";

            var payload = new Dictionary<string, object>
            {
                ["text"] = text,
                ["format"] = "wav",
                ["latency"] = string.IsNullOrWhiteSpace(cfg.FishLatency) ? "balanced" : cfg.FishLatency.Trim(),
                ["prosody"] = new Dictionary<string, object>
                {
                    ["speed"] = Math.Clamp(cfg.TtsSpeed <= 0f ? 1f : cfg.TtsSpeed, 0.5f, 2f)
                }
            };

            // 想用自己的声音：把参考音频的 id 填进来（音色克隆）
            if (!string.IsNullOrWhiteSpace(cfg.FishReferenceId))
                payload["reference_id"] = cfg.FishReferenceId.Trim();

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("model",
                string.IsNullOrWhiteSpace(cfg.FishModel) ? "s2.1-pro-free" : cfg.FishModel.Trim());

            string key = (string.IsNullOrWhiteSpace(cfg.FishApiKey) ? cfg.EffectiveTtsKey : cfg.FishApiKey).Trim();
            if (key.Length > 0)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
            {
                throw new Exception("网络错误：" + ex.Message + "（地址：" + url + "）");
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string body = "";
                    try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }

                    string tip = key.Length == 0
                        ? "（还没填 Fish 的 API Key：设置面板最后一段的「Fish 密钥」那一行）"
                        : (int)resp.StatusCode == 401 ? "（密钥不对）" : "";

                    throw new Exception("Fish 合成失败 HTTP " + (int)resp.StatusCode + " " + Short(body) + tip);
                }

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes == null || bytes.Length < 200)
                    throw new Exception("Fish 返回的数据太小（" + (bytes?.Length ?? 0) + " 字节）");

                string path = Path.Combine(outDir, "tts_" + Guid.NewGuid().ToString("N") + ".wav");
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                return path;
            }
        }

        // ---------------- Windows 自带语音（SAPI，离线、免安装） ----------------

        /// <summary>
        /// 借 Windows 自带的 System.Speech 合成：起一个 powershell 子进程，把中文文本用 base64 传进去，
        /// 结果写成 wav。不用装 Python，也不用密钥，代价是每句要多等一次 powershell 启动（约 1 秒）。
        /// </summary>
        private static async Task<string> SapiAsync(ModConfig cfg, string text, string outDir, CancellationToken ct)
        {
            string path = Path.Combine(outDir, "tts_" + Guid.NewGuid().ToString("N") + ".wav");
            string voice = (cfg.SapiVoice ?? "").Trim();

            int rate = (int)Math.Round((Math.Clamp(cfg.TtsSpeed <= 0f ? 1f : cfg.TtsSpeed, 0.5f, 2f) - 1f) * 10);
            string b64Text = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

            var sb = new StringBuilder();
            sb.Append("Add-Type -AssemblyName System.Speech;");
            sb.Append("$sp = New-Object System.Speech.Synthesis.SpeechSynthesizer;");
            sb.Append("$sp.Rate = ").Append(Math.Clamp(rate, -10, 10)).Append(';');

            if (voice.Length > 0)
                sb.Append("try { $sp.SelectVoice('").Append(voice.Replace("'", "''")).Append("') } catch { };");

            sb.Append("$sp.SetOutputToWaveFile('").Append(path.Replace("'", "''")).Append("');");
            sb.Append("$sp.Speak([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('").Append(b64Text).Append("')));");
            sb.Append("$sp.Dispose();");

            // -EncodedCommand 收的是 UTF-16LE 的 base64，这样中文不会在命令行编码上出问题
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
            string lastError = null;

            foreach (string exe in new[] { "powershell.exe", "pwsh.exe" })
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // 忽略
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(encoded);

                try
                {
                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        lastError = "无法启动 " + exe;
                        continue;
                    }

                    string err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                    if (proc.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 2000)
                        return path;

                    lastError = exe + " 退出码 " + proc.ExitCode + "：" + Short(err);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = exe + "：" + ex.Message;
                }
            }

            throw new Exception("Windows 自带语音合成失败：" + lastError);
        }

        // ---------------- OpenAI 兼容的 /audio/speech ----------------

        private static async Task<string> OpenAiAsync(ModConfig cfg, string text, string outDir, CancellationToken ct)
        {
            string root = (cfg.EffectiveTtsUrl ?? "").Trim().TrimEnd('/');
            if (root.Length == 0)
                throw new Exception("没有填写语音合成接口地址（TtsBaseUrl 或 BaseUrl）");

            string url = root.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase) ? root : root + "/audio/speech";

            string format = string.IsNullOrWhiteSpace(cfg.TtsFormat) ? "mp3" : cfg.TtsFormat.Trim().TrimStart('.').ToLowerInvariant();
            if (format is not ("wav" or "mp3"))
                format = "mp3";

            var payload = new Dictionary<string, object>
            {
                ["model"] = string.IsNullOrWhiteSpace(cfg.TtsModel) ? "tts-1" : cfg.TtsModel.Trim(),
                ["input"] = text,
                ["voice"] = string.IsNullOrWhiteSpace(cfg.TtsVoice) ? "nova" : cfg.TtsVoice.Trim(),
                ["response_format"] = format,
                ["speed"] = Math.Clamp(cfg.TtsSpeed <= 0f ? 1f : cfg.TtsSpeed, 0.25f, 4f)
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            string key = (cfg.EffectiveTtsKey ?? "").Trim();
            if (key.Length > 0)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
            {
                throw new Exception("网络错误：" + ex.Message + "（地址：" + url + "）");
            }

            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string body = "";
                    try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }
                    throw new Exception("语音合成失败 HTTP " + (int)resp.StatusCode + " " + Short(body));
                }

                byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (bytes == null || bytes.Length < 200)
                    throw new Exception("语音合成返回的数据太小（" + (bytes?.Length ?? 0) + " 字节）：这个接口可能不支持 /audio/speech");

                string path = Path.Combine(outDir, "tts_" + Guid.NewGuid().ToString("N") + "." + (format == "wav" ? "wav" : "mp3"));
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
                return path;
            }
        }

        // ---------------- 本机 edge-tts ----------------

        private static async Task<string> EdgeAsync(ModConfig cfg, string text, string outDir, CancellationToken ct)
        {
            string path = Path.Combine(outDir, "tts_" + Guid.NewGuid().ToString("N") + ".mp3");
            string input = Path.Combine(outDir, "edge_input.txt");
            await File.WriteAllTextAsync(input, text, new UTF8Encoding(false), ct).ConfigureAwait(false);

            string voice = string.IsNullOrWhiteSpace(cfg.EdgeTtsVoice) ? "zh-CN-XiaoxiaoNeural" : cfg.EdgeTtsVoice.Trim();
            string rate = string.IsNullOrWhiteSpace(cfg.EdgeTtsRate) ? "+0%" : cfg.EdgeTtsRate.Trim();
            string cmd = string.IsNullOrWhiteSpace(cfg.EdgeTtsCommand) ? "edge-tts" : cfg.EdgeTtsCommand.Trim();

            var attempts = new List<string[]>();

            // 用户填的是 python.exe / py.exe 时按「解释器 -m edge_tts」调，填的是 edge-tts 命令就直接调
            bool isPython = cmd.IndexOf("python", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            cmd.IndexOf("py.exe", StringComparison.OrdinalIgnoreCase) >= 0;

            attempts.Add(isPython ? new[] { cmd, "-m", "edge_tts" } : new[] { cmd });
            attempts.Add(new[] { "edge-tts" });
            attempts.Add(new[] { "python", "-m", "edge_tts" });
            attempts.Add(new[] { "py", "-m", "edge_tts" });

            string lastError = null;

            foreach (string[] argv in attempts)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // 忽略
                }

                var psi = new ProcessStartInfo
                {
                    FileName = argv[0],
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                for (int i = 1; i < argv.Length; i++)
                    psi.ArgumentList.Add(argv[i]);

                psi.ArgumentList.Add("--file");
                psi.ArgumentList.Add(input);
                psi.ArgumentList.Add("--voice");
                psi.ArgumentList.Add(voice);
                psi.ArgumentList.Add("--rate");
                psi.ArgumentList.Add(rate);
                psi.ArgumentList.Add("--write-media");
                psi.ArgumentList.Add(path);

                try
                {
                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        lastError = "无法启动 " + argv[0];
                        continue;
                    }

                    string err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                    if (proc.ExitCode == 0 && File.Exists(path) && new FileInfo(path).Length > 200)
                        return path;

                    lastError = argv[0] + " 退出码 " + proc.ExitCode + "：" + Short(err);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = argv[0] + "：" + ex.Message;
                }
            }

            throw new Exception("edge-tts 调用失败（先执行 pip install edge-tts）：" + lastError);
        }

        private static string Short(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 200 ? s : s.Substring(0, 200) + "...";
        }
    }
}
