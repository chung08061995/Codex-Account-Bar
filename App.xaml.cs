using System.Drawing;
using System.Windows;
using CodexAccountBar.Services;
using Forms = System.Windows.Forms;

namespace CodexAccountBar;
public partial class App : System.Windows.Application
{
    #region Fields

    private Forms.NotifyIcon? _tray; private MainWindow? _window; private int _handlingUiException;

    #endregion

    #region Framework Lifecycle

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--request-recorder"))
        {
            Task.Run(async () =>
            {
                try { await new RequestRecorder().RunAsync(); }
                catch (Exception exception) { AppLog.Error("Request recorder stopped", new IOException(exception.GetType().Name)); }
                Dispatcher.Invoke(() => Shutdown());
            });
            return;
        }
        if (e.Args.Contains("--enable-request-tracking"))
        {
            EnableRequestTrackingAndExit();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI thread", args.Exception);
            args.Handled = true;
            if (Interlocked.Exchange(ref _handlingUiException, 1) != 0) return;
            try { System.Windows.MessageBox.Show($"Codex Account Bar encountered an error.\n\nLog: {AppLog.CurrentFile}", "Codex Account Bar", MessageBoxButton.OK, MessageBoxImage.Error); }
            catch (Exception ex) { AppLog.Error("Error dialog", ex); }
            finally { Interlocked.Exchange(ref _handlingUiException, 0); }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) AppLog.Error("Unhandled exception", ex); };
        TaskScheduler.UnobservedTaskException += (_, args) => { AppLog.Error("Background task", args.Exception); args.SetObserved(); };
        try
        {
            AppLog.Info("Application starting"); _window = new MainWindow();
            if (RequestTrackingConfiguration.Enabled) StartRequestRecorder();
            var icon = Environment.ProcessPath is { } path ? Icon.ExtractAssociatedIcon(path) : null;
            _tray = new Forms.NotifyIcon { Icon = icon ?? SystemIcons.Application, Text = "Codex Account Bar", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
            _tray.DoubleClick += (_, _) => ShowWindow();
            _tray.ContextMenuStrip.Items.Add("Open", null, (_, _) => ShowWindow());
            _tray.ContextMenuStrip.Items.Add("Refresh", null, async (_, _) => { try { await _window.RefreshAllAsync(); } catch (Exception ex) { AppLog.Error("Tray refresh", ex); } });
            _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
            _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit()); ShowWindow(); AppLog.Info("Main window shown");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed", ex); System.Windows.MessageBox.Show($"Codex Account Bar could not start.\n\nLog: {AppLog.CurrentFile}", "Codex Account Bar", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1);
        }
    }

    #endregion

    #region Request Tracking

    private async void EnableRequestTrackingAndExit()
    {
        try { await RequestTrackingConfiguration.EnableAsync(); AppLog.Info("Request tracking enabled; reload Codex to apply routing"); }
        catch (Exception exception) { AppLog.Error("Enable request tracking", exception); }
        finally { Shutdown(); }
    }

    private async void StartRequestRecorder()
    {
        try { await RequestRecorder.StartAsync(); }
        catch (Exception exception) { AppLog.Error("Start request recorder", exception); }
    }

    #endregion

    #region Private Methods

    private void ShowWindow() { if (_window is null) return; _window.Show(); if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal; _window.Activate(); }
    private void Quit() { if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); } _window?.ForceClose(); Shutdown(); }

    #endregion
}
