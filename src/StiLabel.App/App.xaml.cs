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
        StiLocalizationLoader.TryLoadChinese();
        MainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow.Show();
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
