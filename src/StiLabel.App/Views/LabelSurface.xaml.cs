using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;

namespace StiLabel.App.Views;

public partial class LabelSurface : UserControl
{
    public const double PxPerMm = 4;

    public static readonly DependencyProperty DocumentProperty =
        DependencyProperty.Register(nameof(Document), typeof(LabelDocument), typeof(LabelSurface),
            new PropertyMetadata(null, OnDocumentChanged));

    public static readonly DependencyProperty SampleProperty =
        DependencyProperty.Register(nameof(Sample), typeof(SampleRow), typeof(LabelSurface),
            new PropertyMetadata(null, OnDocumentChanged));

    public LabelDocument? Document
    {
        get => (LabelDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public SampleRow? Sample
    {
        get => (SampleRow?)GetValue(SampleProperty);
        set => SetValue(SampleProperty, value);
    }

    public event EventHandler? Edited;

    private LabelComponent? _drag;
    private FrameworkElement? _dragVisual;
    private Point _dragStart;
    private double _originX;
    private double _originY;

    public LabelSurface()
    {
        InitializeComponent();
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LabelSurface surface)
        {
            surface.Redraw();
        }
    }

    public void Redraw()
    {
        if (Board is null || Paper is null)
        {
            return;
        }

        Board.Children.Clear();
        var doc = Document ?? new LabelDocument();
        Paper.Width = doc.Page.WidthMm * PxPerMm;
        Paper.Height = doc.Page.HeightMm * PxPerMm;
        Board.Width = Paper.Width;
        Board.Height = Paper.Height;

        foreach (var component in doc.Components.OrderBy(c => c.Z))
        {
            if (!component.Visible)
            {
                continue;
            }

            Board.Children.Add(BuildVisual(component, Sample));
        }
    }

    private FrameworkElement BuildVisual(LabelComponent component, SampleRow? sample)
    {
        var text = ResolveText(component, sample);
        var left = component.X * PxPerMm;
        var top = component.Y * PxPerMm;
        var width = Math.Max(8, component.W * PxPerMm);
        var height = Math.Max(8, component.H * PxPerMm);

        FrameworkElement body = component.Type switch
        {
            LabelComponentType.Barcode => BarcodeBlock(text, width, height, component.ShowLabelText),
            LabelComponentType.Qr => QrBlock(text, width, height),
            LabelComponentType.Image => ImageBlock(text, width, height),
            LabelComponentType.Rect => new Border
            {
                Width = width,
                Height = height,
                BorderBrush = BrushFromHex(component.BorderColor),
                BorderThickness = new Thickness(Math.Max(0.6, component.LineWidthMm * PxPerMm)),
                Background = string.IsNullOrWhiteSpace(component.FillColor)
                    ? Brushes.Transparent
                    : BrushFromHex(component.FillColor)
            },
            LabelComponentType.CheckBox => CheckBoxBlock(text, width, height, component),
            LabelComponentType.Ellipse => new Ellipse
            {
                Width = width,
                Height = height,
                Stroke = BrushFromHex(string.IsNullOrWhiteSpace(component.BorderColor) ? component.ForeColor : component.BorderColor),
                StrokeThickness = Math.Max(0.6, component.LineWidthMm * PxPerMm),
                Fill = string.IsNullOrWhiteSpace(component.FillColor)
                    ? Brushes.Transparent
                    : BrushFromHex(component.FillColor)
            },
            LabelComponentType.Triangle => TriangleBlock(width, height, component),
            LabelComponentType.RoundedRect => new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(Math.Max(3, Math.Min(width, height) * 0.18)),
                BorderBrush = BrushFromHex(component.BorderColor),
                BorderThickness = new Thickness(Math.Max(0.6, component.LineWidthMm * PxPerMm)),
                Background = string.IsNullOrWhiteSpace(component.FillColor)
                    ? Brushes.Transparent
                    : BrushFromHex(component.FillColor)
            },
            LabelComponentType.Line => new Border
            {
                Width = width,
                Height = Math.Max(1, component.LineWidthMm * PxPerMm),
                Background = BrushFromHex(component.ForeColor),
                Margin = new Thickness(0, Math.Max(0, height / 2 - component.LineWidthMm * PxPerMm / 2), 0, 0)
            },
            _ => new Border
            {
                Width = width,
                Height = height,
                Background = string.IsNullOrWhiteSpace(component.FillColor)
                    ? Brushes.Transparent
                    : BrushFromHex(component.FillColor),
                BorderBrush = component.Border ? BrushFromHex(component.BorderColor) : Brushes.Transparent,
                BorderThickness = component.Border ? new Thickness(Math.Max(0.6, component.LineWidthMm * PxPerMm)) : new Thickness(0),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = Math.Max(8, component.FontSizePt),
                    FontFamily = new FontFamily(string.IsNullOrWhiteSpace(component.FontName) ? "Microsoft YaHei" : component.FontName),
                    FontWeight = component.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                    FontStyle = component.Italic ? FontStyles.Italic : FontStyles.Normal,
                    TextDecorations = component.Underline ? TextDecorations.Underline : null,
                    TextAlignment = component.TextAlign switch
                    {
                        "center" => TextAlignment.Center,
                        "right" => TextAlignment.Right,
                        _ => TextAlignment.Left
                    },
                    VerticalAlignment = component.VertAlign switch
                    {
                        "middle" => VerticalAlignment.Center,
                        "bottom" => VerticalAlignment.Bottom,
                        _ => VerticalAlignment.Top
                    },
                    TextWrapping = component.TextFit == "clip" ? TextWrapping.NoWrap : TextWrapping.Wrap,
                    TextTrimming = component.TextFit == "clip" ? TextTrimming.CharacterEllipsis : TextTrimming.None,
                    Foreground = BrushFromHex(component.ForeColor)
                }
            }
        };

        if (Math.Abs(component.Rotation) > 0.1)
        {
            body.RenderTransformOrigin = new Point(0.5, 0.5);
            body.RenderTransform = new RotateTransform(component.Rotation);
        }

        Canvas.SetLeft(body, left);
        Canvas.SetTop(body, top);
        body.Cursor = System.Windows.Input.Cursors.SizeAll;
        body.Tag = component;
        body.MouseLeftButtonDown += OnDragStart;
        body.MouseMove += OnDragMove;
        body.MouseLeftButtonUp += OnDragEnd;
        return body;
    }

    private void OnDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement visual || visual.Tag is not LabelComponent component || component.Locked)
        {
            return;
        }

        _drag = component;
        _dragVisual = visual;
        _dragStart = e.GetPosition(Board);
        _originX = component.X;
        _originY = component.Y;
        visual.CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_drag is null || _dragVisual is null || Document is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(Board);
        var x = _originX + (point.X - _dragStart.X) / PxPerMm;
        var y = _originY + (point.Y - _dragStart.Y) / PxPerMm;
        _drag.X = Math.Clamp(x, 0, Math.Max(0, Document.Page.WidthMm - _drag.W));
        _drag.Y = Math.Clamp(y, 0, Math.Max(0, Document.Page.HeightMm - _drag.H));
        Canvas.SetLeft(_dragVisual, _drag.X * PxPerMm);
        Canvas.SetTop(_dragVisual, _drag.Y * PxPerMm);
        e.Handled = true;
    }

    private void OnDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragVisual is not null)
        {
            _dragVisual.ReleaseMouseCapture();
        }

        var moved = _drag is not null;
        _drag = null;
        _dragVisual = null;
        if (moved)
        {
            Edited?.Invoke(this, EventArgs.Empty);
        }
    }

    private static FrameworkElement TriangleBlock(double width, double height, LabelComponent component)
    {
        var stroke = BrushFromHex(string.IsNullOrWhiteSpace(component.BorderColor) ? component.ForeColor : component.BorderColor);
        return new Polygon
        {
            Width = width,
            Height = height,
            Stretch = Stretch.Fill,
            Points = new PointCollection { new(width / 2, 0), new(width, height), new(0, height) },
            Stroke = stroke,
            StrokeThickness = Math.Max(0.6, component.LineWidthMm * PxPerMm),
            Fill = string.IsNullOrWhiteSpace(component.FillColor)
                ? Brushes.Transparent
                : BrushFromHex(component.FillColor)
        };
    }

    private static FrameworkElement CheckBoxBlock(string text, double width, double height, LabelComponent component)
    {
        var size = Math.Max(10, Math.Min(width, height));
        var on = text.Trim() is "1" or "true" or "True" or "yes" or "YES" or "是" or "勾" or "checked" or "on";
        return new Border
        {
            Width = size,
            Height = size,
            BorderBrush = BrushFromHex(component.ForeColor),
            BorderThickness = new Thickness(1.4),
            Background = Brushes.White,
            Child = new TextBlock
            {
                Text = on ? "✓" : "",
                FontSize = Math.Max(8, size * 0.7),
                Foreground = BrushFromHex(component.ForeColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
    }

    private static FrameworkElement ImageBlock(string path, double width, double height)
    {
        var border = new Border
        {
            Width = width,
            Height = height,
            BorderBrush = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
            BorderThickness = new Thickness(0.6),
            Background = new SolidColorBrush(Color.FromRgb(248, 248, 248))
        };

        if (File.Exists(path))
        {
            var image = new System.Windows.Controls.Image
            {
                Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri(path, UriKind.Absolute))
            };
            border.Child = image;
            border.ToolTip = path;
            return border;
        }

        border.Child = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(path) ? "图片" : System.IO.Path.GetFileName(path),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        border.ToolTip = string.IsNullOrWhiteSpace(path) ? "未指定图片文件" : path;
        return border;
    }

    private static FrameworkElement BarcodeBlock(string text, double width, double height, bool showLabel)
    {
        width = Math.Max(8, width);
        height = Math.Max(8, height);
        var panel = new StackPanel { Width = width, Height = height };
        var bars = new Canvas { Height = Math.Max(10, showLabel ? height - 12 : height), Width = width, ClipToBounds = true };
        var seed = (uint)Math.Abs(text.GetHashCode(StringComparison.Ordinal));
        var x = 0.0;
        while (x < width)
        {
            seed = seed * 1664525u + 1013904223u;
            var w = 1 + (int)(seed % 3);
            bars.Children.Add(new Rectangle
            {
                Width = w,
                Height = Math.Max(1, bars.Height),
                Fill = (seed & 1) == 0 ? Brushes.Black : Brushes.Transparent
            });
            Canvas.SetLeft(bars.Children[^1], x);
            x += w + 0.6;
        }

        panel.Children.Add(bars);
        if (showLabel)
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 8,
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        return new Border
        {
            Child = panel,
            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
            BorderThickness = new Thickness(0.5)
        };
    }

    private static FrameworkElement QrBlock(string text, double width, double height)
    {
        var size = Math.Max(8, Math.Min(width, height));
        var grid = new UniformGrid { Rows = 7, Columns = 7, Width = size, Height = size };
        var seed = Math.Abs(text.GetHashCode(StringComparison.Ordinal));
        for (var i = 0; i < 49; i++)
        {
            seed = seed * 1664525 + 1013904223;
            grid.Children.Add(new Rectangle
            {
                Fill = (seed & 1) == 0 ? Brushes.Black : Brushes.White,
                Margin = new Thickness(0.4)
            });
        }

        return new Border
        {
            Child = grid,
            ToolTip = text,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1)
        };
    }

    private static Brush BrushFromHex(string? hex)
    {
        try
        {
            var value = string.IsNullOrWhiteSpace(hex) ? "#1C1C1C" : hex.Trim();
            return (Brush)new BrushConverter().ConvertFromString(value)!;
        }
        catch (Exception)
        {
            return new SolidColorBrush(Color.FromRgb(28, 28, 28));
        }
    }

    private static string ResolveText(LabelComponent component, SampleRow? sample)
    {
        if (component.Bind.Kind == BindKind.Literal)
        {
            return component.Bind.Literal ?? "";
        }

        var key = component.Bind.FieldKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        if (sample?.Values.TryGetValue(key, out var value) == true)
        {
            return value;
        }

        return $"[{key}]";
    }
}
