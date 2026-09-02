using System.Text.Json;
using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;
using StiLabel.Core.Services;
using StiLabel.Data.Entities;

namespace StiLabel.Data.Stores;

public sealed class TemplateWorkspace : ITemplateWorkspace
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly StiLabelDb _db;
    private LabelDocument _document = new();

    public TemplateWorkspace(StiLabelDb db) => _db = db;

    public string? FilePath { get; private set; }
    public string? SourcePath { get; private set; }

    public string MrtPath =>
        SourcePath is not null && SourcePath.EndsWith(".mrt", StringComparison.OrdinalIgnoreCase)
            ? SourcePath
            : Path.ChangeExtension(FilePath ?? Path.Combine(AppPaths.Templates, "label.label.json"), ".mrt");

    public LabelDocument Document => _document;
    public bool IsDirty { get; private set; }
    public event EventHandler? Changed;

    public void NewBlank(PagePreset preset)
    {
        FilePath = null;
        SourcePath = null;
        _document = new LabelDocument
        {
            Page = new LabelPage
            {
                WidthMm = preset.WidthMm,
                HeightMm = preset.HeightMm
            }
        };
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ReplaceDocument(LabelDocument document, bool markDirty = true)
    {
        _document = document;
        IsDirty = markDirty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var irPath = ResolveIrPath(path);
        if (File.Exists(irPath))
        {
            await using var stream = File.OpenRead(irPath);
            _document = await JsonSerializer.DeserializeAsync<LabelDocument>(stream, JsonOptions, cancellationToken)
                        ?? new LabelDocument();
        }
        else if (path.EndsWith(".mrt", StringComparison.OrdinalIgnoreCase))
        {
            _document = new LabelDocument();
        }
        else
        {
            throw new InvalidOperationException("打不开这个文件。请选择 .label.json 或带 sidecar 的 .mrt。");
        }

        FilePath = irPath;
        SourcePath = path;
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync(string? path = null, CancellationToken cancellationToken = default)
    {
        var chosen = path ?? SourcePath ?? FilePath ?? Path.Combine(AppPaths.Templates, $"label-{DateTime.Now:yyyyMMdd-HHmmss}.label.json");
        var target = ResolveIrPath(chosen);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var stream = File.Create(target))
        {
            await JsonSerializer.SerializeAsync(stream, _document, JsonOptions, cancellationToken);
        }

        FilePath = target;
        SourcePath = chosen;
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> SaveVersionAsync(string? note, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(FilePath))
        {
            await SaveAsync(cancellationToken: cancellationToken);
        }

        var name = $"{Path.GetFileNameWithoutExtension(FilePath)}-{DateTime.Now:yyyyMMdd-HHmmss}.label.json";
        var versionPath = Path.Combine(AppPaths.Versions, name);
        Directory.CreateDirectory(AppPaths.Versions);
        await using (var stream = File.Create(versionPath))
        {
            await JsonSerializer.SerializeAsync(stream, _document, JsonOptions, cancellationToken);
        }

        await _db.Client.Insertable(new TemplateVersionRow
        {
            SourcePath = FilePath ?? "",
            VersionPath = versionPath,
            Note = note,
            CreatedAt = DateTime.Now
        }).ExecuteCommandAsync(cancellationToken);
        return versionPath;
    }

    public void MarkClean()
    {
        IsDirty = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string ResolveIrPath(string path) =>
        path.EndsWith(".mrt", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(path, ".label.json")
            : path;
}
