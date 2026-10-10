using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NetManager.App.ViewModels;
using NetManager.App.Views;
using NetManager.Core.Localization;
using NetManager.Core.Services;
using NetManager.Windows;

namespace NetManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Real Windows backend on Windows, demo data everywhere else (lets the UI be developed on macOS/Linux).
            INetworkService network = OperatingSystem.IsWindows() ? new WindowsNetworkService() : new MockNetworkService();
            var settings = new SettingsStore();
            var saved = settings.Load();
            Loc.SetLanguage(saved.Language ?? Loc.DetectSystemLanguage());
            ThemeService.Apply(saved.Theme);
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(network, new ProfileStore(), settings, new RecoveryStore()) };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
