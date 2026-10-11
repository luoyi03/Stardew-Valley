using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace InGameCodex.Ai
{
    /// <summary>OpenAI 兼容的 Chat Completions 客户端，支持 SSE 流式。</summary>
    public static class LlmClient
    {
        private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        private static readonly JsonSerializerOptions WriteOpts = new();

        private static readonly JsonSerializerOptions ReadOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>发起一次对话。流式时会逐段回调。</summary>
        public static async Task<string> ChatAsync(
            string baseUrl,
            string apiKey,
            string model,
            float temperature,
            int maxTokens,
            bool stream,
            List<ChatMsg> messages,
            Action<string> onDelta,
            CancellationToken ct)
        {
            string root = (baseUrl ?? "").Trim().TrimEnd('/');
            string url = root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? root
                : root + "/chat/completions";
            var payload = new LlmRequest
            {
                Model = model,
                Messages = messages,
                Temperature = temperature,
                MaxTokens = maxTokens,
                Stream = stream
            };

            string json = JsonSerializer.Serialize(payload, WriteOpts);

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd(stream ? "text/event-stream" : "application/json");

            string key = (apiKey ?? "").Trim();
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
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new Exception("请求超时（地址：" + url + "）");
            }

            using (resp)
                return await ReadResponseAsync(resp, stream, onDelta, ct).ConfigureAwait(false);
        }

        private static async Task<string> ReadResponseAsync(HttpResponseMessage resp, bool stream, Action<string> onDelta, CancellationToken ct)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string body = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
                throw new Exception("HTTP " + (int)resp.StatusCode + " " + Truncate(ExtractError(body), 300));
            }

            if (!stream)
            {
                string full = await ReadBodyAsync(resp, ct).ConfigureAwait(false);
                string text = ParseFull(full);

                if (string.IsNullOrEmpty(text) && full.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("模型只输出了推理内容，正文被 token 上限截断");

                onDelta?.Invoke(text);
                return text;
            }

            var sb = new StringBuilder();
            using Stream stream2 = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream2, Encoding.UTF8);

            bool sawData = false;
            bool sawReasoning = false;
            string line;

            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                ct.ThrowIfCancellationRequested();

                if (line.Length == 0)
                    continue;

                if (line.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase))
                    sawReasoning = true;

                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    sawData = true;
                    line = line.Substring(5).Trim();
                }
                else if (line.StartsWith("{", StringComparison.Ordinal))
                {
                    sawData = true;
                }

                if (line.Length == 0)
                    continue;

                if (line.Equals("[DONE]", StringComparison.Ordinal))
                    break;

                string piece = ExtractDeltaContent(line);
                if (piece == null)
                {
                    // 某些网关即使 stream=true 也直接吐完整 JSON
                    string whole = TryParseFull(line);
                    if (whole != null)
                        piece = whole;
                }

                if (string.IsNullOrEmpty(piece))
                    continue;

                sb.Append(piece);
                onDelta?.Invoke(piece);
            }

            if (!sawData)
                throw new Exception("接口返回的不是对话数据流（地址可能少了 /v1，正确写法类似 https://www.moyu.cn/v1）");

            if (sb.Length == 0 && sawReasoning)
                throw new Exception("模型只输出了推理内容，正文被 token 上限截断");

            return sb.ToString();
        }

        /// <summary>拉取可用模型列表（GET /models），用于本机网关等自定义接口。owner 非空时只保留该归属方的模型。</summary>
        public static async Task<List<string>> GetModelsAsync(string baseUrl, string apiKey, CancellationToken ct, string owner = null)
        {
            string root = (baseUrl ?? "").Trim().TrimEnd('/');
            string url = root.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                ? root
                : root + "/models";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);

            string key = (apiKey ?? "").Trim();
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
                    throw new Exception("HTTP " + (int)resp.StatusCode + "（地址：" + url + "）");

                string body = await ReadBodyAsync(resp, ct).ConfigureAwait(false);

                try
                {
                    var list = JsonSerializer.Deserialize<LlmModelList>(body, ReadOpts);
                    var rows = list?.Data ?? list?.Models;
                    var all = new List<string>();
                    var owned = new List<string>();
                    var foreign = new List<string>();

                    if (rows != null)
                    {
                        foreach (var m in rows)
                        {
                            string id = m?.Id ?? m?.Name;
                            if (string.IsNullOrWhiteSpace(id))
                                continue;

                            id = id.Trim();
                            AddUnique(all, id);

                            if (string.IsNullOrWhiteSpace(owner))
                                continue;

                            string own = (m?.OwnedBy ?? "").Trim();
                            if (own.Length == 0)
                                continue;

                            if (string.Equals(own, owner.Trim(), StringComparison.OrdinalIgnoreCase))
                                AddUnique(owned, id);
                            else
                                AddUnique(foreign, id);
                        }
                    }

                    // 网关是按 id 先到先得路由的：先只留本项目归属的模型，再剔除同名却归别家的
                    var res = all;
                    if (!string.IsNullOrWhiteSpace(owner) && owned.Count > 0)
                    {
                        res = new List<string>();
                        foreach (string id in owned)
                        {
                            if (!Contains(foreign, id))
                                res.Add(id);
                        }

                        if (res.Count == 0)
                            res = owned;
                    }

                    if (res.Count == 0)
                        throw new Exception("模型列表为空：" + Truncate(body, 120));

                    return res;
                }
                catch (JsonException)
                {
                    throw new Exception("无法解析模型列表：" + Truncate(body, 120));
                }
            }
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) ?? "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>解析一段 SSE 的 data JSON，取出文字增量。解析不出来返回 null。</summary>
        private static string ExtractDeltaContent(string data)
        {
            if (!data.StartsWith("{", StringComparison.Ordinal))
                return null;

            try
            {
                var res = JsonSerializer.Deserialize<LlmResponse>(data, ReadOpts);
                if (res?.Error != null && !string.IsNullOrEmpty(res.Error.Message))
                    throw new Exception(Truncate(res.Error.Message, 200));

                var choices = res?.Choices;
                if (choices == null || choices.Count == 0)
                    return "";

                var delta = choices[0].Delta ?? choices[0].Message;
                if (delta == null)
                    return "";

                return delta.Content ?? "";
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string TryParseFull(string data)
        {
            try
            {
                var res = JsonSerializer.Deserialize<LlmResponse>(data, ReadOpts);
                var choices = res?.Choices;
                if (choices == null || choices.Count == 0)
                    return null;
                return (choices[0].Message ?? choices[0].Delta)?.Content;
            }
            catch
            {
                return null;
            }
        }

        private static string ParseFull(string body)
        {
            string whole = TryParseFull(body);
            if (whole != null)
                return whole;

            // 有些网关（含本机 DSH 网关）即使请求 stream=false 也照样回 SSE，
            // 这时逐行取 data: 里的正文增量，别当成坏 JSON 报「无法解析响应」
            if (body != null && body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                bool sawData = false;

                foreach (string raw in body.Split('\n'))
                {
                    string line = raw.Trim();

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        line = line.Substring(5).Trim();
                        sawData = true;
                    }

                    if (line.Length == 0 || !line.StartsWith("{", StringComparison.Ordinal))
                        continue;

                    string piece = ExtractDeltaContent(line);
                    if (piece == null)
                    {
                        string w = TryParseFull(line);
                        if (w != null)
                            piece = w;
                    }

                    if (!string.IsNullOrEmpty(piece))
                        sb.Append(piece);
                }

                if (sb.Length > 0)
                    return sb.ToString();

                return sawData ? "" : null;
            }

            if (body != null && body.TrimStart().StartsWith("<", StringComparison.Ordinal))
                throw new Exception("接口返回了网页而不是数据（地址可能少了 /v1，正确写法类似 https://www.moyu.cn/v1）");

            try
            {
                var res = JsonSerializer.Deserialize<LlmResponse>(body, ReadOpts);
                if (res?.Error != null)
                    throw new Exception(Truncate(res.Error.Message, 200));
            }
            catch (JsonException)
            {
                // 忽略，下面统一报「解析失败」
            }

            throw new Exception("无法解析响应：" + Truncate(body, 200));
        }

        private static string ExtractError(string body)
        {
            try
            {
                if (body.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    var res = JsonSerializer.Deserialize<LlmResponse>(body, ReadOpts);
                    if (res?.Error?.Message != null)
                        return res.Error.Message;
                }
            }
            catch
            {
                // 保持原样
            }

            return body;
        }

        private static void AddUnique(List<string> list, string value)
        {
            foreach (string s in list)
            {
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            list.Add(value);
        }

        private static bool Contains(List<string> list, string value)
        {
            foreach (string s in list)
            {
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}