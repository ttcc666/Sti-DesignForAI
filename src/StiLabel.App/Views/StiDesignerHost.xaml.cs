using System.IO;
using System.Windows;
using System.Windows.Controls;
using StiLabel.App.Sti;
using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;
using Stimulsoft.Controls;
using Stimulsoft.Report;
using Stimulsoft.Report.Check;
using Stimulsoft.Report.Design;
using Stimulsoft.Report.Design.Check;
using WinForms = System.Windows.Forms;

namespace StiLabel.App.Views;

public partial class StiDesignerHost : UserControl, IStiWorkbench
{
    private StiDesignerControl? _designer;
    private StiReport _report = new();
    private LabelDocument _document = new();
    private IReadOnlyList<FieldItem> _fields = [];
    private SampleRow? _sample;
    private bool _ready;

    public StiDesignerHost()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        FallbackSurface.Edited += (_, _) =>
        {
            if (FallbackSurface.Document is not null)
            {
                _document = FallbackSurface.Document;
            }

            CanvasEdited?.Invoke(this, EventArgs.Empty);
        };
    }

    public bool IsEmbedded { get; private set; }

    public event EventHandler? CanvasEdited;

    private string? _appliedKey;

    public void ApplyDocument(LabelDocument document, IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        _document = document;
        _fields = fields;
        _sample = sample;
        FallbackSurface.Document = document;
        FallbackSurface.Sample = sample;
        if (!_ready)
        {
            return;
        }

        var key = System.Text.Json.JsonSerializer.Serialize(document);
        if (key == _appliedKey)
        {
            StiReportFactory.RegisterSample(CurrentReport, fields, sample);
            RefreshPreview(switchToPreview: false);
            return;
        }

        _appliedKey = key;
        AssignReport(StiReportFactory.FromDocument(document, fields, sample));
    }

    public void LoadMrt(string path)
    {
        var report = new StiReport
        {
            CalculationMode = StiCalculationMode.Interpretation
        };
        report.Load(path);
        report.CalculationMode = StiCalculationMode.Interpretation;
        _document = StiReportFactory.ToDocument(report);
        _appliedKey = null;
        AssignReport(report);
        FallbackSurface.Document = _document;
        FallbackSurface.Sample = _sample;
    }

    public void SaveMrt(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CurrentReport.Save(path);
    }

    public void Preview(IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        if (IsEmbedded && _designer is not null)
        {
            RegisterFields(fields, sample);
            RefreshPreview(switchToPreview: true);
            return;
        }

        var report = (CurrentReport.Clone() as StiReport) ?? CurrentReport;
        report.CalculationMode = StiCalculationMode.Interpretation;
        StiReportFactory.RegisterSample(report, fields, sample);
        report.Render(false);
        report.Show();
    }

    public void Print(IReadOnlyList<FieldItem> fields, SampleRow? sample, string? printerName = null)
    {
        var report = (CurrentReport.Clone() as StiReport) ?? CurrentReport;
        report.CalculationMode = StiCalculationMode.Interpretation;
        StiReportFactory.RegisterSample(report, fields, sample);

        var hasPrinter = !string.IsNullOrWhiteSpace(printerName);
        if (hasPrinter)
        {
            report.PrinterSettings.PrinterName = printerName!;
        }

        report.Render(false);
        // 已指定打印机时直接打印（虚拟打印机如PDF会自动提示保存路径，物理机直接出样），避免WPF下WinForms对话框被遮挡
        report.Print(showPrintDialog: !hasPrinter);
    }

    public void Export(string path, string format, IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        var report = (CurrentReport.Clone() as StiReport) ?? CurrentReport;
        report.CalculationMode = StiCalculationMode.Interpretation;
        StiReportFactory.RegisterSample(report, fields, sample);
        report.Render(false);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pdf = format.Equals("pdf", StringComparison.OrdinalIgnoreCase);
        report.ExportDocument(pdf ? StiExportFormat.Pdf : StiExportFormat.ImagePng, path);
    }

    public LabelDocument CaptureDocument(IReadOnlyList<FieldItem> fields)
    {
        _fields = fields;
        if (!IsEmbedded)
        {
            return (FallbackSurface.Document ?? _document).Clone();
        }

        return StiReportFactory.ToDocument(CurrentReport);
    }

    public IReadOnlyList<FieldItem> ExtractFields()
    {
        var fromReport = StiReportFactory.ExtractFields(CurrentReport);
        var fromDocument = StiReportFactory.ExtractFields(_document);
        if (fromReport.Count == 0)
        {
            return fromDocument;
        }

        var keys = fromReport.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return fromReport.Concat(fromDocument.Where(f => !keys.Contains(f.Key))).ToList();
    }

    public void RegisterFields(IReadOnlyList<FieldItem> fields, SampleRow? sample)
    {
        _fields = fields;
        _sample = sample;
        StiReportFactory.RegisterSample(CurrentReport, fields, sample);
        RefreshPreview(switchToPreview: false);
    }

    private StiReport CurrentReport => _designer?.Report ?? _report;

    private bool _hasAssignedReport;

    private void AssignReport(StiReport report)
    {
        _report = report;
        if (_designer is null)
        {
            return;
        }

        _designer.Report = report;
        // 官网：程序改报表后刷新设计器 https://admin.stimulsoft.com/documentation/classreference-net/Stimulsoft_Report_Design_StiDesignerControl_InvokeRefreshDesigner.html
        _designer.InvokeRefreshDesigner();
        if (_hasAssignedReport)
        {
            RefreshPreview(switchToPreview: true);
        }

        _hasAssignedReport = true;
    }

    private void RefreshPreview(bool switchToPreview)
    {
        if (_designer is null)
        {
            return;
        }

        if (switchToPreview && !_designer.IsPreview)
        {
            TrySelectPreviewTab(_designer);
        }

        if (_designer.DesignerPreviewControl is { } preview &&
            (switchToPreview || _designer.IsPreview || preview.Visible))
        {
            preview.DoRefresh();
        }

        DismissChecker();
    }

    private static void DisableReportChecker()
    {
        foreach (var check in StiCheckEngine.Checks)
        {
            check.Enabled = false;
        }

        StiOptions.Engine.ForceInterpretationMode = true;
        StiOptions.Engine.ShowReportRenderingMessages = false;
        StiOptions.Designer.Toolbars.StatusBar.ShowReportChecker = false;
    }

    private static void DismissChecker()
    {
        foreach (WinForms.Form form in WinForms.Application.OpenForms)
        {
            if (form is StiChecksViewerForm)
            {
                form.Close();
            }
        }
    }

    private static void TrySelectPreviewTab(StiDesignerControl designer)
    {
        var preview = designer.DesignerPreviewControl;
        foreach (var tabs in EnumerateTabControls(designer))
        {
            var page = tabs.Tabs.FirstOrDefault(tab =>
                preview is not null && (tab.Contains(preview) || preview.Parent == tab));
            page ??= tabs.Tabs.FirstOrDefault(IsPreviewTab);
            if (page is null)
            {
                continue;
            }

            tabs.SelectedTab = page;
            return;
        }
    }

    private static bool IsPreviewTab(StiTabPage tab) =>
        tab.Text.Equals("预览", StringComparison.OrdinalIgnoreCase) ||
        tab.Text.Equals("Preview", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<StiTabControl> EnumerateTabControls(WinForms.Control root)
    {
        foreach (WinForms.Control child in root.Controls)
        {
            if (child is StiTabControl tabs)
            {
                yield return tabs;
            }

            foreach (var nested in EnumerateTabControls(child))
            {
                yield return nested;
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            return;
        }

        DisableReportChecker();
        try
        {
            _designer = new StiDesignerControl
            {
                Dock = WinForms.DockStyle.Fill,
                Report = _report,
                AutoSaveReportBeforePreview = false
            };
            _designer.ShowingReportCheckerResults += (_, _) =>
                _designer.BeginInvoke(DismissChecker);
            _designer.MouseUp += (_, _) => CanvasEdited?.Invoke(this, EventArgs.Empty);
            _designer.KeyUp += (_, _) => CanvasEdited?.Invoke(this, EventArgs.Empty);
            FormsHost.Child = _designer;
            FallbackPanel.Visibility = Visibility.Collapsed;
            IsEmbedded = true;
        }
        catch (Exception ex)
        {
            IsEmbedded = false;
            FallbackPanel.Visibility = Visibility.Visible;
            FallbackHint.Text = "未能嵌入官方设计器：" + ex.Message + "。可点下方用独立窗口打开，或继续用工位画布。";
        }

        _ready = true;
        ApplyDocument(_document, _fields, _sample);
    }

    private void OpenDialogDesigner_Click(object sender, RoutedEventArgs e)
    {
        CurrentReport.Design();
    }
}
