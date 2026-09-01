using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;

namespace StiLabel.Core.Services;

public interface IFieldCatalog
{
    Task<IReadOnlyList<FieldItem>> ListAsync(CancellationToken cancellationToken = default);
    Task<FieldItem?> FindAsync(string keyOrName, CancellationToken cancellationToken = default);
    Task<FieldItem> UpsertAsync(string displayName, string? key = null, string dataType = "text", CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IAppStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecentFileItem>> ListRecentAsync(CancellationToken cancellationToken = default);
    Task TouchRecentAsync(string path, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SampleRow>> LoadSampleRowsAsync(CancellationToken cancellationToken = default);
    Task SaveSampleRowsAsync(IReadOnlyList<SampleRow> rows, CancellationToken cancellationToken = default);
}

public interface ITemplateWorkspace
{
    string? FilePath { get; }
    string? SourcePath { get; }
    string MrtPath { get; }
    LabelDocument Document { get; }
    bool IsDirty { get; }
    event EventHandler? Changed;

    void NewBlank(PagePreset preset);
    void ReplaceDocument(LabelDocument document, bool markDirty = true);
    Task OpenAsync(string path, CancellationToken cancellationToken = default);
    Task SaveAsync(string? path = null, CancellationToken cancellationToken = default);
    Task<string> SaveVersionAsync(string? note, CancellationToken cancellationToken = default);
    void MarkClean();
}

public sealed class AgentReply
{
    public string Text { get; init; } = "";
    public LabelDocument? Document { get; init; }
    public bool RequiresConfirm { get; init; }
    public bool Applied { get; init; }
    public string? OpenPath { get; init; }
    public SampleRow? Sample { get; init; }
    public string? SaveMode { get; init; }
    public bool PrintRequested { get; init; }
    public IReadOnlyList<string>? SelectedKeys { get; init; }
    public bool NewBlankRequested { get; init; }
    public string? ExportMode { get; init; }
    public string? ExportPath { get; init; }
    public bool Failed { get; init; }

    /// <summary>本轮发给模型的输入 token。没报用量时为空。</summary>
    public int? ContextUsed { get; set; }

    /// <summary>设置里的上下文上限。</summary>
    public int ContextLimit { get; set; }

    /// <summary>本轮实际调用过的工具，按出现顺序。</summary>
    public IReadOnlyList<string> Tools { get; set; } = [];
}

public sealed class AgentProgress
{
    public string Text { get; init; } = "";
    public IReadOnlyList<string> Tools { get; init; } = [];
}

public sealed class ModelOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }

    /// <summary>厂商预设 ID，只用于设置界面回显，不参与请求。</summary>
    public string Provider { get; set; } = "";

    /// <summary>请求格式：openai / openai-responses / anthropic / gemini / azure-openai / azure-responses / ollama。</summary>
    public string Protocol { get; set; } = "";

    /// <summary>模型上下文窗口（token）。压缩触发按这个预算算。</summary>
    public int ContextTokens { get; set; } = 32_000;

    /// <summary>none / window / truncate / sliding / summarize。对应官网 Compaction 策略。</summary>
    public string CompactMode { get; set; } = "window";

    /// <summary>滑动窗口保留的最近轮数。</summary>
    public int CompactTurns { get; set; } = 8;

    public bool IsReady =>
        Enabled
        && !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Model);
}

public interface IModelOptionsStore
{
    Task<ModelOptions> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ModelOptions options, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> LoadModelNamesAsync(CancellationToken cancellationToken = default);
    Task SaveModelNamesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default);
}

public interface ILlmClient
{
    Task<string> TestAsync(ModelOptions options, CancellationToken cancellationToken = default);
}

public interface IWorkbenchAgent
{
    Task<AgentReply> HandleAsync(
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
        CancellationToken cancellationToken = default);

    void ResetConversation();
}

public interface IDraftBuilder
{
    LabelDocument Build(PagePreset preset, IReadOnlyList<FieldItem> selected, string? printerName);
    LabelDocument Build(PagePreset preset, IReadOnlyList<FieldItem> selected, string? printerName, DraftOptions options);
    LabelDocument AddField(LabelDocument source, FieldItem field);
    LabelDocument AddComponent(LabelDocument source, string type, string? fieldKey, string? literal, double? xMm, double? yMm, double? wMm, double? hMm);
    LabelDocument Remove(LabelDocument source, string target);
    LabelDocument Clear(LabelDocument source);
    LabelDocument SetPage(LabelDocument source, double widthMm, double heightMm);
    LabelDocument SetOrientation(LabelDocument source, string orientation);
    LabelDocument Move(LabelDocument source, string target, double xMm, double yMm, bool relative);
    LabelDocument SetBounds(LabelDocument source, string target, double? xMm, double? yMm, double? wMm, double? hMm, bool relative);
    LabelDocument BindField(LabelDocument source, string target, string fieldKey);
    LabelDocument Unbind(LabelDocument source, string target, string? literal = null);
    LabelDocument SameSize(LabelDocument source, string[] targets);
    LabelDocument CopyStyle(LabelDocument source, string from, string[] to);
    LabelDocument FitPage(LabelDocument source);
    LabelDocument SetLiteral(LabelDocument source, string target, string text);
    LabelDocument SetFont(LabelDocument source, string target, double? sizePt, bool? bold, string? fontName = null, bool? italic = null, bool? underline = null);
    LabelDocument SetTextAlign(LabelDocument source, string target, string align);
    LabelDocument SetTextFit(LabelDocument source, string target, string mode);
    LabelDocument SetRotation(LabelDocument source, string target, double degrees);
    LabelDocument SetColor(LabelDocument source, string target, string color);
    LabelDocument SetMargin(LabelDocument source, double marginMm);
    LabelDocument SetPrinter(LabelDocument source, string? printerName, double? marginMm);
    LabelDocument SetBarcode(LabelDocument source, string target, string symbology);
    LabelDocument SetBarcodeOptions(LabelDocument source, string target, bool? showText);
    LabelDocument SetLine(LabelDocument source, string target, double? widthMm, string? color);
    LabelDocument SetBorder(LabelDocument source, string target, bool? enabled, double? widthMm, string? color);
    LabelDocument SetVertAlign(LabelDocument source, string target, string align);
    LabelDocument SetLocked(LabelDocument source, string target, bool locked);
    LabelDocument SetVisible(LabelDocument source, string target, bool visible);
    LabelDocument SetExpression(LabelDocument source, string target, string? expression);
    LabelDocument SetEnabledWhen(LabelDocument source, string target, string? expression);
    LabelDocument SetVariable(LabelDocument source, string name, string value, string dataType = "text");
    LabelDocument RemoveVariable(LabelDocument source, string name);
    LabelDocument SetFill(LabelDocument source, string target, string? color);
    LabelDocument SetZ(LabelDocument source, string target, string layer);
    LabelDocument Align(LabelDocument source, string[] targets, string edge);
    LabelDocument Distribute(LabelDocument source, string[] targets, string axis);
    LabelDocument Duplicate(LabelDocument source, string target, double offsetXMm = 2, double offsetYMm = 2);
    LabelDocument Swap(LabelDocument source, string a, string b);
}

public sealed class DraftOptions
{
    public string? Title { get; init; } = "物料标签";
    public bool Barcode { get; init; }
    public bool Qr { get; init; }
    public string Layout { get; init; } = "split";
    public string? ImagePath { get; init; }
}
