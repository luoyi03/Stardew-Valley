using System;
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
    /// 语音转文字：默认先走「OpenAI 兼容」的 /audio/transcriptions（whisper-1、SenseVoice 等），
    /// 那家接口没开的话自动退回 Windows 自带的离线识别（System.Speech，免装免密钥）。
    /// </summary>
    internal static class SttClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

        /// <summary>把一段录音转成文字。</summary>
        public static async Task<string> TranscribeAsync(ModConfig cfg, string wavPath, CancellationToken ct)
        {
            if (!File.Exists(wavPath))
                throw new Exception("录音文件不见了：" + wavPath);

            switch (NormalizeBackend(cfg.SttBackend))
            {
                case "none":
                    throw new Exception("语音识别已关闭（SttBackend = none）");

                case "sapi":
                    return await SapiAsync(cfg, wavPath, ct).ConfigureAwait(false);

                default:
                    try
                    {
                        return await ApiAsync(cfg, wavPath, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (NormalizeBackend(cfg.SttBackend) == "auto" && RouteMissing(ex.Message))
                    {
                        ModEntry.Log?.Log("语音接口不支持 /audio/transcriptions（" + ex.Message + "），已自动改用 Windows 自带离线识别。", LogLevel.Warn);
                        return await SapiAsync(cfg, wavPath, ct).ConfigureAwait(false);
                    }
            }
        }

        private static string NormalizeBackend(string raw)
        {
            return (raw ?? "").Trim().ToLowerInvariant() switch
            {
                "none" => "none",
                "sapi" or "windows" => "sapi",
                "api" or "openai" => "api",
                _ => "auto"
            };
        }

        private static bool RouteMissing(string message)
        {
            if (string.IsNullOrEmpty(message))
                return false;

            return message.Contains("404", StringComparison.Ordinal)
                   || message.Contains("405", StringComparison.Ordinal)
                   || message.Contains("no route", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("not found", StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- Windows 自带离线识别（System.Speech） ----------------

        /// <summary>
        /// 用 Windows 自带的听写引擎识别（中文系统里通常是 MS-2052-80-DESK）。
        /// 免安装、免密钥、离线，代价是准确率不如 whisper（没有标点、长句容易听歪）。
        /// </summary>
        private static async Task<string> SapiAsync(ModConfig cfg, string wavPath, CancellationToken ct)
        {
            string lang = (cfg.SttLanguage ?? "").Trim();
            if (lang.Length == 0)
                lang = "zh";

            var sb = new StringBuilder();
            sb.Append("Add-Type -AssemblyName System.Speech;");
            sb.Append("$recs = [System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers();");
            sb.Append("$info = $recs | Where-Object { $_.Culture.Name -like '").Append(lang.Replace("'", "''")).Append("*' } | Select-Object -First 1;");
            sb.Append("if (-not $info) { $info = $recs | Select-Object -First 1 };");
            sb.Append("if (-not $info) { exit 3 };");
            sb.Append("$rec = New-Object System.Speech.Recognition.SpeechRecognitionEngine $info;");
            sb.Append("$rec.SetInputToWaveFile('").Append(wavPath.Replace("'", "''")).Append("');");
            sb.Append("$rec.LoadGrammar((New-Object System.Speech.Recognition.DictationGrammar));");
            sb.Append("$t = $rec.Recognize([TimeSpan]::FromSeconds(30));");
            sb.Append("if ($t) { [Console]::Out.Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($t.Text))) };");
            sb.Append("$rec.Dispose();");

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
            string lastError = null;
            bool noEngine = false;

            foreach (string exe in new[] { "powershell.exe", "pwsh.exe" })
            {
                ct.ThrowIfCancellationRequested();

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
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

                    string outText = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                    await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    await proc.WaitForExitAsync(ct).ConfigureAwait(false);

                    if (proc.ExitCode == 3)
                    {
                        noEngine = true;
                        continue;
                    }

                    if (proc.ExitCode != 0)
                    {
                        lastError = exe + " 退出码 " + proc.ExitCode;
                        continue;
                    }

                    string text = Decode(outText);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        lastError = "没听清（Windows 离线识别没给出结果）";
                        continue;
                    }

                    return text.Trim();
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

            if (noEngine)
                throw new Exception("这台机器没装中文语音识别引擎：Windows 设置 → 时间和语言 → 语言和区域 → 中文 → 语言选项 → 补装「语音识别」");

            throw new Exception("Windows 自带离线识别失败：" + lastError);
        }

        /// <summary>子进程把识别结果用 base64 传回来（避开控制台编码问题）。</summary>
        private static string Decode(string outText)
        {
            string b64 = (outText ?? "").Trim();
            if (b64.Length == 0)
                return "";

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch
            {
                return b64;
            }
        }

        // ---------------- OpenAI 兼容的 /audio/transcriptions ----------------

        private static async Task<string> ApiAsync(ModConfig cfg, string wavPath, CancellationToken ct)
        {
            string root = (cfg.EffectiveSttUrl ?? "").Trim().TrimEnd('/');
            if (root.Length == 0)
                throw new Exception("没有填写语音识别接口地址（SttBaseUrl 或 BaseUrl）");

            string url = root.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase)
                ? root
                : root + "/audio/transcriptions";

            string model = string.IsNullOrWhiteSpace(cfg.SttModel) ? "whisper-1" : cfg.SttModel.Trim();

            byte[] bytes = await File.ReadAllBytesAsync(wavPath, ct).ConfigureAwait(false);

            using var form = new MultipartFormDataContent();

            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            form.Add(new StringContent(model, Encoding.UTF8), "model");
            form.Add(new StringContent("json", Encoding.UTF8), "response_format");

            if (!string.IsNullOrWhiteSpace(cfg.SttLanguage))
                form.Add(new StringContent(cfg.SttLanguage.Trim(), Encoding.UTF8), "language");

            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };

            string key = (cfg.EffectiveSttKey ?? "").Trim();
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
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }

                if (!resp.IsSuccessStatusCode)
                {
                    string tip = "";

                    if ((int)resp.StatusCode is 404 or 405 || body.Contains("no route", StringComparison.OrdinalIgnoreCase))
                        tip = "（这家接口没有 /audio/transcriptions，语音识别用不了；换一家支持 whisper 的，或把 SttBaseUrl 指到别家）";
                    else if ((int)resp.StatusCode == 401)
                        tip = "（密钥不对或没权限）";

                    throw new Exception("语音识别失败 HTTP " + (int)resp.StatusCode + " " + Short(body) + tip);
                }

                string text = ExtractText(body);
                if (string.IsNullOrWhiteSpace(text))
                    throw new Exception("没听清（识别结果为空）" + (string.IsNullOrWhiteSpace(body) ? "" : "：" + Short(body)));

                return text.Trim();
            }
        }

        private static string ExtractText(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return "";

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                    return root.GetString();

                foreach (string name in new[] { "text", "result", "transcription", "content" })
                {
                    if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                        return el.GetString();
                }

                // 有的网关套一层 data / result
                foreach (string name in new[] { "data", "output" })
                {
                    if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object)
                    {
                        foreach (string inner in new[] { "text", "transcription", "content" })
                        {
                            if (el.TryGetProperty(inner, out var t) && t.ValueKind == JsonValueKind.String)
                                return t.GetString();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // 不是 JSON：有的接口直接回纯文本
                return body.Trim();
            }

            return "";
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
