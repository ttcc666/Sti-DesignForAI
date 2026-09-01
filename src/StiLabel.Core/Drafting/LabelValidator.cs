using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;

namespace StiLabel.Core.Drafting;

public sealed class LabelCheck
{
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<string> Skipped { get; init; } = [];
    public bool Passed => Problems.Count == 0;

    public string Summary
    {
        get
        {
            var tail = Skipped.Count == 0 ? "" : "\n未校验：" + string.Join("；", Skipped);
            return Passed
                ? "体检通过，未发现问题。" + tail
                : $"发现 {Problems.Count} 个问题：\n" + string.Join("\n", Problems) + tail;
        }
    }
}

public static class LabelValidator
{
    public static LabelCheck Validate(LabelDocument document, IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        if (document.Components.Count == 0)
        {
            return new LabelCheck();
        }

        var problems = new List<string>();
        var skipped = new List<string>();
        var margin = Math.Max(0, document.Page.MarginMm);

        for (var i = 0; i < document.Components.Count; i++)
        {
            var c = document.Components[i];
            var who = Describe(c);

            if (c.Bind.Kind == BindKind.Field)
            {
                var key = c.Bind.FieldKey;
                if (string.IsNullOrWhiteSpace(key))
                {
                    problems.Add($"{who}：绑定了字段但 key 为空");
                }
                else if (FindField(key, fields) is null)
                {
                    problems.Add($"{who}：字典里没有字段 {key}");
                }
            }

            var content = ResolveContent(c, fields, sample);
            switch (c.Type)
            {
                case LabelComponentType.Barcode or LabelComponentType.Qr:
                    if (content is null)
                    {
                        skipped.Add($"{who}：无示例值，未校验内容");
                    }
                    else if (DraftBuilder.ValidateBarcode(c.BarcodeSymbology, content) is { } codeError)
                    {
                        problems.Add($"{who}：{codeError}");
                    }

                    break;
                case LabelComponentType.Image:
                    if (c.Bind.Kind == BindKind.Literal && ImageSourceError(c.Bind.Literal) is { } imageError)
                    {
                        problems.Add($"{who}：{imageError}");
                    }

                    break;
                case LabelComponentType.Text:
                    if (c.Bind.Kind == BindKind.Literal
                        && string.IsNullOrWhiteSpace(c.Bind.Literal)
                        && string.IsNullOrWhiteSpace(c.Expression))
                    {
                        problems.Add($"{who}：文字为空");
                    }

                    break;
            }

            if (c.X < 0 || c.Y < 0
                || c.X + c.W > document.Page.WidthMm + 0.01
                || c.Y + c.H > document.Page.HeightMm + 0.01)
            {
                problems.Add($"{who}：超出页面");
            }
            else if (c.X < margin || c.Y < margin
                     || c.X + c.W > document.Page.WidthMm - margin
                     || c.Y + c.H > document.Page.HeightMm - margin)
            {
                problems.Add($"{who}：压到边距（{margin:0.#} mm）");
            }

            for (var j = i + 1; j < document.Components.Count; j++)
            {
                var other = document.Components[j];
                if (!c.Visible || !other.Visible)
                {
                    continue;
                }

                if (Overlaps(c, other))
                {
                    problems.Add($"{who} 与 {Describe(other)} 明显重叠");
                }
            }
        }

        return new LabelCheck { Problems = problems, Skipped = skipped };
    }

    private static bool Overlaps(LabelComponent a, LabelComponent b)
    {
        const double slack = 0.4;
        return a.X + slack < b.X + b.W
               && b.X + slack < a.X + a.W
               && a.Y + slack < b.Y + b.H
               && b.Y + slack < a.Y + a.H;
    }

    private static string? ResolveContent(LabelComponent item, IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        if (item.Bind.Kind == BindKind.Literal)
        {
            return item.Bind.Literal ?? "";
        }

        var key = item.Bind.FieldKey;
        if (string.IsNullOrWhiteSpace(key) || sample is null)
        {
            return null;
        }

        if (sample.Values.TryGetValue(key, out var byKey))
        {
            return byKey;
        }

        var field = FindField(key, fields);
        return field is not null && sample.Values.TryGetValue(field.DisplayName, out var byName)
            ? byName
            : null;
    }

    private static FieldItem? FindField(string key, IReadOnlyList<FieldItem> fields) =>
        fields.FirstOrDefault(f =>
            f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
            || f.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase));

    private static string? ImageSourceError(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "图片路径为空";
        }

        var value = path.Trim().Trim('"');
        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(value) ? null : "找不到图片文件：" + value;
    }

    private static string Describe(LabelComponent item)
    {
        var title = item.Bind.FieldKey ?? item.Bind.Literal ?? item.Expression ?? item.Type.ToString();
        if (title.Length > 16)
        {
            title = title[..16];
        }

        return $"{title}({item.Type})";
    }
}
