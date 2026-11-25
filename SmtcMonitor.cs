using System.Diagnostics;
using Windows.Media.Control;

namespace PixelBarLyrics;

/// <summary>
/// 当前系统媒体播放快照
/// </summary>
public sealed record MediaSnapshot(
    string AppId,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    double PositionSec,
    double DurationSec);

/// <summary>
/// Windows SMTC 系统媒体会话监听器，支持优先匹配 Limusic / YouTube Music 并对播放进度做平滑插值
/// </summary>
public sealed class SmtcMonitor
{
    private readonly AppConfig _config;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    // 虚拟进度追踪状态（防止某些播放器不高频刷新 TimelineProperties）
    private string _trackKey = string.Empty;
    private double _anchorPositionSec;
    private long _anchorTimestamp;
    private double _lastRawPositionSec = -1;
    private DateTimeOffset _lastTimelineUpdated;

    public SmtcMonitor(AppConfig config)
    {
        _config = config;
    }

    public async Task InitializeAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
    }

    /// <summary>
    /// 列出所有当前活跃的 SMTC 会话，用于诊断排查
    /// </summary>
    public async Task<List<MediaSnapshot>> GetAllSnapshotsAsync()
    {
        await InitializeAsync();
        var list = new List<MediaSnapshot>();
        if (_manager == null)
        {
            return list;
        }

        foreach (var session in _manager.GetSessions())
        {
            try
            {
                var pb = session.GetPlaybackInfo();
                var tl = session.GetTimelineProperties();
                var mp = await session.TryGetMediaPropertiesAsync();
                bool isPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                list.Add(new MediaSnapshot(
                    session.SourceAppUserModelId ?? string.Empty,
                    mp?.Title ?? string.Empty,
                    mp?.Artist ?? string.Empty,
                    mp?.AlbumTitle ?? string.Empty,
                    isPlaying,
                    tl.Position.TotalSeconds,
                    tl.EndTime.TotalSeconds));
            }
            catch
            {
                // 忽略瞬时失效的会话
            }
        }
        return list;
    }

    /// <summary>
    /// 获取优先级最高的有效播放会话及其插值后的实时进度
    /// </summary>
    public async Task<MediaSnapshot?> GetActiveSnapshotAsync()
    {
        await InitializeAsync();
        if (_manager == null)
        {
            return null;
        }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = _manager.GetSessions();
        if (sessions.Count == 0)
        {
            return null;
        }

        GlobalSystemMediaTransportControlsSession? best = SelectBestSession(sessions);
        if (best == null)
        {
            return null;
        }

        try
        {
            var pb = best.GetPlaybackInfo();
            var tl = best.GetTimelineProperties();
            var mp = await best.TryGetMediaPropertiesAsync();
            if (mp == null)
            {
                return null;
            }

            string appId = best.SourceAppUserModelId ?? string.Empty;
            string title = (mp.Title ?? string.Empty).Trim();
            string artist = (mp.Artist ?? string.Empty).Trim();
            string album = (mp.AlbumTitle ?? string.Empty).Trim();
            bool isPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double durationSec = tl.EndTime.TotalSeconds;
            double rawPosSec = Math.Max(0, tl.Position.TotalSeconds);

            double computedPos = ComputeInterpolatedPosition(
                $"{appId}|{title}|{artist}",
                isPlaying,
                rawPosSec,
                durationSec,
                tl.LastUpdatedTime);

            return new MediaSnapshot(appId, title, artist, album, isPlaying, computedPos, durationSec);
        }
        catch
        {
            return null;
        }
    }

    private GlobalSystemMediaTransportControlsSession? SelectBestSession(
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions)
    {
        GlobalSystemMediaTransportControlsSession? current = _manager?.GetCurrentSession();

        // 1. 先按 PreferredApps 顺序找正在播放的会话
        foreach (string pref in _config.PreferredApps)
        {
            var matchPlaying = sessions.FirstOrDefault(s =>
                (s.SourceAppUserModelId ?? string.Empty).Contains(pref, StringComparison.OrdinalIgnoreCase) &&
                SafeIsPlaying(s));
            if (matchPlaying != null)
            {
                return matchPlaying;
            }
        }

        // 2. 若允许非白名单播放器，则找任意正在播放的会话（优先系统当前会话）
        if (!_config.WhitelistOnly)
        {
            if (current != null && SafeIsPlaying(current))
            {
                return current;
            }

            var anyPlaying = sessions.FirstOrDefault(SafeIsPlaying);
            if (anyPlaying != null)
            {
                return anyPlaying;
            }
        }

        // 3. 均未播放时，优先返回处于暂停状态的 PreferredApps 会话
        foreach (string pref in _config.PreferredApps)
        {
            var matchAny = sessions.FirstOrDefault(s =>
                (s.SourceAppUserModelId ?? string.Empty).Contains(pref, StringComparison.OrdinalIgnoreCase));
            if (matchAny != null)
            {
                return matchAny;
            }
        }

        return _config.WhitelistOnly ? null : (current ?? sessions.FirstOrDefault());
    }

    private static bool SafeIsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private double ComputeInterpolatedPosition(
        string newTrackKey,
        bool isPlaying,
        double rawPosSec,
        double durationSec,
        DateTimeOffset lastUpdatedTime)
    {
        long nowTs = Stopwatch.GetTimestamp();

        if (!string.Equals(_trackKey, newTrackKey, StringComparison.Ordinal))
        {
            _trackKey = newTrackKey;
            _anchorPositionSec = rawPosSec;
            _anchorTimestamp = nowTs;
            _lastRawPositionSec = rawPosSec;
            _lastTimelineUpdated = lastUpdatedTime;
        }
        else
        {
            // 当播放器上报了新的时间戳或发生明显拖拽跳变（> 1.5s）时校准基准
            double predicted = _anchorPositionSec + (isPlaying ? Stopwatch.GetElapsedTime(_anchorTimestamp, nowTs).TotalSeconds : 0);
            bool timelineUpdatedChanged = lastUpdatedTime != _lastTimelineUpdated && lastUpdatedTime > DateTimeOffset.MinValue;
            bool jumped = Math.Abs(rawPosSec - _lastRawPositionSec) > 0.25 && Math.Abs(rawPosSec - predicted) > 1.5;

            if (timelineUpdatedChanged || jumped)
            {
                _anchorPositionSec = rawPosSec;
                _anchorTimestamp = nowTs;
                _lastRawPositionSec = rawPosSec;
                _lastTimelineUpdated = lastUpdatedTime;
            }
            else if (!isPlaying)
            {
                // 暂停时冻结本地基准到实际汇报位置或当前预测位置
                _anchorPositionSec = rawPosSec > 0 ? rawPosSec : predicted;
                _anchorTimestamp = nowTs;
            }
        }

        double result = _anchorPositionSec;
        if (isPlaying)
        {
            // 若 WinRT 自带的 LastUpdatedTime 较新鲜（5 分钟内），优先参考它，否则用本地 Stopwatch 递推
            if (lastUpdatedTime > DateTimeOffset.UtcNow.AddMinutes(-5) && lastUpdatedTime <= DateTimeOffset.UtcNow.AddSeconds(5))
            {
                double winrtPos = rawPosSec + Math.Max(0, (DateTimeOffset.UtcNow - lastUpdatedTime).TotalSeconds);
                // 与本地基准差值在合理范围内采用 WinRT 墙钟插值，确保无缝精准
                if (Math.Abs(winrtPos - (_anchorPositionSec + Stopwatch.GetElapsedTime(_anchorTimestamp, nowTs).TotalSeconds)) < 3.0)
                {
                    result = winrtPos;
                }
                else
                {
                    result = _anchorPositionSec + Stopwatch.GetElapsedTime(_anchorTimestamp, nowTs).TotalSeconds;
                }
            }
            else
            {
                result = _anchorPositionSec + Stopwatch.GetElapsedTime(_anchorTimestamp, nowTs).TotalSeconds;
            }
        }

        if (durationSec > 1.0 && result > durationSec)
        {
            result = durationSec;
        }
        return Math.Max(0, result);
    }
}
