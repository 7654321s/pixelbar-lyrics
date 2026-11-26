using System.Diagnostics;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace PixelBarLyrics;

internal static class Program
{
    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        bool isCliCommand = args.Any(a => a is "--check-smtc" or "--probe" or "--test-text" or "--test-lyric" or "--help" or "-h");
        bool consoleMode = args.Contains("--console") || isCliCommand;
        if (consoleMode)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }

        AppConfig config = AppConfig.LoadOrCreate();

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        if (args.Contains("--check-smtc"))
        {
            return RunCheckSmtcAsync(config).GetAwaiter().GetResult();
        }

        if (args.Contains("--probe"))
        {
            return RunProbe(config);
        }

        string? testText = GetArgValue(args, "--test-text");
        if (testText != null)
        {
            string? encOverride = GetArgValue(args, "--enc");
            return RunTestText(config, testText, encOverride);
        }

        string? testLyricQuery = GetArgValue(args, "--test-lyric");
        if (testLyricQuery != null)
        {
            return RunTestLyricAsync(config, testLyricQuery).GetAwaiter().GetResult();
        }

        // 常驻后台模式：单实例防重
        using var mutex = new Mutex(true, @"Local\PixelBarLyricsSync", out bool createdNew);
        if (!createdNew)
        {
            if (consoleMode)
            {
                Console.WriteLine("已有 PixelBarLyrics 实例在运行，自动退出重复实例。");
            }
            return 0;
        }

        string? followProcess = GetArgValue(args, "--follow-process");
        using var cts = new CancellationTokenSource();

        if (args.Contains("--console"))
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            return RunSyncLoopAsync(config, followProcess, true, null, cts, null).GetAwaiter().GetResult();
        }

        // 托盘托管模式（无控制台窗口）
        WinForms.Application.EnableVisualStyles();
        WinForms.Application.SetCompatibleTextRenderingDefault(false);

        int forceRefreshFlag = 0;
        using var tray = new TrayApp(config, cts, () => Interlocked.Exchange(ref forceRefreshFlag, 1));
        Task syncTask = Task.Run(() => RunSyncLoopAsync(config, followProcess, false, tray, cts, () => Interlocked.Exchange(ref forceRefreshFlag, 0) == 1));
        WinForms.Application.Run(tray);

        if (!cts.IsCancellationRequested)
        {
            cts.Cancel();
        }
        try { syncTask.Wait(1500); } catch { }
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("PixelBarLyrics - 花再 Halo PixelBar 歌词同步工具");
        Console.WriteLine("用法:");
        Console.WriteLine("  PixelBarLyrics.exe [--follow-process limusic-app] [--console]");
        Console.WriteLine("  PixelBarLyrics.exe --check-smtc");
        Console.WriteLine("  PixelBarLyrics.exe --probe");
        Console.WriteLine("  PixelBarLyrics.exe --test-text \"测试文字\" [--enc none|xor_a5|invert|xtea]");
        Console.WriteLine("  PixelBarLyrics.exe --test-lyric \"歌名|歌手\"");
    }

    private static async Task<int> RunCheckSmtcAsync(AppConfig config)
    {
        var smtc = new SmtcMonitor(config);
        List<MediaSnapshot> list = await smtc.GetAllSnapshotsAsync();
        Console.WriteLine($"共检测到 {list.Count} 个 SMTC 媒体会话:");
        foreach (MediaSnapshot s in list)
        {
            Console.WriteLine($"  AppId={s.AppId} | Playing={s.IsPlaying} | Title={s.Title} | Artist={s.Artist} | Pos={s.PositionSec:F2}/{s.DurationSec:F2}");
        }
        MediaSnapshot? active = await smtc.GetActiveSnapshotAsync();
        Console.WriteLine(active == null
            ? "当前选中的活跃会话: 无"
            : $"当前选中的活跃会话: {active.AppId} -> {active.Title} - {active.Artist} ({active.PositionSec:F2}s)");
        return 0;
    }

    private static int RunProbe(AppConfig config)
    {
        List<HidDeviceInfo> devices = PixelBarHidClient.EnumerateDevices();
        Console.WriteLine($"枚举到 {devices.Count} 个 VID_2D99 HID 接口:");
        foreach (HidDeviceInfo d in devices)
        {
            Console.WriteLine($"  PID=0x{d.ProductId:X4} UsagePage=0x{d.UsagePage:X4} Usage=0x{d.Usage:X4} In={d.InputReportLength} Out={d.OutputReportLength} Path={d.Path}");
        }

        PixelEncryptionMode fallback = PixelBarHidClient.ParseMode(config.FallbackEncryptionMode);
        using var hid = new PixelBarHidClient(fallback, Console.WriteLine);
        if (!hid.Connect())
        {
            Console.WriteLine("连接花再 PixelBar (0xFF14) 失败。");
            return 1;
        }

        Console.WriteLine($"已共享打开目标设备: {hid.ConnectedPath}");
        var (gotReply, ver, enc, maxLen, raw) = hid.ProbeBaseInfo(1000);
        if (gotReply)
        {
            string encName = Enum.IsDefined(typeof(PixelEncryptionMode), (int)enc)
                ? ((PixelEncryptionMode)enc).ToString()
                : $"Unknown(0x{enc:X2})";
            Console.WriteLine($"CM_65 回包成功: Version={ver}, Encryption=0x{enc:X2} ({encName}), MaxLength={maxLen}");
            Console.WriteLine($"CM_65 Raw: {Convert.ToHexString(raw!)}");

            if (Enum.IsDefined(typeof(PixelEncryptionMode), (int)enc))
            {
                var mode = (PixelEncryptionMode)enc;
                var (gotE7, rawE7, decE7) = hid.ProbeTextLimits(mode, 1000);
                if (gotE7)
                {
                    Console.WriteLine($"CM_E7 回包成功: Raw={Convert.ToHexString(rawE7!)}");
                    Console.WriteLine($"CM_E7 Decrypted: {Convert.ToHexString(decE7!)}");
                }
                else
                {
                    Console.WriteLine("CM_E7 请求已发送但 1000ms 内未收到回包。");
                }
            }
        }
        else
        {
            Console.WriteLine($"CM_65 请求已发送但 1000ms 内未收到回包（通常是因为 TempoHub 正在后台读取同一端点），将使用默认加密模式: {fallback}");
        }
        return 0;
    }

    private static int RunTestText(AppConfig config, string text, string? encOverride)
    {
        PixelEncryptionMode mode = PixelBarHidClient.ParseMode(
            encOverride ?? (config.EncryptionMode == "auto" ? config.FallbackEncryptionMode : config.EncryptionMode));

        using var hid = new PixelBarHidClient(mode, Console.WriteLine);
        if (!hid.Connect())
        {
            Console.WriteLine("连接花再 PixelBar 失败。");
            return 1;
        }

        if (encOverride == null && config.EncryptionMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var probe = hid.ProbeBaseInfo(500);
            if (probe.GotReply && Enum.IsDefined(typeof(PixelEncryptionMode), (int)probe.EncryptionByte))
            {
                mode = (PixelEncryptionMode)probe.EncryptionByte;
                hid.SetEncryptionMode(mode);
            }
        }

        string cleaned = FormatForScreen(text, config);
        bool ok = hid.SendLyricLine(cleaned, out List<byte[]> frames);
        Console.WriteLine($"发送结果={ok}, 加密模式={mode}, 分包数={frames.Count}, 文本={cleaned}");
        for (int i = 0; i < frames.Count; i++)
        {
            Console.WriteLine($"  Frame[{i}]: {Convert.ToHexString(frames[i])}");
        }
        return ok ? 0 : 1;
    }

    private static async Task<int> RunTestLyricAsync(AppConfig config, string query)
    {
        string[] parts = query.Split('|', 2);
        string rawTitle = parts[0].Trim();
        string rawArtist = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        (string cleanTitle, string cleanArtist) = LyricsFetcher.NormalizeTrackInfo(rawTitle, rawArtist);
        var fetcher = new LyricsFetcher(config, Console.WriteLine);
        LyricTimeline tl = await fetcher.FetchAsync(rawTitle, rawArtist, 0);
        List<LyricLine> effective = tl.GetEffectiveLines(config.FilterCreditLines);

        Console.WriteLine($"清洗结果: Title=\"{cleanTitle}\" | Artist=\"{cleanArtist}\"");
        Console.WriteLine($"歌词源={tl.Source}, 含中文翻译={tl.HasTranslation}, 原始行数={tl.Lines.Count}, 有效行数={effective.Count}");
        foreach (LyricLine line in effective.Take(10))
        {
            string chosen = (config.PreferTranslation && !string.IsNullOrWhiteSpace(line.Translation))
                ? line.Translation
                : line.Text;
            string display = FormatForScreen(chosen, config);
            Console.WriteLine($"  [{line.TimeSec:F2}s] {display}");
        }
        return effective.Count > 0 ? 0 : 1;
    }

    private static async Task<int> RunSyncLoopAsync(
        AppConfig config,
        string? followProcess,
        bool consoleOutput,
        TrayApp? tray,
        CancellationTokenSource cts,
        Func<bool>? consumeForceRefresh)
    {
        string logPath = config.GetLogFilePath();
        void Log(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            if (consoleOutput)
            {
                Console.WriteLine(line);
            }
            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { }
        }

        try { File.WriteAllText(logPath, string.Empty); } catch { }

        Log($"PixelBarLyrics 启动 (Follow={followProcess ?? "无"}, Enc={config.EncryptionMode}/{config.FallbackEncryptionMode}, Offset={config.LyricOffsetMs}ms, Trans={config.PreferTranslation})");

        string? followProcClean = followProcess?.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        bool seenFollowProcess = false;
        if (!string.IsNullOrWhiteSpace(followProcClean))
        {
            DateTime waitUntil = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < waitUntil && !cts.IsCancellationRequested)
            {
                if (IsProcessRunning(followProcClean))
                {
                    seenFollowProcess = true;
                    break;
                }
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }
            if (!seenFollowProcess)
            {
                Log($"等待目标进程 {followProcClean} 超时（30秒内未启动），退出。");
                tray?.RequestExit();
                return 0;
            }
            Log($"已检测到目标进程 {followProcClean}，开始监听同步。");
        }

        PixelEncryptionMode initialEnc = PixelBarHidClient.ParseMode(
            config.EncryptionMode.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? config.FallbackEncryptionMode
                : config.EncryptionMode);

        using var hid = new PixelBarHidClient(initialEnc, Log);
        if (hid.Connect())
        {
            if (config.EncryptionMode.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                var probe = hid.ProbeBaseInfo(600);
                if (probe.GotReply && Enum.IsDefined(typeof(PixelEncryptionMode), (int)probe.EncryptionByte))
                {
                    hid.SetEncryptionMode((PixelEncryptionMode)probe.EncryptionByte);
                    Log($"自动探测加密模式成功: {(PixelEncryptionMode)probe.EncryptionByte}");
                }
                else
                {
                    Log($"自动探测未收到回包，使用默认加密模式: {hid.CurrentEncryptionMode}");
                }
            }
            else
            {
                Log($"已连接 HID，使用固定加密模式: {hid.CurrentEncryptionMode}");
            }
        }

        var smtc = new SmtcMonitor(config);
        var fetcher = new LyricsFetcher(config, Log);

        string currentTrackKey = string.Empty;
        string currentCleanDisplay = string.Empty;
        LyricTimeline? currentTimeline = null;
        CancellationTokenSource? fetchCts = null;
        string lastSentLine = string.Empty;
        DateTime lastProcessCheck = DateTime.UtcNow;
        object hidLock = new();
        long externalOverrideUntilTicks = 0;

        bool SendScreenText(string rawText, out string screenText, string logTag = "推送")
        {
            screenText = FormatForScreen(rawText, config);
            if (string.IsNullOrWhiteSpace(screenText))
            {
                return false;
            }

            lock (hidLock)
            {
                if (string.Equals(screenText, lastSentLine, StringComparison.Ordinal))
                {
                    return true;
                }

                if (hid.SendLyricLine(screenText, out _))
                {
                    lastSentLine = screenText;
                    Log($"[{logTag}] {screenText}");
                    return true;
                }
                return false;
            }
        }

        using LocalSubtitleServer? httpServer = config.EnableHttpApi
            ? new LocalSubtitleServer(
                config.HttpPort,
                onPushText: (rawText, source, ttlMs) =>
                {
                    if (tray != null && tray.IsPaused)
                    {
                        string preview = FormatForScreen(rawText, config);
                        return new ExternalTextResult(false, false, preview);
                    }

                    Interlocked.Exchange(ref externalOverrideUntilTicks, DateTime.UtcNow.AddMilliseconds(ttlMs).Ticks);
                    bool sent = SendScreenText(rawText, out string screenText, $"字幕:{source}");
                    tray?.UpdateStatus($"外部字幕 ({source})", $"接口: 127.0.0.1:{config.HttpPort}");
                    return new ExternalTextResult(true, sent, screenText);
                },
                onClear: () =>
                {
                    Interlocked.Exchange(ref externalOverrideUntilTicks, 0);
                    lock (hidLock)
                    {
                        lastSentLine = string.Empty;
                    }
                    Log("[字幕] 收到清除指令，恢复音乐歌词监听。");
                },
                getStatus: () =>
                {
                    bool active = DateTime.UtcNow.Ticks < Interlocked.Read(ref externalOverrideUntilTicks);
                    string last;
                    lock (hidLock) { last = lastSentLine; }
                    return new ServerStatusSnapshot(hid.IsConnected, tray?.IsPaused ?? false, active, last);
                },
                Log)
            : null;

        if (httpServer != null)
        {
            bool started = httpServer.Start();
            tray?.UpdateApiInfo(started ? $"字幕接口: 127.0.0.1:{config.HttpPort}" : "字幕接口: 启动失败");
        }
        else
        {
            tray?.UpdateApiInfo("字幕接口: 未启用");
        }

        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (seenFollowProcess && (DateTime.UtcNow - lastProcessCheck).TotalSeconds >= 2)
                {
                    lastProcessCheck = DateTime.UtcNow;
                    if (!IsProcessRunning(followProcClean!))
                    {
                        Log($"目标进程 {followProcClean} 已退出，歌词同步随之退出。");
                        break;
                    }
                }

                if (consumeForceRefresh != null && consumeForceRefresh())
                {
                    lock (hidLock)
                    {
                        lastSentLine = string.Empty;
                    }
                }

                // 外部视频字幕接管期间，暂不让 SMTC 覆盖屏幕
                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref externalOverrideUntilTicks))
                {
                    await Task.Delay(config.PollIntervalMs, cts.Token);
                    continue;
                }

                MediaSnapshot? snap = await smtc.GetActiveSnapshotAsync();
                if (snap == null || string.IsNullOrWhiteSpace(snap.Title))
                {
                    await Task.Delay(config.PollIntervalMs, cts.Token);
                    continue;
                }

                string trackKey = $"{snap.AppId}|{snap.Title}|{snap.Artist}";
                if (!string.Equals(trackKey, currentTrackKey, StringComparison.Ordinal))
                {
                    currentTrackKey = trackKey;
                    lock (hidLock)
                    {
                        lastSentLine = string.Empty;
                    }
                    currentTimeline = null;

                    (string cleanTitle, string cleanArtist) = LyricsFetcher.NormalizeTrackInfo(snap.Title, snap.Artist);
                    currentCleanDisplay = FormatForScreen(LyricsFetcher.FormatTrackDisplay(cleanTitle, cleanArtist), config);

                    fetchCts?.Cancel();
                    fetchCts?.Dispose();
                    fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    CancellationToken localToken = fetchCts.Token;

                    Log($"切歌 [{snap.AppId}]: {cleanTitle} - {cleanArtist} ({snap.DurationSec:F0}s)");
                    tray?.UpdateStatus(currentCleanDisplay, "词源: 检索中...");

                    string t = snap.Title;
                    string a = snap.Artist;
                    double d = snap.DurationSec;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            LyricTimeline tl = await fetcher.FetchAsync(t, a, d, localToken);
                            if (!localToken.IsCancellationRequested && string.Equals(currentTrackKey, trackKey, StringComparison.Ordinal))
                            {
                                currentTimeline = tl;
                                string transTag = tl.HasTranslation ? "含翻译" : "原文";
                                Log($"歌词就绪: source={tl.Source} ({transTag}), lines={tl.Lines.Count}");
                                tray?.UpdateStatus(currentCleanDisplay, $"词源: {tl.Source} ({transTag}, {tl.Lines.Count}行)");
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            Log($"异步获取歌词失败: {ex.Message}");
                        }
                    }, localToken);
                }

                if (tray != null && tray.IsPaused)
                {
                    await Task.Delay(config.PollIntervalMs, cts.Token);
                    continue;
                }

                if (snap.IsPlaying)
                {
                    double pos = snap.PositionSec + config.LyricOffsetMs / 1000.0;
                    string? chosen = currentTimeline?.FindCurrentLine(
                        pos,
                        config.PreferTranslation,
                        config.FilterCreditLines);

                    // 切歌加载中或前奏空白期，先显示干净的“歌名 - 歌手”
                    if (string.IsNullOrWhiteSpace(chosen) && config.ShowTitleOnIntro && !string.IsNullOrWhiteSpace(currentCleanDisplay))
                    {
                        chosen = currentCleanDisplay;
                    }

                    if (!string.IsNullOrWhiteSpace(chosen))
                    {
                        SendScreenText(chosen, out _, $"推送 ({pos:F1}s)");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"主循环异常: {ex.Message}");
            }

            try
            {
                await Task.Delay(config.PollIntervalMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        tray?.RequestExit();
        return 0;
    }

    private static string FormatForScreen(string rawText, AppConfig config)
    {
        string text = config.ConvertToSimplified
            ? LyricsFetcher.ToSimplifiedIfChinese(rawText)
            : rawText;
        return LyricsFetcher.SanitizeForPixelBar(text);
    }

    private static bool IsProcessRunning(string processNameWithoutExe)
    {
        return Process.GetProcessesByName(processNameWithoutExe).Length > 0;
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
