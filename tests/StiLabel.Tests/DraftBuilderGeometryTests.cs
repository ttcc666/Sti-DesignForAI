using StiLabel.Core.Catalog;
using StiLabel.Core.Drafting;
using StiLabel.Core.Labeling;
using StiLabel.Core.Services;
using Xunit;

namespace StiLabel.Tests;

public class DraftBuilderGeometryTests
{
    private static void AssertDouble(double expected, double actual, double precision = 0.05)
    {
        Assert.True(Math.Abs(expected - actual) <= precision,
            $"Expected: {expected}, Actual: {actual}, Delta: {Math.Abs(expected - actual)}");
    }

    private readonly DraftBuilder _builder = new();

    [Fact]
    public void Build_SplitLayout_CalculatesCorrectGeometry()
    {
        // Arrange
        var preset = new PagePreset { Name = "70×40", WidthMm = 70, HeightMm = 40 };
        var fields = new List<FieldItem>
        {
            new() { Key = "MaterialCode", DisplayName = "物料编码" },
            new() { Key = "MaterialName", DisplayName = "物料名称" }
        };
        var options = new DraftOptions { Title = "物料标签", Barcode = true, Qr = true, Layout = "split" };

        // Act
        var doc = _builder.Build(preset, fields, "TestPrinter", options);

        // Assert: 页面属性
        Assert.Equal(70, doc.Page.WidthMm);
        Assert.Equal(40, doc.Page.HeightMm);
        Assert.Equal("Landscape", doc.Page.Orientation);
        Assert.Equal(2, doc.Page.MarginMm);

        // 标题几何
        var title = doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Text && c.Bind.Literal == "物料标签");
        Assert.NotNull(title);
        AssertDouble(2, title.X);
        AssertDouble(2, title.Y);
        AssertDouble(6, title.H);

        // QR 靠右放置几何
        var qr = doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Qr);
        Assert.NotNull(qr);
        // qrSize = Math.Clamp(40 * 0.32, 12, 18) = 12.8
        var expectedQrSize = Math.Clamp(Math.Min(70, 40) * 0.32, 12, 18);
        AssertDouble(expectedQrSize, qr.W);
        AssertDouble(expectedQrSize, qr.H);
        AssertDouble(70 - 2 - expectedQrSize, qr.X);
        AssertDouble(2, qr.Y);

        // 文本行宽度
        var textRow = doc.Components.FirstOrDefault(c => c.Bind.FieldKey == "MaterialCode" && c.Type == LabelComponentType.Text);
        Assert.NotNull(textRow);
        var expectedTextW = Math.Max(18, (70 - 4) - expectedQrSize - 2);
        AssertDouble(expectedTextW, textRow.W);

        // 条码在底部
        var bar = doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Barcode);
        Assert.NotNull(bar);
        AssertDouble(2, bar.X);
        AssertDouble(66, bar.W);
        AssertDouble(8, bar.H);
        Assert.True(bar.Y >= 20 && bar.Y <= 40 - 2 - 8);
    }

    [Fact]
    public void Build_TableLayout_CalculatesNameValueGeometry()
    {
        // Arrange
        var preset = new PagePreset { Name = "100×60", WidthMm = 100, HeightMm = 60 };
        var fields = new List<FieldItem>
        {
            new() { Key = "MaterialCode", DisplayName = "编码" },
            new() { Key = "MaterialName", DisplayName = "品名" }
        };
        var options = new DraftOptions { Title = "物料标签", Barcode = false, Qr = false, Layout = "table" };

        // Act
        var doc = _builder.Build(preset, fields, null, options);

        // Assert: 找到 NameValue 成对组件
        var labelComp = doc.Components.FirstOrDefault(c => c.Bind.Literal == "编码");
        var valueComp = doc.Components.FirstOrDefault(c => c.Bind.FieldKey == "MaterialCode");

        Assert.NotNull(labelComp);
        Assert.NotNull(valueComp);

        // 验证 Label 宽度与 Value 宽度配比
        AssertDouble(labelComp.Y, valueComp.Y);
        AssertDouble(labelComp.X + labelComp.W + 0.6, valueComp.X);
        Assert.True(labelComp.W >= 10 && labelComp.W <= 16);
    }

    [Fact]
    public void Build_ShippingAndShelfLayouts_CalculatesCorrectComponents()
    {
        // Arrange
        var preset = new PagePreset { Name = "100×60", WidthMm = 100, HeightMm = 60 };
        var fields = new List<FieldItem>
        {
            new() { Key = "SKU", DisplayName = "SKU编码" },
            new() { Key = "Qty", DisplayName = "数量" }
        };

        // Shipping 模式
        var shippingDoc = _builder.Build(preset, fields, null, new DraftOptions { Layout = "shipping", Barcode = true, Qr = true });
        var shippingBar = shippingDoc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Barcode);
        Assert.NotNull(shippingBar);
        AssertDouble(9, shippingBar.H); // Shipping 条码高度为 9

        // Shelf 模式
        var shelfDoc = _builder.Build(preset, fields, null, new DraftOptions { Layout = "shelf", Barcode = true, Qr = true });
        var shelfCodeText = shelfDoc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Text && c.Bind.FieldKey == "SKU");
        Assert.NotNull(shelfCodeText);
        AssertDouble(10, shelfCodeText.H);
        AssertDouble(16, shelfCodeText.FontSizePt);
        Assert.Equal("center", shelfCodeText.TextAlign);
    }

    [Fact]
    public void Build_PlaceLogo_CalculatesTopRightGeometry()
    {
        // Arrange
        var preset = new PagePreset { Name = "70×40", WidthMm = 70, HeightMm = 40 };
        var options = new DraftOptions { Title = "测试", ImagePath = "C:\\logo.png" };

        // Act
        var doc = _builder.Build(preset, [], null, options);

        // Assert
        var logo = doc.Components.FirstOrDefault(c => c.Type == LabelComponentType.Image);
        Assert.NotNull(logo);
        // size = Math.Clamp(40 * 0.22, 10, 16) = 10
        AssertDouble(10, logo.W);
        AssertDouble(10, logo.H);
        AssertDouble(70 - 2 - 10, logo.X);
        AssertDouble(2, logo.Y);
    }

    [Fact]
    public void SetPage_And_SetOrientation_AdjustsBoundsAndInsets()
    {
        // Arrange
        var initial = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40, Orientation = "Landscape", MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 2, Y = 2, W = 60, H = 10 }
            ]
        };

        // Act 1: 切换到 Portrait -> 宽高互换 40×70
        var portraitDoc = _builder.SetOrientation(initial, "Portrait");
        Assert.Equal(40, portraitDoc.Page.WidthMm);
        Assert.Equal(70, portraitDoc.Page.HeightMm);
        Assert.Equal("Portrait", portraitDoc.Page.Orientation);

        var c1Portrait = portraitDoc.Components.First();
        Assert.True(c1Portrait.W <= 40);
        Assert.True(c1Portrait.X >= 2 && c1Portrait.X + c1Portrait.W <= 40 - 2);

        // Act 2: SetPage 调整为 100×50
        var resized = _builder.SetPage(portraitDoc, 100, 50);
        Assert.Equal(100, resized.Page.WidthMm);
        Assert.Equal(50, resized.Page.HeightMm);
        Assert.Equal("Landscape", resized.Page.Orientation);
    }

    [Fact]
    public void SetMargin_ClampsMarginAndInsetsComponents()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 60, HeightMm = 60, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 1, Y = 1, W = 10, H = 10 }
            ]
        };

        // Act: 设置 Margin 为 5
        var updated = _builder.SetMargin(doc, 5);

        // Assert
        Assert.Equal(5, updated.Page.MarginMm);
        var comp = updated.Components.First();
        Assert.True(comp.X >= 5);
        Assert.True(comp.Y >= 5);
    }

    [Fact]
    public void FitPage_ShrinksComponentsAndClamps()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 50, HeightMm = 40, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "huge", Type = LabelComponentType.Text, X = 40, Y = 30, W = 100, H = 100 }
            ]
        };

        // Act
        var fitted = _builder.FitPage(doc);

        // Assert
        var comp = fitted.Components.First();
        Assert.True(comp.W <= 50 - 4);
        Assert.True(comp.H <= 40 - 4);
        Assert.True(comp.X >= 2 && comp.X + comp.W <= 50 - 2);
        Assert.True(comp.Y >= 2 && comp.Y + comp.H <= 40 - 2);
    }

    [Fact]
    public void AddComponent_And_SetBounds_ClampsWithinPage()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 70, HeightMm = 40, MarginMm = 2 }
        };

        // Act 1: 默认添加
        var doc1 = _builder.AddComponent(doc, "barcode", "SKU", null, null, null, null, null);
        var bar = doc1.Components.First();
        AssertDouble(2, bar.X);
        AssertDouble(66, bar.W);
        AssertDouble(8, bar.H);

        // Act 2: 移出边界 (超大坐标)
        var clamped = _builder.SetBounds(doc1, bar.Id, 999, 999, null, null, relative: false);
        var clampedBar = clamped.Components.First();
        AssertDouble(70 - 66, clampedBar.X);
        AssertDouble(40 - 8, clampedBar.Y);

        // Act 3: 相对移动
        var moved = _builder.Move(clamped, clampedBar.Id, -2, -2, relative: true);
        var movedBar = moved.Components.First();
        AssertDouble(70 - 66 - 2, movedBar.X);
        AssertDouble(40 - 8 - 2, movedBar.Y);
    }

    [Fact]
    public void Align_SingleComponent_AlignsToPageMarginBounds()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 100, HeightMm = 80, MarginMm = 5 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 30, Y = 30, W = 20, H = 10 }
            ]
        };

        // Act & Assert: left
        var leftDoc = _builder.Align(doc, ["c1"], "left");
        AssertDouble(5, leftDoc.Components[0].X);

        // Act & Assert: right
        var rightDoc = _builder.Align(doc, ["c1"], "right");
        AssertDouble(100 - 5 - 20, rightDoc.Components[0].X);

        // Act & Assert: top
        var topDoc = _builder.Align(doc, ["c1"], "top");
        AssertDouble(5, topDoc.Components[0].Y);

        // Act & Assert: bottom
        var bottomDoc = _builder.Align(doc, ["c1"], "bottom");
        AssertDouble(80 - 5 - 10, bottomDoc.Components[0].Y);

        // Act & Assert: center-x
        var cxDoc = _builder.Align(doc, ["c1"], "center-x");
        // left + (right - left - W) / 2 = 5 + (95 - 5 - 20) / 2 = 5 + 35 = 40
        AssertDouble(40, cxDoc.Components[0].X);

        // Act & Assert: center-y
        var cyDoc = _builder.Align(doc, ["c1"], "center-y");
        // top + (bottom - top - H) / 2 = 5 + (75 - 5 - 10) / 2 = 5 + 30 = 35
        AssertDouble(35, cyDoc.Components[0].Y);
    }

    [Fact]
    public void Align_MultipleComponents_AlignsToReferenceComponent()
    {
        // Arrange: c1 为基准 (X=10, Y=15, W=30, H=20)
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 100, HeightMm = 80, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 10, Y = 15, W = 30, H = 20 },
                new LabelComponent { Id = "c2", Type = LabelComponentType.Text, X = 50, Y = 50, W = 10, H = 10 }
            ]
        };

        // Align left: c2.X -> c1.X = 10
        var leftDoc = _builder.Align(doc, ["c1", "c2"], "left");
        AssertDouble(10, leftDoc.Components[1].X);

        // Align right: c2.X -> c1.Right - c2.W = 40 - 10 = 30
        var rightDoc = _builder.Align(doc, ["c1", "c2"], "right");
        AssertDouble(30, rightDoc.Components[1].X);

        // Align top: c2.Y -> c1.Y = 15
        var topDoc = _builder.Align(doc, ["c1", "c2"], "top");
        AssertDouble(15, topDoc.Components[1].Y);

        // Align bottom: c2.Y -> c1.Bottom - c2.H = 35 - 10 = 25
        var bottomDoc = _builder.Align(doc, ["c1", "c2"], "bottom");
        AssertDouble(25, bottomDoc.Components[1].Y);

        // Align center-x: c2.X -> 10 + (30 - 10)/2 = 20
        var cxDoc = _builder.Align(doc, ["c1", "c2"], "center-x");
        AssertDouble(20, cxDoc.Components[1].X);

        // Align center-y: c2.Y -> 15 + (20 - 10)/2 = 20
        var cyDoc = _builder.Align(doc, ["c1", "c2"], "center-y");
        AssertDouble(20, cyDoc.Components[1].Y);
    }

    [Fact]
    public void Distribute_HorizontalAndVertical_CalculatesEqualGaps()
    {
        // Arrange: 3个组件，初始混乱位置
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 100, HeightMm = 100, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 10, Y = 10, W = 10, H = 10 },
                new LabelComponent { Id = "c2", Type = LabelComponentType.Text, X = 40, Y = 30, W = 10, H = 10 },
                new LabelComponent { Id = "c3", Type = LabelComponentType.Text, X = 70, Y = 80, W = 10, H = 10 }
            ]
        };

        // Act 1: 水平分布
        // span = 70 + 10 - 10 = 70. total = 30. gap = (70 - 30) / 2 = 20.
        // c1.X = 10, c2.X = 10 + 10 + 20 = 40, c3.X = 40 + 10 + 20 = 70.
        var hDist = _builder.Distribute(doc, ["c1", "c2", "c3"], "horizontal");
        var h1 = hDist.Components.First(c => c.Id == "c1");
        var h2 = hDist.Components.First(c => c.Id == "c2");
        var h3 = hDist.Components.First(c => c.Id == "c3");
        AssertDouble(10, h1.X);
        AssertDouble(40, h2.X);
        AssertDouble(70, h3.X);
        AssertDouble(h2.X - (h1.X + h1.W), h3.X - (h2.X + h2.W)); // 间距严格相等

        // Act 2: 垂直分布
        // span = 80 + 10 - 10 = 80. total = 30. gap = (80 - 30) / 2 = 25.
        // c1.Y = 10, c2.Y = 10 + 10 + 25 = 45, c3.Y = 45 + 10 + 25 = 80.
        var vDist = _builder.Distribute(doc, ["c1", "c2", "c3"], "vertical");
        var v1 = vDist.Components.First(c => c.Id == "c1");
        var v2 = vDist.Components.First(c => c.Id == "c2");
        var v3 = vDist.Components.First(c => c.Id == "c3");
        AssertDouble(10, v1.Y);
        AssertDouble(45, v2.Y);
        AssertDouble(80, v3.Y);
        AssertDouble(v2.Y - (v1.Y + v1.H), v3.Y - (v2.Y + v2.H)); // 间距严格相等
    }

    [Fact]
    public void SameSize_ResizesComponentsToReferenceAndClamps()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 100, HeightMm = 80, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 10, Y = 10, W = 35, H = 18 },
                new LabelComponent { Id = "c2", Type = LabelComponentType.Text, X = 20, Y = 20, W = 10, H = 8 },
                new LabelComponent { Id = "c3", Type = LabelComponentType.Text, X = 80, Y = 70, W = 5, H = 5 } // 靠右下
            ]
        };

        // Act
        var result = _builder.SameSize(doc, ["c1", "c2", "c3"]);

        // Assert
        var c2 = result.Components.First(c => c.Id == "c2");
        AssertDouble(35, c2.W);
        AssertDouble(18, c2.H);

        var c3 = result.Components.First(c => c.Id == "c3");
        AssertDouble(35, c3.W);
        AssertDouble(18, c3.H);
        // c3 必须被 Clamp 在页面内
        Assert.True(c3.X + c3.W <= 100);
        Assert.True(c3.Y + c3.H <= 80);
    }

    [Fact]
    public void SetRotation_RightAngle_SwapsWidthAndHeightAndClamps()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 80, HeightMm = 60, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 10, Y = 10, W = 40, H = 15, Rotation = 0 }
            ]
        };

        // Act 1: 旋转 90度 -> W与H互换
        var rot90 = _builder.SetRotation(doc, "c1", 90);
        var c90 = rot90.Components.First();
        AssertDouble(15, c90.W);
        AssertDouble(40, c90.H);
        AssertDouble(90, c90.Rotation);

        // Act 2: 从 90度旋转到 180度 -> W与H再次互换恢复
        var rot180 = _builder.SetRotation(rot90, "c1", 180);
        var c180 = rot180.Components.First();
        AssertDouble(40, c180.W);
        AssertDouble(15, c180.H);
        AssertDouble(180, c180.Rotation);

        // Act 3: 从 180度旋转到 270度 -> W与H互换
        var rot270 = _builder.SetRotation(rot180, "c1", 270);
        var c270 = rot270.Components.First();
        AssertDouble(15, c270.W);
        AssertDouble(40, c270.H);
        AssertDouble(270, c270.Rotation);
    }

    [Fact]
    public void Duplicate_And_Swap_UpdatesPositionsCorrectly()
    {
        // Arrange
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 80, HeightMm = 60, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "a", Type = LabelComponentType.Text, X = 10, Y = 10, W = 20, H = 8 },
                new LabelComponent { Id = "b", Type = LabelComponentType.Text, X = 40, Y = 30, W = 20, H = 8 }
            ]
        };

        // Act 1: Duplicate a (offset 5, 5)
        var dupDoc = _builder.Duplicate(doc, "a", 5, 5);
        Assert.Equal(3, dupDoc.Components.Count);
        var copy = dupDoc.Components.Last();
        Assert.NotEqual("a", copy.Id);
        AssertDouble(15, copy.X);
        AssertDouble(15, copy.Y);

        // Act 2: Swap a and b
        var swapped = _builder.Swap(doc, "a", "b");
        var compA = swapped.Components.First(c => c.Id == "a");
        var compB = swapped.Components.First(c => c.Id == "b");
        AssertDouble(40, compA.X);
        AssertDouble(30, compA.Y);
        AssertDouble(10, compB.X);
        AssertDouble(10, compB.Y);
    }

    [Fact]
    public void SetFont_LargeFontSize_ExpandsHeightWhenNeeded()
    {
        // Arrange: 初始高度很小 (H = 3)
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 80, HeightMm = 60, MarginMm = 2 },
            Components =
            [
                new LabelComponent { Id = "c1", Type = LabelComponentType.Text, X = 5, Y = 5, W = 40, H = 3, FontSizePt = 8 }
            ]
        };

        // Act: 设置字号为 20pt (20 * 0.45 = 9 > 3) -> 自动扩张到 20 * 0.5 = 10
        var updated = _builder.SetFont(doc, "c1", sizePt: 20, bold: true);

        // Assert
        var comp = updated.Components.First();
        AssertDouble(20, comp.FontSizePt);
        Assert.True(comp.Bold);
        AssertDouble(10, comp.H);
    }
}
