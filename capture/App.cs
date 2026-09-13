using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;

namespace Greenlight.GalleryCapture;

[SupportedOSPlatform("windows")]
public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewfinder = new ViewfinderWindow();
            var panel = new PanelWindow(viewfinder);

            // The panel is the app: closing it takes the frame with it, and the frame on its
            // own would be a transparent window with a hole in it and no way out.
            desktop.MainWindow = panel;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            viewfinder.Show();
            panel.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

[SupportedOSPlatform("windows")]
public static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
