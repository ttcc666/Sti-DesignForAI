using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StiLabel.App.Sti;
using StiLabel.App.ViewModels;
using StiLabel.App.Views;
using StiLabel.Core.Services;

namespace StiLabel.App;

public partial class MainWindow : Window
{
    private readonly IModelOptionsStore _models;
    private readonly ILlmClient _llm;
    private readonly StiWorkbench _sti;
    private readonly MainViewModel _viewModel;
    private ScrollViewer? _chatScroll;
    private bool _scrollQueued;

    public MainWindow(MainViewModel viewModel, IModelOptionsStore models, ILlmClient llm, StiWorkbench sti)
    {
        _viewModel = viewModel;
        _models = models;
        _llm = llm;
        _sti = sti;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.OpenSettingsRequested += ShowSettings;
        viewModel.Messages.CollectionChanged += OnMessagesChanged;
        Loaded += async (_, _) =>
        {
            _sti.Host = DesignerHost;
            await viewModel.InitializeAsync();
        };
        Closing += async (_, e) =>
        {
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            if (await viewModel.ConfirmDiscardAsync())
            {
                _allowClose = true;
                Close();
            }
        };
    }

    private bool _allowClose;

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void ChatInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Clipboard.ContainsImage())
        {
            _viewModel.AttachClipboardImage();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            if (_viewModel.SendChatCommand.CanExecute(null))
            {
                _viewModel.SendChatCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private void ChatInput_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ChatInput_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return;
        }

        foreach (var file in files)
        {
            _viewModel.AttachDroppedImage(file);
        }
    }

    // 新消息进来时贴着底部，流式回复变长时也跟着走。
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (var item in e.NewItems.OfType<ChatLine>())
        {
            item.PropertyChanged += OnChatLineChanged;
        }

        ScrollChatToEnd();
    }

    private void OnChatLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatLine.Text))
        {
            ScrollChatToEnd();
        }
    }

    private void ScrollChatToEnd()
    {
        if (_scrollQueued || _viewModel.Messages.Count == 0)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _scrollQueued = false;
            if (_viewModel.Messages.Count == 0)
            {
                return;
            }

            _chatScroll ??= FindScrollViewer(ChatList);
            if (_chatScroll is not null)
            {
                _chatScroll.ScrollToEnd();
                return;
            }

            ChatList.ScrollIntoView(_viewModel.Messages[^1]);
        }, DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private async void ShowSettings()
    {
        DesignerHost.Visibility = Visibility.Collapsed;
        try
        {
            var window = new SettingsWindow(_models, _llm)
            {
                Owner = this,
                Topmost = true
            };
            window.ShowDialog();
            await _viewModel.RefreshModelStateAsync();
        }
        finally
        {
            DesignerHost.Visibility = Visibility.Visible;
        }
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) =>
        MessageBox.Show(
            "Ctrl+N  新建\nCtrl+O  打开\nCtrl+S  保存\nCtrl+Shift+S  另存为\nCtrl+P  预览\nCtrl+Shift+P  打样\nEsc  停止生成\nEnter  发送对话（Shift+Enter 换行）",
            "快捷键",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.2";
        var license = StiLicenseLoader.IsLoaded
            ? "Stimulsoft 许可已加载"
            : "未找到 license.key（%AppData%\\StiLabel 或程序目录）";
        MessageBox.Show(
            "STI 智能标签设计工作台\n版本 " + version + "\n\n中间是 Stimulsoft 设计器，左边对话出草稿，右边管字段和打印机。\n" + license,
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
