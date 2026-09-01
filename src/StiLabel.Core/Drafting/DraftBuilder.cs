using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;
using StiLabel.Core.Services;

namespace StiLabel.Core.Drafting;

public sealed class DraftBuilder : IDraftBuilder
{
    public LabelDocument Build(PagePreset preset, IReadOnlyList<FieldItem> selected, string? printerName) =>
        Build(preset, selected, printerName, new DraftOptions { Title = "物料标签", Barcode = true, Qr = true, Layout = "split" });

    public LabelDocument Build(PagePreset preset, IReadOnlyList<FieldItem> selected, string? printerName, DraftOptions options)
    {
        var doc = new LabelDocument
        {
            Page = new LabelPage
            {
                WidthMm = preset.WidthMm,
                HeightMm = preset.HeightMm,
                Orientation = preset.WidthMm > preset.HeightMm ? "Landscape" : "Portrait",
                PrinterName = printerName,
                MarginMm = 2
            }
        };

        var layout = NormalizeLayout(options.Layout);
        switch (layout)
        {
            case "table":
            case "material":
                LayoutNamedValue(doc, selected, options, DefaultTitle(options.Title, "物料标签"), qrRight: true);
                break;
            case "shipping":
                LayoutShipping(doc, selected, options);
                break;
            case "shelf":
                LayoutShelf(doc, selected, options);
                break;
            default:
                LayoutSplitOrStack(doc, selected, options, layout == "split");
                break;
        }

        PlaceLogo(doc, options.ImagePath);
        return doc;
    }

    public LabelDocument SetPage(LabelDocument source, double widthMm, double heightMm)
    {
        var doc = source.Clone();
        doc.Page.WidthMm = widthMm;
        doc.Page.HeightMm = heightMm;
        doc.Page.Orientation = DerivedOrientation(doc.Page);
        return doc;
    }

    public LabelDocument SetOrientation(LabelDocument source, string orientation)
    {
        var mapped = NormalizeOrientation(orientation);
        if (mapped is null)
        {
            return source;
        }

        var doc = source.Clone();
        doc.Page.Orientation = mapped;
        var wantsWide = mapped == "Landscape";
        if (wantsWide != doc.Page.WidthMm > doc.Page.HeightMm)
        {
            (doc.Page.WidthMm, doc.Page.HeightMm) = (doc.Page.HeightMm, doc.Page.WidthMm);
        }

        foreach (var item in doc.Components)
        {
            item.W = Math.Min(item.W, doc.Page.WidthMm);
            item.H = Math.Min(item.H, doc.Page.HeightMm);
        }

        Inset(doc, doc.Page.MarginMm);
        return doc;
    }

    public LabelDocument AddField(LabelDocument source, FieldItem field)
    {
        var doc = source.Clone();
        if (doc.Components.Any(c => c.Bind.Kind == BindKind.Field &&
                                    string.Equals(c.Bind.FieldKey, field.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return doc;
        }

        var m = doc.Page.MarginMm;
        if (LooksLikeNameValue(doc))
        {
            AddNameValueRow(doc, m, NextY(doc), Math.Max(18, doc.Page.WidthMm - m * 2), field);
        }
        else
        {
            Place(doc, TextRow(m, NextY(doc), doc.Page.WidthMm - m * 2, field));
        }

        return doc;
    }

    public LabelDocument AddComponent(
        LabelDocument source,
        string type,
        string? fieldKey,
        string? literal,
        double? xMm,
        double? yMm,
        double? wMm,
        double? hMm)
    {
        var doc = source.Clone();
        var kind = ParseType(type);
        var m = doc.Page.MarginMm;
        var item = new LabelComponent
        {
            Type = kind,
            X = xMm ?? m,
            Y = yMm ?? NextY(doc),
            W = wMm ?? DefaultWidth(kind, doc, m),
            H = hMm ?? DefaultHeight(kind),
            Bind = !string.IsNullOrWhiteSpace(fieldKey)
                ? new LabelBind { Kind = BindKind.Field, FieldKey = fieldKey }
                : new LabelBind { Kind = BindKind.Literal, Literal = literal ?? "" },
            BarcodeSymbology = kind == LabelComponentType.Qr ? "QR" : kind == LabelComponentType.Barcode ? "Code128" : null,
            FontSizePt = kind == LabelComponentType.Text ? 8 : 0,
            Border = kind is LabelComponentType.Rect or LabelComponentType.Ellipse
                or LabelComponentType.Triangle or LabelComponentType.RoundedRect,
            LineWidthMm = kind is LabelComponentType.Line or LabelComponentType.Rect or LabelComponentType.Ellipse ? 0.3 : 0.3
        };
        Place(doc, item);
        return doc;
    }

    public LabelDocument Remove(LabelDocument source, string target)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        doc.Components.Remove(item);
        return doc;
    }

    public LabelDocument Clear(LabelDocument source)
    {
        var doc = source.Clone();
        doc.Components.Clear();
        return doc;
    }

    public LabelDocument Move(LabelDocument source, string target, double xMm, double yMm, bool relative) =>
        SetBounds(source, target, xMm, yMm, null, null, relative);

    public LabelDocument SetBounds(
        LabelDocument source,
        string target,
        double? xMm,
        double? yMm,
        double? wMm,
        double? hMm,
        bool relative)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        if (xMm is not null)
        {
            item.X = relative ? item.X + xMm.Value : xMm.Value;
        }

        if (yMm is not null)
        {
            item.Y = relative ? item.Y + yMm.Value : yMm.Value;
        }

        if (wMm is > 0)
        {
            item.W = wMm.Value;
        }

        if (hMm is > 0)
        {
            item.H = hMm.Value;
        }

        item.X = Math.Clamp(item.X, 0, Math.Max(0, doc.Page.WidthMm - item.W));
        item.Y = Math.Clamp(item.Y, 0, Math.Max(0, doc.Page.HeightMm - item.H));
        return doc;
    }

    public LabelDocument BindField(LabelDocument source, string target, string fieldKey)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        item.Bind = new LabelBind { Kind = BindKind.Field, FieldKey = fieldKey };
        item.Expression = null;
        return doc;
    }

    public LabelDocument Unbind(LabelDocument source, string target, string? literal = null)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        item.Bind = new LabelBind
        {
            Kind = BindKind.Literal,
            Literal = string.IsNullOrWhiteSpace(literal) ? item.Bind.Literal ?? item.Bind.FieldKey ?? "" : literal
        };
        item.Expression = null;
        return doc;
    }

    public LabelDocument SameSize(LabelDocument source, string[] targets)
    {
        var doc = source.Clone();
        var items = ResolveMany(doc, targets);
        if (items.Count < 2)
        {
            return source;
        }

        var reference = items[0];
        foreach (var item in items.Skip(1))
        {
            item.W = reference.W;
            item.H = reference.H;
            item.X = Math.Clamp(item.X, 0, Math.Max(0, doc.Page.WidthMm - item.W));
            item.Y = Math.Clamp(item.Y, 0, Math.Max(0, doc.Page.HeightMm - item.H));
        }

        return doc;
    }

    public LabelDocument CopyStyle(LabelDocument source, string from, string[] to)
    {
        var doc = source.Clone();
        var style = FindComponent(doc, from);
        var targets = ResolveMany(doc, to);
        if (style is null || targets.Count == 0)
        {
            return source;
        }

        foreach (var item in targets)
        {
            if (ReferenceEquals(item, style))
            {
                continue;
            }

            item.FontSizePt = style.FontSizePt;
            item.Bold = style.Bold;
            item.Italic = style.Italic;
            item.Underline = style.Underline;
            item.FontName = style.FontName;
            item.TextAlign = style.TextAlign;
            item.VertAlign = style.VertAlign;
            item.TextFit = style.TextFit;
            item.ForeColor = style.ForeColor;
            item.FillColor = style.FillColor;
            item.Border = style.Border;
            item.BorderColor = style.BorderColor;
        }

        return doc;
    }

    public LabelDocument FitPage(LabelDocument source)
    {
        var doc = source.Clone();
        var m = doc.Page.MarginMm;
        foreach (var item in doc.Components)
        {
            item.W = Math.Min(item.W, Math.Max(2, doc.Page.WidthMm - m * 2));
            item.H = Math.Min(item.H, Math.Max(2, doc.Page.HeightMm - m * 2));
        }

        Inset(doc, m);
        return doc;
    }

    public LabelDocument SetLiteral(LabelDocument source, string target, string text)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target) ?? doc.Components.FirstOrDefault(c => c.Bind.Kind == BindKind.Literal);
        if (item is null)
        {
            return AddComponent(source, "text", null, text, null, null, null, null);
        }

        item.Bind = new LabelBind { Kind = BindKind.Literal, Literal = text };
        item.Expression = null;
        return doc;
    }

    public LabelDocument SetFont(
        LabelDocument source,
        string target,
        double? sizePt,
        bool? bold,
        string? fontName = null,
        bool? italic = null,
        bool? underline = null)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        if (sizePt is > 0)
        {
            item.FontSizePt = sizePt.Value;
            if (item.H < sizePt.Value * 0.45)
            {
                item.H = Math.Max(item.H, sizePt.Value * 0.5);
            }
        }

        if (bold is not null)
        {
            item.Bold = bold.Value;
        }

        if (!string.IsNullOrWhiteSpace(fontName))
        {
            item.FontName = NormalizeFontName(fontName) ?? item.FontName;
        }

        if (italic is not null)
        {
            item.Italic = italic.Value;
        }

        if (underline is not null)
        {
            item.Underline = underline.Value;
        }

        return doc;
    }

    public LabelDocument SetTextAlign(LabelDocument source, string target, string align)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        var mapped = NormalizeAlign(align);
        if (item is null || mapped is null)
        {
            return source;
        }

        item.TextAlign = mapped;
        return doc;
    }

    public LabelDocument SetTextFit(LabelDocument source, string target, string mode)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        var mapped = NormalizeTextFit(mode);
        if (item is null || mapped is null)
        {
            return source;
        }

        item.TextFit = mapped;
        return doc;
    }

    public LabelDocument SetRotation(LabelDocument source, string target, double degrees)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        var next = NormalizeRotation(degrees);
        var wasTall = IsTall(item.Rotation);
        var nowTall = IsTall(next);
        if (wasTall != nowTall)
        {
            (item.W, item.H) = (item.H, item.W);
        }

        item.Rotation = next;
        item.X = Math.Clamp(item.X, 0, Math.Max(0, doc.Page.WidthMm - item.W));
        item.Y = Math.Clamp(item.Y, 0, Math.Max(0, doc.Page.HeightMm - item.H));
        return doc;
    }

    public LabelDocument SetColor(LabelDocument source, string target, string color)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        var hex = NormalizeColor(color);
        if (item is null || hex is null)
        {
            return source;
        }

        item.ForeColor = hex;
        return doc;
    }

    public LabelDocument SetMargin(LabelDocument source, double marginMm)
    {
        var doc = source.Clone();
        doc.Page.MarginMm = Math.Clamp(marginMm, 0, Math.Min(doc.Page.WidthMm, doc.Page.HeightMm) / 3);
        Inset(doc, doc.Page.MarginMm);
        return doc;
    }

    public LabelDocument SetPrinter(LabelDocument source, string? printerName, double? marginMm)
    {
        var doc = source.Clone();
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            doc.Page.PrinterName = printerName.Trim();
        }

        return marginMm is >= 0 ? SetMargin(doc, marginMm.Value) : doc;
    }

    public LabelDocument SetBarcode(LabelDocument source, string target, string symbology)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target)
                   ?? doc.Components.FirstOrDefault(c => c.Type is LabelComponentType.Barcode or LabelComponentType.Qr);
        if (item is null)
        {
            return source;
        }

        var kind = NormalizeSymbology(symbology);
        if (kind is null)
        {
            return source;
        }

        item.BarcodeSymbology = kind;
        item.Type = IsMatrixSymbology(kind) ? LabelComponentType.Qr : LabelComponentType.Barcode;
        return doc;
    }

    public LabelDocument SetBarcodeOptions(LabelDocument source, string target, bool? showText)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target)
                   ?? doc.Components.FirstOrDefault(c => c.Type is LabelComponentType.Barcode or LabelComponentType.Qr);
        if (item is null)
        {
            return source;
        }

        if (showText is not null)
        {
            item.ShowLabelText = showText.Value;
        }

        return doc;
    }

    public LabelDocument SetLine(LabelDocument source, string target, double? widthMm, string? color)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target)
                   ?? doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Line);
        if (item is null)
        {
            return source;
        }

        if (widthMm is > 0)
        {
            item.LineWidthMm = widthMm.Value;
            if (item.Type == LabelComponentType.Line)
            {
                item.H = Math.Max(item.H, widthMm.Value);
            }
        }

        if (NormalizeColor(color) is { } hex)
        {
            item.ForeColor = hex;
        }

        return doc;
    }

    public LabelDocument SetBorder(LabelDocument source, string target, bool? enabled, double? widthMm, string? color)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        if (enabled is not null)
        {
            item.Border = enabled.Value;
        }

        if (widthMm is > 0)
        {
            item.LineWidthMm = widthMm.Value;
        }

        if (NormalizeColor(color) is { } hex)
        {
            item.BorderColor = hex;
        }

        return doc;
    }

    public LabelDocument SetVertAlign(LabelDocument source, string target, string align)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        var mapped = NormalizeVertAlign(align);
        if (item is null || mapped is null)
        {
            return source;
        }

        item.VertAlign = mapped;
        return doc;
    }

    public LabelDocument SetLocked(LabelDocument source, string target, bool locked)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        item.Locked = locked;
        return doc;
    }

    public LabelDocument SetVisible(LabelDocument source, string target, bool visible)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        item.Visible = visible;
        return doc;
    }

    public LabelDocument SetExpression(LabelDocument source, string target, string? expression)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        item.Expression = string.IsNullOrWhiteSpace(expression) ? null : expression.Trim();
        return doc;
    }

    public LabelDocument SetEnabledWhen(LabelDocument source, string target, string? expression)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        item.EnabledWhen = string.IsNullOrWhiteSpace(expression) ? null : expression.Trim();
        return doc;
    }

    public LabelDocument SetVariable(LabelDocument source, string name, string value, string dataType = "text")
    {
        var key = name.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return source;
        }

        var doc = source.Clone();
        var item = doc.Variables.FirstOrDefault(v => v.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new LabelVariable { Name = key };
            doc.Variables.Add(item);
        }

        item.Value = value ?? "";
        item.DataType = NormalizeVariableType(dataType) ?? item.DataType;
        return doc;
    }

    public LabelDocument RemoveVariable(LabelDocument source, string name)
    {
        var doc = source.Clone();
        doc.Variables.RemoveAll(v => v.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return doc;
    }

    public LabelDocument SetFill(LabelDocument source, string target, string? color)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        var token = color?.Trim() ?? "";
        if (token.Length == 0 || token is "none" or "透明" or "无" or "clear")
        {
            item.FillColor = "";
            return doc;
        }

        if (NormalizeColor(token) is not { } hex)
        {
            return source;
        }

        item.FillColor = hex;
        return doc;
    }

    public LabelDocument SetZ(LabelDocument source, string target, string layer)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        var max = doc.Components.Count == 0 ? 0 : doc.Components.Max(c => c.Z);
        var min = doc.Components.Count == 0 ? 0 : doc.Components.Min(c => c.Z);
        var token = layer.Trim().ToLowerInvariant();
        item.Z = token switch
        {
            "front" or "top" or "上" or "前" => max + 1,
            "back" or "bottom" or "下" or "后" => min - 1,
            _ when int.TryParse(layer, out var z) => z,
            _ => item.Z
        };
        return doc;
    }

    public LabelDocument Align(LabelDocument source, string[] targets, string edge)
    {
        var doc = source.Clone();
        var items = ResolveMany(doc, targets);
        if (items.Count == 0)
        {
            return source;
        }

        var mode = NormalizeEdge(edge);
        if (mode is null)
        {
            return source;
        }

        double left;
        double top;
        double right;
        double bottom;
        if (items.Count == 1)
        {
            var m = doc.Page.MarginMm;
            left = m;
            top = m;
            right = doc.Page.WidthMm - m;
            bottom = doc.Page.HeightMm - m;
        }
        else
        {
            var reference = items[0];
            left = reference.X;
            top = reference.Y;
            right = reference.X + reference.W;
            bottom = reference.Y + reference.H;
            items = items.Skip(1).ToList();
        }

        foreach (var item in items)
        {
            switch (mode)
            {
                case "left":
                    item.X = left;
                    break;
                case "right":
                    item.X = right - item.W;
                    break;
                case "top":
                    item.Y = top;
                    break;
                case "bottom":
                    item.Y = bottom - item.H;
                    break;
                case "center-x":
                    item.X = left + (right - left - item.W) / 2;
                    break;
                case "center-y":
                    item.Y = top + (bottom - top - item.H) / 2;
                    break;
            }

            item.X = Math.Clamp(item.X, 0, Math.Max(0, doc.Page.WidthMm - item.W));
            item.Y = Math.Clamp(item.Y, 0, Math.Max(0, doc.Page.HeightMm - item.H));
        }

        return doc;
    }

    public LabelDocument Distribute(LabelDocument source, string[] targets, string axis)
    {
        var doc = source.Clone();
        var items = ResolveMany(doc, targets);
        if (items.Count < 3)
        {
            return source;
        }

        var horizontal = axis.Trim().ToLowerInvariant() is "h" or "x" or "horizontal" or "横向" or "水平";
        if (horizontal)
        {
            items.Sort((a, b) => a.X.CompareTo(b.X));
            var span = items[^1].X + items[^1].W - items[0].X;
            var total = items.Sum(i => i.W);
            var gap = Math.Max(0, (span - total) / (items.Count - 1));
            var x = items[0].X;
            foreach (var item in items)
            {
                item.X = x;
                x += item.W + gap;
            }
        }
        else
        {
            items.Sort((a, b) => a.Y.CompareTo(b.Y));
            var span = items[^1].Y + items[^1].H - items[0].Y;
            var total = items.Sum(i => i.H);
            var gap = Math.Max(0, (span - total) / (items.Count - 1));
            var y = items[0].Y;
            foreach (var item in items)
            {
                item.Y = y;
                y += item.H + gap;
            }
        }

        return doc;
    }

    public LabelDocument Duplicate(LabelDocument source, string target, double offsetXMm = 2, double offsetYMm = 2)
    {
        var doc = source.Clone();
        var item = FindComponent(doc, target);
        if (item is null)
        {
            return source;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(item);
        var copy = System.Text.Json.JsonSerializer.Deserialize<LabelComponent>(json);
        if (copy is null)
        {
            return source;
        }

        copy.Id = Guid.NewGuid().ToString("N")[..8];
        copy.X = item.X + offsetXMm;
        copy.Y = item.Y + offsetYMm;
        Place(doc, copy);
        return doc;
    }

    public LabelDocument Swap(LabelDocument source, string a, string b)
    {
        var doc = source.Clone();
        var left = FindComponent(doc, a);
        var right = FindComponent(doc, b);
        if (left is null || right is null || ReferenceEquals(left, right))
        {
            return source;
        }

        (left.X, right.X) = (right.X, left.X);
        (left.Y, right.Y) = (right.Y, left.Y);
        return doc;
    }

    public static LabelComponent? FindComponent(LabelDocument doc, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        if (target is "标题" or "title")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Text && c.Bind.Kind == BindKind.Literal);
        }

        if (target is "二维码" or "qr" or "QR")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Qr);
        }

        if (target is "条码" or "barcode" or "Barcode")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Barcode);
        }

        if (target is "图片" or "图" or "logo" or "image" or "Image")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Image);
        }

        if (target is "线" or "line" or "Line")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Line);
        }

        if (target is "框" or "边框" or "rect" or "box")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Rect);
        }

        if (target is "圆" or "椭圆" or "circle" or "ellipse" or "oval")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Ellipse);
        }

        if (target is "勾选" or "复选" or "checkbox" or "check" or "勾")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.CheckBox);
        }

        if (target is "三角" or "三角形" or "triangle")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Triangle);
        }

        if (target is "圆角" or "圆角框" or "rounded" or "roundrect")
        {
            return doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.RoundedRect);
        }

        return doc.Components.FirstOrDefault(c =>
            c.Id.Equals(target, StringComparison.OrdinalIgnoreCase)
            || (c.Bind.FieldKey?.Equals(target, StringComparison.OrdinalIgnoreCase) ?? false)
            || (c.Bind.Literal?.Equals(target, StringComparison.OrdinalIgnoreCase) ?? false)
            || c.Type.ToString().Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    private static void Place(LabelDocument doc, LabelComponent item)
    {
        item.X = Math.Clamp(item.X, 0, Math.Max(0, doc.Page.WidthMm - item.W));
        item.Y = Math.Clamp(item.Y, 0, Math.Max(0, doc.Page.HeightMm - item.H));
        item.Z = doc.Components.Count == 0 ? 0 : doc.Components.Max(c => c.Z) + 1;
        doc.Components.Add(item);
    }

    private static void Inset(LabelDocument doc, double margin)
    {
        foreach (var item in doc.Components)
        {
            item.X = Math.Clamp(item.X, margin, Math.Max(margin, doc.Page.WidthMm - margin - item.W));
            item.Y = Math.Clamp(item.Y, margin, Math.Max(margin, doc.Page.HeightMm - margin - item.H));
        }
    }

    public static List<LabelComponent> ResolveMany(LabelDocument doc, string[]? targets)
    {
        var items = new List<LabelComponent>();
        if (targets is null || targets.Length == 0)
        {
            return items;
        }

        foreach (var target in targets)
        {
            var item = FindComponent(doc, target);
            if (item is not null && !items.Contains(item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    public static string? NormalizeFontName(string? name)
    {
        var t = name?.Trim() ?? "";
        if (t.Length == 0)
        {
            return null;
        }

        return t switch
        {
            "雅黑" or "微软雅黑" or "Microsoft YaHei" => "Microsoft YaHei",
            "黑体" or "SimHei" => "SimHei",
            "宋体" or "SimSun" => "SimSun",
            "楷体" or "KaiTi" => "KaiTi",
            "仿宋" or "FangSong" => "FangSong",
            _ => t
        };
    }

    public static string? NormalizeVertAlign(string? align)
    {
        var t = align?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            "top" or "上" or "顶" => "top",
            "middle" or "center" or "中" or "居中" => "middle",
            "bottom" or "下" or "底" => "bottom",
            _ => null
        };
    }

    public static string DerivedOrientation(LabelPage page) =>
        page.WidthMm > page.HeightMm ? "Landscape" : "Portrait";

    public static string? NormalizeOrientation(string? orientation)
    {
        var t = orientation?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            "portrait" or "纵向" or "竖" or "竖向" or "竖排" => "Portrait",
            "landscape" or "横向" or "横" or "横排" => "Landscape",
            _ => null
        };
    }

    public static string? NormalizeTextFit(string? mode)
    {
        var t = mode?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            "wrap" or "换行" or "自动换行" => "wrap",
            "shrink" or "缩字" or "自动缩字" or "缩小字号" => "shrink",
            "clip" or "裁切" or "截断" or "不换行" => "clip",
            _ => null
        };
    }

    public static string? NormalizeAlign(string? align)
    {
        var t = align?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            "left" or "左" or "左对齐" => "left",
            "center" or "居中" or "中" or "中间" => "center",
            "right" or "右" or "右对齐" => "right",
            _ => null
        };
    }

    public static double NormalizeRotation(double degrees)
    {
        var n = ((int)Math.Round(degrees / 90.0) * 90) % 360;
        return n < 0 ? n + 360 : n;
    }

    public static string? NormalizeColor(string? color)
    {
        var t = color?.Trim() ?? "";
        if (t.Length == 0)
        {
            return null;
        }

        return t switch
        {
            "黑" or "黑色" or "black" => "#1C1C1C",
            "红" or "红色" or "red" => "#C62828",
            "蓝" or "蓝色" or "blue" => "#1565C0",
            "绿" or "绿色" or "green" => "#2E7D32",
            "灰" or "灰色" or "gray" or "grey" => "#616161",
            "白" or "白色" or "white" => "#FFFFFF",
            _ when t.StartsWith('#') && t.Length is 7 => t.ToUpperInvariant(),
            _ => null
        };
    }

    private static bool IsTall(double rotation) => Math.Abs(rotation % 180) is 90;

    public static string? NormalizeEdge(string? edge)
    {
        var t = edge?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            "left" or "左" or "左对齐" => "left",
            "right" or "右" or "右对齐" => "right",
            "top" or "上" or "顶对齐" => "top",
            "bottom" or "下" or "底对齐" => "bottom",
            "center" or "center-x" or "hcenter" or "居中" or "水平居中" => "center-x",
            "center-y" or "vcenter" or "垂直居中" => "center-y",
            _ => null
        };
    }

    public const string SupportedSymbologies =
        "Code128、Code39、Code39Ext、Code93、Code93Ext、Code11、Codabar、EAN13、EAN8、UPCA、UPCE、UpcSup2、UpcSup5、ITF14、I2of5、S2of5、GS1128、SSCC18、ISBN13、ISBN10、JAN13、JAN8、QR、GS1QR、DataMatrix、GS1DataMatrix、Aztec、Maxicode、PDF417、PDF417Macro、MSI、Plessey、Pharmacode、AustraliaPost、DutchKIX、FIM、IntelligentMail、Postnet、RoyalMail";

    public static bool IsMatrixSymbology(string? kind) =>
        kind is "QR" or "GS1QR" or "DataMatrix" or "GS1DataMatrix" or "Aztec" or "Maxicode";

    public static string ListSymbologies() =>
        "可用制式：" + SupportedSymbologies + "。GS1 内容用 (01)(17)(10) 括号。";

    public static string? NormalizeSymbology(string? value)
    {
        var t = value?.Trim().ToLowerInvariant() ?? "";
        return t switch
        {
            "qr" or "qrcode" or "二维码" => "QR",
            "gs1qr" or "gs1-qr" or "gs1qrcode" or "gs1-qrcode" => "GS1QR",
            "datamatrix" or "data-matrix" or "dm" => "DataMatrix",
            "gs1datamatrix" or "gs1-datamatrix" or "gs1dm" or "gs1-dm" => "GS1DataMatrix",
            "aztec" => "Aztec",
            "maxicode" or "maxi-code" or "maxi" => "Maxicode",
            "pdf417" or "pdf-417" or "pdf" => "PDF417",
            "pdf417macro" or "pdf417-macro" or "macro-pdf417" => "PDF417Macro",
            "code39" or "code-39" or "39" => "Code39",
            "code39ext" or "code39-ext" or "code-39-ext" or "code39extended" => "Code39Ext",
            "code93" or "code-93" or "93" => "Code93",
            "code93ext" or "code93-ext" or "code-93-ext" or "code93extended" => "Code93Ext",
            "code11" or "code-11" => "Code11",
            "codabar" or "nw7" or "nw-7" => "Codabar",
            "ean13" or "ean-13" or "ean" => "EAN13",
            "ean8" or "ean-8" => "EAN8",
            "upc" or "upca" or "upc-a" or "upc_a" => "UPCA",
            "upce" or "upc-e" or "upc_e" => "UPCE",
            "upcsup2" or "upc-sup2" or "ean2" or "addon2" => "UpcSup2",
            "upcsup5" or "upc-sup5" or "ean5" or "addon5" => "UpcSup5",
            "itf" or "itf14" or "itf-14" or "itf_14" => "ITF14",
            "i2of5" or "interleaved2of5" or "interleaved-2of5" or "交叉25" or "交叉二五" => "I2of5",
            "s2of5" or "standard2of5" or "standard-2of5" or "工业25" => "S2of5",
            "gs1" or "gs1128" or "gs1-128" or "gs1_128" or "ean128" or "ean-128"
                or "ucc128" or "ucc-128" or "物流码" => "GS1128",
            "sscc" or "sscc18" or "sscc-18" or "sscc_18" => "SSCC18",
            "isbn13" or "isbn-13" or "isbn" => "ISBN13",
            "isbn10" or "isbn-10" => "ISBN10",
            "jan13" or "jan-13" or "jan" => "JAN13",
            "jan8" or "jan-8" => "JAN8",
            "msi" => "MSI",
            "plessey" => "Plessey",
            "pharmacode" or "pharma" => "Pharmacode",
            "australiapost" or "australia-post" or "auspost" => "AustraliaPost",
            "dutchkix" or "dutch-kix" or "kix" => "DutchKIX",
            "fim" => "FIM",
            "intelligentmail" or "intelligent-mail" or "imail" or "usps" => "IntelligentMail",
            "postnet" => "Postnet",
            "royalmail" or "royal-mail" or "rm4scc" => "RoyalMail",
            "code128" or "code-128" or "128" or "条码" => "Code128",
            _ => null
        };
    }

    /// <summary>按制式规则校验条码内容；返回 null 表示没发现问题。</summary>
    public static string? ValidateBarcode(string? symbology, string? value)
    {
        var kind = NormalizeSymbology(symbology) ?? "Code128";
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "内容为空";
        }

        if (kind is "GS1128" or "SSCC18" or "GS1DataMatrix" or "GS1QR"
            && !text.Contains('(') && !text.Contains('['))
        {
            return "GS1 内容要带 AI 括号，例如 (01)06901234567892(10)LOT";
        }

        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        var digitsOnly = text.All(char.IsAsciiDigit);
        switch (kind)
        {
            case "EAN13" or "JAN13" or "ISBN13":
                return FixedDigits(text, digitsOnly, 13);
            case "EAN8" or "JAN8" or "UPCE":
                return FixedDigits(text, digitsOnly, 8);
            case "UPCA":
                return FixedDigits(text, digitsOnly, 12);
            case "ITF14":
                return FixedDigits(text, digitsOnly, 14);
            case "ISBN10":
                return FixedDigits(text, digitsOnly, 10);
            case "SSCC18":
                return digits.Length is 17 or 18 ? null : $"SSCC-18 需要 18 位数字，当前 {digits.Length} 位";
            case "UpcSup2":
                return FixedDigits(text, digitsOnly, 2);
            case "UpcSup5":
                return FixedDigits(text, digitsOnly, 5);
            case "I2of5":
                if (!digitsOnly)
                {
                    return "交叉 2of5 只能是数字";
                }

                return text.Length % 2 == 0 ? null : "交叉 2of5 需要偶数位数字";
            case "S2of5" or "Postnet" or "Pharmacode" or "MSI" or "Plessey":
                return digitsOnly ? null : $"{kind} 只能是数字";
            case "Code11":
                return text.All(c => char.IsAsciiDigit(c) || c == '-') ? null : "Code11 只能是数字和减号";
            case "Code39":
                const string code39 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
                return text.All(c => code39.Contains(c)) ? null : "Code39 只能是大写字母、数字和 - . 空格 $ / + %";
            case "Codabar":
                const string codabar = "0123456789-$:/.+ABCD";
                return text.ToUpperInvariant().All(c => codabar.Contains(c)) ? null : "Codabar 含不支持的字符";
            default:
                return null;
        }
    }

    private static string? FixedDigits(string text, bool digitsOnly, int length)
    {
        if (!digitsOnly)
        {
            return $"需要 {length} 位数字，当前含非数字字符";
        }

        return text.Length == length
            ? null
            : $"需要 {length} 位数字，当前 {text.Length} 位";
    }

    private static double NextY(LabelDocument doc)
    {
        var m = doc.Page.MarginMm;
        if (doc.Components.Count == 0)
        {
            return m;
        }

        var y = doc.Components.Max(c => c.Y + c.H) + 1.2;
        return y + 5 > doc.Page.HeightMm - m
            ? Math.Max(m, doc.Page.HeightMm - m - 5)
            : y;
    }

    private static double DefaultWidth(LabelComponentType type, LabelDocument doc, double margin) =>
        type switch
        {
            LabelComponentType.Qr or LabelComponentType.Image => 16,
            LabelComponentType.Ellipse or LabelComponentType.Triangle => 12,
            LabelComponentType.CheckBox => 5,
            _ => Math.Max(10, doc.Page.WidthMm - margin * 2)
        };

    private static double DefaultHeight(LabelComponentType type) => type switch
    {
        LabelComponentType.Qr or LabelComponentType.Image => 16,
        LabelComponentType.Ellipse or LabelComponentType.Triangle => 12,
        LabelComponentType.CheckBox => 5,
            LabelComponentType.Barcode => 8,
        LabelComponentType.Line => 0.6,
        LabelComponentType.Rect => 8,
        _ => 4.8
    };

    private static LabelComponentType ParseType(string type)
    {
        var t = type.Trim().ToLowerInvariant();
        return t switch
        {
            "barcode" or "bar" or "条码" => LabelComponentType.Barcode,
            "qr" or "qrcode" or "二维码" => LabelComponentType.Qr,
            "image" or "img" or "logo" or "图片" or "图" => LabelComponentType.Image,
            "line" or "线" => LabelComponentType.Line,
            "rect" or "box" or "框" => LabelComponentType.Rect,
            "ellipse" or "oval" or "circle" or "圆" or "椭圆" => LabelComponentType.Ellipse,
            "checkbox" or "check" or "勾选" or "复选" or "勾" => LabelComponentType.CheckBox,
            "triangle" or "三角" or "三角形" => LabelComponentType.Triangle,
            "rounded" or "roundrect" or "roundedrect" or "圆角" or "圆角框" => LabelComponentType.RoundedRect,
            _ => LabelComponentType.Text
        };
    }

    private static void LayoutSplitOrStack(
        LabelDocument doc,
        IReadOnlyList<FieldItem> selected,
        DraftOptions options,
        bool split)
    {
        var m = doc.Page.MarginMm;
        var pageW = doc.Page.WidthMm;
        var pageH = doc.Page.HeightMm;
        var contentW = Math.Max(10, pageW - m * 2);
        split = split && options.Qr;
        var qrSize = split ? Math.Clamp(Math.Min(pageW, pageH) * 0.32, 12, 18) : 0;
        var textW = split ? Math.Max(18, contentW - qrSize - 2) : contentW;
        var y = m;

        y = AddTitle(doc, m, y, textW, options.Title);
        foreach (var field in selected)
        {
            doc.Components.Add(TextRow(m, y, textW, field));
            y += 5.2;
        }

        var code = selected.FirstOrDefault(IsCodeLike) ?? selected.FirstOrDefault();
        AddQr(doc, code, options.Qr, split, m, pageW, pageH, qrSize, ref y);
        AddBarcode(doc, code, options.Barcode, m, contentW, pageH, y);
    }

    private static void LayoutNamedValue(
        LabelDocument doc,
        IReadOnlyList<FieldItem> selected,
        DraftOptions options,
        string? title,
        bool qrRight)
    {
        var m = doc.Page.MarginMm;
        var pageW = doc.Page.WidthMm;
        var pageH = doc.Page.HeightMm;
        var contentW = Math.Max(10, pageW - m * 2);
        var qrSize = qrRight && options.Qr ? Math.Clamp(Math.Min(pageW, pageH) * 0.32, 12, 18) : 0;
        var textW = qrSize > 0 ? Math.Max(22, contentW - qrSize - 2) : contentW;
        var y = AddTitle(doc, m, m, textW, title);
        foreach (var field in selected)
        {
            AddNameValueRow(doc, m, y, textW, field);
            y += 5.4;
        }

        var code = selected.FirstOrDefault(IsCodeLike) ?? selected.FirstOrDefault();
        AddQr(doc, code, options.Qr && qrRight, qrRight, m, pageW, pageH, qrSize, ref y);
        AddBarcode(doc, code, options.Barcode, m, contentW, pageH, y);
    }

    private static void LayoutShipping(LabelDocument doc, IReadOnlyList<FieldItem> selected, DraftOptions options)
    {
        var m = doc.Page.MarginMm;
        var pageW = doc.Page.WidthMm;
        var pageH = doc.Page.HeightMm;
        var contentW = Math.Max(10, pageW - m * 2);
        var qrSize = options.Qr ? Math.Clamp(Math.Min(pageW, pageH) * 0.30, 12, 18) : 0;
        var textW = qrSize > 0 ? Math.Max(22, contentW - qrSize - 2) : contentW;
        var code = selected.FirstOrDefault(IsCodeLike) ?? selected.FirstOrDefault();
        var rest = selected.Where(f => f != code).ToList();
        var y = AddTitle(doc, m, m, textW, DefaultTitle(options.Title, "出货标签"));
        if (code is not null)
        {
            doc.Components.Add(new LabelComponent
            {
                Type = LabelComponentType.Barcode,
                X = m,
                Y = y,
                W = textW,
                H = 9,
                BarcodeSymbology = "Code128",
                Bind = FieldBind(code)
            });
            y += 10.2;
        }

        foreach (var field in rest)
        {
            AddNameValueRow(doc, m, y, textW, field);
            y += 5.4;
        }

        AddQr(doc, code, options.Qr, true, m, pageW, pageH, qrSize, ref y);
    }

    private static void LayoutShelf(LabelDocument doc, IReadOnlyList<FieldItem> selected, DraftOptions options)
    {
        var m = doc.Page.MarginMm;
        var pageW = doc.Page.WidthMm;
        var pageH = doc.Page.HeightMm;
        var contentW = Math.Max(10, pageW - m * 2);
        var qrSize = options.Qr ? Math.Clamp(Math.Min(pageW, pageH) * 0.34, 14, 20) : 0;
        var textW = qrSize > 0 ? Math.Max(20, contentW - qrSize - 2) : contentW;
        var code = selected.FirstOrDefault(IsCodeLike) ?? selected.FirstOrDefault();
        var rest = selected.Where(f => f != code).ToList();
        var y = AddTitle(doc, m, m, textW, string.IsNullOrWhiteSpace(options.Title) ? null : options.Title);
        if (code is not null)
        {
            doc.Components.Add(new LabelComponent
            {
                Type = LabelComponentType.Text,
                X = m,
                Y = y,
                W = textW,
                H = 10,
                FontSizePt = 16,
                Bold = true,
                TextAlign = "center",
                VertAlign = "middle",
                Bind = FieldBind(code)
            });
            y += 11.2;
        }

        foreach (var field in rest)
        {
            AddNameValueRow(doc, m, y, textW, field);
            y += 5.4;
        }

        AddQr(doc, code, options.Qr, true, m, pageW, pageH, qrSize, ref y);
        AddBarcode(doc, code, options.Barcode, m, contentW, pageH, y);
    }

    private static double AddTitle(LabelDocument doc, double x, double y, double w, string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return y;
        }

        doc.Components.Add(new LabelComponent
        {
            Type = LabelComponentType.Text,
            X = x,
            Y = y,
            W = w,
            H = 6,
            FontSizePt = 11,
            Bold = true,
            Bind = new LabelBind { Kind = BindKind.Literal, Literal = title.Trim() }
        });
        return y + 7;
    }

    private static void AddNameValueRow(LabelDocument doc, double x, double y, double w, FieldItem field)
    {
        var labelW = Math.Clamp(w * 0.34, 10, 16);
        doc.Components.Add(new LabelComponent
        {
            Type = LabelComponentType.Text,
            X = x,
            Y = y,
            W = labelW,
            H = 5,
            FontSizePt = 7,
            ForeColor = "#5E6772",
            VertAlign = "middle",
            Bind = new LabelBind { Kind = BindKind.Literal, Literal = field.DisplayName }
        });
        doc.Components.Add(new LabelComponent
        {
            Type = LabelComponentType.Text,
            X = x + labelW + 0.6,
            Y = y,
            W = Math.Max(8, w - labelW - 0.6),
            H = 5,
            FontSizePt = 8,
            Bold = true,
            VertAlign = "middle",
            Bind = FieldBind(field)
        });
    }

    private static void AddQr(
        LabelDocument doc,
        FieldItem? code,
        bool enabled,
        bool right,
        double m,
        double pageW,
        double pageH,
        double qrSize,
        ref double y)
    {
        if (!enabled || code is null)
        {
            return;
        }

        var size = right ? qrSize : Math.Clamp(pageH - y - m, 12, 18);
        if (size < 10)
        {
            size = 12;
        }

        doc.Components.Add(new LabelComponent
        {
            Type = LabelComponentType.Qr,
            X = right ? pageW - m - size : m,
            Y = right ? m : y,
            W = size,
            H = size,
            BarcodeSymbology = "QR",
            Bind = FieldBind(code)
        });
        if (!right)
        {
            y += size + 1.2;
        }
    }

    private static void AddBarcode(
        LabelDocument doc,
        FieldItem? code,
        bool enabled,
        double m,
        double contentW,
        double pageH,
        double y)
    {
        if (!enabled || code is null)
        {
            return;
        }

        var barY = Math.Clamp(Math.Max(y + 0.6, pageH - m - 9), m, Math.Max(m, pageH - m - 8));
        doc.Components.Add(new LabelComponent
        {
            Type = LabelComponentType.Barcode,
            X = m,
            Y = barY,
            W = contentW,
            H = 8,
            BarcodeSymbology = "Code128",
            Bind = FieldBind(code)
        });
    }

    private static void PlaceLogo(LabelDocument doc, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        var m = doc.Page.MarginMm;
        var size = Math.Clamp(Math.Min(doc.Page.WidthMm, doc.Page.HeightMm) * 0.22, 10, 16);
        Place(doc, new LabelComponent
        {
            Type = LabelComponentType.Image,
            X = doc.Page.WidthMm - m - size,
            Y = m,
            W = size,
            H = size,
            Bind = new LabelBind { Kind = BindKind.Literal, Literal = imagePath.Trim() }
        });
    }

    private static bool LooksLikeNameValue(LabelDocument doc) =>
        doc.Components.Any(label =>
            label.Type == LabelComponentType.Text
            && label.Bind.Kind == BindKind.Literal
            && doc.Components.Any(value =>
                value.Type == LabelComponentType.Text
                && value.Bind.Kind == BindKind.Field
                && Math.Abs(value.Y - label.Y) < 0.4
                && value.X > label.X));

    private static string? DefaultTitle(string? title, string fallback) =>
        title is null ? fallback : string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    private static string NormalizeLayout(string? layout)
    {
        var t = layout?.Trim().ToLowerInvariant() ?? "split";
        return t switch
        {
            "table" or "grid" or "表" or "表格式" or "名称值" => "table",
            "material" or "物料" or "物料标签" => "material",
            "shipping" or "ship" or "出货" or "出货标签" or "物流" => "shipping",
            "shelf" or "bin" or "货架" or "仓位" or "库位" => "shelf",
            "stack" or "竖" or "上下" => "stack",
            _ => "split"
        };
    }

    private static string? NormalizeVariableType(string? dataType)
    {
        var t = dataType?.Trim().ToLowerInvariant();
        return t switch
        {
            "number" or "num" or "int" or "数字" or "数值" => "number",
            "bool" or "boolean" or "开关" or "布尔" => "bool",
            "date" or "datetime" or "日期" => "date",
            "text" or "string" or "文本" or null or "" => "text",
            _ => "text"
        };
    }

    private static LabelComponent TextRow(double x, double y, double w, FieldItem field) =>
        new()
        {
            Type = LabelComponentType.Text,
            X = x,
            Y = y,
            W = w,
            H = 4.8,
            FontSizePt = 8,
            Bind = FieldBind(field)
        };

    private static LabelBind FieldBind(FieldItem field) =>
        new() { Kind = BindKind.Field, FieldKey = field.Key };

    private static bool IsCodeLike(FieldItem field)
    {
        var text = $"{field.Key}{field.DisplayName}";
        return text.Contains("编码", StringComparison.OrdinalIgnoreCase)
               || text.Contains("SKU", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Code", StringComparison.OrdinalIgnoreCase);
    }
}
