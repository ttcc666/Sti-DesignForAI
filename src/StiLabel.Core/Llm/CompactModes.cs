namespace StiLabel.Core.Llm;

/// <summary>
/// 上下文压缩方式。策略来自 Microsoft Learn Agent Framework Compaction：
/// https://learn.microsoft.com/en-us/agent-framework/concepts/agents/conversations/compaction
/// </summary>
public sealed class CompactMode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Note { get; init; } = "";
}

public static class CompactModes
{
    public const string None = "none";
    public const string Window = "window";
    public const string Truncate = "truncate";
    public const string Sliding = "sliding";
    public const string Summarize = "summarize";

    public const int DefaultContextTokens = 32_000;
    public const int DefaultTurns = 8;

    public static IReadOnlyList<CompactMode> All { get; } =
    [
        new()
        {
            Id = None,
            Name = "关闭",
            Note = "整段对话都发给模型。对话一长就容易顶满窗口。"
        },
        new()
        {
            Id = Window,
            Name = "按窗口自动",
            Note = "官网 ContextWindow：先收旧工具结果，再按上下文上限截掉最早的轮次。"
        },
        new()
        {
            Id = Truncate,
            Name = "截断最早消息",
            Note = "官网 Truncation：超过 token 上限就丢掉最早的完整轮次，系统提示和最近几轮留下。"
        },
        new()
        {
            Id = Sliding,
            Name = "只留最近轮次",
            Note = "官网 SlidingWindow：只保留最近 N 轮，更早的一律丢掉。"
        },
        new()
        {
            Id = Summarize,
            Name = "摘要压缩",
            Note = "官网 Summarization：用当前模型把旧对话收成一段摘要。会多一次请求。"
        }
    ];

    public static IReadOnlyList<string> ContextSizes { get; } =
        ["8K", "16K", "32K", "64K", "128K", "200K"];

    public static IReadOnlyList<string> TurnChoices { get; } =
        ["4", "6", "8", "12", "16"];

    public static string Normalize(string? id) =>
        id?.Trim().ToLowerInvariant() switch
        {
            None or "off" or "关闭" => None,
            Truncate or "cut" or "截断" => Truncate,
            Sliding or "slide" or "window-turns" or "滑动" => Sliding,
            Summarize or "summary" or "摘要" => Summarize,
            _ => Window
        };

    public static CompactMode Resolve(string? id)
    {
        var normalized = Normalize(id);
        return All.First(m => m.Id == normalized);
    }

    public static bool IsOff(string? id) => Normalize(id) == None;

    public static bool UsesTurns(string? id) => Normalize(id) == Sliding;

    public static int ClampContextTokens(int tokens) =>
        tokens < 2_048 ? DefaultContextTokens : Math.Clamp(tokens, 2_048, 2_000_000);

    public static int ClampTurns(int turns) =>
        turns < 2 ? DefaultTurns : Math.Clamp(turns, 2, 64);

    public static int ParseContextTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultContextTokens;
        }

        var raw = text.Trim().ToUpperInvariant().Replace(",", "").Replace(" ", "");
        var scale = 1;
        if (raw.EndsWith('K'))
        {
            scale = 1_000;
            raw = raw[..^1];
        }
        else if (raw.EndsWith('M'))
        {
            scale = 1_000_000;
            raw = raw[..^1];
        }

        return int.TryParse(raw, out var n) ? ClampContextTokens(n * scale) : DefaultContextTokens;
    }

    public static string FormatContextTokens(int tokens)
    {
        var value = ClampContextTokens(tokens);
        return value % 1_000 == 0 ? $"{value / 1_000}K" : value.ToString();
    }

    public static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000d:0.#}M"
        : tokens >= 10_000 ? $"{tokens / 1_000}K"
        : tokens >= 1_000 ? $"{tokens / 1_000d:0.#}K"
        : tokens.ToString();

    public static int UsagePercent(int used, int limit)
    {
        if (limit <= 0 || used <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(used * 100d / limit), 0, 100);
    }

    public static int ParseTurns(string? text) =>
        int.TryParse(text?.Trim(), out var n) ? ClampTurns(n) : DefaultTurns;
}
