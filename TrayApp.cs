using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace PixelBarLyrics;

/// <summary>
/// 轻量系统托盘上下文：提供实时状态展示、翻译/繁简/偏移快捷调节与一键打开配置日志
/// </summary>
public sealed class TrayApp : WinForms.ApplicationContext
{
    private readonly AppConfig _config;
    private readonly CancellationTokenSource _cts;
    private readonly Action _onSettingsChanged;
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _trackItem;
    private readonly WinForms.ToolStripMenuItem _sourceItem;
    private readonly WinForms.ToolStripMenuItem _transItem;
    private readonly WinForms.ToolStripMenuItem _simpItem;
    private readonly WinForms.ToolStripMenuItem _filterItem;
    private readonly WinForms.ToolStripMenuItem _offsetMenu;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly Icon _trayIcon;
    private readonly IntPtr _iconHandle;

    public volatile bool IsPaused;

    public TrayApp(AppConfig config, CancellationTokenSource cts, Action onSettingsChanged)
    {
        _config = config;
        _cts = cts;
        _onSettingsChanged = onSettingsChanged;

        (_trayIcon, _iconHandle) = CreatePixelIcon();

        _menu = new WinForms.ContextMenuStrip();
        _trackItem = new WinForms.ToolStripMenuItem("等待 Limusic 播放...") { Enabled = false };
        _sourceItem = new WinForms.ToolStripMenuItem("状态: 初始化中") { Enabled = false };

        _transItem = new WinForms.ToolStripMenuItem("外语歌优先显示中文翻译")
        {
            Checked = _config.PreferTranslation,
            CheckOnClick = true
        };
        _transItem.CheckedChanged += (_, _) =>
        {
            _config.PreferTranslation = _transItem.Checked;
            _config.Save();
            _onSettingsChanged();
        };

        _simpItem = new WinForms.ToolStripMenuItem("繁体自动转简体")
        {
            Checked = _config.ConvertToSimplified,
            CheckOnClick = true
        };
        _simpItem.CheckedChanged += (_, _) =>
        {
            _config.ConvertToSimplified = _simpItem.Checked;
            _config.Save();
            _onSettingsChanged();
        };

        _filterItem = new WinForms.ToolStripMenuItem("过滤作词/作曲元信息")
        {
            Checked = _config.FilterCreditLines,
            CheckOnClick = true
        };
        _filterItem.CheckedChanged += (_, _) =>
        {
            _config.FilterCreditLines = _filterItem.Checked;
            _config.Save();
            _onSettingsChanged();
        };

        _offsetMenu = new WinForms.ToolStripMenuItem(FormatOffsetLabel(_config.LyricOffsetMs));
        var offsetEarly = new WinForms.ToolStripMenuItem("提前 250ms (+250ms)");
        offsetEarly.Click += (_, _) => AdjustOffset(250);
        var offsetLate = new WinForms.ToolStripMenuItem("延后 250ms (-250ms)");
        offsetLate.Click += (_, _) => AdjustOffset(-250);
        var offsetDefault = new WinForms.ToolStripMenuItem("恢复默认 (+350ms)");
        offsetDefault.Click += (_, _) => SetOffset(350);
        var offsetZero = new WinForms.ToolStripMenuItem("归零 (0ms)");
        offsetZero.Click += (_, _) => SetOffset(0);
        _offsetMenu.DropDownItems.AddRange([offsetEarly, offsetLate, new WinForms.ToolStripSeparator(), offsetDefault, offsetZero]);

        _pauseItem = new WinForms.ToolStripMenuItem("暂停推送歌词");
        _pauseItem.Click += (_, _) =>
        {
            IsPaused = !IsPaused;
            _pauseItem.Text = IsPaused ? "恢复推送歌词" : "暂停推送歌词";
            if (!IsPaused)
            {
                _onSettingsChanged();
            }
        };

        var openConfig = new WinForms.ToolStripMenuItem("打开配置文件");
        openConfig.Click += (_, _) => OpenFileShell(Path.Combine(_config.BaseDirectory, "config.json"));

        var openLog = new WinForms.ToolStripMenuItem("打开运行日志");
        openLog.Click += (_, _) => OpenFileShell(_config.GetLogFilePath());

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => RequestExit();

        _menu.Items.AddRange(
        [
            _trackItem,
            _sourceItem,
            new WinForms.ToolStripSeparator(),
            _transItem,
            _simpItem,
            _filterItem,
            _offsetMenu,
            _pauseItem,
            new WinForms.ToolStripSeparator(),
            openConfig,
            openLog,
            exitItem
        ]);

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "花再 PixelBar 歌词同步",
            ContextMenuStrip = _menu,
            Visible = true
        };
    }

    public void UpdateStatus(string trackDisplay, string sourceInfo)
    {
        if (_menu.IsDisposed) return;

        void Apply()
        {
            if (_menu.IsDisposed) return;
            string shortTrack = trackDisplay.Length > 36 ? trackDisplay[..36] + "..." : trackDisplay;
            _trackItem.Text = $"曲目: {shortTrack}";
            _sourceItem.Text = sourceInfo;
            string tip = $"花再歌词: {shortTrack}";
            _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
        }

        if (_menu.InvokeRequired)
        {
            try { _menu.BeginInvoke(Apply); } catch { }
        }
        else
        {
            Apply();
        }
    }

    public void RequestExit()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        void DoExit()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            ExitThread();
        }

        if (!_menu.IsDisposed && _menu.InvokeRequired)
        {
            try { _menu.BeginInvoke(DoExit); } catch { }
        }
        else
        {
            DoExit();
        }
    }

    private void AdjustOffset(int deltaMs) => SetOffset(_config.LyricOffsetMs + deltaMs);

    private void SetOffset(int newOffsetMs)
    {
        _config.LyricOffsetMs = Math.Clamp(newOffsetMs, -5000, 5000);
        _config.Save();
        _offsetMenu.Text = FormatOffsetLabel(_config.LyricOffsetMs);
        _onSettingsChanged();
    }

    private static string FormatOffsetLabel(int ms) => $"时间偏移 ({(ms >= 0 ? "+" : "")}{ms}ms)";

    private static void OpenFileShell(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch { }
    }

    private static (Icon Icon, IntPtr Handle) CreatePixelIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(24, 28, 36));
        using var borderPen = new Pen(Color.FromArgb(64, 80, 100), 2);
        g.DrawRectangle(borderPen, 1, 1, 29, 29);

        using var barBrush = new SolidBrush(Color.FromArgb(0, 216, 255));
        using var accentBrush = new SolidBrush(Color.FromArgb(120, 255, 214));
        // 绘制像素频谱三柱
        g.FillRectangle(barBrush, 6, 14, 5, 12);
        g.FillRectangle(accentBrush, 13, 7, 5, 19);
        g.FillRectangle(barBrush, 20, 11, 5, 15);

        IntPtr hIcon = bmp.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _trayIcon.Dispose();
        }
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
