namespace StiLabel.Core.Labeling;

public enum LabelComponentType
{
    Text,
    Barcode,
    Qr,
    Image,
    Line,
    Rect,
    Ellipse,
    CheckBox,
    Triangle,
    RoundedRect
}

public enum BindKind
{
    Field,
    Literal
}

public sealed class LabelPage
{
    public double WidthMm { get; set; } = 70;
    public double HeightMm { get; set; } = 40;
    public string Orientation { get; set; } = "Portrait";
    public string? PrinterName { get; set; }
    public double MarginMm { get; set; } = 2;
}

public sealed class LabelBind
{
    public BindKind Kind { get; set; } = BindKind.Literal;
    public string? FieldKey { get; set; }
    public string? Literal { get; set; }
}

public sealed class LabelComponent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public LabelComponentType Type { get; set; } = LabelComponentType.Text;
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public int Z { get; set; }
    public LabelBind Bind { get; set; } = new();
    public string? BarcodeSymbology { get; set; }
    public double FontSizePt { get; set; } = 8;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string FontName { get; set; } = "Microsoft YaHei";
    public string TextAlign { get; set; } = "left";
    public string VertAlign { get; set; } = "top";
    public string TextFit { get; set; } = "wrap";
    public double Rotation { get; set; }
    public string ForeColor { get; set; } = "#1C1C1C";
    public string FillColor { get; set; } = "";
    public double LineWidthMm { get; set; } = 0.3;
    public bool Border { get; set; }
    public string BorderColor { get; set; } = "#282828";
    public bool ShowLabelText { get; set; } = true;
    public bool Locked { get; set; }
    public bool Visible { get; set; } = true;
    public string? Expression { get; set; }
    public string? EnabledWhen { get; set; }
}

public sealed class LabelVariable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string DataType { get; set; } = "text";
}

public sealed class LabelDocument
{
    public string SchemaVersion { get; set; } = "1.0";
    public LabelPage Page { get; set; } = new();
    public List<LabelComponent> Components { get; set; } = [];
    public List<LabelVariable> Variables { get; set; } = [];

    public LabelDocument Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<LabelDocument>(json) ?? new LabelDocument();
    }
}
