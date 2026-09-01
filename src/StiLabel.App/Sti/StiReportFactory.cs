using System.Data;
using System.Drawing.Printing;
using System.Text.RegularExpressions;
using StiLabel.Core.Catalog;
using StiLabel.Core.Drafting;
using StiLabel.Core.Labeling;
using Stimulsoft.Base.Drawing;
using Stimulsoft.Report;
using Stimulsoft.Report.BarCodes;
using Stimulsoft.Report.Components;
using Stimulsoft.Report.Components.ShapeTypes;
using Stimulsoft.Report.Dictionary;
using Stimulsoft.Report.Units;

namespace StiLabel.App.Sti;

public static class StiReportFactory
{
    public const string DataName = "LabelData";

    public static StiReport FromDocument(
        LabelDocument document,
        IReadOnlyList<FieldItem> fields,
        SampleRow? sample)
    {
        var report = new StiReport
        {
            ReportUnit = StiReportUnitType.Millimeters,
            ReportName = "Label"
        };
        report.Pages.Clear();

        var page = new StiPage(report)
        {
            Name = "Page1",
            PaperSize = PaperKind.Custom,
            Orientation = (DraftBuilder.NormalizeOrientation(document.Page.Orientation)
                           ?? DraftBuilder.DerivedOrientation(document.Page)) == "Landscape"
                ? StiPageOrientation.Landscape
                : StiPageOrientation.Portrait,
            PageWidth = document.Page.WidthMm,
            PageHeight = document.Page.HeightMm,
            Margins = new StiMargins(0)
        };
        report.Pages.Add(page);
        report.ReportUnit = StiReportUnitType.Millimeters;

        // 组件放在 DataBand 上，预览时官方检查器才不会因「页上无数据带」弹窗
        var band = new StiDataBand
        {
            Name = "Data",
            DataSourceName = DataName,
            Height = document.Page.HeightMm,
            CanGrow = false,
            CanBreak = false
        };
        page.Components.Add(band);
        foreach (var item in document.Components.OrderBy(c => c.Z))
        {
            band.Components.Add(CreateComponent(item));
        }

        RegisterSample(report, fields, sample);
        RegisterVariables(report, document.Variables);
        return report;
    }

    public static IReadOnlyList<FieldItem> ExtractFields(StiReport report)
    {
        var items = new Dictionary<string, FieldItem>(StringComparer.OrdinalIgnoreCase);
        if (report.Dictionary?.DataSources is not null)
        {
            foreach (StiDataSource source in report.Dictionary.DataSources)
            {
                foreach (StiDataColumn column in source.Columns)
                {
                    AddExtracted(items, column.Name, column.Alias, column.Type);
                }
            }
        }

        if (report.Dictionary?.Variables is not null)
        {
            foreach (StiVariable variable in report.Dictionary.Variables)
            {
                if (IsReserved(variable.Name))
                {
                    continue;
                }

                AddExtracted(items, variable.Name, variable.Alias, variable.Type);
            }
        }

        foreach (StiComponent component in report.GetComponents())
        {
            foreach (var raw in ComponentTexts(component))
            {
                foreach (var key in ParseFieldKeys(raw))
                {
                    AddExtracted(items, key, null, typeof(string));
                }
            }
        }

        return items.Values.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<FieldItem> ExtractFields(LabelDocument document)
    {
        var items = new Dictionary<string, FieldItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in document.Components)
        {
            if (component.Bind.Kind == BindKind.Field && !string.IsNullOrWhiteSpace(component.Bind.FieldKey))
            {
                AddExtracted(items, component.Bind.FieldKey, null, typeof(string));
            }

            foreach (var key in ParseFieldKeys(component.Bind.Literal))
            {
                AddExtracted(items, key, null, typeof(string));
            }
        }

        return items.Values.ToList();
    }

    public static void RegisterSample(StiReport report, IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        report.RegData(DataName, BuildTable(DataName, fields, sample));
        if (report.Dictionary?.DataSources is not null)
        {
            foreach (StiDataSource source in report.Dictionary.DataSources)
            {
                if (string.Equals(source.Name, DataName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var columns = source.Columns.Cast<StiDataColumn>()
                    .Select(c => new FieldItem
                    {
                        Key = c.Name,
                        DisplayName = string.IsNullOrWhiteSpace(c.Alias) ? c.Name : c.Alias
                    })
                    .ToList();
                report.RegData(source.Name, BuildTable(source.Name, columns.Count > 0 ? columns : fields, sample));
            }
        }

        report.Dictionary?.Synchronize();
    }

    public static void RegisterVariables(StiReport report, IReadOnlyList<LabelVariable> variables)
    {
        if (report.Dictionary is null)
        {
            return;
        }

        foreach (var item in variables)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || IsReserved(item.Name))
            {
                continue;
            }

            var type = item.DataType switch
            {
                "number" => typeof(double),
                "bool" => typeof(bool),
                "date" => typeof(DateTime),
                _ => typeof(string)
            };
            if (report.Dictionary.Variables.Contains(item.Name))
            {
                report.Dictionary.Variables[item.Name].Value = item.Value ?? "";
                continue;
            }

            report.Dictionary.Variables.Add(new StiVariable(item.Name, type) { Value = item.Value ?? "" });
        }

        report.Dictionary.Synchronize();
    }

    public static LabelDocument ToDocument(StiReport report)
    {
        var page = report.Pages.Count > 0 ? report.Pages[0] : new StiPage();
        var unit = report.Unit as StiMillimetersUnit ?? new StiMillimetersUnit();
        var doc = new LabelDocument
        {
            Page = new LabelPage
            {
                WidthMm = ToMm(page.PageWidth, unit),
                HeightMm = ToMm(page.PageHeight, unit),
                Orientation = page.Orientation == StiPageOrientation.Landscape ? "Landscape" : "Portrait",
                MarginMm = ToMm(page.Margins.Left, unit)
            }
        };

        foreach (StiComponent component in page.GetComponents())
        {
            if (component is StiBand or StiPage)
            {
                continue;
            }

            var mapped = MapComponent(component, unit);
            if (mapped is not null)
            {
                doc.Components.Add(mapped);
            }
        }

        if (report.Dictionary?.Variables is not null)
        {
            foreach (StiVariable variable in report.Dictionary.Variables)
            {
                if (IsReserved(variable.Name))
                {
                    continue;
                }

                doc.Variables.Add(new LabelVariable
                {
                    Name = variable.Name,
                    Value = variable.ValueObject?.ToString() ?? variable.Value ?? "",
                    DataType = MapDataType(variable.Type)
                });
            }
        }

        return doc;
    }

    private static StiComponent CreateComponent(LabelComponent item)
    {
        var rect = new RectangleD(item.X, item.Y, item.W, item.H);
        var expr = ToExpression(item);
        var name = "c_" + item.Id;

        switch (item.Type)
        {
            case LabelComponentType.Barcode:
            case LabelComponentType.Qr:
            {
                var barcode = new StiBarCode(rect)
                {
                    Name = name,
                    AutoScale = true,
                    BarCodeType = CreateBarCodeType(item)
                };
                barcode.Code.Value = expr;
                barcode.ShowLabelText = item.ShowLabelText;
                barcode.ForeColor = ParseColor(item.ForeColor);
                Unlock(barcode, item);
                return barcode;
            }
            case LabelComponentType.Image:
            {
                var picture = new StiImage(rect)
                {
                    Name = name,
                    Stretch = true,
                    AspectRatio = true
                };
                if (item.Bind.Kind == BindKind.Field && !string.IsNullOrWhiteSpace(item.Bind.FieldKey))
                {
                    picture.DataColumn = DataName + "." + item.Bind.FieldKey;
                }
                else
                {
                    var path = item.Bind.Literal ?? "";
                    if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        picture.ImageURL.Value = path;
                    }
                    else
                    {
                        picture.File = path;
                    }
                }

                Unlock(picture, item);
                return picture;
            }
            case LabelComponentType.Line:
            {
                var line = new StiHorizontalLinePrimitive(rect)
                {
                    Name = name,
                    Size = (float)Math.Max(0.3, item.LineWidthMm),
                    Color = ParseColor(item.ForeColor)
                };
                Unlock(line, item);
                return line;
            }
            case LabelComponentType.Ellipse:
            {
                var shape = new StiShape(rect)
                {
                    Name = name,
                    ShapeType = new StiOvalShapeType(),
                    Size = (float)Math.Max(0.3, item.LineWidthMm),
                    BorderColor = ParseColor(string.IsNullOrWhiteSpace(item.BorderColor) ? item.ForeColor : item.BorderColor),
                    Brush = string.IsNullOrWhiteSpace(item.FillColor)
                        ? new StiEmptyBrush()
                        : new StiSolidBrush(ParseColor(item.FillColor))
                };
                Unlock(shape, item);
                return shape;
            }
            case LabelComponentType.Triangle:
            {
                var shape = new StiShape(rect)
                {
                    Name = name,
                    ShapeType = new StiTriangleShapeType(),
                    Size = (float)Math.Max(0.3, item.LineWidthMm),
                    BorderColor = ParseColor(string.IsNullOrWhiteSpace(item.BorderColor) ? item.ForeColor : item.BorderColor),
                    Brush = string.IsNullOrWhiteSpace(item.FillColor)
                        ? new StiEmptyBrush()
                        : new StiSolidBrush(ParseColor(item.FillColor))
                };
                Unlock(shape, item);
                return shape;
            }
            case LabelComponentType.RoundedRect:
            {
                var shape = new StiShape(rect)
                {
                    Name = name,
                    ShapeType = new StiRoundedRectangleShapeType(),
                    Size = (float)Math.Max(0.3, item.LineWidthMm),
                    BorderColor = ParseColor(string.IsNullOrWhiteSpace(item.BorderColor) ? item.ForeColor : item.BorderColor),
                    Brush = string.IsNullOrWhiteSpace(item.FillColor)
                        ? new StiEmptyBrush()
                        : new StiSolidBrush(ParseColor(item.FillColor))
                };
                Unlock(shape, item);
                return shape;
            }
            case LabelComponentType.CheckBox:
            {
                var box = new StiCheckBox(rect)
                {
                    Name = name,
                    ContourColor = ParseColor(item.ForeColor),
                    TextBrush = new StiSolidBrush(ParseColor(item.ForeColor))
                };
                box.Checked.Value = ToCheckExpression(item);
                Unlock(box, item);
                return box;
            }
            case LabelComponentType.Rect:
            {
                var box = new StiText(rect)
                {
                    Name = name,
                    Text = ""
                };
                ApplyFill(box, item);
                ApplyBorder(box, item, force: true);
                Unlock(box, item);
                return box;
            }
            default:
            {
                var text = new StiText(rect)
                {
                    Name = name,
                    Text = expr,
                    Font = CreateFont(item),
                    Angle = (float)item.Rotation,
                    HorAlignment = item.TextAlign switch
                    {
                        "center" => StiTextHorAlignment.Center,
                        "right" => StiTextHorAlignment.Right,
                        _ => StiTextHorAlignment.Left
                    },
                    VertAlignment = item.VertAlign switch
                    {
                        "middle" => StiVertAlignment.Center,
                        "bottom" => StiVertAlignment.Bottom,
                        _ => StiVertAlignment.Top
                    },
                    TextBrush = new StiSolidBrush(ParseColor(item.ForeColor))
                };
                ApplyTextFit(text, item);
                ApplyFill(text, item);
                ApplyBorder(text, item, force: false);
                Unlock(text, item);
                return text;
            }
        }
    }

    private static void Unlock(StiComponent component, LabelComponent item)
    {
        component.Locked = item.Locked;
        component.Enabled = item.Visible;
        component.Restrictions = Stimulsoft.Report.Components.StiRestrictions.All;
        ApplyCondition(component, item);
    }

    private static void ApplyCondition(StiComponent component, LabelComponent item)
    {
        if (string.IsNullOrWhiteSpace(item.EnabledWhen))
        {
            return;
        }

        // 官网条件：表达式为真时套用格式。这里用「条件不成立则隐藏」。
        // https://forum.stimulsoft.com/viewtopic.php?t=55733
        component.Conditions.Add(new StiCondition
        {
            Item = StiFilterItem.Expression,
            Expression = new StiExpression("!(" + item.EnabledWhen.Trim() + ")"),
            Enabled = false
        });
    }

    private static void ApplyTextFit(StiText text, LabelComponent item)
    {
        switch (DraftBuilder.NormalizeTextFit(item.TextFit) ?? "wrap")
        {
            case "shrink":
                text.WordWrap = true;
                text.ShrinkFontToFit = true;
                text.Trimming = System.Drawing.StringTrimming.None;
                break;
            case "clip":
                text.WordWrap = false;
                text.ShrinkFontToFit = false;
                text.Trimming = System.Drawing.StringTrimming.Character;
                break;
            default:
                text.WordWrap = true;
                text.ShrinkFontToFit = false;
                text.Trimming = System.Drawing.StringTrimming.None;
                break;
        }
    }

    private static void ApplyFill(StiText text, LabelComponent item)
    {
        text.Brush = string.IsNullOrWhiteSpace(item.FillColor)
            ? new StiEmptyBrush()
            : new StiSolidBrush(ParseColor(item.FillColor));
    }

    private static void ApplyBorder(StiText text, LabelComponent item, bool force)
    {
        if (!item.Border && !force)
        {
            text.Border.Side = StiBorderSides.None;
            return;
        }

        text.Border.Side = StiBorderSides.All;
        text.Border.Size = Math.Max(0.3, item.LineWidthMm);
        text.Border.Color = ParseColor(item.BorderColor);
    }

    private static LabelComponent? MapComponent(StiComponent component, StiUnit unit)
    {
        var rect = component.ClientRectangle;
        var item = new LabelComponent
        {
            Id = component.Name.StartsWith("c_", StringComparison.Ordinal) ? component.Name[2..] : component.Name,
            X = ToMm(rect.X, unit),
            Y = ToMm(rect.Y, unit),
            W = ToMm(rect.Width, unit),
            H = ToMm(rect.Height, unit),
            Locked = component.Locked,
            Visible = component.Enabled,
            EnabledWhen = ReadEnabledWhen(component)
        };

        switch (component)
        {
            case StiImage picture:
                item.Type = LabelComponentType.Image;
                if (!string.IsNullOrWhiteSpace(picture.DataColumn))
                {
                    var column = picture.DataColumn;
                    const string prefix = DataName + ".";
                    if (column.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        column = column[prefix.Length..];
                    }

                    item.Bind = new LabelBind { Kind = BindKind.Field, FieldKey = column };
                }
                else
                {
                    var path = picture.File;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        path = picture.ImageURL?.Value;
                    }

                    item.Bind = new LabelBind { Kind = BindKind.Literal, Literal = path };
                }

                return item;
            case StiCheckBox check:
                item.Type = LabelComponentType.CheckBox;
                ApplyExpression(item, check.Checked?.Value);
                item.ForeColor = $"#{check.ContourColor.R:X2}{check.ContourColor.G:X2}{check.ContourColor.B:X2}";
                return item;
            case StiShape shape:
                item.Type = shape.ShapeType switch
                {
                    StiOvalShapeType => LabelComponentType.Ellipse,
                    StiTriangleShapeType => LabelComponentType.Triangle,
                    StiRoundedRectangleShapeType => LabelComponentType.RoundedRect,
                    _ => LabelComponentType.Rect
                };
                item.LineWidthMm = shape.Size;
                item.Border = true;
                item.BorderColor = $"#{shape.BorderColor.R:X2}{shape.BorderColor.G:X2}{shape.BorderColor.B:X2}";
                if (shape.Brush is StiSolidBrush shapeFill)
                {
                    item.FillColor = $"#{shapeFill.Color.R:X2}{shapeFill.Color.G:X2}{shapeFill.Color.B:X2}";
                }

                return item;
            case StiBarCode barcode:
                item.Type = barcode.BarCodeType is StiQRCodeBarCodeType or StiDataMatrixBarCodeType
                    or StiAztecBarCodeType or StiMaxicodeBarCodeType
                    ? LabelComponentType.Qr
                    : LabelComponentType.Barcode;
                ApplyExpression(item, barcode.Code.Value);
                item.BarcodeSymbology = barcode.BarCodeType switch
                {
                    StiGS1QRCodeBarCodeType => "GS1QR",
                    StiQRCodeBarCodeType => "QR",
                    StiGS1DataMatrixBarCodeType => "GS1DataMatrix",
                    StiDataMatrixBarCodeType => "DataMatrix",
                    StiAztecBarCodeType => "Aztec",
                    StiMaxicodeBarCodeType => "Maxicode",
                    StiPdf417MacroBarCodeType => "PDF417Macro",
                    StiPdf417BarCodeType => "PDF417",
                    StiCode39ExtBarCodeType => "Code39Ext",
                    StiCode39BarCodeType => "Code39",
                    StiCode93ExtBarCodeType => "Code93Ext",
                    StiCode93BarCodeType => "Code93",
                    StiCode11BarCodeType => "Code11",
                    StiCodabarBarCodeType => "Codabar",
                    StiIsbn10BarCodeType => "ISBN10",
                    StiIsbn13BarCodeType => "ISBN13",
                    StiJan8BarCodeType => "JAN8",
                    StiJan13BarCodeType => "JAN13",
                    StiEAN8BarCodeType => "EAN8",
                    StiUpcSup2BarCodeType => "UpcSup2",
                    StiUpcSup5BarCodeType => "UpcSup5",
                    StiUpcEBarCodeType => "UPCE",
                    StiUpcABarCodeType => "UPCA",
                    StiEAN13BarCodeType => "EAN13",
                    StiITF14BarCodeType => "ITF14",
                    StiInterleaved2of5BarCodeType => "I2of5",
                    StiStandard2of5BarCodeType => "S2of5",
                    StiSSCC18BarCodeType => "SSCC18",
                    StiGS1_128BarCodeType => "GS1128",
                    StiEAN128AutoBarCodeType or StiEAN128aBarCodeType or StiEAN128bBarCodeType
                        or StiEAN128cBarCodeType => "GS1128",
                    StiMsiBarCodeType => "MSI",
                    StiPlesseyBarCodeType => "Plessey",
                    StiPharmacodeBarCodeType => "Pharmacode",
                    StiAustraliaPost4StateBarCodeType => "AustraliaPost",
                    StiDutchKIXBarCodeType => "DutchKIX",
                    StiFIMBarCodeType => "FIM",
                    StiIntelligentMail4StateBarCodeType => "IntelligentMail",
                    StiPostnetBarCodeType => "Postnet",
                    StiRoyalMail4StateBarCodeType => "RoyalMail",
                    _ => "Code128"
                };
                item.ShowLabelText = barcode.ShowLabelText;
                item.ForeColor = $"#{barcode.ForeColor.R:X2}{barcode.ForeColor.G:X2}{barcode.ForeColor.B:X2}";
                return item;
            case StiText text:
                item.Type = LabelComponentType.Text;
                ApplyExpression(item, text.Text?.ToString());
                item.FontSizePt = text.Font?.Size ?? 8;
                item.Bold = text.Font?.Bold == true;
                item.Italic = text.Font?.Italic == true;
                item.Underline = text.Font?.Underline == true;
                item.FontName = string.IsNullOrWhiteSpace(text.Font?.Name) ? "Microsoft YaHei" : text.Font.Name;
                item.TextAlign = text.HorAlignment switch
                {
                    StiTextHorAlignment.Center => "center",
                    StiTextHorAlignment.Right => "right",
                    _ => "left"
                };
                item.VertAlign = text.VertAlignment switch
                {
                    StiVertAlignment.Center => "middle",
                    StiVertAlignment.Bottom => "bottom",
                    _ => "top"
                };
                item.Rotation = text.Angle;
                item.TextFit = text.ShrinkFontToFit ? "shrink" : text.WordWrap ? "wrap" : "clip";
                item.Border = text.Border.Side != StiBorderSides.None;
                item.LineWidthMm = text.Border.Size;
                item.BorderColor = $"#{text.Border.Color.R:X2}{text.Border.Color.G:X2}{text.Border.Color.B:X2}";
                if (text.TextBrush is StiSolidBrush solid)
                {
                    item.ForeColor = $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}";
                }

                if (text.Brush is StiSolidBrush fill)
                {
                    item.FillColor = $"#{fill.Color.R:X2}{fill.Color.G:X2}{fill.Color.B:X2}";
                }

                return item;
            case StiHorizontalLinePrimitive line:
                item.Type = LabelComponentType.Line;
                item.LineWidthMm = line.Size;
                item.ForeColor = $"#{line.Color.R:X2}{line.Color.G:X2}{line.Color.B:X2}";
                return item;
            default:
                return null;
        }
    }

    private static void ApplyExpression(LabelComponent item, string? raw)
    {
        var keys = ParseFieldKeys(raw);
        if (keys.Count == 1 && IsPlainFieldRef(raw, keys[0]))
        {
            item.Bind = new LabelBind { Kind = BindKind.Field, FieldKey = keys[0] };
            return;
        }

        if (keys.Count > 0)
        {
            item.Bind = new LabelBind { Kind = BindKind.Field, FieldKey = keys[0] };
            item.Expression = raw;
            return;
        }

        if (LooksLikeExpression(raw))
        {
            item.Expression = raw;
            item.Bind = new LabelBind { Kind = BindKind.Literal, Literal = raw ?? "" };
            return;
        }

        item.Bind = new LabelBind { Kind = BindKind.Literal, Literal = raw ?? "" };
    }

    private static bool IsPlainFieldRef(string? raw, string key)
    {
        var text = raw?.Trim() ?? "";
        return text.Equals("{" + DataName + "." + key + "}", StringComparison.OrdinalIgnoreCase)
               || text.Equals("{" + key + "}", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExpression(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && (raw.Contains('{') || raw.Contains("IIF", StringComparison.OrdinalIgnoreCase));

    private static string? ReadEnabledWhen(StiComponent component)
    {
        foreach (StiBaseCondition raw in component.Conditions)
        {
            if (raw is not StiCondition condition || condition.Item != StiFilterItem.Expression)
            {
                continue;
            }

            var expr = condition.Expression?.Value ?? condition.Expression?.ToString();
            if (string.IsNullOrWhiteSpace(expr))
            {
                continue;
            }

            var text = expr.Trim();
            if (text.StartsWith("!(", StringComparison.Ordinal) && text.EndsWith(')'))
            {
                return text[2..^1];
            }

            return text;
        }

        return null;
    }

    private static List<string> ParseFieldKeys(string? raw)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return keys;
        }

        foreach (Match match in FieldRef.Matches(raw))
        {
            var key = match.Groups["col"].Value;
            if (string.IsNullOrWhiteSpace(key) || IsReserved(key) || IsReserved(match.Groups["src"].Value))
            {
                continue;
            }

            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private static IEnumerable<string> ComponentTexts(StiComponent component) =>
        component switch
        {
            StiText text => [text.Text?.ToString() ?? ""],
            StiBarCode barcode => [barcode.Code?.Value ?? ""],
            StiImage picture => [picture.DataColumn ?? "", picture.File ?? "", picture.ImageURL?.Value ?? ""],
            StiCheckBox check => [check.Checked?.Value ?? ""],
            _ => []
        };

    private static void AddExtracted(Dictionary<string, FieldItem> items, string? key, string? alias, Type? type)
    {
        if (string.IsNullOrWhiteSpace(key) || IsReserved(key))
        {
            return;
        }

        if (items.ContainsKey(key))
        {
            if (!string.IsNullOrWhiteSpace(alias) && items[key].DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                items[key].DisplayName = alias;
            }

            return;
        }

        items[key] = new FieldItem
        {
            Key = key,
            DisplayName = string.IsNullOrWhiteSpace(alias) ? key : alias,
            DataType = MapDataType(type),
            Selected = true
        };
    }

    private static string MapDataType(Type? type)
    {
        if (type is null)
        {
            return "text";
        }

        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(DateTime) || t.Name is "DateOnly")
        {
            return "date";
        }

        return t == typeof(string) || t == typeof(char) || t == typeof(bool) ? "text" : "number";
    }

    private static bool IsReserved(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Reserved.Contains(name);

    private static DataTable BuildTable(string name, IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        var table = new DataTable(name);
        foreach (var field in fields)
        {
            if (!table.Columns.Contains(field.Key))
            {
                table.Columns.Add(field.Key, typeof(string));
            }
        }

        var row = table.NewRow();
        foreach (var field in fields)
        {
            string? value = null;
            var has = sample?.Values.TryGetValue(field.Key, out value) == true
                      || sample?.Values.TryGetValue(field.DisplayName, out value) == true;
            row[field.Key] = has ? value : $"[{field.DisplayName}]";
        }

        table.Rows.Add(row);
        return table;
    }

    private static readonly Regex FieldRef = new(
        @"\{(?:(?<src>[\p{L}_][\p{L}\p{N}_]*)\.)?(?<col>[\p{L}_][\p{L}\p{N}_]*)\}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "PageNumber", "TotalPageCount", "PageNofM", "PageCopyNumber",
        "Today", "Now", "Time", "Line", "LineThrough", "Column",
        "ReportName", "ReportAlias", "ReportAuthor", "ReportDescription",
        "IsFirstPage", "IsLastPage", "IIF", "If", "Format", "Switch",
        "ToString", "Mid", "Left", "Right", "Len", "Trim", "Upper", "Lower"
    };

    private static System.Drawing.Font CreateFont(LabelComponent item)
    {
        var style = System.Drawing.FontStyle.Regular;
        if (item.Bold)
        {
            style |= System.Drawing.FontStyle.Bold;
        }

        if (item.Italic)
        {
            style |= System.Drawing.FontStyle.Italic;
        }

        if (item.Underline)
        {
            style |= System.Drawing.FontStyle.Underline;
        }

        var family = string.IsNullOrWhiteSpace(item.FontName) ? "Microsoft YaHei" : item.FontName;
        return new System.Drawing.Font(family, (float)Math.Max(7, item.FontSizePt), style);
    }

    private static System.Drawing.Color ParseColor(string? hex)
    {
        var value = string.IsNullOrWhiteSpace(hex) ? "#1C1C1C" : hex.Trim();
        try
        {
            return System.Drawing.ColorTranslator.FromHtml(value);
        }
        catch (Exception)
        {
            return System.Drawing.Color.FromArgb(28, 28, 28);
        }
    }

    private static StiBarCodeTypeService CreateBarCodeType(LabelComponent item)
    {
        var kind = DraftBuilder.NormalizeSymbology(item.BarcodeSymbology)
                   ?? (item.Type == LabelComponentType.Qr ? "QR" : "Code128");
        return kind switch
        {
            "QR" => new StiQRCodeBarCodeType(),
            "GS1QR" => new StiGS1QRCodeBarCodeType(),
            "DataMatrix" => new StiDataMatrixBarCodeType(),
            "GS1DataMatrix" => new StiGS1DataMatrixBarCodeType(),
            "Aztec" => new StiAztecBarCodeType(),
            "Maxicode" => new StiMaxicodeBarCodeType(),
            "PDF417" => new StiPdf417BarCodeType(),
            "PDF417Macro" => new StiPdf417MacroBarCodeType(),
            "Code39" => new StiCode39BarCodeType(),
            "Code39Ext" => new StiCode39ExtBarCodeType(),
            "Code93" => new StiCode93BarCodeType(),
            "Code93Ext" => new StiCode93ExtBarCodeType(),
            "Code11" => new StiCode11BarCodeType(),
            "Codabar" => new StiCodabarBarCodeType(),
            "EAN13" => new StiEAN13BarCodeType(),
            "EAN8" => new StiEAN8BarCodeType(),
            "UPCA" => new StiUpcABarCodeType(),
            "UPCE" => new StiUpcEBarCodeType(),
            "UpcSup2" => new StiUpcSup2BarCodeType(),
            "UpcSup5" => new StiUpcSup5BarCodeType(),
            "ITF14" => new StiITF14BarCodeType(),
            "I2of5" => new StiInterleaved2of5BarCodeType(),
            "S2of5" => new StiStandard2of5BarCodeType(),
            "GS1128" => new StiGS1_128BarCodeType(),
            "SSCC18" => new StiSSCC18BarCodeType(),
            "ISBN13" => new StiIsbn13BarCodeType(),
            "ISBN10" => new StiIsbn10BarCodeType(),
            "JAN13" => new StiJan13BarCodeType(),
            "JAN8" => new StiJan8BarCodeType(),
            "MSI" => new StiMsiBarCodeType(),
            "Plessey" => new StiPlesseyBarCodeType(),
            "Pharmacode" => new StiPharmacodeBarCodeType(),
            "AustraliaPost" => new StiAustraliaPost4StateBarCodeType(),
            "DutchKIX" => new StiDutchKIXBarCodeType(),
            "FIM" => new StiFIMBarCodeType(),
            "IntelligentMail" => new StiIntelligentMail4StateBarCodeType(),
            "Postnet" => new StiPostnetBarCodeType(),
            "RoyalMail" => new StiRoyalMail4StateBarCodeType(),
            _ => new StiCode128AutoBarCodeType()
        };
    }

    private static string ToExpression(LabelComponent item)
    {
        if (!string.IsNullOrWhiteSpace(item.Expression))
        {
            return item.Expression.Trim();
        }

        return item.Bind.Kind == BindKind.Field && !string.IsNullOrWhiteSpace(item.Bind.FieldKey)
            ? "{" + DataName + "." + item.Bind.FieldKey + "}"
            : item.Bind.Literal ?? "";
    }

    private static string ToCheckExpression(LabelComponent item)
    {
        if (item.Bind.Kind == BindKind.Field && !string.IsNullOrWhiteSpace(item.Bind.FieldKey))
        {
            return "{" + DataName + "." + item.Bind.FieldKey + "}";
        }

        var raw = item.Bind.Literal?.Trim() ?? "";
        return raw is "1" or "true" or "True" or "yes" or "YES" or "是" or "勾" or "checked" or "on"
            ? "true"
            : "false";
    }

    private static double ToMm(double value, StiUnit unit) =>
        unit is StiMillimetersUnit ? value : unit.ConvertToHInches(value) * 0.254;
}
