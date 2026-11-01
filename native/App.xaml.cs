using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using EtherlightingEvolved.Services;
using Forms = System.Windows.Forms;

namespace EtherlightingEvolved;

public partial class App : Application
{
    private Mutex? instance;
    private EngineClient? engine;
    private Forms.NotifyIcon? tray;
    private bool quitting;
    private readonly CancellationTokenSource lifetime = new();
    public bool Demo { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Demo = e.Args.Contains("--demo") || e.Args.Contains("--verify");
        var isolated = Demo;
        if (!isolated)
        {
            instance = new Mutex(false, "EtherlightingEvolved", out var created);
            if (!created)
            {
                instance.Dispose(); instance = null;
                await SignalExisting(e.Args.Contains("--quit") ? "quit" : "show");
                Shutdown(); return;
            }
            if (e.Args.Contains("--quit")) { Shutdown(); return; }
            _ = ListenAsync();
        }
        engine = new EngineClient();
        var window = new MainWindow(engine, Demo);
        if (e.Args.Contains("--verify")) window.ShowActivated = false;
        MainWindow = window;
        window.Closing += async (_, args) => { if (!quitting) { args.Cancel = true; if (isolated) await QuitAsync(); else window.Hide(); } };
        if (!e.Args.Contains("--background")) window.Show();
        if (!isolated) CreateTray(window);
        try { await engine.StartAsync(Demo); await window.ReadyAsync(); }
        catch (Exception) { window.StartupFailed("The lighting service could not start. Close the app and try again."); }
        if (e.Args.Contains("--verify"))
        {
            try
            {
                var folder = e.Args.SkipWhile(x => x != "--verify").Skip(1).FirstOrDefault();
                if (string.IsNullOrEmpty(folder)) throw new ArgumentException("Verification folder required.");
                await window.VerifyAsync(Path.GetFullPath(folder));
            }
            catch (Exception error)
            {
                var folder = e.Args.SkipWhile(x => x != "--verify").Skip(1).FirstOrDefault();
                if (folder != null) { Directory.CreateDirectory(folder); await File.WriteAllTextAsync(Path.Combine(folder, "failure.txt"), error.ToString()); }
                Environment.ExitCode = 1;
            }
            finally { await QuitAsync(); }
        }
    }

    private void CreateTray(Window window)
    {
        using var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/ee.ico"))!.Stream;
        tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(resource), Text = "Etherlighting Evolved", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { window.Show(); window.Activate(); });
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(() => { window.Show(); window.Activate(); }));
        menu.Items.Add("Quit", null, async (_, _) => await Dispatcher.InvokeAsync(QuitAsync).Task.Unwrap());
        tray.ContextMenuStrip = menu;
    }

    public async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true;
        if (engine != null)
        {
            try { await engine.DisposeAsync(); }
            catch (Exception)
            {
                quitting = false;
                if (MainWindow is MainWindow window) { window.Show(); window.StartupFailed("Output release is still pending. Keep the app open and try Quit again."); }
                return;
            }
        }
        lifetime.Cancel();
        tray?.Dispose(); instance?.Dispose();
        Shutdown();
    }

    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream("EtherlightingEvolved.Native", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token);
                var command = await new StreamReader(pipe).ReadLineAsync(lifetime.Token);
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (command == "quit") await QuitAsync();
                    else if (command == "show") { MainWindow?.Show(); MainWindow?.Activate(); }
                });
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
        }
    }

    private static async Task SignalExisting(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "EtherlightingEvolved.Native", PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(700);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(command);
            if (command == "quit") await WaitForShutdownAsync();
            return;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { }
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".etherlighting-evolved", "desktop.json");
            var record = JsonNode.Parse(await File.ReadAllTextAsync(file));
            var port = record?["port"]?.GetValue<int>() ?? 0;
            if (port is < 1024 or > 65535) return;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await client.PostAsync($"http://127.0.0.1:{port}/api/desktop/{command}", null);
            if (command == "quit") await WaitForShutdownAsync();
        }
        catch (Exception) { }
    }

    private static async Task WaitForShutdownAsync()
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (!Mutex.TryOpenExisting("EtherlightingEvolved", out var running)) return;
            running.Dispose();
            await Task.Delay(200);
        }
        Environment.ExitCode = 2;
    }
}
