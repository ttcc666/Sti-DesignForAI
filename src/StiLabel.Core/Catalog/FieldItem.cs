namespace StiLabel.Core.Catalog;

public sealed class FieldItem
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DataType { get; set; } = "text";
    public bool Required { get; set; }
    public int SortOrder { get; set; }
    public bool Selected { get; set; }
    public bool Bound { get; set; }
}

public sealed class PagePreset
{
    public string Name { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }

    public override string ToString() => $"{Name}  {WidthMm:0}×{HeightMm:0} mm";
}

public static class PagePresets
{
    public static IReadOnlyList<PagePreset> All { get; } =
    [
        new() { Name = "物料 70×40", WidthMm = 70, HeightMm = 40 },
        new() { Name = "货架 100×50", WidthMm = 100, HeightMm = 50 },
        new() { Name = "外箱 100×60", WidthMm = 100, HeightMm = 60 }
    ];
}

public sealed class RecentFileItem
{
    public string Path { get; set; } = "";
    public DateTime OpenedAt { get; set; }
}

public sealed class SampleRow
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
