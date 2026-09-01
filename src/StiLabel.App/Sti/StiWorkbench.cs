using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;

namespace StiLabel.App.Sti;

public sealed class StiWorkbench : IStiWorkbench
{
    private IStiWorkbench? _host;

    public IStiWorkbench? Host
    {
        get => _host;
        set
        {
            if (_host is not null)
            {
                _host.CanvasEdited -= OnHostEdited;
            }

            _host = value;
            if (_host is not null)
            {
                _host.CanvasEdited += OnHostEdited;
            }
        }
    }

    public bool IsEmbedded => Host?.IsEmbedded == true;

    public event EventHandler? CanvasEdited;

    public void ApplyDocument(LabelDocument document, IReadOnlyList<FieldItem> fields, SampleRow? sample) =>
        Host?.ApplyDocument(document, fields, sample);

    public void LoadMrt(string path) => Host?.LoadMrt(path);

    public void SaveMrt(string path) => Host?.SaveMrt(path);

    public void Preview(IReadOnlyList<FieldItem> fields, SampleRow? sample) =>
        Host?.Preview(fields, sample);

    public void Print(IReadOnlyList<FieldItem> fields, SampleRow? sample, string? printerName = null) =>
        Host?.Print(fields, sample, printerName);

    public void Export(string path, string format, IReadOnlyList<FieldItem> fields, SampleRow? sample) =>
        Host?.Export(path, format, fields, sample);

    public LabelDocument CaptureDocument(IReadOnlyList<FieldItem> fields) =>
        Host?.CaptureDocument(fields) ?? new LabelDocument();

    public IReadOnlyList<FieldItem> ExtractFields() =>
        Host?.ExtractFields() ?? [];

    public void RegisterFields(IReadOnlyList<FieldItem> fields, SampleRow? sample) =>
        Host?.RegisterFields(fields, sample);

    private void OnHostEdited(object? sender, EventArgs e) => CanvasEdited?.Invoke(this, e);
}
