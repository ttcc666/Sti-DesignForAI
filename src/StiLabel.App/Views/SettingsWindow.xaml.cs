using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using StiLabel.Core.Llm;
using StiLabel.Core.Services;

namespace StiLabel.App.Views;

public partial class SettingsWindow : Window
{
    private readonly IModelOptionsStore _options;
    private readonly ILlmClient _llm;
    private bool _loading;

    public SettingsWindow(IModelOptionsStore options, ILlmClient llm)
    {
        _options = options;
        _llm = llm;
        InitializeComponent();

        var view = new ListCollectionView(ModelProviders.All.ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModelProvider.Group)));
        ProviderList.ItemsSource = view;
        ProtocolBox.ItemsSource = ChatProtocols.All;
        ContextBox.ItemsSource = CompactModes.ContextSizes;
        CompactBox.ItemsSource = CompactModes.All;
        TurnsBox.ItemsSource = CompactModes.TurnChoices;

        Loaded += async (_, _) => await LoadAsync();
    }

    private ModelProvider Selected =>
        ProviderList.SelectedItem as ModelProvider ?? ModelProviders.Custom;

    private string Protocol =>
        (ProtocolBox.SelectedItem as ChatProtocol)?.Id ?? ChatProtocols.OpenAi;

    private async Task LoadAsync()
    {
        var model = await _options.LoadAsync();
        var provider = ModelProviders.Resolve(model.Provider, model.Endpoint);

        // 老配置里没有协议字段，按厂商推荐值补上
        var protocol = string.IsNullOrWhiteSpace(model.Protocol) ? provider.Protocol : model.Protocol;

        _loading = true;
        try
        {
            ProviderList.SelectedItem = provider;
            ProviderList.ScrollIntoView(provider);
            SelectProtocol(protocol);
            ShowProvider(provider);

            EndpointBox.Text = string.IsNullOrWhiteSpace(model.Endpoint)
                ? provider.EndpointFor(protocol)
                : model.Endpoint;
            ModelBox.Text = string.IsNullOrWhiteSpace(model.Model)
                ? provider.Models.FirstOrDefault() ?? ""
                : model.Model;
            KeyBox.Password = model.ApiKey ?? "";
            ContextBox.Text = CompactModes.FormatContextTokens(model.ContextTokens);
            CompactBox.SelectedItem = CompactModes.Resolve(model.CompactMode);
            TurnsBox.Text = CompactModes.ClampTurns(model.CompactTurns).ToString();
            ShowCompact();
            EnableBox.IsChecked = model.Enabled;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderList.SelectedItem is not ModelProvider provider)
        {
            return;
        }

        _loading = true;
        try
        {
            SelectProtocol(provider.Protocol);
        }
        finally
        {
            _loading = false;
        }

        ShowProvider(provider);
        EndpointBox.Text = provider.EndpointFor(provider.Protocol);
        ModelBox.Text = provider.Models.FirstOrDefault() ?? "";
        HideStatus();
    }

    private void Protocol_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // 同一家可能同时提供原生和兼容两个地址，换协议时把地址跟着换过去。
        // 用户手填过别的地址就不动，避免覆盖自建网关。
        var provider = Selected;
        var current = EndpointBox.Text.Trim().TrimEnd('/');
        var known = current.Length == 0
            || Preset(provider).Any(e => string.Equals(e, current, StringComparison.OrdinalIgnoreCase));

        if (known && provider.EndpointFor(Protocol) is { Length: > 0 } target)
        {
            EndpointBox.Text = target;
        }

        ShowProvider(provider);
        HideStatus();
    }

    private void SelectProtocol(string? id) =>
        ProtocolBox.SelectedItem = ChatProtocols.Resolve(id);

    private static IEnumerable<string> Preset(ModelProvider provider) =>
        new[] { provider.Endpoint, provider.OpenAiEndpoint }
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.TrimEnd('/'));

    private void ShowProvider(ModelProvider provider)
    {
        ProviderName.Text = provider.Name;
        ProviderBadgeText.Text = provider.Badge;
        ProviderBadge.Background = Brush(provider.Accent);
        ProviderNote.Text = string.IsNullOrWhiteSpace(provider.Note)
            ? "填好地址、模型、密钥即可。"
            : provider.Note;
        ConsoleLink.Visibility = string.IsNullOrWhiteSpace(provider.ConsoleUrl)
            ? Visibility.Collapsed
            : Visibility.Visible;
        KeyHint.Text = provider.RequiresKey
            ? provider.KeyHint
            : provider.KeyHint + "（本地服务留空即可）";

        var protocol = ChatProtocols.Resolve(Protocol);
        ProtocolNote.Text = protocol.Note;
        EndpointNote.Text = protocol.Id switch
        {
            ChatProtocols.Anthropic => "官网默认 https://api.anthropic.com。自建网关填站点根，不要带 /messages。",
            ChatProtocols.Gemini => "官网默认 generativelanguage.googleapis.com。自建网关填到 /v1beta，不要带模型名。",
            ChatProtocols.AzureOpenAi or ChatProtocols.AzureResponses =>
                "官网填 https://资源名.openai.azure.com ，不要带 /openai 或 /v1。",
            ChatProtocols.Ollama => "官网填 http://localhost:11434 ，不要带 /v1。",
            ChatProtocols.OpenAiResponses => "填到 /v1 这一级。这是 OpenAI 官方推荐的 Responses API。",
            _ => "填到 /v1 这一级，不要带 /chat/completions。自建网关只要兼容 Chat Completions 即可。"
        };

        var current = ModelBox.Text;
        ModelBox.ItemsSource = provider.Models;
        ModelBox.Text = current;
    }

    private ModelOptions Current(bool enable)
    {
        var endpoint = EndpointBox.Text.Trim().TrimEnd('/');
        var name = ModelBox.Text.Trim();
        var complete = !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(name);
        return new ModelOptions
        {
            Enabled = enable && complete,
            Endpoint = endpoint,
            Model = name,
            ApiKey = string.IsNullOrWhiteSpace(KeyBox.Password) ? null : KeyBox.Password,
            Provider = Selected.Id,
            Protocol = Protocol,
            ContextTokens = CompactModes.ParseContextTokens(ContextBox.Text),
            CompactMode = CompactModes.Normalize((CompactBox.SelectedItem as CompactMode)?.Id),
            CompactTurns = CompactModes.ParseTurns(TurnsBox.Text)
        };
    }

    private void Compact_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ShowCompact();
    }

    private void ShowCompact()
    {
        var mode = CompactBox.SelectedItem as CompactMode ?? CompactModes.Resolve(null);
        CompactNote.Text = mode.Note;
        TurnsPanel.Visibility = CompactModes.UsesTurns(mode.Id)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var model = Current(enable: true);
        if (string.IsNullOrWhiteSpace(model.Endpoint))
        {
            Status("请先填接口地址。", StatusKind.Warning);
            return;
        }

        FetchButton.IsEnabled = false;
        Status("正在读取模型列表…", StatusKind.Info);
        try
        {
            var names = await ModelListClient.ListAsync(model);
            if (names.Count == 0)
            {
                Status("这个地址没返回模型列表，手动填模型名即可。", StatusKind.Warning);
                return;
            }

            var keep = ModelBox.Text;
            ModelBox.ItemsSource = names;
            ModelBox.Text = names.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : names[0];
            await _options.SaveModelNamesAsync(names);
            Status($"读到 {names.Count} 个模型，已填入下拉列表。", StatusKind.Success);
        }
        catch (Exception ex)
        {
            Status("拉取失败：" + ex.Message + " 手动填模型名也能用。", StatusKind.Error);
        }
        finally
        {
            FetchButton.IsEnabled = true;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var model = Current(enable: true);
        if (!model.IsReady)
        {
            Status("请先填写地址和模型名。", StatusKind.Warning);
            return;
        }

        Status("正在测连通…", StatusKind.Info);
        try
        {
            var reply = await _llm.TestAsync(model);
            EnableBox.IsChecked = true;
            await _options.SaveAsync(model);
            Status(
                "连通成功，已保存并启用对话。" + (string.IsNullOrWhiteSpace(reply) ? "" : " 回复：" + reply),
                StatusKind.Success);
        }
        catch (Exception ex)
        {
            Status("连通失败：" + ex.Message, StatusKind.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (EnableBox.IsChecked == false)
        {
            await _options.SaveAsync(Current(enable: false));
            DialogResult = true;
            return;
        }

        var model = Current(enable: true);
        if (!model.IsReady)
        {
            Status("要启用对话，请填写地址和模型名。", StatusKind.Warning);
            return;
        }

        EnableBox.IsChecked = true;
        await _options.SaveAsync(model);
        DialogResult = true;
    }

    private void Console_Click(object sender, RoutedEventArgs e)
    {
        var url = Selected.ConsoleUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            Status("打不开浏览器，地址是 " + url, StatusKind.Warning);
        }
    }

    private enum StatusKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    private void Status(string text, StatusKind kind)
    {
        TestResult.Text = text;
        StatusStrip.Visibility = Visibility.Visible;
        (StatusStrip.Background, StatusStrip.BorderBrush, TestResult.Foreground) = kind switch
        {
            StatusKind.Success => (Brush("#E6F6F0"), Brush("#B7E4D2"), Brush("#0B7A55")),
            StatusKind.Warning => (Brush("#FFF8E7"), Brush("#F5E3B3"), Brush("#8A6D1F")),
            StatusKind.Error => (Brush("#FDECEC"), Brush("#F5C6C6"), Brush("#B42318")),
            _ => (Brush("#F7F8FA"), Brush("#E4E7EC"), Brush("#5B6472"))
        };
    }

    private void HideStatus() => StatusStrip.Visibility = Visibility.Collapsed;

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
