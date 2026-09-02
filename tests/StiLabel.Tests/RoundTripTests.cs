using StiLabel.App.Sti;
using StiLabel.Core.Catalog;
using StiLabel.Core.Drafting;
using StiLabel.Core.Labeling;
using Stimulsoft.Report;
using Xunit;

namespace StiLabel.Tests;

public class RoundTripTests
{
    private static void AssertDouble(double expected, double actual, double precision = 0.05)
    {
        Assert.True(Math.Abs(expected - actual) <= precision,
            $"Expected: {expected}, Actual: {actual}, Delta: {Math.Abs(expected - actual)}");
    }

    [Fact]
    public void RoundTrip_AllComponentTypes_PreservesProperties()
    {
        // Arrange: 创建包含全部 10 种组件类型的 LabelDocument
        var original = new LabelDocument
        {
            Page = new LabelPage
            {
                WidthMm = 100,
                HeightMm = 60,
                Orientation = "Landscape"
            },
            Variables =
            [
                new LabelVariable { Name = "BatchPrefix", Value = "LOT-", DataType = "text" },
                new LabelVariable { Name = "DefaultQty", Value = "100", DataType = "number" }
            ],
            Components =
            [
                // 1. Text
                new LabelComponent
                {
                    Id = "txt_01",
                    Type = LabelComponentType.Text,
                    X = 5,
                    Y = 5,
                    W = 40,
                    H = 6,
                    Z = 1,
                    FontSizePt = 10,
                    Bold = true,
                    Italic = false,
                    Underline = false,
                    FontName = "Microsoft YaHei",
                    TextAlign = "center",
                    VertAlign = "middle",
                    TextFit = "shrink",
                    ForeColor = "#1C1C1C",
                    FillColor = "#E0E0E0",
                    Border = true,
                    BorderColor = "#000000",
                    LineWidthMm = 0.5,
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "MaterialName" },
                    Locked = false,
                    Visible = true
                },
                // 2. Barcode
                new LabelComponent
                {
                    Id = "bar_01",
                    Type = LabelComponentType.Barcode,
                    X = 5,
                    Y = 15,
                    W = 50,
                    H = 12,
                    Z = 2,
                    BarcodeSymbology = "Code128",
                    ShowLabelText = true,
                    ForeColor = "#000000",
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "MaterialCode" },
                    Locked = true,
                    Visible = true
                },
                // 3. Qr
                new LabelComponent
                {
                    Id = "qr_01",
                    Type = LabelComponentType.Qr,
                    X = 65,
                    Y = 5,
                    W = 25,
                    H = 25,
                    Z = 3,
                    BarcodeSymbology = "QR",
                    ShowLabelText = false,
                    ForeColor = "#000000",
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "MaterialCode" },
                    Visible = true
                },
                // 4. Image
                new LabelComponent
                {
                    Id = "img_01",
                    Type = LabelComponentType.Image,
                    X = 65,
                    Y = 32,
                    W = 20,
                    H = 15,
                    Z = 4,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "C:\\images\\logo.png" },
                    Visible = true
                },
                // 5. Line
                new LabelComponent
                {
                    Id = "lin_01",
                    Type = LabelComponentType.Line,
                    X = 5,
                    Y = 30,
                    W = 55,
                    H = 1,
                    Z = 5,
                    LineWidthMm = 0.4,
                    ForeColor = "#1565C0",
                    Visible = true
                },
                // 6. Rect
                new LabelComponent
                {
                    Id = "rct_01",
                    Type = LabelComponentType.Rect,
                    X = 5,
                    Y = 33,
                    W = 25,
                    H = 10,
                    Z = 6,
                    LineWidthMm = 0.5,
                    BorderColor = "#C62828",
                    FillColor = "#FFF8E1",
                    Visible = true
                },
                // 7. Ellipse
                new LabelComponent
                {
                    Id = "elp_01",
                    Type = LabelComponentType.Ellipse,
                    X = 33,
                    Y = 33,
                    W = 10,
                    H = 10,
                    Z = 7,
                    LineWidthMm = 0.3,
                    BorderColor = "#2E7D32",
                    FillColor = "#E8F5E9",
                    Visible = true
                },
                // 8. Triangle
                new LabelComponent
                {
                    Id = "tri_01",
                    Type = LabelComponentType.Triangle,
                    X = 45,
                    Y = 33,
                    W = 10,
                    H = 10,
                    Z = 8,
                    LineWidthMm = 0.3,
                    BorderColor = "#EF6C00",
                    FillColor = "#FFF3E0",
                    Visible = true
                },
                // 9. RoundedRect
                new LabelComponent
                {
                    Id = "rnd_01",
                    Type = LabelComponentType.RoundedRect,
                    X = 5,
                    Y = 46,
                    W = 40,
                    H = 10,
                    Z = 9,
                    LineWidthMm = 0.4,
                    BorderColor = "#6A1B9A",
                    FillColor = "#F3E5F5",
                    Visible = true
                },
                // 10. CheckBox
                new LabelComponent
                {
                    Id = "chk_01",
                    Type = LabelComponentType.CheckBox,
                    X = 50,
                    Y = 48,
                    W = 8,
                    H = 8,
                    Z = 10,
                    ForeColor = "#00838F",
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "true" },
                    Visible = true
                }
            ]
        };

        var fields = new List<FieldItem>
        {
            new() { Key = "MaterialCode", DisplayName = "物料编码", DataType = "text" },
            new() { Key = "MaterialName", DisplayName = "物料名称", DataType = "text" }
        };

        // Act: IR -> StiReport -> IR'
        var report = StiReportFactory.FromDocument(original, fields, null);
        var roundTripped = StiReportFactory.ToDocument(report);

        // Assert: 页面属性
        AssertDouble(original.Page.WidthMm, roundTripped.Page.WidthMm);
        AssertDouble(original.Page.HeightMm, roundTripped.Page.HeightMm);
        Assert.Equal(original.Page.Orientation, roundTripped.Page.Orientation);

        // Assert: 变量
        Assert.Equal(original.Variables.Count, roundTripped.Variables.Count);
        foreach (var origVar in original.Variables)
        {
            var matchVar = roundTripped.Variables.FirstOrDefault(v => v.Name == origVar.Name);
            Assert.NotNull(matchVar);
            Assert.Equal(origVar.Value, matchVar.Value);
            Assert.Equal(origVar.DataType, matchVar.DataType);
        }

        // Assert: 组件
        Assert.Equal(original.Components.Count, roundTripped.Components.Count);
        foreach (var origComp in original.Components)
        {
            var target = roundTripped.Components.FirstOrDefault(c => c.Id == origComp.Id);
            Assert.NotNull(target);

            // 基础类型与几何
            Assert.Equal(origComp.Type, target.Type);
            AssertDouble(origComp.X, target.X);
            AssertDouble(origComp.Y, target.Y);
            AssertDouble(origComp.W, target.W);
            if (origComp.Type != LabelComponentType.Line)
            {
                AssertDouble(origComp.H, target.H);
            }
            Assert.Equal(origComp.Locked, target.Locked);
            Assert.Equal(origComp.Visible, target.Visible);

            // 类型特有属性
            switch (origComp.Type)
            {
                case LabelComponentType.Text:
                    AssertDouble(origComp.FontSizePt, target.FontSizePt);
                    Assert.Equal(origComp.Bold, target.Bold);
                    Assert.Equal(origComp.Italic, target.Italic);
                    Assert.Equal(origComp.Underline, target.Underline);
                    Assert.Equal(origComp.TextAlign, target.TextAlign);
                    Assert.Equal(origComp.VertAlign, target.VertAlign);
                    Assert.Equal(origComp.TextFit, target.TextFit);
                    Assert.Equal(origComp.Border, target.Border);
                    AssertDouble(origComp.LineWidthMm, target.LineWidthMm);
                    Assert.Equal(origComp.Bind.Kind, target.Bind.Kind);
                    Assert.Equal(origComp.Bind.FieldKey, target.Bind.FieldKey);
                    break;

                case LabelComponentType.Barcode:
                case LabelComponentType.Qr:
                    Assert.Equal(origComp.BarcodeSymbology, target.BarcodeSymbology);
                    Assert.Equal(origComp.ShowLabelText, target.ShowLabelText);
                    Assert.Equal(origComp.Bind.Kind, target.Bind.Kind);
                    Assert.Equal(origComp.Bind.FieldKey, target.Bind.FieldKey);
                    break;

                case LabelComponentType.Image:
                    Assert.Equal(origComp.Bind.Kind, target.Bind.Kind);
                    Assert.Equal(origComp.Bind.Literal, target.Bind.Literal);
                    break;

                case LabelComponentType.Line:
                    AssertDouble(origComp.LineWidthMm, target.LineWidthMm);
                    Assert.Equal(origComp.ForeColor, target.ForeColor, ignoreCase: true);
                    break;

                case LabelComponentType.Rect:
                case LabelComponentType.Ellipse:
                case LabelComponentType.Triangle:
                case LabelComponentType.RoundedRect:
                    AssertDouble(origComp.LineWidthMm, target.LineWidthMm);
                    Assert.Equal(origComp.BorderColor, target.BorderColor, ignoreCase: true);
                    Assert.Equal(origComp.FillColor, target.FillColor, ignoreCase: true);
                    break;

                case LabelComponentType.CheckBox:
                    Assert.Equal(origComp.ForeColor, target.ForeColor, ignoreCase: true);
                    break;
            }
        }
    }

    [Theory]
    [InlineData("Code128", LabelComponentType.Barcode)]
    [InlineData("Code39", LabelComponentType.Barcode)]
    [InlineData("Code39Ext", LabelComponentType.Barcode)]
    [InlineData("Code93", LabelComponentType.Barcode)]
    [InlineData("Code93Ext", LabelComponentType.Barcode)]
    [InlineData("Code11", LabelComponentType.Barcode)]
    [InlineData("Codabar", LabelComponentType.Barcode)]
    [InlineData("EAN13", LabelComponentType.Barcode)]
    [InlineData("EAN8", LabelComponentType.Barcode)]
    [InlineData("UPCA", LabelComponentType.Barcode)]
    [InlineData("UPCE", LabelComponentType.Barcode)]
    [InlineData("UpcSup2", LabelComponentType.Barcode)]
    [InlineData("UpcSup5", LabelComponentType.Barcode)]
    [InlineData("ITF14", LabelComponentType.Barcode)]
    [InlineData("I2of5", LabelComponentType.Barcode)]
    [InlineData("S2of5", LabelComponentType.Barcode)]
    [InlineData("GS1128", LabelComponentType.Barcode)]
    [InlineData("SSCC18", LabelComponentType.Barcode)]
    [InlineData("ISBN13", LabelComponentType.Barcode)]
    [InlineData("ISBN10", LabelComponentType.Barcode)]
    [InlineData("JAN13", LabelComponentType.Barcode)]
    [InlineData("JAN8", LabelComponentType.Barcode)]
    [InlineData("MSI", LabelComponentType.Barcode)]
    [InlineData("Plessey", LabelComponentType.Barcode)]
    [InlineData("Pharmacode", LabelComponentType.Barcode)]
    [InlineData("AustraliaPost", LabelComponentType.Barcode)]
    [InlineData("DutchKIX", LabelComponentType.Barcode)]
    [InlineData("FIM", LabelComponentType.Barcode)]
    [InlineData("IntelligentMail", LabelComponentType.Barcode)]
    [InlineData("Postnet", LabelComponentType.Barcode)]
    [InlineData("RoyalMail", LabelComponentType.Barcode)]
    [InlineData("PDF417", LabelComponentType.Barcode)]
    [InlineData("PDF417Macro", LabelComponentType.Barcode)]
    [InlineData("QR", LabelComponentType.Qr)]
    [InlineData("GS1QR", LabelComponentType.Qr)]
    [InlineData("DataMatrix", LabelComponentType.Qr)]
    [InlineData("GS1DataMatrix", LabelComponentType.Qr)]
    [InlineData("Aztec", LabelComponentType.Qr)]
    [InlineData("Maxicode", LabelComponentType.Qr)]
    public void RoundTrip_AllSupportedSymbologies_PreservesSymbology(string symbology, LabelComponentType expectedType)
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40 },
            Components =
            [
                new LabelComponent
                {
                    Id = "sym_test",
                    Type = expectedType,
                    X = 5,
                    Y = 5,
                    W = expectedType == LabelComponentType.Qr ? 20 : 40,
                    H = expectedType == LabelComponentType.Qr ? 20 : 10,
                    BarcodeSymbology = symbology,
                    ShowLabelText = true,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "12345678" }
                }
            ]
        };

        // Act
        var report = StiReportFactory.FromDocument(doc, [], null);
        var roundTripped = StiReportFactory.ToDocument(report);

        // Assert
        var comp = Assert.Single(roundTripped.Components);
        Assert.Equal(expectedType, comp.Type);
        Assert.Equal(symbology, comp.BarcodeSymbology);
        Assert.True(comp.ShowLabelText);
    }

    [Fact]
    public void RoundTrip_TextStyling_PreservesAllStyleAttributes()
    {
        // Arrange: 验证多种文本样式与溢出策略
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 80, HeightMm = 50 },
            Components =
            [
                new LabelComponent
                {
                    Id = "t1",
                    Type = LabelComponentType.Text,
                    X = 2,
                    Y = 2,
                    W = 30,
                    H = 8,
                    FontSizePt = 12,
                    Bold = true,
                    Italic = true,
                    Underline = true,
                    FontName = "SimHei",
                    TextAlign = "right",
                    VertAlign = "bottom",
                    TextFit = "clip",
                    Rotation = 90,
                    ForeColor = "#C62828",
                    FillColor = "#FFFFFF",
                    Border = true,
                    BorderColor = "#1565C0",
                    LineWidthMm = 0.6,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "Sample Heading" }
                },
                new LabelComponent
                {
                    Id = "t2",
                    Type = LabelComponentType.Text,
                    X = 35,
                    Y = 2,
                    W = 30,
                    H = 8,
                    FontSizePt = 9,
                    Bold = false,
                    Italic = false,
                    Underline = false,
                    FontName = "Microsoft YaHei",
                    TextAlign = "left",
                    VertAlign = "top",
                    TextFit = "wrap",
                    Rotation = 0,
                    ForeColor = "#1C1C1C",
                    FillColor = "",
                    Border = false,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "Regular Body" }
                }
            ]
        };

        // Act
        var report = StiReportFactory.FromDocument(doc, [], null);
        var roundTripped = StiReportFactory.ToDocument(report);

        // Assert
        var t1 = roundTripped.Components.FirstOrDefault(c => c.Id == "t1");
        Assert.NotNull(t1);
        AssertDouble(12, t1.FontSizePt);
        Assert.True(t1.Bold);
        Assert.True(t1.Italic);
        Assert.True(t1.Underline);
        Assert.Equal("SimHei", t1.FontName);
        Assert.Equal("right", t1.TextAlign);
        Assert.Equal("bottom", t1.VertAlign);
        Assert.Equal("clip", t1.TextFit);
        AssertDouble(90, t1.Rotation);
        Assert.Equal("#C62828", t1.ForeColor, ignoreCase: true);
        Assert.Equal("#FFFFFF", t1.FillColor, ignoreCase: true);
        Assert.True(t1.Border);
        Assert.Equal("#1565C0", t1.BorderColor, ignoreCase: true);
        AssertDouble(0.6, t1.LineWidthMm);
        Assert.Equal("Sample Heading", t1.Bind.Literal);

        var t2 = roundTripped.Components.FirstOrDefault(c => c.Id == "t2");
        Assert.NotNull(t2);
        AssertDouble(9, t2.FontSizePt);
        Assert.False(t2.Bold);
        Assert.False(t2.Italic);
        Assert.False(t2.Underline);
        Assert.Equal("Microsoft YaHei", t2.FontName);
        Assert.Equal("left", t2.TextAlign);
        Assert.Equal("top", t2.VertAlign);
        Assert.Equal("wrap", t2.TextFit);
        AssertDouble(0, t2.Rotation);
        Assert.False(t2.Border);
    }

    [Fact]
    public void RoundTrip_DataBindingsAndExpressions_PreservesBindings()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40 },
            Components =
            [
                // 普通字段绑定
                new LabelComponent
                {
                    Id = "f1",
                    Type = LabelComponentType.Text,
                    X = 2,
                    Y = 2,
                    W = 30,
                    H = 5,
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "ItemCode" }
                },
                // 字面量绑定
                new LabelComponent
                {
                    Id = "f2",
                    Type = LabelComponentType.Text,
                    X = 2,
                    Y = 8,
                    W = 30,
                    H = 5,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "固定说明" }
                },
                // 复杂表达式绑定 (含 IIF)
                new LabelComponent
                {
                    Id = "f3",
                    Type = LabelComponentType.Text,
                    X = 2,
                    Y = 14,
                    W = 50,
                    H = 5,
                    Expression = "{IIF(LabelData.Qty > 10, \"合格\", \"不足\")}",
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "" }
                },
                // 多字段混合表达式
                new LabelComponent
                {
                    Id = "f4",
                    Type = LabelComponentType.Text,
                    X = 2,
                    Y = 20,
                    W = 50,
                    H = 5,
                    Expression = "编码: {LabelData.ItemCode} - 批次: {LabelData.BatchNo}",
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "ItemCode" }
                }
            ]
        };

        var fields = new List<FieldItem>
        {
            new() { Key = "ItemCode", DisplayName = "物料编码" },
            new() { Key = "BatchNo", DisplayName = "批次" },
            new() { Key = "Qty", DisplayName = "数量", DataType = "number" }
        };

        // Act
        var report = StiReportFactory.FromDocument(doc, fields, null);
        var roundTripped = StiReportFactory.ToDocument(report);

        // Assert
        var f1 = roundTripped.Components.FirstOrDefault(c => c.Id == "f1");
        Assert.NotNull(f1);
        Assert.Equal(BindKind.Field, f1.Bind.Kind);
        Assert.Equal("ItemCode", f1.Bind.FieldKey);

        var f2 = roundTripped.Components.FirstOrDefault(c => c.Id == "f2");
        Assert.NotNull(f2);
        Assert.Equal(BindKind.Literal, f2.Bind.Kind);
        Assert.Equal("固定说明", f2.Bind.Literal);

        var f3 = roundTripped.Components.FirstOrDefault(c => c.Id == "f3");
        Assert.NotNull(f3);
        Assert.Equal("{IIF(LabelData.Qty > 10, \"合格\", \"不足\")}", f3.Expression);

        var f4 = roundTripped.Components.FirstOrDefault(c => c.Id == "f4");
        Assert.NotNull(f4);
        Assert.Equal("编码: {LabelData.ItemCode} - 批次: {LabelData.BatchNo}", f4.Expression);
    }

    [Fact]
    public void RoundTrip_EnabledWhenCondition_PreservesCondition()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40 },
            Components =
            [
                new LabelComponent
                {
                    Id = "cond_txt",
                    Type = LabelComponentType.Text,
                    X = 5,
                    Y = 5,
                    W = 40,
                    H = 6,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "特批放行" },
                    EnabledWhen = "LabelData.IsApproved == true"
                }
            ]
        };

        // Act
        var report = StiReportFactory.FromDocument(doc, [], null);
        var roundTripped = StiReportFactory.ToDocument(report);

        // Assert
        var comp = Assert.Single(roundTripped.Components);
        Assert.Equal("LabelData.IsApproved == true", comp.EnabledWhen);
    }

    [Fact]
    public void RoundTrip_Variables_PreservesTypesAndValues()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40 },
            Variables =
            [
                new LabelVariable { Name = "StrVar", Value = "HelloWorld", DataType = "text" },
                new LabelVariable { Name = "NumVar", Value = "123.45", DataType = "number" },
                new LabelVariable { Name = "BoolVar", Value = "True", DataType = "bool" },
                new LabelVariable { Name = "DateVar", Value = "2026-09-01", DataType = "date" }
            ]
        };

        // Act
        var report = StiReportFactory.FromDocument(doc, [], null);
        var roundTripped = StiReportFactory.ToDocument(report);

        // Assert
        Assert.Equal(4, roundTripped.Variables.Count);
        var strVar = roundTripped.Variables.FirstOrDefault(v => v.Name == "StrVar");
        Assert.NotNull(strVar);
        Assert.Equal("HelloWorld", strVar.Value);
        Assert.Equal("text", strVar.DataType);

        var numVar = roundTripped.Variables.FirstOrDefault(v => v.Name == "NumVar");
        Assert.NotNull(numVar);
        Assert.Equal("123.45", numVar.Value);
        Assert.Equal("number", numVar.DataType);

        var boolVar = roundTripped.Variables.FirstOrDefault(v => v.Name == "BoolVar");
        Assert.NotNull(boolVar);
        Assert.Equal("True", boolVar.Value);
        Assert.Equal("bool", boolVar.DataType);

        var dateVar = roundTripped.Variables.FirstOrDefault(v => v.Name == "DateVar");
        Assert.NotNull(dateVar);
        Assert.Equal("2026-09-01", dateVar.Value);
        Assert.Equal("date", dateVar.DataType);
    }

    [Fact]
    public void RoundTrip_ImageBindings_SupportsFieldAndLiteral()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40 },
            Components =
            [
                // 本地文件图片
                new LabelComponent
                {
                    Id = "img_local",
                    Type = LabelComponentType.Image,
                    X = 2,
                    Y = 2,
                    W = 15,
                    H = 15,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "D:\\logos\\logo.png" }
                },
                // 字段图片
                new LabelComponent
                {
                    Id = "img_field",
                    Type = LabelComponentType.Image,
                    X = 20,
                    Y = 2,
                    W = 15,
                    H = 15,
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "ProductPhoto" }
                },
                // 网络图片
                new LabelComponent
                {
                    Id = "img_url",
                    Type = LabelComponentType.Image,
                    X = 40,
                    Y = 2,
                    W = 15,
                    H = 15,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "https://example.com/icon.png" }
                }
            ]
        };

        var fields = new List<FieldItem>
        {
            new() { Key = "ProductPhoto", DisplayName = "产品照片" }
        };

        // Act
        var report = StiReportFactory.FromDocument(doc, fields, null);
        var roundTripped = StiReportFactory.ToDocument(report);

        // Assert
        var local = roundTripped.Components.FirstOrDefault(c => c.Id == "img_local");
        Assert.NotNull(local);
        Assert.Equal(BindKind.Literal, local.Bind.Kind);
        Assert.Equal("D:\\logos\\logo.png", local.Bind.Literal);

        var fieldImg = roundTripped.Components.FirstOrDefault(c => c.Id == "img_field");
        Assert.NotNull(fieldImg);
        Assert.Equal(BindKind.Field, fieldImg.Bind.Kind);
        Assert.Equal("ProductPhoto", fieldImg.Bind.FieldKey);

        var urlImg = roundTripped.Components.FirstOrDefault(c => c.Id == "img_url");
        Assert.NotNull(urlImg);
        Assert.Equal(BindKind.Literal, urlImg.Bind.Kind);
        Assert.Equal("https://example.com/icon.png", urlImg.Bind.Literal);
    }

    [Fact]
    public void RoundTrip_MrtXmlSerialization_MaintainsEquivalence()
    {
        // Arrange: 验证从 Document 生成 Report -> 保存为 MRT XML 字符串 -> 重新加载 Report -> 转为 Document 的全链路
        var doc = new LabelDocument
        {
            Page = new LabelPage
            {
                WidthMm = 70,
                HeightMm = 40,
                Orientation = "Landscape"
            },
            Components =
            [
                new LabelComponent
                {
                    Id = "title_01",
                    Type = LabelComponentType.Text,
                    X = 2,
                    Y = 2,
                    W = 40,
                    H = 6,
                    FontSizePt = 11,
                    Bold = true,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "入库标签" }
                },
                new LabelComponent
                {
                    Id = "qr_01",
                    Type = LabelComponentType.Qr,
                    X = 48,
                    Y = 2,
                    W = 20,
                    H = 20,
                    BarcodeSymbology = "QR",
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "SKU" }
                },
                new LabelComponent
                {
                    Id = "bar_01",
                    Type = LabelComponentType.Barcode,
                    X = 2,
                    Y = 28,
                    W = 66,
                    H = 9,
                    BarcodeSymbology = "Code128",
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "SKU" }
                }
            ]
        };

        var fields = new List<FieldItem>
        {
            new() { Key = "SKU", DisplayName = "SKU编码" }
        };

        // Act: IR -> StiReport -> SaveToString -> LoadFromString -> IR'
        var report = StiReportFactory.FromDocument(doc, fields, null);
        var xml = report.SaveToString();
        Assert.NotEmpty(xml);

        var loadedReport = new StiReport();
        loadedReport.LoadFromString(xml);
        var roundTripped = StiReportFactory.ToDocument(loadedReport);

        // Assert
        AssertDouble(doc.Page.WidthMm, roundTripped.Page.WidthMm);
        AssertDouble(doc.Page.HeightMm, roundTripped.Page.HeightMm);
        Assert.Equal(3, roundTripped.Components.Count);

        var title = roundTripped.Components.FirstOrDefault(c => c.Id == "title_01");
        Assert.NotNull(title);
        Assert.Equal("入库标签", title.Bind.Literal);

        var qr = roundTripped.Components.FirstOrDefault(c => c.Id == "qr_01");
        Assert.NotNull(qr);
        Assert.Equal("QR", qr.BarcodeSymbology);
        Assert.Equal("SKU", qr.Bind.FieldKey);

        var bar = roundTripped.Components.FirstOrDefault(c => c.Id == "bar_01");
        Assert.NotNull(bar);
        Assert.Equal("Code128", bar.BarcodeSymbology);
        Assert.Equal("SKU", bar.Bind.FieldKey);
    }

    [Fact]
    public void ExtractFields_FromDocumentAndReport_ProducesConsistentResults()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40 },
            Components =
            [
                new LabelComponent
                {
                    Id = "c1",
                    Type = LabelComponentType.Text,
                    Bind = new LabelBind { Kind = BindKind.Field, FieldKey = "MaterialCode" }
                },
                new LabelComponent
                {
                    Id = "c2",
                    Type = LabelComponentType.Text,
                    Bind = new LabelBind { Kind = BindKind.Literal, Literal = "品名: {MaterialName} (规格: {Spec})" }
                }
            ]
        };

        var registeredFields = new List<FieldItem>
        {
            new() { Key = "MaterialCode", DisplayName = "物料编码" },
            new() { Key = "MaterialName", DisplayName = "物料名称" },
            new() { Key = "Spec", DisplayName = "规格型号" }
        };

        // Act
        var docFields = StiReportFactory.ExtractFields(doc);
        var report = StiReportFactory.FromDocument(doc, registeredFields, null);
        var reportFields = StiReportFactory.ExtractFields(report);

        // Assert
        var docKeys = docFields.Select(f => f.Key).OrderBy(k => k).ToList();
        var reportKeys = reportFields.Select(f => f.Key).OrderBy(k => k).ToList();

        Assert.Contains("MaterialCode", docKeys);
        Assert.Contains("MaterialName", docKeys);
        Assert.Contains("Spec", docKeys);

        Assert.Contains("MaterialCode", reportKeys);
        Assert.Contains("MaterialName", reportKeys);
        Assert.Contains("Spec", reportKeys);
    }
}
