using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace EtherlightingEvolved.Native;

public sealed class App : Application
{
    public static Uri Workspace { get; set; } = new("http://127.0.0.1:4180/");

    public override void Initialize()
    {
        Name = "Etherlighting Evolved";
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Workspace, new WorkspaceClient(Workspace));
        base.OnFrameworkInitializationCompleted();
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var address = Environment.GetEnvironmentVariable("EE_WORKSPACE_URL");
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--url") address = args[i + 1];
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return 2;
            App.Workspace = uri;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
