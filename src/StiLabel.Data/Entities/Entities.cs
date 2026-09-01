using SqlSugar;

namespace StiLabel.Data.Entities;

[SugarTable("app_setting")]
public sealed class AppSettingRow
{
    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string Key { get; set; } = "";

    [SugarColumn(IsNullable = true, Length = 2000)]
    public string? Value { get; set; }
}

[SugarTable("field_definition")]
public sealed class FieldDefinitionRow
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 64)]
    public string Key { get; set; } = "";

    [SugarColumn(Length = 64)]
    public string DisplayName { get; set; } = "";

    [SugarColumn(Length = 32)]
    public string DataType { get; set; } = "text";

    public bool Required { get; set; }
    public int SortOrder { get; set; }
}

[SugarTable("recent_file")]
public sealed class RecentFileRow
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 500)]
    public string Path { get; set; } = "";

    public DateTime OpenedAt { get; set; }
}

[SugarTable("template_version")]
public sealed class TemplateVersionRow
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 260)]
    public string SourcePath { get; set; } = "";

    [SugarColumn(Length = 500)]
    public string VersionPath { get; set; } = "";

    [SugarColumn(IsNullable = true, Length = 200)]
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
}
