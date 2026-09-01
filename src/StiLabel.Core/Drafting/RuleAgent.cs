using System.Globalization;
using System.Text.RegularExpressions;
using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;
using StiLabel.Core.Services;

namespace StiLabel.Core.Drafting;

/// <summary>
/// 本机规则助手：不调用外网模型，先把「出草稿 / 加字段 / 分析 / 拒幻觉字段」跑通。
/// 接大模型时实现同一 IWorkbenchAgent 即可替换。
/// </summary>
public sealed class RuleAgent : IWorkbenchAgent
{
    private static readonly Regex SizeRegex = new(
        @"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IDraftBuilder _drafts;

    public RuleAgent(IDraftBuilder drafts) => _drafts = drafts;

    public void ResetConversation()
    {
    }

    public Task<AgentReply> HandleAsync(
        string userText,
        LabelDocument current,
        IReadOnlyList<FieldItem> fields,
        PagePreset? preset,
        IReadOnlyList<string>? printers = null,
        SampleRow? sample = null,
        IReadOnlyList<PagePreset>? presets = null,
        IReadOnlyList<string>? recentFiles = null,
        IReadOnlyList<string>? versions = null,
        IReadOnlyList<string>? imagePaths = null,
        IProgress<AgentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = userText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(new AgentReply { Text = "请说要出什么样的标签，或点右边「按所选生成草稿」。" });
        }

        if (ContainsAny(text, "分析", "有哪些字段", "风险"))
        {
            return Task.FromResult(Analyze(current, fields));
        }

        var unknown = FindUnknownField(text, fields);
        if (unknown is not null)
        {
            var names = string.Join("、", fields.Select(f => f.DisplayName));
            return Task.FromResult(new AgentReply
            {
                Text = $"字典里没有「{unknown}」，我不能编造字段。可用：{names}。"
            });
        }

        if (ContainsAny(text, "加一个", "加上", "增加"))
        {
            var field = MatchField(text, fields);
            if (field is null)
            {
                return Task.FromResult(new AgentReply { Text = "请指明要加的字段名，必须来自右边字典。" });
            }

            var next = _drafts.AddField(current, field);
            return Task.FromResult(new AgentReply
            {
                Text = $"已增加「{field.DisplayName}」并绑定 {field.Key}。可在画布上拖位置，或点撤销本次。",
                Document = next,
                Applied = true
            });
        }

        var mentioned = fields.Where(f => Mentions(text, f)).ToList();
        if (mentioned.Count == 0 && ContainsAny(text, "草稿", "标签", "生成", "做一张", "出一张"))
        {
            mentioned = fields.Where(f => f.Selected).ToList();
            if (mentioned.Count == 0)
            {
                mentioned = fields.Take(4).ToList();
            }
        }

        if (mentioned.Count > 0 || SizeRegex.IsMatch(text))
        {
            var page = ResolvePreset(text, preset) ?? PagePresets.All[0];
            if (mentioned.Count == 0)
            {
                mentioned = fields.Where(f => f.Selected).DefaultIfEmpty(fields[0]).ToList();
            }

            var doc = _drafts.Build(page, mentioned, current.Page.PrinterName);
            var names = string.Join("、", mentioned.Select(f => f.DisplayName));
            return Task.FromResult(new AgentReply
            {
                Text = $"已按 {page.WidthMm:0}×{page.HeightMm:0} mm 生成草稿，包含：{names}。请在中间画布微调后再预览打样。",
                Document = doc,
                Applied = true
            });
        }

        return Task.FromResult(new AgentReply
        {
            Text = "我能做：出草稿、加一个字典字段、分析当前稿。也可以直接勾选右边字段后点「按所选生成草稿」。"
        });
    }

    private static AgentReply Analyze(LabelDocument current, IReadOnlyList<FieldItem> fields)
    {
        var binds = current.Components
            .Where(c => c.Bind.Kind == BindKind.Field && !string.IsNullOrWhiteSpace(c.Bind.FieldKey))
            .Select(c => c.Bind.FieldKey!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var risks = new List<string>();
        foreach (var c in current.Components)
        {
            if (c.X < 1 || c.Y < 1)
            {
                risks.Add($"{c.Type} 贴边，打印可能裁切。");
            }

            if (c.X + c.W > current.Page.WidthMm - 1 || c.Y + c.H > current.Page.HeightMm - 1)
            {
                risks.Add($"{c.Type} 超出页边。");
            }
        }

        if (current.Components.Count == 0)
        {
            risks.Add("画布是空的。");
        }

        var fieldLine = binds.Count == 0 ? "尚未绑定字段" : "已绑定：" + string.Join("、", binds);
        var riskLine = risks.Count == 0 ? "未发现明显贴边/超页。" : string.Join(" ", risks.Distinct());
        var dict = string.Join("、", fields.Select(f => f.DisplayName));

        return new AgentReply
        {
            Text = $"页 {current.Page.WidthMm:0}×{current.Page.HeightMm:0} mm，{current.Components.Count} 个组件。{fieldLine}。{riskLine} 字典：{dict}。"
        };
    }

    private static PagePreset? ResolvePreset(string text, PagePreset? current)
    {
        var match = SizeRegex.Match(text);
        if (match.Success)
        {
            var w = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var h = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var known = PagePresets.All.FirstOrDefault(p =>
                Math.Abs(p.WidthMm - w) < 0.1 && Math.Abs(p.HeightMm - h) < 0.1);
            return known ?? new PagePreset { Name = "自定义", WidthMm = w, HeightMm = h };
        }

        return current;
    }

    private static FieldItem? MatchField(string text, IReadOnlyList<FieldItem> fields) =>
        fields.FirstOrDefault(f => Mentions(text, f));

    private static bool Mentions(string text, FieldItem field) =>
        text.Contains(field.DisplayName, StringComparison.OrdinalIgnoreCase)
        || text.Contains(field.Key, StringComparison.OrdinalIgnoreCase);

    private static string? FindUnknownField(string text, IReadOnlyList<FieldItem> fields)
    {
        if (!ContainsAny(text, "绑", "字段"))
        {
            return null;
        }

        foreach (var token in Regex.Split(text, @"[\s,，。；;：:]+"))
        {
            if (token.Length < 2 || token.Length > 24)
            {
                continue;
            }

            if (fields.Any(f => Mentions(token, f)))
            {
                continue;
            }

            if (token.Any(char.IsLetter) && !ContainsAny(token, "字段", "绑定", "一下"))
            {
                return token;
            }
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] parts) =>
        parts.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
}
