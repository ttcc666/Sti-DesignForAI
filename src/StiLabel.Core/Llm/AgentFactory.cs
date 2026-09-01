using System.ClientModel;
using Anthropic;
using Azure.AI.OpenAI;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using StiLabel.Core.Services;

namespace StiLabel.Core.Llm;

/// <summary>
/// 按选定格式，用官网给出的 SDK 构造方式造出 AIAgent。
///
/// 来源（Microsoft Learn Agent Framework / 各厂商官方 SDK）：
///   OpenAI Chat Completions  —— OpenAIClient.GetChatClient(model).AsAIAgent
///   OpenAI Responses         —— OpenAIClient.GetResponsesClient().AsAIAgent(model)
///   Anthropic Messages       —— new AnthropicClient { ApiKey }.AsAIAgent(model)
///   Gemini generateContent   —— new Client(vertexAI: false, apiKey).AsIChatClient(model)
///   Azure OpenAI Chat        —— AzureOpenAIClient.GetChatClient(deployment).AsAIAgent
///   Azure OpenAI Responses   —— AzureOpenAIClient.GetResponsesClient().AsAIAgent(model)
///   Ollama 原生              —— new OllamaApiClient(endpoint, model).AsAIAgent
/// </summary>
internal static class AgentFactory
{
    private const string AgentName = "StiLabel";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(180);

    public static AIAgent Create(ModelOptions options, string instructions, IList<AITool>? tools = null) =>
        ChatProtocols.Normalize(options.Protocol) switch
        {
            ChatProtocols.Anthropic => Anthropic(options, instructions, tools),
            ChatProtocols.Gemini => Gemini(options, instructions, tools),
            ChatProtocols.Ollama => Ollama(options, instructions, tools),
            ChatProtocols.AzureOpenAi => AzureChat(options, instructions, tools),
            ChatProtocols.AzureResponses => AzureResponses(options, instructions, tools),
            ChatProtocols.OpenAiResponses => OpenAiResponses(options, instructions, tools),
            _ => OpenAiChat(options, instructions, tools)
        };

    /// <summary>
    /// https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/openai
    /// Chat Completion Client：GetChatClient(model).AsAIAgent
    /// </summary>
    private static AIAgent OpenAiChat(ModelOptions options, string instructions, IList<AITool>? tools) =>
        CreateOpenAiClient(options)
            .GetChatClient(options.Model)
            .AsAIAgent(
                instructions: instructions,
                name: AgentName,
                tools: tools,
                clientFactory: ClientFactory(options));

    /// <summary>
    /// 同一页官方推荐：GetResponsesClient().AsAIAgent(model)
    /// </summary>
    private static AIAgent OpenAiResponses(ModelOptions options, string instructions, IList<AITool>? tools) =>
        CreateOpenAiClient(options)
            .GetResponsesClient()
            .AsAIAgent(
                model: options.Model,
                instructions: instructions,
                name: AgentName,
                tools: tools,
                clientFactory: ClientFactory(options));

    /// <summary>
    /// https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/anthropic
    /// AnthropicClient { ApiKey }.AsAIAgent(model)
    /// </summary>
    private static AIAgent Anthropic(ModelOptions options, string instructions, IList<AITool>? tools)
    {
        var endpoint = ChatProtocols.BaseUri(ChatProtocols.Anthropic, options.Endpoint);
        var client = IsHost(endpoint, "api.anthropic.com")
            ? new AnthropicClient { ApiKey = options.ApiKey ?? "" }
            : new AnthropicClient
            {
                ApiKey = options.ApiKey ?? "",
                // 官方 SDK 自己拼 v1/messages，自建网关只留站点前缀，并保留结尾斜杠。
                BaseUrl = AnthropicBaseUrl(endpoint)
            };

        return client.AsAIAgent(
            model: options.Model,
            name: AgentName,
            instructions: instructions,
            tools: tools,
            clientFactory: ClientFactory(options));
    }

    /// <summary>
    /// https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/google-gemini
    /// new Client(vertexAI: false, apiKey).AsIChatClient(model)
    /// </summary>
    private static AIAgent Gemini(ModelOptions options, string instructions, IList<AITool>? tools)
    {
        var endpoint = ChatProtocols.BaseUri(ChatProtocols.Gemini, options.Endpoint);
        Client client;
        if (IsHost(endpoint, "generativelanguage.googleapis.com"))
        {
            client = new Client(vertexAI: false, apiKey: options.ApiKey ?? "");
        }
        else
        {
            var (baseUrl, apiVersion) = SplitGemini(endpoint);
            var http = new HttpOptions
            {
                ApiVersion = apiVersion,
                Timeout = (int)NetworkTimeout.TotalMilliseconds
            };
            if (baseUrl is not null)
            {
                http.BaseUrl = baseUrl;
            }

            client = new Client(vertexAI: false, apiKey: options.ApiKey ?? "", httpOptions: http);
        }

        return WithCompaction(client.AsIChatClient(options.Model), options)
            .AsAIAgent(instructions: instructions, name: AgentName, tools: tools);
    }

    /// <summary>
    /// https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/azure-openai
    /// AzureOpenAIClient.GetChatClient(deployment).AsAIAgent
    /// 桌面设置里填的是密钥，对应官网「Create client with an API key」。
    /// </summary>
    private static AIAgent AzureChat(ModelOptions options, string instructions, IList<AITool>? tools) =>
        CreateAzureClient(options)
            .GetChatClient(options.Model)
            .AsAIAgent(
                instructions: instructions,
                name: AgentName,
                tools: tools,
                clientFactory: ClientFactory(options));

    /// <summary>同一页官方推荐：GetResponsesClient().AsAIAgent(model)</summary>
    private static AIAgent AzureResponses(ModelOptions options, string instructions, IList<AITool>? tools) =>
        CreateAzureClient(options)
            .GetResponsesClient()
            .AsAIAgent(
                model: options.Model,
                instructions: instructions,
                name: AgentName,
                tools: tools,
                clientFactory: ClientFactory(options));

    /// <summary>
    /// https://learn.microsoft.com/en-us/agent-framework/integrations/by-component/model-providers/ollama
    /// new OllamaApiClient(endpoint, model).AsAIAgent
    /// </summary>
    private static AIAgent Ollama(ModelOptions options, string instructions, IList<AITool>? tools) =>
        WithCompaction(
                new OllamaApiClient(ChatProtocols.BaseUri(ChatProtocols.Ollama, options.Endpoint), options.Model),
                options)
            .AsAIAgent(instructions: instructions, name: AgentName, tools: tools);

    /// <summary>
    /// 官网要求挂在 ChatClientBuilder 上，这样工具循环里也会压缩。
    /// 只写到 ChatClientAgentOptions.AIContextProviders 会跳过工具轮。
    /// https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/compaction
    /// </summary>
    private static Func<IChatClient, IChatClient>? ClientFactory(ModelOptions options) =>
        CompactModes.IsOff(options.CompactMode)
            ? null
            : inner => WithCompaction(inner, options);

    private static IChatClient WithCompaction(IChatClient inner, ModelOptions options)
    {
        var strategy = CreateStrategy(inner, options);
        return strategy is null
            ? inner
            : inner.AsBuilder()
                .UseAIContextProviders(new CompactionProvider(strategy))
                .Build();
    }

    private static CompactionStrategy? CreateStrategy(IChatClient inner, ModelOptions options)
    {
        var tokens = CompactModes.ClampContextTokens(options.ContextTokens);
        var turns = CompactModes.ClampTurns(options.CompactTurns);
        return CompactModes.Normalize(options.CompactMode) switch
        {
            CompactModes.Window => new ContextWindowCompactionStrategy(
                maxContextWindowTokens: tokens,
                maxOutputTokens: OutputBudget(tokens)),
            CompactModes.Truncate => new TruncationCompactionStrategy(
                CompactionTriggers.TokensExceed(tokens),
                minimumPreservedGroups: 4),
            CompactModes.Sliding => new SlidingWindowCompactionStrategy(
                CompactionTriggers.TurnsExceed(turns)),
            CompactModes.Summarize => new SummarizationCompactionStrategy(
                inner,
                CompactionTriggers.TokensExceed(tokens),
                minimumPreservedGroups: 4),
            _ => null
        };
    }

    private static int OutputBudget(int contextTokens)
    {
        var output = Math.Min(4_096, Math.Max(256, contextTokens / 8));
        return Math.Min(output, contextTokens - 256);
    }

    /// <summary>
    /// 官网：自定义 URL 时用 OpenAIClientOptions.Endpoint；官方主机可只传 key。
    /// https://learn.microsoft.com/en-us/agent-framework/agents/
    /// </summary>
    private static OpenAIClient CreateOpenAiClient(ModelOptions options)
    {
        var key = new ApiKeyCredential(string.IsNullOrWhiteSpace(options.ApiKey) ? "nokey" : options.ApiKey);
        var endpoint = ChatProtocols.BaseUri(ChatProtocols.OpenAi, options.Endpoint);
        var clientOptions = new OpenAIClientOptions { NetworkTimeout = NetworkTimeout };
        if (!IsHost(endpoint, "api.openai.com"))
        {
            clientOptions.Endpoint = endpoint;
        }

        return new OpenAIClient(key, clientOptions);
    }

    private static AzureOpenAIClient CreateAzureClient(ModelOptions options)
    {
        var endpoint = ChatProtocols.BaseUri(ChatProtocols.AzureOpenAi, options.Endpoint);
        var key = new ApiKeyCredential(options.ApiKey ?? "");
        return new AzureOpenAIClient(
            endpoint,
            key,
            new AzureOpenAIClientOptions { NetworkTimeout = NetworkTimeout });
    }

    private static string AnthropicBaseUrl(Uri endpoint)
    {
        var url = endpoint.AbsoluteUri.TrimEnd('/');
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^3].TrimEnd('/');
        }

        return url + "/";
    }

    private static (string? BaseUrl, string ApiVersion) SplitGemini(Uri endpoint)
    {
        var segments = endpoint.AbsolutePath.Trim('/');
        var origin = endpoint.GetLeftPart(UriPartial.Authority);

        if (segments.Length == 0)
        {
            return (origin, "v1beta");
        }

        var index = segments.LastIndexOf('/');
        var last = index < 0 ? segments : segments[(index + 1)..];
        if (last.StartsWith("v1", StringComparison.OrdinalIgnoreCase)
            || last.StartsWith("v2", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = index < 0 ? "" : segments[..index];
            var baseUrl = prefix.Length == 0 ? origin : $"{origin}/{prefix}";
            return (baseUrl, last);
        }

        return ($"{origin}/{segments}", "v1beta");
    }

    private static bool IsHost(Uri uri, string host) =>
        uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
}
