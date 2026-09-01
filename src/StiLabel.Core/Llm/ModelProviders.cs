namespace StiLabel.Core.Llm;

/// <summary>
/// 一个厂商的接入预设。默认协议按该厂商官网推荐；
/// 换厂商只换 Endpoint / Model / ApiKey / Protocol，上层工具集不变。
/// </summary>
public sealed class ModelProvider
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Group { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Accent { get; init; } = "#4F46E5";
    public string Endpoint { get; init; } = "";
    public IReadOnlyList<string> Models { get; init; } = [];
    public bool RequiresKey { get; init; } = true;
    public string KeyHint { get; init; } = "";
    public string ConsoleUrl { get; init; } = "";
    public string Note { get; init; } = "";

    /// <summary>这家推荐用的请求格式。</summary>
    public string Protocol { get; init; } = ChatProtocols.OpenAi;

    /// <summary>原生协议之外，这家还提供的 OpenAI 兼容地址；为空表示和 Endpoint 相同。</summary>
    public string OpenAiEndpoint { get; init; } = "";

    public string Caption => string.IsNullOrWhiteSpace(Endpoint) ? Note : Endpoint;

    /// <summary>按选定协议给出该填的地址。</summary>
    public string EndpointFor(string? protocol)
    {
        var id = ChatProtocols.Normalize(protocol);
        var useCompat = (id is ChatProtocols.OpenAi or ChatProtocols.OpenAiResponses)
                        && !string.IsNullOrWhiteSpace(OpenAiEndpoint);
        return useCompat ? OpenAiEndpoint : Endpoint;
    }
}

public static class ModelProviders
{
    public const string CustomId = "custom";

    public static IReadOnlyList<ModelProvider> All { get; } =
    [
        new()
        {
            Id = "openai",
            Name = "OpenAI",
            Group = "国际厂商",
            Badge = "AI",
            Accent = "#10A37F",
            Endpoint = "https://api.openai.com/v1",
            Models = ["gpt-5.6", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.1", "gpt-4.1", "gpt-4o-mini"],
            KeyHint = "以 sk- 开头的 API Key",
            ConsoleUrl = "https://platform.openai.com/api-keys",
            Protocol = ChatProtocols.OpenAiResponses,
            Note = "官网推荐 Responses API；兼容网关请改成 Chat Completions"
        },
        new()
        {
            Id = "anthropic",
            Name = "Anthropic Claude",
            Group = "国际厂商",
            Badge = "CL",
            Accent = "#D97757",
            Endpoint = "https://api.anthropic.com/v1",
            Models = ["claude-opus-5", "claude-sonnet-5", "claude-fable-5", "claude-haiku-4-5"],
            KeyHint = "以 sk-ant- 开头的 API Key",
            ConsoleUrl = "https://console.anthropic.com/settings/keys",
            Protocol = ChatProtocols.Anthropic,
            OpenAiEndpoint = "https://api.anthropic.com/v1",
            Note = "默认走 Messages 原生协议，也可切成官方的 OpenAI 兼容层"
        },
        new()
        {
            Id = "gemini",
            Name = "Google Gemini",
            Group = "国际厂商",
            Badge = "GM",
            Accent = "#1A73E8",
            Endpoint = "https://generativelanguage.googleapis.com/v1beta",
            Models = ["gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash"],
            KeyHint = "AI Studio 生成的 API Key",
            ConsoleUrl = "https://aistudio.google.com/apikey",
            Protocol = ChatProtocols.Gemini,
            OpenAiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai",
            Note = "默认走 generateContent 原生协议，也可切成官方的 OpenAI 兼容层"
        },
        new()
        {
            Id = "xai",
            Name = "xAI Grok",
            Group = "国际厂商",
            Badge = "xAI",
            Accent = "#111827",
            Endpoint = "https://api.x.ai/v1",
            Models = ["grok-4", "grok-4-fast", "grok-3"],
            KeyHint = "以 xai- 开头的 API Key",
            ConsoleUrl = "https://console.x.ai"
        },
        new()
        {
            Id = "azure-openai",
            Name = "Azure OpenAI",
            Group = "国际厂商",
            Badge = "AZ",
            Accent = "#0078D4",
            Endpoint = "https://<资源名>.openai.azure.com",
            Models = ["gpt-4o", "gpt-4o-mini"],
            KeyHint = "Azure 门户里的密钥 1 / 密钥 2",
            ConsoleUrl = "https://portal.azure.com",
            Protocol = ChatProtocols.AzureOpenAi,
            OpenAiEndpoint = "https://<资源名>.openai.azure.com/openai/v1",
            Note = "官方 Azure.AI.OpenAI SDK。地址填资源根，把资源名换成自己的"
        },

        new()
        {
            Id = "deepseek",
            Name = "DeepSeek 深度求索",
            Group = "国内厂商",
            Badge = "DS",
            Accent = "#4D6BFE",
            Endpoint = "https://api.deepseek.com/v1",
            Models = ["deepseek-chat", "deepseek-reasoner"],
            KeyHint = "开放平台生成的 API Key",
            ConsoleUrl = "https://platform.deepseek.com/api_keys"
        },
        new()
        {
            Id = "qwen",
            Name = "阿里云百炼 通义千问",
            Group = "国内厂商",
            Badge = "QW",
            Accent = "#615CED",
            Endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1",
            Models = ["qwen-max", "qwen-plus", "qwen-turbo", "qwen3-max"],
            KeyHint = "百炼控制台的 API Key，注意地域要对应",
            ConsoleUrl = "https://bailian.console.aliyun.com"
        },
        new()
        {
            Id = "zhipu",
            Name = "智谱 GLM",
            Group = "国内厂商",
            Badge = "GLM",
            Accent = "#3859FF",
            Endpoint = "https://open.bigmodel.cn/api/paas/v4",
            Models = ["glm-4.6", "glm-4-plus", "glm-4-air", "glm-4-flash"],
            KeyHint = "开放平台的 API Key",
            ConsoleUrl = "https://bigmodel.cn/usercenter/apikeys"
        },
        new()
        {
            Id = "moonshot",
            Name = "月之暗面 Kimi",
            Group = "国内厂商",
            Badge = "KM",
            Accent = "#0F1114",
            Endpoint = "https://api.moonshot.cn/v1",
            Models = ["kimi-k2-turbo-preview", "moonshot-v1-128k", "moonshot-v1-32k", "moonshot-v1-8k"],
            KeyHint = "开放平台的 API Key",
            ConsoleUrl = "https://platform.moonshot.cn/console/api-keys"
        },
        new()
        {
            Id = "volcengine",
            Name = "火山方舟 豆包",
            Group = "国内厂商",
            Badge = "DB",
            Accent = "#1664FF",
            Endpoint = "https://ark.cn-beijing.volces.com/api/v3",
            Models = ["doubao-pro-32k", "doubao-lite-32k"],
            KeyHint = "方舟 API Key",
            ConsoleUrl = "https://console.volcengine.com/ark",
            Note = "模型名也可以填接入点 ID（ep- 开头）"
        },
        new()
        {
            Id = "qianfan",
            Name = "百度千帆 文心",
            Group = "国内厂商",
            Badge = "ERN",
            Accent = "#2932E1",
            Endpoint = "https://qianfan.baidubce.com/v2",
            Models = ["ernie-4.5-8k", "ernie-4.5-turbo-128k", "ernie-speed-128k"],
            KeyHint = "千帆 v2 的 Bearer Token",
            ConsoleUrl = "https://console.bce.baidu.com/qianfan"
        },
        new()
        {
            Id = "hunyuan",
            Name = "腾讯混元",
            Group = "国内厂商",
            Badge = "HY",
            Accent = "#0052D9",
            Endpoint = "https://api.hunyuan.cloud.tencent.com/v1",
            Models = ["hunyuan-turbos-latest", "hunyuan-large", "hunyuan-standard"],
            KeyHint = "混元的 OpenAI 兼容 API Key",
            ConsoleUrl = "https://console.cloud.tencent.com/hunyuan/api-key"
        },
        new()
        {
            Id = "minimax",
            Name = "MiniMax 稀宇",
            Group = "国内厂商",
            Badge = "MM",
            Accent = "#F23F5D",
            Endpoint = "https://api.minimax.chat/v1",
            Models = ["MiniMax-Text-01", "abab6.5s-chat"],
            KeyHint = "开放平台的 API Key",
            ConsoleUrl = "https://platform.minimaxi.com"
        },
        new()
        {
            Id = "stepfun",
            Name = "阶跃星辰 Step",
            Group = "国内厂商",
            Badge = "ST",
            Accent = "#005CFF",
            Endpoint = "https://api.stepfun.com/v1",
            Models = ["step-2-16k", "step-1-flash"],
            KeyHint = "开放平台的 API Key",
            ConsoleUrl = "https://platform.stepfun.com"
        },
        new()
        {
            Id = "baichuan",
            Name = "百川智能",
            Group = "国内厂商",
            Badge = "BC",
            Accent = "#FF6933",
            Endpoint = "https://api.baichuan-ai.com/v1",
            Models = ["Baichuan4", "Baichuan3-Turbo"],
            KeyHint = "开放平台的 API Key",
            ConsoleUrl = "https://platform.baichuan-ai.com"
        },
        new()
        {
            Id = "spark",
            Name = "讯飞星火",
            Group = "国内厂商",
            Badge = "SP",
            Accent = "#1E6FFF",
            Endpoint = "https://spark-api-open.xf-yun.com/v1",
            Models = ["4.0Ultra", "generalv3.5", "lite"],
            KeyHint = "控制台的 APIPassword",
            ConsoleUrl = "https://console.xfyun.cn"
        },

        new()
        {
            Id = "siliconflow",
            Name = "硅基流动 SiliconFlow",
            Group = "聚合与加速",
            Badge = "SF",
            Accent = "#7C3AED",
            Endpoint = "https://api.siliconflow.cn/v1",
            Models = ["deepseek-ai/DeepSeek-V3", "Qwen/Qwen2.5-72B-Instruct"],
            KeyHint = "以 sk- 开头的 API Key",
            ConsoleUrl = "https://cloud.siliconflow.cn/account/ak"
        },
        new()
        {
            Id = "openrouter",
            Name = "OpenRouter",
            Group = "聚合与加速",
            Badge = "OR",
            Accent = "#6467F2",
            Endpoint = "https://openrouter.ai/api/v1",
            Models = ["openai/gpt-5.6", "anthropic/claude-sonnet-5", "google/gemini-2.5-pro"],
            KeyHint = "以 sk-or- 开头的 API Key",
            ConsoleUrl = "https://openrouter.ai/keys",
            Note = "一个 Key 打通几百个模型"
        },
        new()
        {
            Id = "groq",
            Name = "Groq",
            Group = "聚合与加速",
            Badge = "GQ",
            Accent = "#F55036",
            Endpoint = "https://api.groq.com/openai/v1",
            Models = ["llama-3.3-70b-versatile", "qwen-2.5-32b"],
            KeyHint = "以 gsk_ 开头的 API Key",
            ConsoleUrl = "https://console.groq.com/keys"
        },
        new()
        {
            Id = "nvidia",
            Name = "NVIDIA NIM",
            Group = "聚合与加速",
            Badge = "NV",
            Accent = "#76B900",
            Endpoint = "https://integrate.api.nvidia.com/v1",
            Models = ["deepseek-ai/deepseek-r1", "meta/llama-3.3-70b-instruct", "qwen/qwen2.5-coder-32b-instruct"],
            KeyHint = "以 nvapi- 开头的 API Key",
            ConsoleUrl = "https://build.nvidia.com"
        },

        new()
        {
            Id = "ollama",
            Name = "Ollama 本地",
            Group = "本地部署",
            Badge = "OL",
            Accent = "#111827",
            Endpoint = "http://localhost:11434",
            Models = ["qwen2.5:14b", "llama3.1:8b"],
            RequiresKey = false,
            KeyHint = "本地服务不用填",
            ConsoleUrl = "https://ollama.com/library",
            Protocol = ChatProtocols.Ollama,
            OpenAiEndpoint = "http://localhost:11434/v1",
            Note = "官方 OllamaApiClient，地址不要带 /v1。也可切成 OpenAI 兼容层"
        },
        new()
        {
            Id = "lmstudio",
            Name = "LM Studio 本地",
            Group = "本地部署",
            Badge = "LM",
            Accent = "#6B7280",
            Endpoint = "http://localhost:1234/v1",
            RequiresKey = false,
            KeyHint = "本地服务不用填",
            Note = "在 LM Studio 里开启本地服务器"
        },
        new()
        {
            Id = "vllm",
            Name = "vLLM 自建",
            Group = "本地部署",
            Badge = "vL",
            Accent = "#334155",
            Endpoint = "http://localhost:8000/v1",
            RequiresKey = false,
            KeyHint = "按自建服务的配置填，没有就留空",
            Note = "自建推理服务，注意开启 --enable-auto-tool-choice"
        },

        new()
        {
            Id = CustomId,
            Name = "自定义 OpenAI 兼容",
            Group = "自定义",
            Badge = "＋",
            Accent = "#6B7280",
            KeyHint = "按你的网关要求填，没有就留空",
            Note = "任何提供 /chat/completions 的地址都能接"
        }
    ];

    public static ModelProvider Custom { get; } = All.First(p => p.Id == CustomId);

    public static IReadOnlyList<string> Groups { get; } =
        All.Select(p => p.Group).Distinct(StringComparer.Ordinal).ToList();

    public static ModelProvider? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 先按保存的厂商 ID 找；老配置里没有 ID 时，退回用地址反查，实在认不出就归到「自定义」。
    /// </summary>
    public static ModelProvider Resolve(string? id, string? endpoint)
    {
        if (Find(id) is { } byId)
        {
            return byId;
        }

        var url = endpoint?.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return Custom;
        }

        var match = All
            .SelectMany(p => new[] { p.Endpoint, p.OpenAiEndpoint }.Select(e => (Provider: p, Endpoint: e)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Endpoint) && !pair.Endpoint.Contains('<'))
            .Where(pair => url.StartsWith(pair.Endpoint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Endpoint.Length)
            .Select(pair => pair.Provider)
            .FirstOrDefault();

        return match ?? Custom;
    }
}
