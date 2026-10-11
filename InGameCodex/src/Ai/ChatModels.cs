using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace InGameCodex.Ai
{
    /// <summary>一条对话消息（OpenAI 兼容格式）。</summary>
    public sealed class ChatMsg
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }

        public ChatMsg() { }

        public ChatMsg(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    public sealed class LlmRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; }

        [JsonPropertyName("messages")]
        public List<ChatMsg> Messages { get; set; }

        [JsonPropertyName("temperature")]
        public float Temperature { get; set; }

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    public sealed class LlmChunk
    {
        public string Content { get; set; }

        public bool HasContent => Content != null;
    }

    public sealed class LlmResponse
    {
        [JsonPropertyName("choices")]
        public List<LlmChoice> Choices { get; set; }

        [JsonPropertyName("error")]
        public LlmError Error { get; set; }
    }

    public sealed class LlmChoice
    {
        [JsonPropertyName("message")]
        public LlmDelta Message { get; set; }

        [JsonPropertyName("delta")]
        public LlmDelta Delta { get; set; }
    }

    public sealed class LlmModelList
    {
        [JsonPropertyName("data")]
        public List<LlmModel> Data { get; set; }

        [JsonPropertyName("models")]
        public List<LlmModel> Models { get; set; }
    }

    public sealed class LlmModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>归属方。本机网关会标 trio-trae / trio-qoder / trio-workbuddy。</summary>
        [JsonPropertyName("owned_by")]
        public string OwnedBy { get; set; }
    }

    public sealed class LlmDelta
    {
        [JsonPropertyName("content")]
        public string Content { get; set; }

        [JsonPropertyName("reasoning_content")]
        public string ReasoningContent { get; set; }
    }

    public sealed class LlmError
    {
        [JsonPropertyName("message")]
        public string Message { get; set; }
    }
}