using System;
using StiLabel.App.Sti;
using StiLabel.Core.Drafting;
using StiLabel.Core.Labeling;
using Stimulsoft.Report;
using Xunit;
using Xunit.Abstractions;

namespace StiLabel.Tests;

public class PreviewRegressionTests
{
    private readonly ITestOutputHelper? _output;

    public PreviewRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FromDocument_SetsInterpretationCalculationMode()
    {
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 100, HeightMm = 60, Orientation = "Landscape" }
        };
        var report = StiReportFactory.FromDocument(doc, [], null);

        Assert.Equal(StiCalculationMode.Interpretation, report.CalculationMode);
    }

    [Fact]
    public void RegisterSample_AfterRender_DoesNotTriggerCompilationRequirement()
    {
        var doc = new LabelDocument
        {
            Page = new LabelPage { WidthMm = 100, HeightMm = 60, Orientation = "Landscape" }
        };
        var report = StiReportFactory.FromDocument(doc, [], null);

        report.Render(false);
        Assert.True(report.IsRendered);

        StiReportFactory.RegisterSample(report, [], null);

        Assert.Equal(StiCalculationMode.Interpretation, report.CalculationMode);

        report.Render(false);
        Assert.True(report.IsRendered);
    }
}
