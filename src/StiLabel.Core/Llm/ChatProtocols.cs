namespace StiLabel.Core.Llm;

/// <summary>
/// 一种请求格式。选项和构造方式都按各厂商 / Microsoft Agent Framework 官网来，不自造协议。
/// </summary>
public sealed class ChatProtocol
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Note { get; init; } = "";
}

public static class ChatProtocols
{
    public const string OpenAi = "openai";
    public const string OpenAiResponses = "openai-responses";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string AzureOpenAi = "azure-openai";
    public const string AzureResponses = "azure-responses";
    public const string Ollama = "ollama";

    public static IReadOnlyList<ChatProtocol> All { get; } =
    [
        new()
        {
            Id = OpenAi,
            Name = "OpenAI Chat Completions",
            Note = "官方 OpenAI SDK：OpenAIClient.GetChatClient(model).AsAIAgent。绝大多数厂商和自建网关都是这套。"
        },
        new()
        {
            Id = OpenAiResponses,
            Name = "OpenAI Responses",
            Note = "官方推荐的 OpenAI 接入：OpenAIClient.GetResponsesClient().AsAIAgent。"
        },
        new()
        {
            Id = Anthropic,
            Name = "Anthropic Messages",
            Note = "官方 Anthropic SDK：new AnthropicClient { ApiKey }.AsAIAgent(model)。"
        },
        new()
        {
            Id = Gemini,
            Name = "Gemini generateContent",
            Note = "官方 Google.GenAI：new Client(vertexAI: false, apiKey).AsIChatClient(model)。"
        },
        new()
        {
            Id = AzureOpenAi,
            Name = "Azure OpenAI Chat Completions",
            Note = "官方 Azure.AI.OpenAI：AzureOpenAIClient.GetChatClient(deployment).AsAIAgent。"
        },
        new()
        {
            Id = AzureResponses,
            Name = "Azure OpenAI Responses",
            Note = "官方 Azure.AI.OpenAI：AzureOpenAIClient.GetResponsesClient().AsAIAgent。"
        },
        new()
        {
            Id = Ollama,
            Name = "Ollama 原生",
            Note = "官方 OllamaSharp：new OllamaApiClient(endpoint, model).AsAIAgent。地址不要带 /v1。"
        }
    ];

    public static string Normalize(string? id)
    {
        var value = id?.Trim().ToLowerInvariant();
        return value switch
        {
            "openai-responses" or "responses" => OpenAiResponses,
            Anthropic => Anthropic,
            Gemini => Gemini,
            "azure-openai" or "azure" => AzureOpenAi,
            "azure-responses" => AzureResponses,
            Ollama => Ollama,
            _ => OpenAi
        };
    }

    public static ChatProtocol Resolve(string? id)
    {
        var normalized = Normalize(id);
        return All.First(p => p.Id == normalized);
    }

    /// <summary>
    /// 把用户填的地址收敛成该协议官方 SDK 要的基地址。
    /// 用户常常直接粘完整请求 URL，所以这里按各官网路径去掉动作后缀。
    /// </summary>
    public static Uri BaseUri(string? protocol, string endpoint)
    {
        var url = (endpoint ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            throw new InvalidOperationException("没有填接口地址。");
        }

        url = Normalize(protocol) switch
        {
            Anthropic => StripSuffix(url, "/messages"),
            Gemini => StripGemini(url),
            Ollama => StripOllama(url),
            AzureOpenAi or AzureResponses => StripAzure(url),
            OpenAiResponses => StripSuffix(StripSuffix(url, "/responses"), "/chat/completions"),
            _ => StripSuffix(url, "/chat/completions")
        };

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"接口地址不是合法的 URL：{endpoint}");
        }

        if (uri.Host.Contains('<') || uri.Host.Contains('>'))
        {
            throw new InvalidOperationException("请把地址里的资源名占位符换成你自己的。");
        }

        return uri;
    }

    private static string StripSuffix(string url, string suffix) =>
        url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? url[..^suffix.Length].TrimEnd('/')
            : url;

    private static string StripOllama(string url) =>
        StripSuffix(StripSuffix(url, "/api/tags"), "/v1");

    private static string StripAzure(string url)
    {
        url = StripSuffix(url, "/chat/completions");
        url = StripSuffix(url, "/responses");
        url = StripSuffix(url, "/openai/v1");
        return StripSuffix(url, "/openai");
    }

    private static string StripGemini(string url)
    {
        var colon = url.LastIndexOf(':');
        if (colon > url.IndexOf("://", StringComparison.Ordinal) + 2)
        {
            var tail = url[colon..];
            if (tail.Contains("generateContent", StringComparison.OrdinalIgnoreCase))
            {
                url = url[..colon];
                var slash = url.LastIndexOf('/');
                if (slash > 0)
                {
                    url = url[..slash];
                }
            }
        }

        url = StripSuffix(url, "/openai");
        return StripSuffix(url, "/models");
    }
}
