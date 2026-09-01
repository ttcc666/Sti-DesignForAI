using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;

namespace StiLabel.App.Sti;

public interface IStiWorkbench
{
    bool IsEmbedded { get; }
    event EventHandler? CanvasEdited;
    void ApplyDocument(LabelDocument document, IReadOnlyList<FieldItem> fields, SampleRow? sample);
    void LoadMrt(string path);
    void SaveMrt(string path);
    void Preview(IReadOnlyList<FieldItem> fields, SampleRow? sample);
    void Print(IReadOnlyList<FieldItem> fields, SampleRow? sample, string? printerName = null);
    void Export(string path, string format, IReadOnlyList<FieldItem> fields, SampleRow? sample);
    LabelDocument CaptureDocument(IReadOnlyList<FieldItem> fields);
    IReadOnlyList<FieldItem> ExtractFields();
    void RegisterFields(IReadOnlyList<FieldItem> fields, SampleRow? sample);
}
