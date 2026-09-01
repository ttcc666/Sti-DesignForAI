using System.Collections.ObjectModel;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows.Threading;
using StiLabel.App.Sti;
using StiLabel.Core.Catalog;
using StiLabel.Core.Drafting;
using StiLabel.Core.Labeling;
using StiLabel.Core.Llm;
using StiLabel.Core.Services;
using StiLabel.Data;

namespace StiLabel.App.ViewModels;

public sealed partial class FieldRow : ObservableObject
{
    public int Id { get; }
    public string Key { get; }
    public string DisplayName { get; }
    public string DataType { get; }

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private bool _bound;

    public FieldRow(FieldItem item)
    {
        Id = item.Id;
        Key = item.Key;
        DisplayName = item.DisplayName;
        DataType = item.DataType;
        Selected = item.Selected || item.Required;
    }

    public FieldItem ToItem() => new()
    {
        Id = Id,
        Key = Key,
        DisplayName = DisplayName,
        DataType = DataType,
        Selected = Selected,
        Bound = Bound
    };
}

public sealed partial class ChatLine : ObservableObject
{
    public string Role { get; init; } = "助手";

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _canApply;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _applied;
    [ObservableProperty] private bool _needsConfirm;
    [ObservableProperty] private bool _canRetry;

    public string RetryText { get; set; } = "";
    public IReadOnlyList<ChatImage> RetryImages { get; set; } = [];
    public IReadOnlyList<ChatImage> Images { get; set; } = [];
    public bool HasImages => Images.Count > 0;
    public ObservableCollection<string> Tools { get; } = [];
    public bool HasTools => Tools.Count > 0;
    public LabelDocument? Before { get; set; }
    public LabelDocument? After { get; set; }

    public void ReplaceTools(IReadOnlyList<string>? names)
    {
        var next = names is null
            ? []
            : names.Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        if (Tools.Count == next.Count && Tools.SequenceEqual(next))
        {
            return;
        }

        Tools.Clear();
        foreach (var name in next)
        {
            Tools.Add(name);
        }

        OnPropertyChanged(nameof(HasTools));
    }
}

public sealed class ChatImage
{
    public required string Path { get; init; }
    public ImageSource? Preview { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ITemplateWorkspace _workspace;
    private readonly IFieldCatalog _fields;
    private readonly IAppStore _store;
    private readonly IDraftBuilder _drafts;
    private readonly IWorkbenchAgent _agent;
    private readonly IStiWorkbench _sti;
    private readonly IModelOptionsStore _models;
    private readonly Stack<LabelDocument> _undo = new();
    private readonly DispatcherTimer _persistTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _suppressDesignerApply;
    private bool _syncingModel;
    private bool _syncingPage;
    private CancellationTokenSource? _chatCts;
    private int _chatEpoch;

    [ObservableProperty] private string _title = "STI 标签工位";
    [ObservableProperty] private string _statusFile = "未命名";
    [ObservableProperty] private string _statusPage = "70×40 mm";
    [ObservableProperty] private string _statusPrinter = "未选择打印机";
    [ObservableProperty] private string _statusModel = "模型：未启用";
    [ObservableProperty] private string _statusDirty = "已保存";
    [ObservableProperty] private double _pageWidthMm = 70;
    [ObservableProperty] private double _pageHeightMm = 40;
    [ObservableProperty] private double _pageMarginMm = 2;
    [ObservableProperty] private bool _pageLandscape;
    [ObservableProperty] private string _chatInput = "";
    [ObservableProperty] private string _newFieldName = "";
    [ObservableProperty] private string _agentBanner = "未启用大模型。设计、保存、打样仍可用。";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isChatEnabled;
    [ObservableProperty] private bool _chatCollapsed;
    [ObservableProperty] private PagePreset _selectedPreset = PagePresets.All[0];
    [ObservableProperty] private string? _selectedPrinter;
    [ObservableProperty] private LabelDocument _document = new();
    [ObservableProperty] private SampleRow? _sampleRow;
    [ObservableProperty] private string _sampleSummary = "尚未加载示例数据";
    [ObservableProperty] private ChatLine? _lastAssistant;
    [ObservableProperty] private string _selectedChatModel = "";
    [ObservableProperty] private int _contextUsed;
    [ObservableProperty] private int _contextLimit = CompactModes.DefaultContextTokens;

    public string ContextUsageText =>
        $"上下文 {CompactModes.FormatTokens(ContextUsed)} / {CompactModes.FormatContextTokens(ContextLimit)}";

    public string ContextUsagePercentText =>
        CompactModes.UsagePercent(ContextUsed, ContextLimit) + "%";

    public int ContextUsagePercent =>
        CompactModes.UsagePercent(ContextUsed, ContextLimit);

    public string ContextUsageLevel =>
        ContextUsagePercent >= 90 ? "high"
        : ContextUsagePercent >= 70 ? "warn"
        : "ok";

    public ObservableCollection<FieldRow> Fields { get; } = [];
    public ObservableCollection<ChatLine> Messages { get; } = [];
    public ObservableCollection<PagePreset> Presets { get; } = new(PagePresets.All);
    public ObservableCollection<string> Printers { get; } = [];
    public ObservableCollection<string> RecentFiles { get; } = [];
    public ObservableCollection<VersionEntry> VersionFiles { get; } = [];
    public ObservableCollection<string> ModelChoices { get; } = [];
    public ObservableCollection<ChatImage> PendingImages { get; } = [];
    public bool HasPendingImages => PendingImages.Count > 0;
    public bool CanSwitchModel => IsChatEnabled && !IsBusy;
    public bool CanPrint => Printers.Count > 0;
    public bool IsWelcomeState => Messages.Count <= 1 && !IsBusy;
    public string WelcomeSubtitle => IsChatEnabled
        ? "我可以帮你做标签、改布局、加字段。点下面的提示，或直接告诉我你的需求。"
        : AgentBanner;
    public GridLength ChatPaneWidth => ChatCollapsed ? new GridLength(56) : new GridLength(400);
    public event Action? OpenSettingsRequested;

    public MainViewModel(
        ITemplateWorkspace workspace,
        IFieldCatalog fields,
        IAppStore store,
        IDraftBuilder drafts,
        IWorkbenchAgent agent,
        IStiWorkbench sti,
        IModelOptionsStore models)
    {
        _workspace = workspace;
        _fields = fields;
        _store = store;
        _drafts = drafts;
        _agent = agent;
        _sti = sti;
        _models = models;
        _workspace.Changed += (_, _) => SyncFromWorkspace();
        _sti.CanvasEdited += (_, _) => SchedulePersistDesigner();
        _persistTimer.Tick += (_, _) =>
        {
            _persistTimer.Stop();
            PersistDesigner();
        };
        PendingImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingImages));
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsWelcomeState));
    }

    public async Task InitializeAsync()
    {
        await ResetDictionaryAsync();

        RefreshPrinters();
        var lastPrinter = await _store.GetAsync("LastPrinter");
        if (!string.IsNullOrWhiteSpace(lastPrinter) && Printers.Contains(lastPrinter))
        {
            SelectedPrinter = lastPrinter;
        }

        var rows = await _store.LoadSampleRowsAsync();
        SampleRow = rows.FirstOrDefault();
        SampleSummary = SampleRow is null
            ? "没有示例数据"
            : string.Join("  ", SampleRow.Values.Select(kv => $"{kv.Key}={kv.Value}"));

        foreach (var recent in await _store.ListRecentAsync())
        {
            if (File.Exists(recent.Path))
            {
                RecentFiles.Add(recent.Path);
            }
        }

        _workspace.NewBlank(SelectedPreset);
        RefreshVersionFiles();
        await RefreshModelStateAsync();
        Messages.Add(new ChatLine
        {
            Role = "助手",
            Text = IsChatEnabled
                ? "可以说「做一张 70×40 物料标签，要物料编码、品名、批次、QR」。大改会先让你确认。"
                : "对话未开启。请到「模型设置」启用并填写地址、模型。设计器仍可直接用。"
        });
    }

    public async Task RefreshModelStateAsync()
    {
        var wasEnabled = IsChatEnabled;
        var options = await _models.LoadAsync();
        IsChatEnabled = options.IsReady;
        StatusModel = options.IsReady
            ? $"模型：{options.Model}"
            : options.Enabled
                ? "模型：未配置完整"
                : "模型：未启用";
        AgentBanner = options.IsReady
            ? $"已接 {options.Model}。对话记住上下文，回复会边生成边显示。"
            : "未启用大模型。打开、保存、设计、打样不受影响。";
        await SyncModelChoicesAsync(options);
        ContextLimit = CompactModes.ClampContextTokens(options.ContextTokens);
        if (!IsChatEnabled)
        {
            ContextUsed = 0;
            _agent.ResetConversation();
        }
        if (IsChatEnabled && !wasEnabled)
        {
            Messages.Add(new ChatLine
            {
                Role = "助手",
                Text = "对话已启用。可以说「做一张 70×40 物料标签，要物料编码、品名、批次、QR」。"
            });
        }
    }

    private async Task SyncModelChoicesAsync(ModelOptions options)
    {
        var extras = await _models.LoadModelNamesAsync();
        var provider = ModelProviders.Resolve(options.Provider, options.Endpoint);
        var names = extras
            .Concat(provider.Models)
            .Append(options.Model)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ModelChoices.Clear();
        foreach (var name in names)
        {
            ModelChoices.Add(name);
        }

        _syncingModel = true;
        SelectedChatModel = options.Model;
        _syncingModel = false;
        OnPropertyChanged(nameof(CanSwitchModel));
    }

    partial void OnSelectedChatModelChanged(string value)
    {
        if (_syncingModel || string.IsNullOrWhiteSpace(value) || !IsChatEnabled)
        {
            return;
        }

        _ = SwitchChatModelAsync(value);
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSwitchModel));
        OnPropertyChanged(nameof(IsWelcomeState));
    }

    partial void OnIsChatEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSwitchModel));
        OnPropertyChanged(nameof(WelcomeSubtitle));
    }

    partial void OnAgentBannerChanged(string value) => OnPropertyChanged(nameof(WelcomeSubtitle));

    [RelayCommand]
    private void UsePrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || IsBusy || !IsChatEnabled)
        {
            return;
        }

        ChatInput = text;
        _ = SendChatAsync();
    }

    private async Task SwitchChatModelAsync(string name)
    {
        var options = await _models.LoadAsync();
        var model = name.Trim();
        if (string.Equals(options.Model, model, StringComparison.Ordinal))
        {
            return;
        }

        options.Model = model;
        await _models.SaveAsync(options);
        if (!ModelChoices.Contains(model, StringComparer.OrdinalIgnoreCase))
        {
            ModelChoices.Add(model);
        }

        StatusModel = options.IsReady ? $"模型：{options.Model}" : StatusModel;
        AgentBanner = options.IsReady
            ? $"已切到 {options.Model}。下一句会用新模型，当前会话会新开一轮。"
            : AgentBanner;
        ContextUsed = 0;
    }

    [RelayCommand]
    private async Task NewLabelAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        _undo.Clear();
        await ResetDictionaryAsync();
        _workspace.NewBlank(SelectedPreset);
        BeginNewSession("已打开新模板，对话已新开一轮。");
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "标签文件 (*.label.json;*.mrt)|*.label.json;*.mrt|所有文件|*.*",
            InitialDirectory = AppPaths.Templates
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await OpenPathAsync(dialog.FileName);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !await ConfirmDiscardAsync())
        {
            return;
        }

        await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_workspace.FilePath))
        {
            await SaveAsAsync();
            return;
        }

        PersistDesigner();
        if (!ConfirmHealth("保存"))
        {
            return;
        }

        await _workspace.SaveAsync();
        SaveMrt();
        await _store.TouchRecentAsync(_workspace.SourcePath ?? _workspace.FilePath!);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "STI 模板 (*.mrt)|*.mrt|标签草稿 (*.label.json)|*.label.json",
            InitialDirectory = AppPaths.Templates,
            FileName = "物料标签.mrt"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        PersistDesigner();
        if (!ConfirmHealth("保存"))
        {
            return;
        }

        await _workspace.SaveAsync(dialog.FileName);
        SaveMrt();
        await _store.TouchRecentAsync(dialog.FileName);
        RememberRecent(dialog.FileName);
    }

    [RelayCommand]
    private async Task SaveVersionAsync()
    {
        PersistDesigner();
        if (!ConfirmHealth("另存一版"))
        {
            return;
        }

        var path = await _workspace.SaveVersionAsync("另存一版");
        try
        {
            _sti.SaveMrt(Path.ChangeExtension(path, ".mrt"));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("版本 .mrt 未写出：" + ex.Message, "STI 标签工位");
        }

        RefreshVersionFiles();
        System.Windows.MessageBox.Show("已写入版本目录。", "STI 标签工位");
    }

    [RelayCommand]
    private async Task OpenVersionAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !await ConfirmDiscardAsync())
        {
            return;
        }

        await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task AddFieldAsync()
    {
        var name = NewFieldName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var item = await _fields.UpsertAsync(name);
            NewFieldName = "";
            await ReloadFieldsAsync();
            var row = Fields.FirstOrDefault(f => f.Id == item.Id);
            if (row is not null)
            {
                row.Selected = true;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "STI 标签工位");
        }
    }

    [RelayCommand]
    private async Task RemoveFieldAsync(FieldRow? row)
    {
        if (row is null || row.Id <= 0)
        {
            return;
        }

        await _fields.DeleteAsync(row.Id);
        Fields.Remove(row);
        if (!_suppressDesignerApply)
        {
            _sti.ApplyDocument(Document, Fields.Select(f => f.ToItem()).ToList(), SampleRow);
        }
    }

    [RelayCommand]
    private void InsertImage()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var before = SnapshotCanvas();
        var doc = _drafts.AddComponent(before, "image", null, dialog.FileName, null, null, 16, 16);
        _workspace.ReplaceDocument(doc);
        Messages.Add(new ChatLine
        {
            Role = "助手",
            Text = "已插入图片。可在设计器里拖位置、改大小。",
            Before = before,
            After = doc.Clone(),
            Applied = true,
            CanUndo = true
        });
    }

    [RelayCommand]
    private void GenerateDraft()
    {
        var selected = Fields.Where(f => f.Selected).Select(f => f.ToItem()).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("请先在右边勾选字段。", "STI 标签工位");
            return;
        }

        var before = SnapshotCanvas();
        var doc = _drafts.Build(SelectedPreset, selected, SelectedPrinter);
        _workspace.ReplaceDocument(doc);
        var line = new ChatLine
        {
            Role = "助手",
            Text = $"已按所选 {selected.Count} 个字段生成草稿。可继续微调或打样。",
            Before = before,
            After = doc.Clone(),
            Applied = true,
            CanUndo = true
        };
        Messages.Add(line);
        LastAssistant = line;
    }

    [RelayCommand]
    private Task SendChatAsync()
    {
        var text = ChatInput.Trim();
        var images = PendingImages.ToList();
        if ((string.IsNullOrWhiteSpace(text) && images.Count == 0) || IsBusy || !IsChatEnabled)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            text = images.Count == 1 ? "请看这张图。" : "请看这些图。";
        }

        ChatInput = "";
        PendingImages.Clear();
        return SendPromptAsync(text, reuse: null, images);
    }

    [RelayCommand]
    private Task ResendChatAsync(ChatLine? line)
    {
        if (line is null || !line.CanRetry || IsBusy || !IsChatEnabled)
        {
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(line.RetryText) && line.RetryImages.Count == 0)
        {
            return Task.CompletedTask;
        }

        return SendPromptAsync(line.RetryText, reuse: line, line.RetryImages);
    }

    [RelayCommand]
    private void AttachImage()
    {
        if (!IsChatEnabled || IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要发给模型的图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var path in dialog.FileNames)
        {
            TryAddPendingImage(path);
        }
    }

    [RelayCommand]
    private void RemovePendingImage(ChatImage? image)
    {
        if (image is not null)
        {
            PendingImages.Remove(image);
        }
    }

    public void AttachDroppedImage(string path) => TryAddPendingImage(path);

    public void AttachClipboardImage()
    {
        if (!IsChatEnabled || IsBusy || !Clipboard.ContainsImage())
        {
            return;
        }

        var frame = Clipboard.GetImage();
        if (frame is null)
        {
            return;
        }

        AppPaths.EnsureCreated();
        var path = Path.Combine(AppPaths.ChatImages, $"paste-{DateTime.Now:yyyyMMddHHmmssfff}.png");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(stream);
        TryAddPendingImage(path);
    }

    private void TryAddPendingImage(string path)
    {
        if (PendingImages.Count >= 4)
        {
            return;
        }

        var file = path.Trim().Trim('"');
        if (!File.Exists(file)
            || PendingImages.Any(p => string.Equals(p.Path, file, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var info = new FileInfo(file);
        if (info.Length > 8 * 1024 * 1024)
        {
            return;
        }

        PendingImages.Add(new ChatImage { Path = file, Preview = LoadPreview(file) });
    }

    private static ImageSource? LoadPreview(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 240;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task SendPromptAsync(string text, ChatLine? reuse, IReadOnlyList<ChatImage> images)
    {
        var epoch = _chatEpoch;
        ChatLine line;
        if (reuse is null)
        {
            Messages.Add(new ChatLine { Role = "用户", Text = text, Images = images });
            line = new ChatLine { Role = "助手", Text = "…" };
            Messages.Add(line);
        }
        else
        {
            reuse.Text = "正在重发…";
            reuse.CanRetry = false;
            reuse.CanApply = false;
            reuse.CanUndo = false;
            reuse.Applied = false;
            reuse.NeedsConfirm = false;
            reuse.Before = null;
            reuse.After = null;
            reuse.ReplaceTools([]);
            line = reuse;
        }

        line.RetryText = text;
        line.RetryImages = images;
        LastAssistant = line;
        IsBusy = true;
        _chatCts = new CancellationTokenSource();
        var before = SnapshotCanvas();
        try
        {
            var progress = new Progress<AgentProgress>(chunk =>
            {
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    line.Text = chunk.Text;
                }

                line.ReplaceTools(chunk.Tools);
            });
            var reply = await _agent.HandleAsync(
                text,
                before,
                Fields.Select(f => f.ToItem()).ToList(),
                SelectedPreset,
                Printers.ToList(),
                SampleRow,
                Presets.ToList(),
                RecentFiles.ToList(),
                ListVersionFiles(),
                images.Select(i => i.Path).ToList(),
                progress,
                _chatCts.Token);

            if (epoch != _chatEpoch)
            {
                return;
            }

            line.Text = string.IsNullOrWhiteSpace(reply.Text) ? line.Text : reply.Text;
            line.Before = before;
            line.After = reply.Document;
            line.ReplaceTools(reply.Tools);
            ApplyContextUsage(reply.ContextUsed, reply.ContextLimit);
            if (reply.Failed)
            {
                line.CanRetry = true;
                return;
            }

            if (reply.Sample is not null)
            {
                SampleRow = reply.Sample;
                SampleSummary = string.Join("  ", reply.Sample.Values.Select(kv => $"{kv.Key}={kv.Value}"));
                await _store.SaveSampleRowsAsync([reply.Sample]);
                if (!_suppressDesignerApply)
                {
                    _sti.RegisterFields(Fields.Select(f => f.ToItem()).ToList(), SampleRow);
                }
            }

            if (reply.SelectedKeys is { Count: > 0 })
            {
                var keys = reply.SelectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var field in Fields)
                {
                    field.Selected = keys.Contains(field.Key) || keys.Contains(field.DisplayName);
                }
            }

            if (!string.IsNullOrWhiteSpace(reply.OpenPath))
            {
                if (!await ConfirmDiscardAsync())
                {
                    line.Text += " 未打开，当前稿未保存。";
                    return;
                }

                await OpenPathAsync(reply.OpenPath);
                return;
            }

            if (reply.NewBlankRequested)
            {
                if (!await ConfirmDiscardAsync())
                {
                    line.Text += " 未新建。";
                }
                else
                {
                    _undo.Clear();
                    await ResetDictionaryAsync();
                    var preset = SelectedPreset;
                    if (reply.Document is not null)
                    {
                        preset = new PagePreset
                        {
                            Name = "空白",
                            WidthMm = reply.Document.Page.WidthMm,
                            HeightMm = reply.Document.Page.HeightMm
                        };
                    }

                    _workspace.NewBlank(preset);
                    BeginNewSession("已打开新模板，对话已新开一轮。");
                }
            }
            else if (reply.Document is not null)
            {
                var canvasNow = SnapshotCanvas();
                var userMoved = !SameDocument(canvasNow, before);
                if (userMoved)
                {
                    line.Text += " 你正在改画布，我没自动覆盖。";
                    line.CanApply = true;
                    line.NeedsConfirm = reply.RequiresConfirm;
                }
                else if (reply.RequiresConfirm)
                {
                    line.NeedsConfirm = true;
                    line.CanApply = false;
                }
                else
                {
                    ApplyLine(line);
                }
            }

            if (reply.SaveMode == "version")
            {
                await SaveVersionAsync();
            }
            else if (reply.SaveMode == "save")
            {
                await SaveAsync();
            }

            if (reply.PrintRequested)
            {
                PreviewLabel();
            }

            if (!string.IsNullOrWhiteSpace(reply.ExportMode))
            {
                ExportReply(reply.ExportMode, reply.ExportPath, line);
            }

            await ReloadFieldsAsync(selectNew: true);
        }
        catch (OperationCanceledException)
        {
            if (epoch != _chatEpoch)
            {
                return;
            }

            line.Text = string.IsNullOrWhiteSpace(line.Text) || line.Text is "…" or "正在重发…"
                ? "已停止。"
                : line.Text.Trim() + "\n已停止。";
        }
        catch (Exception ex)
        {
            if (epoch != _chatEpoch)
            {
                return;
            }

            line.Text = "消息发送失败：" + ex.Message;
            line.CanRetry = true;
        }
        finally
        {
            IsBusy = false;
            _chatCts.Dispose();
            _chatCts = null;
        }
    }

    partial void OnContextUsedChanged(int value) => NotifyContextUsage();

    partial void OnContextLimitChanged(int value) => NotifyContextUsage();

    private void NotifyContextUsage()
    {
        OnPropertyChanged(nameof(ContextUsageText));
        OnPropertyChanged(nameof(ContextUsagePercentText));
        OnPropertyChanged(nameof(ContextUsagePercent));
        OnPropertyChanged(nameof(ContextUsageLevel));
    }

    private void ApplyContextUsage(int? used, int limit)
    {
        if (limit > 0)
        {
            ContextLimit = CompactModes.ClampContextTokens(limit);
        }

        if (used is int n)
        {
            ContextUsed = Math.Max(0, n);
        }
    }

    [RelayCommand]
    private void StopChat() => _chatCts?.Cancel();

    [RelayCommand]
    private void NewChat() => BeginNewSession();

    private void BeginNewSession(string? welcome = null)
    {
        if (IsBusy)
        {
            _chatCts?.Cancel();
        }

        _chatEpoch++;
        _agent.ResetConversation();
        Messages.Clear();
        PendingImages.Clear();
        LastAssistant = null;
        ContextUsed = 0;
        Messages.Add(new ChatLine
        {
            Role = "助手",
            Text = welcome
                ?? (IsChatEnabled
                    ? "已开启新会话。上一轮对话不再发给模型。"
                    : "已清空对话。启用模型后即可开始。")
        });
    }

    [RelayCommand]
    private void ToggleChat()
    {
        ChatCollapsed = !ChatCollapsed;
        OnPropertyChanged(nameof(ChatPaneWidth));
    }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    private void ConfirmChat(ChatLine? line)
    {
        if (line?.After is null)
        {
            return;
        }

        var go = MessageBox.Show(line.Text + "\n\n确认应用到设计器？", "STI 标签工位", MessageBoxButton.YesNo);
        if (go != MessageBoxResult.Yes)
        {
            return;
        }

        ApplyLine(line);
    }

    [RelayCommand]
    private void ApplyChat(ChatLine? line)
    {
        if (line?.After is null)
        {
            return;
        }

        ApplyLine(line);
    }

    [RelayCommand]
    private void UndoChat(ChatLine? line)
    {
        if (line?.Before is null || !line.CanUndo)
        {
            return;
        }

        var now = SnapshotCanvas();
        if (line.After is not null && !SameDocument(now, line.After))
        {
            MessageBox.Show("画布已继续改过，这条无法安全撤回。请用设计器撤销。", "STI 标签工位");
            return;
        }

        _workspace.ReplaceDocument(line.Before.Clone(), markDirty: true);
        line.Applied = false;
        line.CanUndo = false;
        line.CanApply = line.After is not null;
        line.NeedsConfirm = false;
    }

    [RelayCommand]
    private void RefreshPrinters()
    {
        Printers.Clear();
        try
        {
            using var server = new LocalPrintServer();
            foreach (var queue in server.GetPrintQueues())
            {
                Printers.Add(queue.Name);
            }
        }
        catch
        {
            // 无打印后台服务时保持空列表
        }

        StatusPrinter = string.IsNullOrWhiteSpace(SelectedPrinter) ? "未选择打印机" : SelectedPrinter;
        OnPropertyChanged(nameof(CanPrint));
    }

    [RelayCommand]
    private async Task ApplyPresetAsync()
    {
        if (_workspace.Document.Components.Count > 0)
        {
            var result = System.Windows.MessageBox.Show(
                "改页尺寸可能造成裁切，是否继续？",
                "STI 标签工位",
                System.Windows.MessageBoxButton.YesNo);
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }
        }

        PersistDesigner();
        PushUndo();
        var width = PageWidthMm > 0 ? PageWidthMm : SelectedPreset.WidthMm;
        var height = PageHeightMm > 0 ? PageHeightMm : SelectedPreset.HeightMm;
        var doc = _drafts.SetPage(_workspace.Document, width, height);
        doc = _drafts.SetOrientation(doc, PageLandscape ? "Landscape" : "Portrait");
        doc = _drafts.SetMargin(doc, PageMarginMm);
        _workspace.ReplaceDocument(doc);
        await _store.SetAsync("LastPagePreset", SelectedPreset.Name);
    }

    [RelayCommand]
    private void PreviewLabel()
    {
        if (!PrepareOutput("预览"))
        {
            return;
        }

        _sti.Preview(Fields.Select(f => f.ToItem()).ToList(), SampleRow);
    }

    [RelayCommand]
    private void PrintLabel()
    {
        if (!CanPrint)
        {
            MessageBox.Show("未检测到打印机。预览仍可用。", "STI 标签工位");
            return;
        }

        if (!PrepareOutput("打样"))
        {
            return;
        }

        _sti.Print(Fields.Select(f => f.ToItem()).ToList(), SampleRow, SelectedPrinter);
    }

    [RelayCommand]
    private void ExportPdf() => ExportReply("pdf", null, null);

    [RelayCommand]
    private void ExportPng() => ExportReply("image", null, null);

    private async Task OpenPathAsync(string path)
    {
        _undo.Clear();
        await _workspace.OpenAsync(path);
        if (path.EndsWith(".mrt", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            _sti.LoadMrt(path);
            _suppressDesignerApply = true;
            try
            {
                _workspace.ReplaceDocument(_sti.CaptureDocument(Fields.Select(f => f.ToItem()).ToList()), false);
            }
            finally
            {
                _suppressDesignerApply = false;
            }
        }

        await ImportTemplateFieldsAsync(path.EndsWith(".mrt", StringComparison.OrdinalIgnoreCase));
        await _store.TouchRecentAsync(_workspace.SourcePath ?? _workspace.FilePath ?? path);
        RememberRecent(_workspace.SourcePath ?? _workspace.FilePath ?? path);
        var preset = PagePresets.All.FirstOrDefault(p =>
            Math.Abs(p.WidthMm - _workspace.Document.Page.WidthMm) < 0.1 &&
            Math.Abs(p.HeightMm - _workspace.Document.Page.HeightMm) < 0.1);
        if (preset is not null)
        {
            SelectedPreset = preset;
        }

        BeginNewSession("已打开新模板，对话已新开一轮。");
    }

    private static IReadOnlyList<string> ListVersionFiles()
    {
        try
        {
            return Directory.Exists(AppPaths.Versions)
                ? new DirectoryInfo(AppPaths.Versions)
                    .GetFiles("*.label.json")
                    .OrderByDescending(f => f.LastWriteTime)
                    .Take(20)
                    .Select(f => f.FullName)
                    .ToList()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private void ExportReply(string mode, string? path, ChatLine? line)
    {
        if (!PrepareOutput("导出", skipSamplePrompt: true))
        {
            return;
        }

        var ext = mode.Equals("pdf", StringComparison.OrdinalIgnoreCase) ? "pdf" : "png";
        var file = ResolveExportPath(path, ext);
        if (file is null)
        {
            if (line is not null)
            {
                line.Text += " 未选择导出路径。";
            }

            return;
        }

        try
        {
            _sti.Export(file, ext == "pdf" ? "pdf" : "image", Fields.Select(f => f.ToItem()).ToList(), SampleRow);
            if (line is not null)
            {
                line.Text += " 已导出 " + file;
            }
        }
        catch (Exception ex)
        {
            if (line is not null)
            {
                line.Text += " 导出失败：" + ex.Message;
            }
            else
            {
                MessageBox.Show("导出失败：" + ex.Message, "STI 标签工位");
            }
        }
    }

    private static string? ResolveExportPath(string? path, string ext)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var file = path.Trim().Trim('"');
            if (Directory.Exists(file))
            {
                file = Path.Combine(file, "标签." + ext);
            }
            else if (!Path.HasExtension(file))
            {
                file += "." + ext;
            }

            var folder = Path.GetDirectoryName(file);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Directory.CreateDirectory(folder);
            }

            return file;
        }

        var dialog = new SaveFileDialog
        {
            Filter = ext == "pdf" ? "PDF (*.pdf)|*.pdf" : "PNG (*.png)|*.png",
            InitialDirectory = AppPaths.Templates,
            FileName = "标签." + ext
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void ApplyLine(ChatLine line)
    {
        if (line.After is null)
        {
            return;
        }

        line.Before ??= SnapshotCanvas();
        _workspace.ReplaceDocument(line.After.Clone(), markDirty: true);
        line.Applied = true;
        line.CanUndo = true;
        line.CanApply = false;
        line.NeedsConfirm = false;
        if (!line.Text.Contains("已应用", StringComparison.Ordinal))
        {
            line.Text = line.Text.Trim() + " 已应用。";
        }
    }

    private LabelDocument SnapshotCanvas()
    {
        PersistDesigner();
        return _workspace.Document.Clone();
    }

    private static bool SameDocument(LabelDocument left, LabelDocument right) =>
        Near(left.Page.WidthMm, right.Page.WidthMm)
        && Near(left.Page.HeightMm, right.Page.HeightMm)
        && Near(left.Page.MarginMm, right.Page.MarginMm)
        && string.Equals(left.Page.Orientation, right.Page.Orientation, StringComparison.OrdinalIgnoreCase)
        && left.Components.Count == right.Components.Count
        && left.Variables.Count == right.Variables.Count
        && left.Components.Zip(right.Components).All(pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.Type == pair.Second.Type
            && pair.First.Bind.Kind == pair.Second.Bind.Kind
            && pair.First.Bind.FieldKey == pair.Second.Bind.FieldKey
            && pair.First.Bind.Literal == pair.Second.Bind.Literal
            && pair.First.FontSizePt.Equals(pair.Second.FontSizePt)
            && pair.First.Bold == pair.Second.Bold
            && pair.First.ForeColor == pair.Second.ForeColor
            && pair.First.Visible == pair.Second.Visible
            && pair.First.Expression == pair.Second.Expression
            && Near(pair.First.X, pair.Second.X)
            && Near(pair.First.Y, pair.Second.Y)
            && Near(pair.First.W, pair.Second.W)
            && Near(pair.First.H, pair.Second.H));

    private static bool Near(double left, double right) => Math.Abs(left - right) < 0.2;

    private async Task ResetDictionaryAsync()
    {
        await _fields.ClearAsync();
        Fields.Clear();
    }

    private async Task ImportTemplateFieldsAsync(bool fromReport)
    {
        await _fields.ClearAsync();
        var extracted = fromReport
            ? _sti.ExtractFields()
            : StiReportFactory.ExtractFields(_workspace.Document);
        if (extracted.Count == 0)
        {
            Fields.Clear();
            return;
        }

        foreach (var item in extracted)
        {
            await _fields.UpsertAsync(item.DisplayName, item.Key, item.DataType);
        }

        var keys = extracted
            .SelectMany(f => new[] { f.Key, f.DisplayName })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await ReloadFieldsAsync(applyDesigner: false);
        foreach (var row in Fields)
        {
            if (keys.Contains(row.Key) || keys.Contains(row.DisplayName))
            {
                row.Selected = true;
            }
        }

        RefreshBoundFlags();
        _sti.RegisterFields(Fields.Select(f => f.ToItem()).ToList(), SampleRow);
        Messages.Add(new ChatLine
        {
            Role = "助手",
            Text = "已从模板读入字段：" + string.Join("、", extracted.Select(f => f.DisplayName)) + "。"
        });
    }

    private async Task ReloadFieldsAsync(bool selectNew = false, bool applyDesigner = true)
    {
        var firstLoad = Fields.Count == 0;
        var selected = Fields
            .Where(f => f.Selected)
            .Select(f => f.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingKeys = Fields
            .Select(f => f.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = await _fields.ListAsync();
        Fields.Clear();
        foreach (var item in items)
        {
            var row = new FieldRow(item);
            if (!firstLoad)
            {
                row.Selected = selected.Contains(item.Key)
                    || item.Selected
                    || (selectNew && !existingKeys.Contains(item.Key));
            }

            Fields.Add(row);
        }

        RefreshBoundFlags();
        if (applyDesigner && !_suppressDesignerApply && Document.Page.WidthMm > 0)
        {
            _sti.ApplyDocument(Document, Fields.Select(f => f.ToItem()).ToList(), SampleRow);
        }
    }

    private void RefreshBoundFlags()
    {
        var bound = new HashSet<string>(
            Document.Components
                .Where(c => c.Bind.Kind == BindKind.Field && c.Bind.FieldKey is not null)
                .Select(c => c.Bind.FieldKey!),
            StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            field.Bound = bound.Contains(field.Key);
        }
    }

    private void PushUndo() => _undo.Push(_workspace.Document.Clone());

    private void SyncFromWorkspace()
    {
        Document = _workspace.Document.Clone();
        StatusFile = string.IsNullOrWhiteSpace(_workspace.FilePath)
            ? "未命名"
            : Path.GetFileName(_workspace.FilePath);
        StatusPage = $"{Document.Page.WidthMm:0}×{Document.Page.HeightMm:0} mm";
        StatusDirty = _workspace.IsDirty ? "未保存" : "已保存";
        _syncingPage = true;
        PageWidthMm = Document.Page.WidthMm;
        PageHeightMm = Document.Page.HeightMm;
        PageMarginMm = Document.Page.MarginMm;
        PageLandscape = Document.Page.WidthMm > Document.Page.HeightMm
            || Document.Page.Orientation.Equals("Landscape", StringComparison.OrdinalIgnoreCase);
        _syncingPage = false;
        Title = string.IsNullOrWhiteSpace(_workspace.FilePath)
            ? "STI 标签工位"
            : $"STI 标签工位 — {Path.GetFileName(_workspace.FilePath)}";
        Document.Page.PrinterName = SelectedPrinter;
        RefreshBoundFlags();

        if (!_suppressDesignerApply)
        {
            _sti.ApplyDocument(Document, Fields.Select(f => f.ToItem()).ToList(), SampleRow);
        }
    }

    private void SchedulePersistDesigner()
    {
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void PersistDesigner()
    {
        _persistTimer.Stop();
        var captured = _sti.CaptureDocument(Fields.Select(f => f.ToItem()).ToList());
        if (SameDocument(_workspace.Document, captured))
        {
            return;
        }

        _suppressDesignerApply = true;
        try
        {
            _workspace.ReplaceDocument(captured, markDirty: true);
        }
        finally
        {
            _suppressDesignerApply = false;
        }
    }

    private void SaveMrt()
    {
        try
        {
            _sti.SaveMrt(_workspace.MrtPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("保存 .mrt 失败：" + ex.Message, "STI 标签工位");
        }
    }

    private void RememberRecent(string path)
    {
        var existing = RecentFiles.FirstOrDefault(p =>
            string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentFiles.Remove(existing);
        }

        RecentFiles.Insert(0, path);
    }

    private void RefreshVersionFiles()
    {
        VersionFiles.Clear();
        foreach (var path in ListVersionFiles())
        {
            VersionFiles.Add(new VersionEntry { Path = path });
        }
    }

    public async Task<bool> ConfirmDiscardAsync()
    {
        PersistDesigner();
        if (!_workspace.IsDirty)
        {
            return true;
        }

        var result = MessageBox.Show(
            "当前稿未保存。要先保存吗？",
            "STI 标签工位",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (result == MessageBoxResult.No)
        {
            return true;
        }

        await SaveAsync();
        return !_workspace.IsDirty;
    }

    private bool PrepareOutput(string action, bool skipSamplePrompt = false)
    {
        PersistDesigner();
        if (!ConfirmHealth(action))
        {
            return false;
        }

        if (!skipSamplePrompt && SampleRow is null)
        {
            var go = MessageBox.Show("没有示例数据，仍要继续吗？", "STI 标签工位", MessageBoxButton.YesNo);
            if (go != MessageBoxResult.Yes)
            {
                return false;
            }
        }

        return true;
    }

    private bool ConfirmHealth(string action)
    {
        var check = LabelValidator.Validate(
            _workspace.Document,
            Fields.Select(f => f.ToItem()).ToList(),
            SampleRow);
        if (check.Passed)
        {
            return true;
        }

        return MessageBox.Show(
            check.Summary + $"\n\n仍要{action}吗？",
            "STI 标签工位",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    partial void OnSelectedPrinterChanged(string? value)
    {
        StatusPrinter = string.IsNullOrWhiteSpace(value) ? "未选择打印机" : value;
        _workspace.Document.Page.PrinterName = value;
        OnPropertyChanged(nameof(CanPrint));
        _ = _store.SetAsync("LastPrinter", value);
    }

    partial void OnSelectedPresetChanged(PagePreset value)
    {
        if (_syncingPage || value is null)
        {
            return;
        }

        PageWidthMm = value.WidthMm;
        PageHeightMm = value.HeightMm;
    }
}

public sealed class VersionEntry
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}
