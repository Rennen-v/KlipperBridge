using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Local\\KlipperBridge-NotifyIcon", out var isFirst);
        if (!isFirst) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new BridgeApplicationContext());
    }
}

internal sealed class BridgeApplicationContext : ApplicationContext
{
    private const string FluiddUrl = "http://127.0.0.1:8765/";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly ProxyHost _proxy = new();
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _state;
    private readonly ToolStripMenuItem _startup;
    private readonly Icon _green = CreateIcon(Color.ForestGreen);
    private readonly Icon _gray = CreateIcon(Color.Gray);
    private readonly Icon _red = CreateIcon(Color.Firebrick);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30000 };
    private bool _checking;
    private bool _proxyFailed;

    public BridgeApplicationContext()
    {
        _state = new ToolStripMenuItem("Состояние: запуск прокси…") { Enabled = false };
        _startup = new ToolStripMenuItem("Запускать с Windows") { CheckOnClick = true, Checked = StartupEnabled() };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_state);
        menu.Items.Add("Открыть Fluidd", null, (_, _) => OpenFluidd());
        menu.Items.Add("Проверить соединение", null, async (_, _) => await CheckAsync());
        menu.Items.Add(_startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);
        _notify = new NotifyIcon { Icon = _gray, Text = "KlipperBridge: запускается", ContextMenuStrip = menu, Visible = true };
        _notify.DoubleClick += (_, _) => OpenFluidd();
        _timer.Tick += async (_, _) => await CheckAsync();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try { await _proxy.StartAsync(); _timer.Start(); await CheckAsync(); }
        catch (Exception ex) { _proxyFailed = true; UpdateState(false, "Ошибка локального прокси: " + ex.Message); }
    }

    private async Task CheckAsync()
    {
        if (_checking || !_proxy.IsRunning) return;
        _checking = true;
        try { UpdateState(await _proxy.CheckMoonrakerAsync(), null); }
        catch (Exception ex)
        {
            Trace.TraceError("KlipperBridge status check failed: {0}", ex);
            UpdateState(false, null);
        }
        finally { _checking = false; }
    }

    private void UpdateState(bool online, string? detail)
    {
        _notify.Icon = _proxyFailed ? _red : online ? _green : _gray;
        var text = _proxyFailed ? "Ошибка локального прокси" : online ? "QIDI Q2 доступен" : "QIDI Q2 недоступен";
        _notify.Text = "KlipperBridge: " + text;
        _state.Text = detail ?? "Состояние: " + text;
    }

    private static void OpenFluidd() => Process.Start(new ProcessStartInfo(FluiddUrl) { UseShellExecute = true });
    private static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("KlipperBridge") is string;
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("KlipperBridge", "\"" + Application.ExecutablePath + "\"");
        else key.DeleteValue("KlipperBridge", false);
    }

    private async Task ExitAsync()
    {
        _timer.Stop();
        _notify.Visible = false;
        try { await _proxy.StopAsync(); }
        finally
        {
            _notify.Dispose(); _timer.Dispose(); _green.Dispose(); _gray.Dispose(); _red.Dispose();
            ExitThread();
        }
    }

    private static Icon CreateIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(color))
        { graphics.Clear(Color.Transparent); graphics.FillEllipse(brush, 4, 4, 24, 24); }
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
