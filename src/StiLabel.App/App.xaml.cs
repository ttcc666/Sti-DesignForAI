using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StiLabel.App.Sti;
using StiLabel.App.ViewModels;
using StiLabel.Core.Hosting;
using StiLabel.Data;
using StiLabel.Data.Hosting;

namespace StiLabel.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        RegisterGlobalExceptionHandling();
        AppPaths.EnsureCreated();
        Stimulsoft.Report.StiOptions.Engine.ForceInterpretationMode = true;
        StiLicenseLoader.TryLoad();
        StiLocalizationLoader.TryLoadChinese();

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddStiLabelCore();
                services.AddStiLabelData();
                services.AddSingleton<StiWorkbench>();
                services.AddSingleton<IStiWorkbench>(sp => sp.GetRequiredService<StiWorkbench>());
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        _host.Services.GetRequiredService<StiLabelDb>().Initialize();
        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
    }

    private void RegisterGlobalExceptionHandling()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogError("UI Dispatcher", args.Exception);
            MessageBox.Show(
                $"程序运行遇到异常：\n{args.Exception.Message}\n\n详细信息已记录至：\n{AppPaths.ErrorLog}",
                "错误提示",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception
                     ?? new Exception(args.ExceptionObject?.ToString() ?? "未知严重异常");
            LogError("AppDomain", ex);
            if (args.IsTerminating)
            {
                MessageBox.Show(
                    $"程序发生不可恢复的致命异常，即将退出：\n{ex.Message}\n\n详细日志请查阅：\n{AppPaths.ErrorLog}",
                    "致命错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Stop);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError("TaskScheduler", args.Exception);
            args.SetObserved();
        };
    }

    private static void LogError(string category, Exception ex)
    {
        try
        {
            AppPaths.EnsureCreated();
            var message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {ex}\n\n";
            File.AppendAllText(AppPaths.ErrorLog, message);
        }
        catch
        {
            // 防御日志写失败时的二次异常
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }
}
