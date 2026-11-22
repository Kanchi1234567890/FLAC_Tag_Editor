using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FLAC_Tag_Editor.Services.Implementations;
using FLAC_Tag_Editor.Services.Interfaces;
using FLAC_Tag_Editor.ViewModels;
using FLAC_Tag_Editor.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FLAC_Tag_Editor;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public IServiceProvider? Services => _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            mainWindow.DataContext = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = mainWindow;
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Services
        services.AddSingleton<IFlacTagService, FlacTagService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IArtworkService, ArtworkService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<ITsvPasteService, TsvPasteService>();
        services.AddSingleton<ITextSearchReplaceService, TextSearchReplaceService>();

        // ViewModels
        services.AddSingleton<MainWindowViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }
}
