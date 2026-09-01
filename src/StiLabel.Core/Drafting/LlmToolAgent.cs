using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using StiLabel.Core.Catalog;
using StiLabel.Core.Labeling;
using StiLabel.Core.Llm;
using StiLabel.Core.Services;

namespace StiLabel.Core.Drafting;

public sealed class LlmToolAgent : IWorkbenchAgent
{
    private const string StableInstructions =
        "你是 STI 标签工位助手。只通过工具改模板，禁止输出 .mrt 或 XML。语气短，说明做了什么。"
        + "字段字典只对当前模板有效，打开模板会换成该模板里的字段。口述出现新字段名时先 ensure_field，再 add_field/build_draft。不要编造用户没说过的字段。批量变体、ERP 取数本期不做。"
        + "口述出整张：build_draft。用户说了 QR/二维码 才 qr=true；说了条码 才 barcode=true；给了图片路径才填 imagePath。"
        + "物料/常规用 layout=material 或 table（左名称右值+右QR）；出货用 shipping；货架/仓位用 shelf；只要上下排列用 stack。"
        + "加 logo/图片用 add_image（本机路径或 http）。改已有图用 set_image。组件可用「图片」。"
        + "改已有组件前先 list_components 或 inspect_component，用 id 指定，不要猜。"
        + "微调用 add_field / add_component / remove_component / move_component / set_bounds / set_font / set_text_align / set_vert_align / set_rotation / set_color / set_fill / set_line / set_border / set_barcode_options / bind_field / unbind / set_literal / set_expression / set_enabled_when / set_variable / list_variables / copy_style / same_size / fit_page / set_margin / set_barcode / set_z / lock_component / set_visible / apply_printer / align / distribute / duplicate / swap。"
        + "组合文字用 set_expression，例如 {LabelData.Qty}+\" PCS\"。按条件显隐用 set_enabled_when，例如 LabelData.Qty>0。报表变量用 set_variable。"
        + "圆或椭圆用 add_component(type: ellipse)。三角用 triangle，圆角框用 rounded，勾选框用 checkbox。改字段显示名用 rename_field，改类型用 set_field_type。"
        + "条码制式先 list_barcodes。GS1 用 GS1128/SSCC18/GS1DataMatrix/GS1QR，内容写 (01)…(17)…(10)…。"
        + "文字装不下用 set_text_fit（长中文品名用 shrink）。横竖用 set_orientation，不要靠对调宽高。"
        + "打样或保存前先 validate_label，有问题先报给用户。示例值用 set_sample / get_sample。本轮改坏了用 revert_changes。"
        + "打开模板用 import_mrt 或 open_recent，回历史版本用 list_versions / open_version。规格用 list_presets / apply_preset。保存用 save_template，打样用 print_preview。导出用 export_pdf / export_image。新建空白页用 new_blank。勾选用 select_fields。"
        + "不要为了改位置、字号或一句标题而重建整张。标题用 set_literal(component: 标题)。"
        + "用户可能发标签或样张照片。先看图；要放到画布用 add_image 并给本机路径。";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly IModelOptionsStore _options;
    private readonly LabelTools _tools;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AIAgent? _runtime;
    private AgentSession? _session;
    private string? _fingerprint;

    public LlmToolAgent(IModelOptionsStore options, IDraftBuilder drafts, IFieldCatalog catalog)
    {
        _options = options;
        _tools = new LabelTools(drafts, catalog);
    }

    public void ResetConversation()
    {
        _runtime = null;
        _session = null;
        _fingerprint = null;
    }

    public async Task<AgentReply> HandleAsync(
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
        CancellationToken cancellationToken = default)
    {
        var options = await _options.LoadAsync(cancellationToken);
        var limit = CompactModes.ClampContextTokens(options.ContextTokens);
        if (!options.IsReady)
        {
            return new AgentReply
            {
                Text = "模型未启用或未配置。请到「模型设置」打开开关并填写地址、模型名。",
                ContextLimit = limit
            };
        }

        await _gate.WaitAsync(cancellationToken);
        var streamed = "";
        int? used = null;
        IReadOnlyList<string> tools = [];
        try
        {
            _tools.Bind(current, fields, preset, printers, sample, presets, recentFiles, versions);
            var (agent, session) = await EnsureRuntimeAsync(options, cancellationToken);
            var message = await BuildUserMessageAsync(userText, imagePaths, cancellationToken);
            try
            {
                (streamed, used, tools) = await StreamTextAsync(agent, message, session, progress, options: null, cancellationToken);
            }
            catch (Exception ex) when (LooksLikeNoTools(ex))
            {
                (streamed, used, tools) = await StreamTextAsync(
                    agent,
                    message,
                    session,
                    progress,
                    new ChatClientAgentRunOptions(new ChatOptions { Tools = [] }),
                    cancellationToken);
            }

            return WithUsage(_tools.ToReply(streamed), used, limit, tools, message);
        }
        catch (OperationCanceledException)
        {
            if (_runtime is not null)
            {
                _session = await _runtime.CreateSessionAsync(CancellationToken.None);
            }

            var stopped = _tools.ToReplyStopped();
            if (!string.IsNullOrWhiteSpace(streamed) && stopped.Document is null)
            {
                stopped = new AgentReply { Text = streamed.Trim() + "\n已停止。" };
            }

            return WithUsage(stopped, used, limit, tools);
        }
        catch (Exception ex)
        {
            return WithUsage(new AgentReply { Text = "消息发送失败：" + ex.Message, Failed = true }, used, limit, tools);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(AIAgent Agent, AgentSession Session)> EnsureRuntimeAsync(
        ModelOptions options,
        CancellationToken cancellationToken)
    {
            var fingerprint = $"{options.Protocol}\n{options.Endpoint}\n{options.Model}\n{options.ApiKey}\ntools-v13"
            + $"\n{options.ContextTokens}\n{options.CompactMode}\n{options.CompactTurns}";
        if (_runtime is null || _session is null || _fingerprint != fingerprint)
        {
            _runtime = AgentFactory.Create(options, StableInstructions, _tools.AsAiTools());
            _session = await _runtime.CreateSessionAsync(cancellationToken);
            _fingerprint = fingerprint;
        }

        return (_runtime, _session);
    }

    private async Task<ChatMessage> BuildUserMessageAsync(
        string userText,
        IReadOnlyList<string>? imagePaths,
        CancellationToken cancellationToken)
    {
        var text = _tools.CanvasNote + "\n\n"
                   + (string.IsNullOrWhiteSpace(userText) ? "请看这张图。" : userText);
        var paths = (imagePaths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Select(p => p.Trim().Trim('"'))
            .ToList();
        if (paths.Count > 0)
        {
            text += "\n【附图本机路径】" + string.Join("；", paths) + "。放到画布用 add_image。";
        }

        var contents = new List<AIContent> { new TextContent(text) };
        foreach (var path in paths)
        {
            var mime = MediaType(path);
            if (mime is null)
            {
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            contents.Add(new DataContent(bytes, mime));
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    private static string? MediaType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => null
        };

    private static async Task<(string Text, int? Used, IReadOnlyList<string> Tools)> StreamTextAsync(
        AIAgent agent,
        ChatMessage message,
        AgentSession session,
        IProgress<AgentProgress>? progress,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        int? used = null;
        var tools = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lastReport = 0L;
        await foreach (var update in agent.RunStreamingAsync(message, session, options, cancellationToken).ConfigureAwait(false))
        {
            var tokens = ReadInputTokens(update);
            if (tokens is int n)
            {
                used = used is int prev ? Math.Max(prev, n) : n;
            }

            var toolsChanged = false;
            foreach (var content in update.Contents)
            {
                if (content is not FunctionCallContent call || string.IsNullOrWhiteSpace(call.Name))
                {
                    continue;
                }

                var label = FormatTool(call);
                if (seen.Add(label))
                {
                    tools.Add(label);
                    toolsChanged = true;
                }
            }

            var hasText = !string.IsNullOrEmpty(update.Text);
            if (hasText)
            {
                text.Append(update.Text);
            }

            if (!hasText && !toolsChanged)
            {
                continue;
            }

            var now = Environment.TickCount64;
            if (toolsChanged || now - lastReport >= 50)
            {
                lastReport = now;
                progress?.Report(new AgentProgress { Text = text.ToString(), Tools = tools.ToArray() });
            }
        }

        return (text.ToString(), used, tools);
    }

    private static string FormatTool(FunctionCallContent call)
    {
        if (call.Arguments is not { Count: > 0 })
        {
            return call.Name;
        }

        var parts = new List<string>();
        foreach (var (key, value) in call.Arguments)
        {
            if (value is null)
            {
                continue;
            }

            var text = value.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (text.Length > 24)
            {
                text = text[..24] + "…";
            }

            parts.Add(key + "=" + text);
            if (parts.Count == 3)
            {
                break;
            }
        }

        return parts.Count == 0 ? call.Name : call.Name + "(" + string.Join(", ", parts) + ")";
    }

    private static int? ReadInputTokens(AgentResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            if (content is UsageContent usage && usage.Details.InputTokenCount is > 0 and var tokens)
            {
                return (int)Math.Min(tokens, int.MaxValue);
            }
        }

        return null;
    }

    private static AgentReply WithUsage(
        AgentReply reply,
        int? used,
        int limit,
        IReadOnlyList<string> tools,
        ChatMessage? message = null)
    {
        reply.ContextLimit = limit;
        reply.ContextUsed = used ?? EstimateTokens(message);
        reply.Tools = tools;
        return reply;
    }

    private static int? EstimateTokens(ChatMessage? message)
    {
        if (message is null)
        {
            return null;
        }

        // 官网 Compaction 无分词器时按 byteCount / 4 估。
        var bytes = Encoding.UTF8.GetByteCount(StableInstructions);
        foreach (var content in message.Contents)
        {
            bytes += content switch
            {
                TextContent text => Encoding.UTF8.GetByteCount(text.Text ?? ""),
                DataContent data => data.Data.Length,
                _ => 0
            };
        }

        return Math.Max(1, bytes / 4);
    }

    private static bool LooksLikeNoTools(Exception ex)
    {
        var text = ex.Message;
        return text.Contains("tool", StringComparison.OrdinalIgnoreCase)
               || text.Contains("function", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LabelTools
    {
        private readonly IDraftBuilder _drafts;
        private readonly IFieldCatalog _catalog;
        private LabelDocument _original = new();
        private IReadOnlyList<FieldItem> _fields = [];
        private IReadOnlyList<string> _printers = [];
        private IReadOnlyList<PagePreset> _presets = [];
        private IReadOnlyList<string> _recents = [];
        private IReadOnlyList<string> _versions = [];
        private PagePreset? _preset;
        private SampleRow? _sample;
        private bool _sampleDirty;
        private LabelDocument _working = new();
        private bool _applied;
        private bool _confirm;
        private string? _openPath;
        private string? _saveMode;
        private bool _print;
        private bool _newBlank;
        private string? _exportMode;
        private string? _exportPath;
        private IReadOnlyList<string>? _selectedKeys;

        public LabelTools(IDraftBuilder drafts, IFieldCatalog catalog)
        {
            _drafts = drafts;
            _catalog = catalog;
        }

        public void Bind(
            LabelDocument current,
            IReadOnlyList<FieldItem> fields,
            PagePreset? preset,
            IReadOnlyList<string>? printers,
            SampleRow? sample,
            IReadOnlyList<PagePreset>? presets,
            IReadOnlyList<string>? recentFiles,
            IReadOnlyList<string>? versions)
        {
            _original = current;
            _fields = fields;
            _preset = preset;
            _printers = printers ?? [];
            _presets = presets ?? [];
            _recents = recentFiles ?? [];
            _versions = versions ?? [];
            _sample = sample is null
                ? new SampleRow()
                : new SampleRow { Values = new Dictionary<string, string>(sample.Values, StringComparer.OrdinalIgnoreCase) };
            _sampleDirty = false;
            _working = current.Clone();
            _applied = false;
            _confirm = false;
            _openPath = null;
            _saveMode = null;
            _print = false;
            _newBlank = false;
            _exportMode = null;
            _exportPath = null;
            _selectedKeys = null;
        }

        public string CanvasNote
        {
            get
            {
                var dict = string.Join("、", _fields.Select(f => $"{f.DisplayName}({f.Key})"));
                var binds = string.Join("、", _working.Components
                    .Where(c => c.Bind.FieldKey is not null)
                    .Select(c => c.Bind.FieldKey)
                    .Distinct());
                return
                    $"【当前画布】{_working.Page.WidthMm:0}×{_working.Page.HeightMm:0} mm，组件 {_working.Components.Count}。"
                    + $"已绑定：{(string.IsNullOrWhiteSpace(binds) ? "无" : binds)}。"
                    + $"字典：{dict}。"
                    + $"组件：{ComponentSummary(_working)}。"
                    + $"默认规格：{_preset?.WidthMm:0}×{_preset?.HeightMm:0} mm。"
                    + $"边距 {_working.Page.MarginMm:0.#} mm，{(DraftBuilder.NormalizeOrientation(_working.Page.Orientation) == "Landscape" ? "横向" : "竖向")}。"
                    + $"打印机：{(_working.Page.PrinterName ?? "未选")}。";
            }
        }

        public IList<AITool> AsAiTools() =>
        [
            AIFunctionFactory.Create(ListFields),
            AIFunctionFactory.Create(EnsureField),
            AIFunctionFactory.Create(RenameField),
            AIFunctionFactory.Create(SetFieldType),
            AIFunctionFactory.Create(RemoveField),
            AIFunctionFactory.Create(ListComponents),
            AIFunctionFactory.Create(InspectComponent),
            AIFunctionFactory.Create(AnalyzeTemplate),
            AIFunctionFactory.Create(ValidateLabel),
            AIFunctionFactory.Create(BuildDraft),
            AIFunctionFactory.Create(AddField),
            AIFunctionFactory.Create(AddComponent),
            AIFunctionFactory.Create(AddImage),
            AIFunctionFactory.Create(SetImage),
            AIFunctionFactory.Create(RemoveComponent),
            AIFunctionFactory.Create(MoveComponent),
            AIFunctionFactory.Create(SetBounds),
            AIFunctionFactory.Create(SetFont),
            AIFunctionFactory.Create(SetTextAlign),
            AIFunctionFactory.Create(SetVertAlign),
            AIFunctionFactory.Create(SetTextFit),
            AIFunctionFactory.Create(SetRotation),
            AIFunctionFactory.Create(SetColor),
            AIFunctionFactory.Create(SetFill),
            AIFunctionFactory.Create(SetLine),
            AIFunctionFactory.Create(SetBorder),
            AIFunctionFactory.Create(SetBarcodeOptions),
            AIFunctionFactory.Create(LockComponent),
            AIFunctionFactory.Create(SetVisible),
            AIFunctionFactory.Create(SetExpression),
            AIFunctionFactory.Create(SetEnabledWhen),
            AIFunctionFactory.Create(ListVariables),
            AIFunctionFactory.Create(SetVariable),
            AIFunctionFactory.Create(RemoveVariable),
            AIFunctionFactory.Create(SetSample),
            AIFunctionFactory.Create(GetSample),
            AIFunctionFactory.Create(BindField),
            AIFunctionFactory.Create(Unbind),
            AIFunctionFactory.Create(CopyStyle),
            AIFunctionFactory.Create(SameSize),
            AIFunctionFactory.Create(FitPage),
            AIFunctionFactory.Create(SetLiteral),
            AIFunctionFactory.Create(ListPresets),
            AIFunctionFactory.Create(ApplyPreset),
            AIFunctionFactory.Create(ListRecent),
            AIFunctionFactory.Create(OpenRecent),
            AIFunctionFactory.Create(ListVersions),
            AIFunctionFactory.Create(OpenVersion),
            AIFunctionFactory.Create(SaveTemplate),
            AIFunctionFactory.Create(PrintPreview),
            AIFunctionFactory.Create(ExportPdf),
            AIFunctionFactory.Create(ExportImage),
            AIFunctionFactory.Create(NewBlank),
            AIFunctionFactory.Create(SelectFields),
            AIFunctionFactory.Create(SetPage),
            AIFunctionFactory.Create(SetOrientation),
            AIFunctionFactory.Create(SetMargin),
            AIFunctionFactory.Create(ApplyPrinter),
            AIFunctionFactory.Create(SetBarcode),
            AIFunctionFactory.Create(ListBarcodes),
            AIFunctionFactory.Create(SetZ),
            AIFunctionFactory.Create(Align),
            AIFunctionFactory.Create(Distribute),
            AIFunctionFactory.Create(Duplicate),
            AIFunctionFactory.Create(Swap),
            AIFunctionFactory.Create(ListPrinters),
            AIFunctionFactory.Create(ImportMrt),
            AIFunctionFactory.Create(FindFieldBinding),
            AIFunctionFactory.Create(RevertChanges),
            AIFunctionFactory.Create(ClearCanvas)
        ];

        [Description("列出当前字段字典。没有的业务字段用 ensure_field 加入，不要编造用户没说过的字段。")]
        public string ListFields() =>
            _fields.Count == 0
                ? "字典是空的。口述新字段时先 ensure_field。"
                : "可用字段：" + string.Join("、", _fields.Select(f => $"{f.DisplayName}({f.Key}/{f.DataType})"));

        [Description("把口述里的业务字段写入本机字典。字典没有时必须先调用，再 add_field 或 build_draft。不要拒绝新的业务字段名。")]
        public async Task<string> EnsureField(
            [Description("中文名，例如 供应商")] string name,
            [Description("可选英文 key")] string? key = null,
            [Description("text、number 或 date")] string dataType = "text")
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "字段名不能为空。";
            }

            var existing = FindField(name, _fields) ?? FindField(key, _fields);
            if (existing is not null)
            {
                existing.Selected = true;
                return $"字典已有「{existing.DisplayName}」({existing.Key})。";
            }

            var item = await _catalog.UpsertAsync(name.Trim(), key, dataType);
            item.Selected = true;
            _fields = _fields.Concat([item]).ToList();
            return $"已加入字典「{item.DisplayName}」({item.Key})。";
        }

        [Description("从本机字典删除字段。不删画布上已有组件。")]
        public async Task<string> RemoveField([Description("字段中文名或 key")] string name)
        {
            var field = FindField(name, _fields);
            if (field is null)
            {
                return UnknownField(name, _fields);
            }

            if (field.Id <= 0)
            {
                return $"「{field.DisplayName}」没有字典编号，无法删除。";
            }

            await _catalog.DeleteAsync(field.Id);
            _fields = _fields.Where(f => f.Id != field.Id).ToList();
            return $"已从字典删除「{field.DisplayName}」。画布上若已绑定，用 remove_component 再删。";
        }

        [Description("改字典字段的显示名，不改 key，画布绑定保持不变。")]
        public async Task<string> RenameField(
            [Description("现有字段中文名或 key")] string name,
            [Description("新的显示名")] string newName)
        {
            var field = FindField(name, _fields);
            if (field is null)
            {
                return UnknownField(name, _fields);
            }

            if (string.IsNullOrWhiteSpace(newName))
            {
                return "新名称不能为空。";
            }

            var item = await _catalog.UpsertAsync(newName.Trim(), field.Key, field.DataType);
            item.Selected = field.Selected;
            _fields = _fields.Select(f => f.Id == field.Id || f.Key == field.Key ? item : f).ToList();
            return $"将把「{field.DisplayName}」显示名改为「{item.DisplayName}」，key 仍是 {item.Key}。";
        }

        [Description("改字典字段的数据类型：text、number 或 date。不改 key 和画布绑定。")]
        public async Task<string> SetFieldType(
            [Description("字段中文名或 key")] string name,
            [Description("text、number 或 date")] string dataType = "text")
        {
            var field = FindField(name, _fields);
            if (field is null)
            {
                return UnknownField(name, _fields);
            }

            var type = NormalizeDataType(dataType);
            if (type is null)
            {
                return "类型用 text / number / date。";
            }

            var item = await _catalog.UpsertAsync(field.DisplayName, field.Key, type);
            item.Selected = field.Selected;
            _fields = _fields.Select(f => f.Id == field.Id || f.Key == field.Key ? item : f).ToList();
            return $"将把「{item.DisplayName}」类型改为 {item.DataType}。";
        }

        [Description("列出当前画布每个组件的 id、类型、绑定和坐标。改组件前先调用，用 id 指定。不改画布。")]
        public string ListComponents()
        {
            if (_working.Components.Count == 0)
            {
                return "画布是空的。";
            }

            return "当前组件（改时用 id）：\n"
                   + string.Join("\n", _working.Components.Select((c, i) => $"{i + 1}. {Describe(c)}"));
        }

        [Description("查看一个组件的完整样式：位置、绑定、字体、颜色、条码制式、显隐。不改画布。")]
        public string InspectComponent([Description("字段名、标题、条码、图片、圆或 id")] string component)
        {
            var item = DraftBuilder.FindComponent(_working, ResolveTarget(component));
            return item is null
                ? "找不到该组件。当前：" + ComponentSummary(_working)
                : Inspect(item);
        }

        [Description("只读分析当前标签：页尺寸、组件坐标、绑定和风险。不改画布。")]
        public string AnalyzeTemplate() => Analyze(_working, _fields);

        [Description("打样或保存前体检：条码内容是否符合制式、有没有空绑定、图片文件在不在、有没有超页、贴边或重叠。不改画布。")]
        public string ValidateLabel() => LabelValidator.Validate(_working, _fields, _sample).Summary;

        [Description("按口述生成整张可编辑草稿。字段须已在字典中；新字段先 ensure_field。未指定尺寸则用当前页。")]
        public string BuildDraft(
            [Description("字典字段 key 或中文名，例如 MaterialCode、品名")] string[]? fieldKeys = null,
            [Description("页宽毫米")] double? widthMm = null,
            [Description("页高毫米")] double? heightMm = null,
            [Description("标题文字；空字符串表示不要标题")] string? title = "物料标签",
            [Description("是否放一维条码")] bool barcode = false,
            [Description("是否放二维码")] bool qr = false,
            [Description("material/table=名称+值+右QR；shipping=出货；shelf=货架；split=左文右QR；stack=上下")] string layout = "material",
            [Description("本机图片路径或 http，放右上角 logo")] string? imagePath = null)
        {
            var selected = ResolveFields(fieldKeys, _fields);
            if (selected.Count == 0)
            {
                return "没有可用字段。" + ListFields();
            }

            if (!string.IsNullOrWhiteSpace(imagePath) && ImageSourceError(imagePath) is { } draftImageError)
            {
                return draftImageError;
            }

            var page = ResolvePage(widthMm, heightMm, _preset, _working);
            var doc = _drafts.Build(page, selected, _working.Page.PrinterName, new DraftOptions
            {
                Title = string.IsNullOrWhiteSpace(title) ? null : title,
                Barcode = barcode,
                Qr = qr,
                Layout = string.IsNullOrWhiteSpace(layout) ? "split" : layout,
                ImagePath = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath.Trim()
            });
            Apply(doc, _original.Components.Count > 0);
            var extras = (qr ? " +QR" : "") + (barcode ? " +条码" : "") + (string.IsNullOrWhiteSpace(imagePath) ? "" : " +图");
            return $"将按 {page.WidthMm:0}×{page.HeightMm:0} mm 生成草稿，含 {string.Join("、", selected.Select(f => f.DisplayName))}{extras}。";
        }

        [Description("在当前稿上增加一个字典字段文本，不重建整张。字典没有时先 ensure_field。")]
        public string AddField([Description("字典字段 key 或显示名")] string fieldKey)
        {
            var field = FindField(fieldKey, _fields);
            if (field is null)
            {
                return UnknownField(fieldKey, _fields);
            }

            Apply(_drafts.AddField(_working, field), false);
            return $"将增加「{field.DisplayName}」并绑定 {field.Key}。";
        }

        [Description("增加一个组件：text/barcode/qr/image/line/rect/ellipse/triangle/rounded/checkbox。图片路径放 literal。字段必须来自字典。")]
        public string AddComponent(
            [Description("text、barcode、qr、image、line、rect、ellipse、triangle、rounded、checkbox")] string type,
            [Description("绑定的字典字段，可空")] string? fieldKey = null,
            [Description("写死文字或图片路径，可空")] string? literal = null,
            [Description("X 毫米")] double? xMm = null,
            [Description("Y 毫米")] double? yMm = null,
            [Description("宽毫米")] double? wMm = null,
            [Description("高毫米")] double? hMm = null)
        {
            string? key = null;
            if (!string.IsNullOrWhiteSpace(fieldKey))
            {
                var field = FindField(fieldKey, _fields);
                if (field is null)
                {
                    return UnknownField(fieldKey, _fields);
                }

                key = field.Key;
            }

            if (IsImageType(type) && string.IsNullOrWhiteSpace(key) && ImageSourceError(literal) is { } addError)
            {
                return addError;
            }

            var doc = _drafts.AddComponent(_working, type, key, literal, xMm, yMm, wMm, hMm);
            Apply(doc, false);
            return $"将增加 {type}" + (key is null ? "" : " 绑定 " + key)
                   + (IsImageType(type) && !string.IsNullOrWhiteSpace(literal) ? "：" + literal : "") + "。";
        }

        [Description("在画布上加一张图。优先给本机路径；也可绑字典字段（字段值是路径）。没有路径时先问用户。")]
        public string AddImage(
            [Description("本机图片路径或 http URL")] string? path = null,
            [Description("绑定的字典字段，可空")] string? fieldKey = null,
            [Description("X 毫米")] double? xMm = null,
            [Description("Y 毫米")] double? yMm = null,
            [Description("宽毫米")] double? wMm = 16,
            [Description("高毫米")] double? hMm = 16)
        {
            string? key = null;
            if (!string.IsNullOrWhiteSpace(fieldKey))
            {
                var field = FindField(fieldKey, _fields);
                if (field is null)
                {
                    return UnknownField(fieldKey, _fields);
                }

                key = field.Key;
            }
            else if (string.IsNullOrWhiteSpace(path))
            {
                return "请给出本机图片路径，或右边点「插入图片」。";
            }
            else if (ImageSourceError(path) is { } imageError)
            {
                return imageError;
            }

            var doc = _drafts.AddComponent(_working, "image", key, path, xMm, yMm, wMm, hMm);
            Apply(doc, false);
            return key is null ? $"将插入图片 {path}。" : $"将插入图片并绑定 {key}。";
        }

        [Description("改已有图片的文件路径，或改绑到字段。component 可用「图片」。")]
        public string SetImage(
            [Description("图片、logo 或 id")] string component = "图片",
            [Description("本机路径或 http")] string? path = null,
            [Description("绑定的字典字段，可空")] string? fieldKey = null)
        {
            var target = ResolveTarget(component);
            if (!string.IsNullOrWhiteSpace(fieldKey))
            {
                var field = FindField(fieldKey, _fields);
                if (field is null)
                {
                    return UnknownField(fieldKey, _fields);
                }

                var bound = _drafts.BindField(_working, target, field.Key);
                return ApplyFound(bound, $"将把图片绑定到「{field.DisplayName}」。");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return "请给出图片路径。";
            }

            if (ImageSourceError(path) is { } setError)
            {
                return setError;
            }

            var found = DraftBuilder.FindComponent(_working, target);
            if (found is null)
            {
                return AddImage(path);
            }

            var doc = _drafts.SetLiteral(_working, target, path.Trim());
            if (found.Type != LabelComponentType.Image)
            {
                var item = DraftBuilder.FindComponent(doc, target);
                if (item is not null)
                {
                    item.Type = LabelComponentType.Image;
                }
            }

            Apply(doc, false);
            return $"将把图片改为 {path}。";
        }

        [Description("删除一个组件。component 用字段名、标题、条码、二维码或 id。")]
        public string RemoveComponent([Description("要删除的组件")] string component)
        {
            var doc = _drafts.Remove(_working, ResolveTarget(component));
            return ApplyFound(doc, "将删除该组件。");
        }

        [Description("移动已有组件。relative 为 true 时 xMm/yMm 是偏移。")]
        public string MoveComponent(
            [Description("字段名、标题、条码、二维码或 id")] string component,
            [Description("X 毫米；相对移动时为正数向右")] double xMm,
            [Description("Y 毫米；相对移动时为正数向下")] double yMm,
            [Description("true 表示按当前位置加减")] bool relative = false)
        {
            var doc = _drafts.Move(_working, ResolveTarget(component), xMm, yMm, relative);
            var item = DraftBuilder.FindComponent(doc, ResolveTarget(component));
            return ApplyFound(doc, $"将把「{Describe(item)}」移到 {item?.X:0.#},{item?.Y:0.#} mm。");
        }

        [Description("改组件位置或宽高（毫米）。只填要改的项。")]
        public string SetBounds(
            [Description("字段名、标题、条码、二维码或 id")] string component,
            [Description("X 毫米")] double? xMm = null,
            [Description("Y 毫米")] double? yMm = null,
            [Description("宽毫米")] double? wMm = null,
            [Description("高毫米")] double? hMm = null,
            [Description("true 时 x/y 为偏移")] bool relative = false)
        {
            var doc = _drafts.SetBounds(_working, ResolveTarget(component), xMm, yMm, wMm, hMm, relative);
            return ApplyFound(doc, "将调整该组件尺寸或位置。");
        }

        [Description("改文字：字号、加粗、字体名、斜体、下划线。")]
        public string SetFont(
            [Description("字段名、标题或 id")] string component,
            [Description("字号磅")] double? sizePt = null,
            [Description("是否加粗")] bool? bold = null,
            [Description("字体名，如 微软雅黑、黑体、宋体、Arial")] string? fontName = null,
            [Description("是否斜体")] bool? italic = null,
            [Description("是否下划线")] bool? underline = null)
        {
            var doc = _drafts.SetFont(_working, ResolveTarget(component), sizePt, bold, fontName, italic, underline);
            return ApplyFound(doc, "将调整文字样式。");
        }

        [Description("改文字水平对齐：left / center / right。")]
        public string SetTextAlign(
            [Description("字段名、标题或 id")] string component,
            [Description("left、center、right")] string align = "left")
        {
            if (DraftBuilder.NormalizeAlign(align) is null)
            {
                return "对齐用 left / center / right。";
            }

            var doc = _drafts.SetTextAlign(_working, ResolveTarget(component), align);
            return ApplyFound(doc, $"将把文字改为 {DraftBuilder.NormalizeAlign(align)} 对齐。");
        }

        [Description("改文字垂直对齐：top / middle / bottom。")]
        public string SetVertAlign(
            [Description("字段名、标题或 id")] string component,
            [Description("top、middle、bottom")] string align = "top")
        {
            if (DraftBuilder.NormalizeVertAlign(align) is null)
            {
                return "垂直对齐用 top / middle / bottom。";
            }

            var doc = _drafts.SetVertAlign(_working, ResolveTarget(component), align);
            return ApplyFound(doc, $"将把垂直对齐改为 {DraftBuilder.NormalizeVertAlign(align)}。");
        }

        [Description("文字装不下时怎么办：wrap 换行、shrink 自动缩小字号、clip 裁切不换行。长中文品名常用 shrink。")]
        public string SetTextFit(
            [Description("字段名、标题或 id")] string component,
            [Description("wrap、shrink 或 clip")] string mode = "shrink")
        {
            if (DraftBuilder.NormalizeTextFit(mode) is null)
            {
                return "用 wrap（换行）、shrink（缩字）或 clip（裁切）。";
            }

            var doc = _drafts.SetTextFit(_working, ResolveTarget(component), mode);
            return ApplyFound(doc, $"将把文字溢出处理改为 {DraftBuilder.NormalizeTextFit(mode)}。");
        }

        [Description("改线宽（毫米）和颜色。component 可用「线」。")]
        public string SetLine(
            [Description("线或 id")] string component = "线",
            [Description("线宽毫米")] double? widthMm = null,
            [Description("颜色，如 黑/红 或 #RRGGBB")] string? color = null)
        {
            var doc = _drafts.SetLine(_working, ResolveTarget(component), widthMm, color);
            return ApplyFound(doc, "将调整线条。");
        }

        [Description("给文字或框加/去边框，可改线宽和颜色。")]
        public string SetBorder(
            [Description("字段名、框或 id")] string component,
            [Description("是否显示边框")] bool? enabled = true,
            [Description("线宽毫米")] double? widthMm = null,
            [Description("边框颜色")] string? color = null)
        {
            var doc = _drafts.SetBorder(_working, ResolveTarget(component), enabled, widthMm, color);
            return ApplyFound(doc, enabled == false ? "将去掉边框。" : "将调整边框。");
        }

        [Description("旋转组件，角度 0/90/180/270。竖排条码用 90。")]
        public string SetRotation(
            [Description("字段名、条码、图片或 id")] string component,
            [Description("角度")] double degrees = 90)
        {
            var doc = _drafts.SetRotation(_working, ResolveTarget(component), degrees);
            return ApplyFound(doc, $"将旋转到 {DraftBuilder.NormalizeRotation(degrees):0}°。");
        }

        [Description("改文字颜色。可用 黑/红/蓝/绿/灰/白 或 #RRGGBB。")]
        public string SetColor(
            [Description("字段名、标题或 id")] string component,
            [Description("颜色")] string color = "黑")
        {
            if (DraftBuilder.NormalizeColor(color) is null)
            {
                return "颜色用 黑/红/蓝/绿/灰/白 或 #RRGGBB。";
            }

            var doc = _drafts.SetColor(_working, ResolveTarget(component), color);
            return ApplyFound(doc, $"将把颜色改为 {DraftBuilder.NormalizeColor(color)}。");
        }

        [Description("改框、圆或文字底色。颜色用 黑/红/蓝/绿/灰/白 或 #RRGGBB；透明用 none。")]
        public string SetFill(
            [Description("框、圆、字段名或 id")] string component,
            [Description("填充色，或 none 去掉填充")] string color = "none")
        {
            var token = color.Trim();
            if (token.Length > 0 && token is not ("none" or "透明" or "无" or "clear")
                && DraftBuilder.NormalizeColor(token) is null)
            {
                return "填充色用 黑/红/蓝/绿/灰/白、#RRGGBB，或 none 表示透明。";
            }

            var doc = _drafts.SetFill(_working, ResolveTarget(component), color);
            return ApplyFound(doc, token is "none" or "透明" or "无" or "clear" || token.Length == 0
                ? "将去掉填充。"
                : $"将把填充改为 {DraftBuilder.NormalizeColor(token)}。");
        }

        [Description("改预览/打样用的示例值，不改模板绑定。")]
        public string SetSample(
            [Description("字段中文名或 key")] string field,
            [Description("示例值")] string value)
        {
            var item = FindField(field, _fields);
            var key = item?.Key ?? field.Trim();
            _sample ??= new SampleRow();
            _sample.Values[key] = value;
            if (item is not null && !string.Equals(item.DisplayName, key, StringComparison.OrdinalIgnoreCase))
            {
                _sample.Values[item.DisplayName] = value;
            }

            _sampleDirty = true;
            return $"预览「{item?.DisplayName ?? key}」将显示为 {value}。";
        }

        [Description("看当前预览/打样用的示例值。不改画布。")]
        public string GetSample()
        {
            var values = _sample?.Values;
            if (values is null || values.Count == 0)
            {
                return "还没有示例值。用 set_sample 填，打样才看得到真实内容。";
            }

            return "示例值：" + string.Join("、", values.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        [Description("把组件绑到字典字段。字典没有时先 ensure_field。")]
        public string BindField(
            [Description("要改绑定的组件")] string component,
            [Description("字典字段 key 或中文名")] string fieldKey)
        {
            var field = FindField(fieldKey, _fields);
            if (field is null)
            {
                return UnknownField(fieldKey, _fields);
            }

            var doc = _drafts.BindField(_working, ResolveTarget(component), field.Key);
            return ApplyFound(doc, $"将把该组件绑定到「{field.DisplayName}」。");
        }

        [Description("去掉组件的字段绑定，改成写死文字。")]
        public string Unbind(
            [Description("字段名、标题或 id")] string component,
            [Description("写死的文字，可空")] string? literal = null)
        {
            var doc = _drafts.Unbind(_working, ResolveTarget(component), literal);
            return ApplyFound(doc, "将取消该组件的字段绑定。");
        }

        [Description("把 from 的字号、字体、颜色、对齐抄到 to。")]
        public string CopyStyle(
            [Description("样式来源")] string from,
            [Description("要改的组件")] string[] to)
        {
            var names = ResolveTargets(to);
            var doc = _drafts.CopyStyle(_working, ResolveTarget(from), names);
            return ApplyFound(doc, $"将把「{from}」的文字样式抄到 {string.Join("、", names)}。");
        }

        [Description("让多个组件和第一个一样宽高。")]
        public string SameSize([Description("第一个是基准")] string[] targets)
        {
            var names = ResolveTargets(targets);
            if (names.Length < 2)
            {
                return "至少两个组件才能统一尺寸。";
            }

            var doc = _drafts.SameSize(_working, names);
            return ApplyFound(doc, $"将按「{names[0]}」统一尺寸。");
        }

        [Description("把超页或贴边的组件收进页边距内。")]
        public string FitPage()
        {
            Apply(_drafts.FitPage(_working), false);
            return "将把内容收进当前页边距内。";
        }

        [Description("列出页规格预设。")]
        public string ListPresets() =>
            _presets.Count == 0
                ? "没有预设。"
                : "页规格：" + string.Join("、", _presets.Select(p => $"{p.Name} {p.WidthMm:0}×{p.HeightMm:0}")) + "。用 apply_preset。";

        [Description("按预设名改页尺寸，例如 物料 70×40。已有内容时需要确认。")]
        public string ApplyPreset([Description("预设名或 70x40")] string name)
        {
            var preset = ResolvePreset(name);
            if (preset is null)
            {
                return "找不到该规格。" + ListPresets();
            }

            var doc = _drafts.SetPage(_working, preset.WidthMm, preset.HeightMm);
            Apply(doc, _original.Components.Count > 0);
            return $"将改用「{preset.Name}」{preset.WidthMm:0}×{preset.HeightMm:0} mm。";
        }

        [Description("列出最近打开的模板路径。")]
        public string ListRecent() =>
            _recents.Count == 0
                ? "还没有最近文件。"
                : "最近文件：" + string.Join("；", _recents) + "。用 open_recent 打开。";

        [Description("打开最近文件。可给完整路径或文件名。")]
        public string OpenRecent([Description("路径或文件名")] string path)
        {
            var file = ResolveRecent(path);
            if (file is null)
            {
                return "找不到该文件。" + ListRecent();
            }

            _openPath = file;
            return "将打开 " + file;
        }

        [Description("列出「另存一版」存下的历史版本，新的在前。不改画布。")]
        public string ListVersions() =>
            _versions.Count == 0
                ? "还没有历史版本。用 save_template(mode: version) 存一版。"
                : "历史版本（新→旧）：\n"
                  + string.Join("\n", _versions.Take(20).Select(v => System.IO.Path.GetFileName(v)))
                  + "\n用 open_version 回到某一版。";

        [Description("打开某个历史版本，回到那一版的内容。当前未保存的改动会丢，先提醒用户。")]
        public string OpenVersion([Description("版本文件名，可只给一段；空则取最新一版")] string? name = null)
        {
            if (_versions.Count == 0)
            {
                return "还没有历史版本。";
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                _openPath = _versions[0];
                return "将回到最新一版 " + System.IO.Path.GetFileName(_openPath);
            }

            var token = name.Trim().Trim('"');
            var hit = _versions.FirstOrDefault(v =>
                          v.Equals(token, StringComparison.OrdinalIgnoreCase)
                          || System.IO.Path.GetFileName(v).Equals(token, StringComparison.OrdinalIgnoreCase))
                      ?? _versions.FirstOrDefault(v =>
                          System.IO.Path.GetFileName(v).Contains(token, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                return "找不到该版本。" + ListVersions();
            }

            _openPath = hit;
            return "将回到版本 " + System.IO.Path.GetFileName(hit);
        }

        [Description("保存当前稿。mode=save 保存，version 另存一版。")]
        public string SaveTemplate([Description("save 或 version")] string mode = "save")
        {
            var token = mode.Trim().ToLowerInvariant();
            _saveMode = token is "version" or "一版" or "另存一版" ? "version" : "save";
            return _saveMode == "version" ? "将另存一版。" : "将保存当前稿。";
        }

        [Description("打开本机打样对话框。")]
        public string PrintPreview()
        {
            _print = true;
            return "将打开打样。";
        }

        [Description("把当前稿导出为 PDF。可给本机路径；不给则弹出另存。")]
        public string ExportPdf([Description("本机完整路径，可空")] string? path = null)
        {
            _exportMode = "pdf";
            _exportPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(_exportPath) ? "将导出 PDF，请选择保存位置。" : "将导出 PDF 到 " + _exportPath;
        }

        [Description("把当前稿导出为 PNG 预览图。可给本机路径；不给则弹出另存。")]
        public string ExportImage([Description("本机完整路径，可空")] string? path = null)
        {
            _exportMode = "image";
            _exportPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(_exportPath) ? "将导出图片，请选择保存位置。" : "将导出图片到 " + _exportPath;
        }

        [Description("新建空白标签页，丢掉当前画布。可指定预设名。已有内容时用户会确认。")]
        public string NewBlank([Description("预设名或 70x40，可空则用当前规格")] string? preset = null)
        {
            var page = ResolvePreset(preset)
                       ?? _preset
                       ?? new PagePreset { Name = "当前", WidthMm = _working.Page.WidthMm, HeightMm = _working.Page.HeightMm };
            _working = new LabelDocument
            {
                Page = new LabelPage
                {
                    WidthMm = page.WidthMm,
                    HeightMm = page.HeightMm,
                    PrinterName = _working.Page.PrinterName,
                    MarginMm = _working.Page.MarginMm
                }
            };
            _newBlank = true;
            return $"将新建空白页 {page.WidthMm:0}×{page.HeightMm:0} mm。";
        }

        [Description("勾选右边字段，供按所选生成草稿。只勾列出的字段。")]
        public string SelectFields([Description("字段中文名或 key")] string[] names)
        {
            var keys = new List<string>();
            foreach (var name in names)
            {
                var field = FindField(name, _fields);
                if (field is null)
                {
                    return UnknownField(name, _fields);
                }

                keys.Add(field.Key);
            }

            _selectedKeys = keys;
            return keys.Count == 0
                ? "请给出要勾选的字段。"
                : "将勾选：" + string.Join("、", names) + "。";
        }

        [Description("改写死文字，例如标题。")]
        public string SetLiteral(
            [Description("组件，标题可用「标题」")] string component,
            [Description("写死的文字")] string text)
        {
            var doc = _drafts.SetLiteral(_working, ResolveTarget(component), text);
            Apply(doc, false);
            return $"将把文字改为「{text}」。";
        }

        [Description("改页宽高（毫米）。已有内容时属于大改，需要用户确认。")]
        public string SetPage(
            [Description("页宽毫米")] double widthMm,
            [Description("页高毫米")] double heightMm)
        {
            var doc = _drafts.SetPage(_working, widthMm, heightMm);
            Apply(doc, _original.Components.Count > 0);
            return $"将把页面改为 {widthMm:0}×{heightMm:0} mm。";
        }

        [Description("改页面横竖：portrait 竖向、landscape 横向。会对调页宽高并把内容收回页内。")]
        public string SetOrientation([Description("portrait 或 landscape")] string orientation = "landscape")
        {
            var mapped = DraftBuilder.NormalizeOrientation(orientation);
            if (mapped is null)
            {
                return "用 portrait（竖向）或 landscape（横向）。";
            }

            var doc = _drafts.SetOrientation(_working, orientation);
            if (ReferenceEquals(doc, _working)
                || (Math.Abs(doc.Page.WidthMm - _working.Page.WidthMm) < 0.01
                    && string.Equals(doc.Page.Orientation, _working.Page.Orientation, StringComparison.OrdinalIgnoreCase)))
            {
                return $"已经是{(mapped == "Landscape" ? "横向" : "竖向")}了，没改。";
            }

            Apply(doc, _original.Components.Count > 0);
            return $"将改为{(mapped == "Landscape" ? "横向" : "竖向")}，页面 {doc.Page.WidthMm:0}×{doc.Page.HeightMm:0} mm。";
        }

        [Description("改内容边距（毫米），并把贴边组件往里收。")]
        public string SetMargin([Description("边距毫米")] double marginMm)
        {
            Apply(_drafts.SetMargin(_working, marginMm), false);
            return $"将把边距改为 {marginMm:0.#} mm。";
        }

        [Description("记下当前打印机，并可按它改边距。未给打印机名时用画布上已选的。")]
        public string ApplyPrinter(
            [Description("本机打印机名，可空")] string? printerName = null,
            [Description("边距毫米；空则只记打印机")] double? marginMm = null)
        {
            var name = string.IsNullOrWhiteSpace(printerName) ? _working.Page.PrinterName : printerName.Trim();
            if (string.IsNullOrWhiteSpace(name) && marginMm is null)
            {
                return "还没选打印机。请先在右边选一台，或告诉我打印机名。";
            }

            Apply(_drafts.SetPrinter(_working, name, marginMm), false);
            var extra = marginMm is >= 0 ? $"，边距 {marginMm:0.#} mm" : "";
            return string.IsNullOrWhiteSpace(name)
                ? $"将调整边距{extra}。"
                : $"将对照打印机「{name}」{extra}。可打印区域以本机队列为准，复杂缺口请在设计器里改。";
        }

        [Description("改条码或二维码制式。不确定时先 list_barcodes。GS1 内容用 (01)(17)(10) 括号。")]
        public string SetBarcode(
            [Description("条码、二维码或 id")] string component = "条码",
            [Description("制式名，如 GS1128、Aztec、Code39Ext、I2of5")] string symbology = "Code128")
        {
            if (DraftBuilder.NormalizeSymbology(symbology) is null)
            {
                return "不支持该制式。可用：" + DraftBuilder.SupportedSymbologies + "。";
            }

            var doc = _drafts.SetBarcode(_working, ResolveTarget(component), symbology);
            return ApplyFound(doc, $"将把条码制式改为 {DraftBuilder.NormalizeSymbology(symbology)}。");
        }

        [Description("列出本机 Stimulsoft 支持的全部条码制式。改制式前可先看。不改画布。")]
        public string ListBarcodes() => DraftBuilder.ListSymbologies();

        [Description("改条码是否显示下方数字。二维码一般保持不显示。")]
        public string SetBarcodeOptions(
            [Description("条码或 id")] string component = "条码",
            [Description("是否显示条码文字")] bool? showText = false)
        {
            var doc = _drafts.SetBarcodeOptions(_working, ResolveTarget(component), showText);
            return ApplyFound(doc, showText == true ? "将显示条码数字。" : "将隐藏条码数字。");
        }

        [Description("锁定或解锁组件，避免误拖。")]
        public string LockComponent(
            [Description("字段名、图片、条码或 id")] string component,
            [Description("true 锁定")] bool locked = true)
        {
            var doc = _drafts.SetLocked(_working, ResolveTarget(component), locked);
            return ApplyFound(doc, locked ? $"将锁定「{component}」。" : $"将解锁「{component}」。");
        }

        [Description("显示或隐藏组件，不删除。")]
        public string SetVisible(
            [Description("字段名、图片、条码、圆或 id")] string component,
            [Description("true 显示，false 隐藏")] bool visible = true)
        {
            var doc = _drafts.SetVisible(_working, ResolveTarget(component), visible);
            return ApplyFound(doc, visible ? $"将显示「{component}」。" : $"将隐藏「{component}」。");
        }

        [Description("给组件写 Stimulsoft 表达式，覆盖简单绑定。空则清除。例：{LabelData.Qty}+\" PCS\"。")]
        public string SetExpression(
            [Description("字段名、标题、条码或 id")] string component,
            [Description("Stimulsoft 表达式，空则清除")] string? expression = null)
        {
            var doc = _drafts.SetExpression(_working, ResolveTarget(component), expression);
            return ApplyFound(doc, string.IsNullOrWhiteSpace(expression)
                ? $"将清除「{component}」的表达式。"
                : $"将把「{component}」设为表达式 {expression.Trim()}。");
        }

        [Description("条件显隐：表达式为真时显示。空则清除。例：LabelData.Qty>0 或 ShowPrice==true。")]
        public string SetEnabledWhen(
            [Description("字段名、条码、图片或 id")] string component,
            [Description("返回布尔的 Stimulsoft 表达式，空则清除")] string? expression = null)
        {
            var doc = _drafts.SetEnabledWhen(_working, ResolveTarget(component), expression);
            return ApplyFound(doc, string.IsNullOrWhiteSpace(expression)
                ? $"将清除「{component}」的显示条件。"
                : $"将在 {expression.Trim()} 为真时显示「{component}」。");
        }

        [Description("列出报表变量。不改画布。")]
        public string ListVariables() =>
            _working.Variables.Count == 0
                ? "还没有报表变量。用 set_variable 添加，例如 ShowPrice=true。"
                : "报表变量：\n" + string.Join("\n", _working.Variables.Select(v =>
                    $"{v.Name}={v.Value} ({v.DataType})"));

        [Description("新增或改 Stimulsoft 报表变量，可在表达式和条件里引用。")]
        public string SetVariable(
            [Description("变量名，例如 ShowPrice")] string name,
            [Description("值")] string value,
            [Description("text、number、bool 或 date")] string dataType = "text")
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "变量名不能空。";
            }

            Apply(_drafts.SetVariable(_working, name.Trim(), value ?? "", dataType), false);
            return $"变量 {name.Trim()} = {value}。";
        }

        [Description("删除报表变量。不改组件。")]
        public string RemoveVariable([Description("变量名")] string name)
        {
            if (_working.Variables.All(v => !v.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return "没有这个变量。" + ListVariables();
            }

            Apply(_drafts.RemoveVariable(_working, name), false);
            return $"已删除变量 {name.Trim()}。";
        }

        [Description("改叠放：front 最上，back 最下，或给整数 z。")]
        public string SetZ(
            [Description("字段名、图片、标题或 id")] string component,
            [Description("front、back 或数字")] string layer = "front")
        {
            var doc = _drafts.SetZ(_working, ResolveTarget(component), layer);
            return ApplyFound(doc, $"将调整「{component}」的叠放。");
        }

        [Description("对齐组件。多个时以第一个为基准；只有一个时相对页面。edge: left/right/top/bottom/center-x/center-y。")]
        public string Align(
            [Description("字段名、标题、条码、图片或 id")] string[] targets,
            [Description("left、right、top、bottom、center-x、center-y")] string edge = "left")
        {
            if (DraftBuilder.NormalizeEdge(edge) is null)
            {
                return "对齐方向用 left / right / top / bottom / center-x / center-y。";
            }

            var names = ResolveTargets(targets);
            var doc = _drafts.Align(_working, names, edge);
            return ApplyFound(doc, $"将按 {DraftBuilder.NormalizeEdge(edge)} 对齐 {string.Join("、", names)}。");
        }

        [Description("均分间距。至少三个组件。axis: horizontal 或 vertical。")]
        public string Distribute(
            [Description("至少三个组件名")] string[] targets,
            [Description("horizontal 或 vertical")] string axis = "vertical")
        {
            var names = ResolveTargets(targets);
            if (names.Length < 3)
            {
                return "均分至少需要三个组件。";
            }

            var doc = _drafts.Distribute(_working, names, axis);
            return ApplyFound(doc, $"将{axis}均分 {string.Join("、", names)}。");
        }

        [Description("复制一个组件，默认向右下偏 2 毫米。")]
        public string Duplicate(
            [Description("字段名、标题、条码、图片或 id")] string component,
            [Description("X 偏移毫米")] double offsetXMm = 2,
            [Description("Y 偏移毫米")] double offsetYMm = 2)
        {
            var doc = _drafts.Duplicate(_working, ResolveTarget(component), offsetXMm, offsetYMm);
            return ApplyFound(doc, $"将复制「{component}」。");
        }

        [Description("对调两个组件的位置（坐标互换，尺寸不变）。")]
        public string Swap(
            [Description("第一个组件")] string a,
            [Description("第二个组件")] string b)
        {
            var doc = _drafts.Swap(_working, ResolveTarget(a), ResolveTarget(b));
            return ApplyFound(doc, $"将对调「{a}」和「{b}」的位置。");
        }

        [Description("列出本机打印机队列。")]
        public string ListPrinters() =>
            _printers.Count == 0
                ? "本机没有打印机队列。可在右边刷新。"
                : "本机打印机：" + string.Join("、", _printers)
                  + "。当前：" + (_working.Page.PrinterName ?? "未选") + "。用 apply_printer 选择。";

        [Description("打开本机已有模板（.mrt 或 .label.json）并抽取字段。不要重建画布。")]
        public string ImportMrt([Description("本机完整路径")] string path)
        {
            var file = path.Trim().Trim('"');
            if (!System.IO.File.Exists(file))
            {
                return "找不到文件：" + file;
            }

            if (!file.EndsWith(".mrt", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".label.json", StringComparison.OrdinalIgnoreCase))
            {
                return "请给 .mrt 或 .label.json 路径。";
            }

            _openPath = file;
            return "将打开模板并抽取字段。";
        }

        [Description("查找字段绑在画布哪个组件、什么坐标。不改画布。")]
        public string FindFieldBinding([Description("字段中文名或 key")] string name)
        {
            var field = FindField(name, _fields);
            var key = field?.Key ?? name;
            var hits = _working.Components
                .Where(c => c.Bind.Kind == BindKind.Field
                            && string.Equals(c.Bind.FieldKey, key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits.Count == 0)
            {
                return $"「{field?.DisplayName ?? name}」还没绑到画布上。";
            }

            return $"「{field?.DisplayName ?? name}」绑在："
                   + string.Join("；", hits.Select(Describe)) + "。";
        }

        [Description("放弃本轮已经做的改动，回到这次对话开始时的画布。跨轮撤销请让用户点消息上的撤回。")]
        public string RevertChanges()
        {
            if (!_applied)
            {
                return "本轮还没有改动。";
            }

            _working = _original.Clone();
            _confirm = false;
            return "已放弃本轮改动，回到对话开始时的画布。";
        }

        [Description("清空画布上的组件，保留页尺寸。已有内容时需要用户确认。")]
        public string ClearCanvas()
        {
            if (_working.Components.Count == 0)
            {
                return "画布已经是空的。";
            }

            Apply(_drafts.Clear(_working), true);
            return "将清空当前画布上的组件。";
        }

        private string ResolveTarget(string component) =>
            FindField(component, _fields)?.Key ?? component;

        private string[] ResolveTargets(string[]? targets) =>
            (targets ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(ResolveTarget)
            .ToArray();

        private PagePreset? ResolvePreset(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var token = name.Trim();
            return _presets.FirstOrDefault(p =>
                       p.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                       || $"{p.WidthMm:0}x{p.HeightMm:0}".Equals(token.Replace("×", "x"), StringComparison.OrdinalIgnoreCase)
                       || $"{p.WidthMm:0}×{p.HeightMm:0}".Equals(token, StringComparison.OrdinalIgnoreCase))
                   ?? PagePresets.All.FirstOrDefault(p =>
                       p.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                       || $"{p.WidthMm:0}x{p.HeightMm:0}".Equals(token.Replace("×", "x"), StringComparison.OrdinalIgnoreCase));
        }

        private string? ResolveRecent(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var token = path.Trim().Trim('"');
            if (System.IO.File.Exists(token))
            {
                return token;
            }

            return _recents.FirstOrDefault(r =>
                r.Equals(token, StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileName(r).Equals(token, StringComparison.OrdinalIgnoreCase)
                || System.IO.Path.GetFileNameWithoutExtension(r).Equals(token, StringComparison.OrdinalIgnoreCase));
        }

        private AgentReply Finish(string text, LabelDocument? document = null, bool confirm = false) =>
            new()
            {
                Text = text,
                Document = document,
                RequiresConfirm = confirm,
                OpenPath = _openPath,
                Sample = _sampleDirty ? _sample : null,
                SaveMode = _saveMode,
                PrintRequested = _print,
                SelectedKeys = _selectedKeys,
                NewBlankRequested = _newBlank,
                ExportMode = _exportMode,
                ExportPath = _exportPath
            };

        private string ApplyFound(LabelDocument doc, string ok)
        {
            if (ReferenceEquals(doc, _working))
            {
                return "找不到该组件。当前：" + ComponentSummary(_working);
            }

            Apply(doc, false);
            return ok;
        }

        public AgentReply ToReply(string? content)
        {
            if (!string.IsNullOrWhiteSpace(_openPath)
                || !string.IsNullOrWhiteSpace(_saveMode)
                || _print
                || _newBlank
                || !string.IsNullOrWhiteSpace(_exportMode)
                || _selectedKeys is not null)
            {
                return Finish(
                    string.IsNullOrWhiteSpace(content) ? "已按工具处理。" : content.Trim(),
                    _applied || _newBlank ? _working : null,
                    _confirm);
            }

            if (_applied)
            {
                return Finish(
                    string.IsNullOrWhiteSpace(content)
                        ? "已按工具更新草稿，请确认后应用到设计器。"
                        : content.Trim(),
                    _working,
                    _confirm);
            }

            if (TryParseAction(content, out var name, out var fieldKeys, out var fieldKey, out var widthMm, out var heightMm, out var relative))
            {
                var (doc, text, large) = Execute(name, fieldKeys, fieldKey, widthMm, heightMm, fieldKey, relative);
                return Finish(text, ReferenceEquals(doc, _working) ? null : doc, large);
            }

            return Finish(
                string.IsNullOrWhiteSpace(content)
                    ? "我没有改画布。可以说出草稿、加字段或分析当前稿。"
                    : content.Trim());
        }

        public AgentReply ToReplyStopped()
        {
            if (_applied)
            {
                return new AgentReply
                {
                    Text = "已停止。已改动的部分可撤销。",
                    Document = _working,
                    RequiresConfirm = _confirm
                };
            }

            return new AgentReply { Text = "已停止。" };
        }

        private void Apply(LabelDocument doc, bool large)
        {
            if (ReferenceEquals(doc, _working))
            {
                return;
            }

            _working = doc;
            _applied = true;
            _confirm |= large;
        }

        private (LabelDocument Document, string Text, bool Large) Execute(
            string name,
            string[]? fieldKeys,
            string? fieldKey,
            double? widthMm,
            double? heightMm,
            string? target = null,
            bool relative = false)
        {
            switch (name)
            {
                case "analyze_template":
                    return (_working, Analyze(_working, _fields), false);
                case "build_draft":
                {
                    var selected = ResolveFields(fieldKeys, _fields);
                    if (selected.Count == 0)
                    {
                        return (_working, "没有可用字段。字典：" + string.Join("、", _fields.Select(f => f.DisplayName)), false);
                    }

                    var page = ResolvePage(widthMm, heightMm, _preset, _working);
                    var doc = _drafts.Build(page, selected, _working.Page.PrinterName, new DraftOptions
                    {
                        Title = "物料标签",
                        Barcode = true,
                        Qr = true,
                        Layout = "material"
                    });
                    var large = _original.Components.Count > 0;
                    return (doc, $"将按 {page.WidthMm:0}×{page.HeightMm:0} mm 生成草稿，含 {string.Join("、", selected.Select(f => f.DisplayName))}。", large);
                }
                case "add_field":
                {
                    var field = FindField(fieldKey, _fields);
                    if (field is null)
                    {
                        return (_working, UnknownField(fieldKey, _fields), false);
                    }

                    return (_drafts.AddField(_working, field), $"将增加「{field.DisplayName}」并绑定 {field.Key}。", false);
                }
                case "set_page":
                {
                    if (widthMm is null || heightMm is null)
                    {
                        return (_working, "改页需要宽和高（毫米）。", false);
                    }

                    var doc = _drafts.SetPage(_working, widthMm.Value, heightMm.Value);
                    return (doc, $"将把页面改为 {widthMm:0}×{heightMm:0} mm。", _original.Components.Count > 0);
                }
                case "move_component":
                {
                    var key = target ?? fieldKey;
                    if (string.IsNullOrWhiteSpace(key) || widthMm is null || heightMm is null)
                    {
                        return (_working, "移动需要组件名和目标坐标（毫米）。例如「把物料编码往右移 5 毫米」。", false);
                    }

                    var doc = _drafts.Move(_working, key, widthMm.Value, heightMm.Value, relative);
                    if (ReferenceEquals(doc, _working))
                    {
                        return (_working, "找不到要移动的组件。可用：" + ComponentSummary(_working), false);
                    }

                    var item = DraftBuilder.FindComponent(doc, key);
                    return (doc, $"将把「{Describe(item)}」移到 {item!.X:0.#},{item.Y:0.#} mm。", false);
                }
                default:
                    return (_working, "这个操作本期不做。", false);
            }
        }

        private static bool TryParseAction(
            string? content,
            out string name,
            out string[]? fieldKeys,
            out string? fieldKey,
            out double? widthMm,
            out double? heightMm,
            out bool relative)
        {
            name = "";
            fieldKeys = null;
            fieldKey = null;
            widthMm = null;
            heightMm = null;
            relative = false;
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return false;
            }

            try
            {
                var action = JsonSerializer.Deserialize<ToolAction>(content[start..(end + 1)], Json) ?? new ToolAction();
                name = string.IsNullOrWhiteSpace(action.Name) ? action.Action ?? "" : action.Name;
                fieldKeys = action.FieldKeys;
                fieldKey = action.FieldKey ?? action.Component;
                widthMm = action.WidthMm ?? action.XMm;
                heightMm = action.HeightMm ?? action.YMm;
                relative = action.Relative;
                if (string.IsNullOrWhiteSpace(name) && action.FieldKeys is { Length: > 0 })
                {
                    name = "build_draft";
                }

                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(action.Component))
                {
                    name = "move_component";
                }

                return !string.IsNullOrWhiteSpace(name) || action.FieldKeys is { Length: > 0 };
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static PagePreset ResolvePage(double? widthMm, double? heightMm, PagePreset? preset, LabelDocument current)
        {
            if (widthMm is > 0 && heightMm is > 0)
            {
                return new PagePreset { Name = "模型", WidthMm = widthMm.Value, HeightMm = heightMm.Value };
            }

            return preset ?? new PagePreset { Name = "当前", WidthMm = current.Page.WidthMm, HeightMm = current.Page.HeightMm };
        }

        private static List<FieldItem> ResolveFields(string[]? keys, IReadOnlyList<FieldItem> fields)
        {
            if (keys is null || keys.Length == 0)
            {
                return fields.Where(f => f.Selected).ToList();
            }

            return keys.Select(k => FindField(k, fields)).OfType<FieldItem>().ToList();
        }

        private static FieldItem? FindField(string? key, IReadOnlyList<FieldItem> fields)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return fields.FirstOrDefault(f =>
                f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                || f.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        private static string UnknownField(string? key, IReadOnlyList<FieldItem> fields) =>
            $"字典里没有「{key}」。请先 ensure_field，或从已有字段里选："
            + string.Join("、", fields.Select(f => f.DisplayName)) + "。";

        private static string? NormalizeDataType(string? dataType)
        {
            var t = dataType?.Trim().ToLowerInvariant() ?? "";
            return t switch
            {
                "text" or "string" or "文本" => "text",
                "number" or "num" or "数字" or "数值" => "number",
                "date" or "datetime" or "日期" => "date",
                _ => null
            };
        }

        private static string Analyze(LabelDocument current, IReadOnlyList<FieldItem> fields)
        {
            var binds = current.Components
                .Where(c => c.Bind.Kind == BindKind.Field && c.Bind.FieldKey is not null)
                .Select(c => c.Bind.FieldKey!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var risks = new List<string>();
            foreach (var c in current.Components)
            {
                if (c.X < 1 || c.Y < 1 || c.X + c.W > current.Page.WidthMm - 1 || c.Y + c.H > current.Page.HeightMm - 1)
                {
                    risks.Add($"{c.Type} 贴边或超页");
                }
            }

            if (current.Components.Count == 0)
            {
                risks.Add("画布是空的");
            }

            var vars = current.Variables.Count == 0
                ? ""
                : "变量：" + string.Join("、", current.Variables.Select(v => v.Name)) + "。";
            return $"页 {current.Page.WidthMm:0}×{current.Page.HeightMm:0} mm，{current.Components.Count} 个组件。"
                   + ComponentSummary(current) + "。"
                   + (binds.Count == 0 ? "尚未绑定字段。" : "已绑定：" + string.Join("、", binds) + "。")
                   + vars
                   + (risks.Count == 0 ? "未发现明显贴边/超页。" : string.Join("；", risks.Distinct()) + "。")
                   + "字典：" + string.Join("、", fields.Select(f => f.DisplayName)) + "。可以说「把物料编码往右移 5 毫米」。";
        }

        private static string ComponentSummary(LabelDocument current) =>
            current.Components.Count == 0
                ? "无"
                : string.Join("；", current.Components.Select(Describe));

        private static string Describe(LabelComponent? item)
        {
            if (item is null)
            {
                return "组件";
            }

            var title = item.Type == LabelComponentType.Image
                ? (string.IsNullOrWhiteSpace(item.Bind.Literal) ? item.Bind.FieldKey ?? "图片" : System.IO.Path.GetFileName(item.Bind.Literal))
                : item.Bind.FieldKey ?? item.Bind.Literal ?? item.Expression ?? item.Type.ToString();
            var hide = item.Visible ? "" : " 隐藏";
            var cond = string.IsNullOrWhiteSpace(item.EnabledWhen) ? "" : " 条件";
            var code = string.IsNullOrWhiteSpace(item.BarcodeSymbology) ? "" : " " + item.BarcodeSymbology;
            return $"id={item.Id} {title}({item.Type}{code}) {item.X:0.#},{item.Y:0.#} {item.W:0.#}×{item.H:0.#}{hide}{cond}";
        }

        private static string Inspect(LabelComponent item)
        {
            var bind = item.Bind.Kind == BindKind.Field
                ? "字段 " + item.Bind.FieldKey
                : "文字 " + (item.Bind.Literal ?? "");
            return $"id={item.Id} 类型={item.Type} 位置={item.X:0.#},{item.Y:0.#} mm 尺寸={item.W:0.#}×{item.H:0.#}"
                   + $" 绑定={bind} 字号={item.FontSizePt:0.#} {item.FontName}"
                   + $" 对齐={item.TextAlign}/{item.VertAlign} 溢出={item.TextFit} 旋转={item.Rotation:0} 颜色={item.ForeColor}"
                   + (string.IsNullOrWhiteSpace(item.FillColor) ? " 无填充" : " 填充=" + item.FillColor)
                   + (item.Border ? $" 边框={item.BorderColor} {item.LineWidthMm:0.#}mm" : "")
                   + (string.IsNullOrWhiteSpace(item.BarcodeSymbology) ? "" : " 制式=" + item.BarcodeSymbology)
                   + (string.IsNullOrWhiteSpace(item.Expression) ? "" : " 表达式=" + item.Expression)
                   + (string.IsNullOrWhiteSpace(item.EnabledWhen) ? "" : " 显示当=" + item.EnabledWhen)
                   + $" 锁定={item.Locked} 可见={item.Visible} z={item.Z}";
        }

        /// <summary>取组件实际会打出来的内容；绑定字段但没有示例值时返回 null。</summary>
        private string? ResolveContent(LabelComponent item)
        {
            if (item.Bind.Kind == BindKind.Literal)
            {
                return item.Bind.Literal ?? "";
            }

            var key = item.Bind.FieldKey;
            if (string.IsNullOrWhiteSpace(key) || _sample is null)
            {
                return null;
            }

            if (_sample.Values.TryGetValue(key, out var byKey))
            {
                return byKey;
            }

            var field = FindField(key, _fields);
            return field is not null && _sample.Values.TryGetValue(field.DisplayName, out var byName)
                ? byName
                : null;
        }

        private static bool IsImageType(string? type)
        {
            var t = type?.Trim().ToLowerInvariant() ?? "";
            return t is "image" or "img" or "logo" or "图片" or "图";
        }

        private static string? ImageSourceError(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "请给出本机图片路径，或右边点「插入图片」。";
            }

            var value = path.Trim().Trim('"');
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return System.IO.File.Exists(value) ? null : "找不到图片文件：" + value;
        }

        private sealed class ToolAction
        {
            public string Name { get; set; } = "";
            public string? Action { get; set; }
            public string? FieldKey { get; set; }
            public string[]? FieldKeys { get; set; }
            public double? WidthMm { get; set; }
            public double? HeightMm { get; set; }
            public double? XMm { get; set; }
            public double? YMm { get; set; }
            public string? Component { get; set; }
            public bool Relative { get; set; }
        }
    }
}
