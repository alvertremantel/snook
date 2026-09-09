using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Snook.Contracts;

namespace Snook.UI;

public partial class App : Avalonia.Application
{
    public static IBackendClient? ConfiguredBackend { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(ConfiguredBackend ?? throw new InvalidOperationException("The desktop backend was not configured."));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
