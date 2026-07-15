using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CodexAccountBar;
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _tray; private MainWindow? _window;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); _window = new MainWindow();
        _tray = new Forms.NotifyIcon { Icon = SystemIcons.Application, Text = "Codex Account Bar", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.ContextMenuStrip.Items.Add("Open", null, (_, _) => ShowWindow());
        _tray.ContextMenuStrip.Items.Add("Refresh", null, async (_, _) => await _window.RefreshAllAsync());
        _tray.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit()); ShowWindow();
    }
    private void ShowWindow() { if (_window is null) return; _window.Show(); if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal; _window.Activate(); }
    private void Quit() { if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); } _window?.ForceClose(); Shutdown(); }
}
