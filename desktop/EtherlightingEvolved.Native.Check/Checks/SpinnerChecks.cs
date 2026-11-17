using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Spinner = EtherlightingEvolved.Native.Shell.Spinner;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The loading spinner turns while it is shown and leaves no failed animation task behind.</summary>
internal static class SpinnerChecks
{
    public static void Run(Action<bool, string> check)
    {
        // Failed tasks from earlier checks surface when the finalizer runs, so they are collected before counting.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var unobserved = new List<string>();
        void Count(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobserved.Add(e.Exception.InnerException?.Message ?? e.Exception.Message);
            e.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += Count;
        try
        {
            var spinner = new Spinner();
            var window = new Window { Content = spinner, Width = 40, Height = 40 };
            window.Show();
            var angles = new List<double>();
            for (var tick = 0; tick < 4; tick++)
            {
                Thread.Sleep(60);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                angles.Add(((RotateTransform)spinner.RenderTransform!).Angle);
            }
            window.Close();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            check(angles.Distinct().Count() > 1, "the loading spinner turns while it is shown");
            check(unobserved.Count == 0, "the loading spinner leaves no failed animation task" + (unobserved.Count > 0 ? $" ({string.Join("; ", unobserved)})" : ""));
        }
        finally { TaskScheduler.UnobservedTaskException -= Count; }
    }
}
