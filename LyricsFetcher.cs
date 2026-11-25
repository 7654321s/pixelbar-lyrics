using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PixelBarLyrics;

/// <summary>
/// 单条时间戳歌词行（可选带中文翻译）
/// </summary>
public sealed record LyricLine(double TimeSec, string Text, string? Translation = null);

/// <summary>
/// 某首歌曲的完整歌词时间轴
/// </summary>
public sealed partial record LyricTimeline(string Source, List<LyricLine> Lines, bool HasTranslation = false)
{
    /// <summary>
    /// 获取有效歌词列表（根据需要过滤制作人员与装饰行；若过滤后为空则安全退回原列表）
    /// </summary>
    public List<LyricLine> GetEffectiveLines(bool filterCredits)
    {
        if (!filterCredits || Lines.Count == 0)
        {
            return Lines;
        }

        var filtered = new List<LyricLine>(Lines.Count);
        foreach (LyricLine line in Lines)
        {
            if (LyricsFetcher.IsCreditOrNoiseLine(line.Text, line.TimeSec))
            {
                continue;
            }
            filtered.Add(line);
        }

        return filtered.Count > 0 ? filtered : Lines;
    }

    /// <summary>
    /// 根据播放进度定位当前歌词（支持翻译优先；早于第一句歌词时返回 null 以便前奏显示歌名）
    /// </summary>
    public string? FindCurrentLine(double positionSec, bool preferTranslation, bool filterCredits)
    {
        List<LyricLine> list = GetEffectiveLines(filterCredits);
        if (list.Count == 0)
        {
            return null;
        }

        int lo = 0;
        int hi = list.Count - 1;
        int ans = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (list[mid].TimeSec <= positionSec)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (ans < 0)
        {
            return null;
        }

        LyricLine hit = list[ans];
        if (preferTranslation && !string.IsNullOrWhiteSpace(hit.Translation))
        {
            return hit.Translation;
        }
        return hit.Text;
    }
}

/// <summary>
/// 多源歌词检索、翻译合并、标题清洗与点阵屏字符净化引擎
/// </summary>
public sealed partial class LyricsFetcher
{
    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly Dictionary<string, LyricTimeline> _memCache = new(StringComparer.Ordinal);

    public LyricsFetcher(AppConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log ?? (_ => { });

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(6)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PixelBarLyrics/2.0 (+https://github.com)");
    }

    public async Task<LyricTimeline> FetchAsync(string rawTitle, string rawArtist, double durationSec, CancellationToken ct = default)
    {
        (string cleanTitle, string cleanArtist) = NormalizeTrackInfo(rawTitle, rawArtist);
        string cacheKey = ComputeCacheKey(cleanTitle, cleanArtist);

        if (_memCache.TryGetValue(cacheKey, out LyricTimeline? memHit))
        {
            return memHit;
        }

        LyricTimeline? diskHit = TryLoadFromDisk(cacheKey);
        if (diskHit != null)
        {
            _memCache[cacheKey] = diskHit;
            return diskHit;
        }

        List<(string Title, string Artist)> candidates = BuildSearchCandidates(cleanTitle, cleanArtist);
        bool preferNeteaseFirst = _config.PreferTranslation && LooksForeign(cleanTitle, cleanArtist);

        LyricTimeline? result = null;
        LyricTimeline? neteaseNoTrans = null;

        if (preferNeteaseFirst)
        {
            // 外语歌开启翻译优先时，先查网易云（自带 tlyric 时间轴中文翻译）
            foreach (var (candTitle, candArtist) in candidates)
            {
                LyricTimeline? ne = await TrySearchNeteaseAsync(candTitle, candArtist, durationSec, ct);
                if (ne != null && ne.Lines.Count > 0)
                {
                    if (ne.HasTranslation)
                    {
                        result = ne;
                        break;
                    }
                    neteaseNoTrans ??= ne;
                }
            }

            // 若网易云无翻译版本，补查 LRCLIB 获取更全的外语同步词库
            if (result == null)
            {
                result = await TrySearchLrcLibCandidatesAsync(candidates, durationSec, ct);
                result ??= neteaseNoTrans;
            }
        }
        else
        {
            // 中文歌或偏好原文时，优先 LRCLIB，再兜底网易云
            result = await TrySearchLrcLibCandidatesAsync(candidates, durationSec, ct);
            if (result == null)
            {
                foreach (var (candTitle, candArtist) in candidates)
                {
                    LyricTimeline? ne = await TrySearchNeteaseAsync(candTitle, candArtist, durationSec, ct);
                    if (ne != null && ne.Lines.Count > 0)
                    {
                        result = ne;
                        break;
                    }
                }
            }
        }

        if (result != null && result.Lines.Count > 0)
        {
            SaveToDisk(cacheKey, result);
            _memCache[cacheKey] = result;
            return result;
        }

        // 无同步歌词时返回净化后的“歌名 - 歌手”
        var fallbackLines = new List<LyricLine>();
        if (_config.FallbackToTitle && !string.IsNullOrWhiteSpace(cleanTitle))
        {
            string display = FormatTrackDisplay(cleanTitle, cleanArtist);
            if (!string.IsNullOrWhiteSpace(display))
            {
                fallbackLines.Add(new LyricLine(0, display));
            }
        }
        var fallback = new LyricTimeline("fallback", fallbackLines, false);
        _memCache[cacheKey] = fallback;
        return fallback;
    }

    private async Task<LyricTimeline?> TrySearchLrcLibCandidatesAsync(
        List<(string Title, string Artist)> candidates,
        double durationSec,
        CancellationToken ct)
    {
        foreach (var (candTitle, candArtist) in candidates)
        {
            LyricTimeline? exact = await TryFetchLrcLibExactAsync(candTitle, candArtist, durationSec, ct);
            if (exact != null && exact.Lines.Count > 0)
            {
                return exact;
            }

            LyricTimeline? searched = await TrySearchLrcLibAsync(candTitle, candArtist, durationSec, ct);
            if (searched != null && searched.Lines.Count > 0)
            {
                return searched;
            }
        }
        return null;
    }

    /// <summary>
    /// 清洗 YouTube Music / Limusic 中常见的装饰符号、金句、官方后缀与视频标记
    /// </summary>
    public static (string Title, string Artist) NormalizeTrackInfo(string rawTitle, string rawArtist)
    {
        string title = (rawTitle ?? string.Empty).Trim();
        string artist = (rawArtist ?? string.Empty).Trim();

        // 清洗歌手末尾 "- Topic" / "VEVO" / "Official"
        artist = TopicSuffixRegex().Replace(artist, string.Empty).Trim();
        if (artist.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase) && artist.Length > 4)
        {
            artist = artist[..^4].Trim();
        }
        artist = OfficialWordSuffixRegex().Replace(artist, string.Empty).Trim();

        // 去除装饰性框括号金句/标签，如 『...』、「...」、【...】
        title = DecorativeQuoteRegex().Replace(title, " ");
        // 去除官方视频/音频/MV/纯享/翻唱等括号噪音
        title = NoiseBracketRegex().Replace(title, " ");
        title = MultiSpaceRegex().Replace(title, " ").Trim(' ', '-', '—', '–', '|', '/');

        // 若 artist 为空且 title 符合 "歌手 - 歌名"
        if (string.IsNullOrWhiteSpace(artist))
        {
            int dashIdx = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0 && dashIdx < title.Length - 3)
            {
                artist = title[..dashIdx].Trim();
                title = title[(dashIdx + 3)..].Trim();
            }
        }

        // 若经过清洗后 title 仍带括注空括号
        title = EmptyBracketRegex().Replace(title, " ");
        title = MultiSpaceRegex().Replace(title, " ").Trim(' ', '-', '—', '–', '|', '/');
        return (title, artist);
    }

    /// <summary>
    /// 生成多级搜词候选（完整清洗词 -> 主歌手降级 -> 拆分长标题后半段曲名）
    /// </summary>
    public static List<(string Title, string Artist)> BuildSearchCandidates(string cleanTitle, string cleanArtist)
    {
        var list = new List<(string Title, string Artist)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string t, string a)
        {
            t = t.Trim();
            a = a.Trim();
            if (string.IsNullOrWhiteSpace(t)) return;
            string key = $"{t}|{a}";
            if (seen.Add(key))
            {
                list.Add((t, a));
            }
        }

        Add(cleanTitle, cleanArtist);

        string primaryArtist = ExtractPrimaryArtist(cleanArtist);
        if (!string.IsNullOrWhiteSpace(primaryArtist) &&
            !string.Equals(primaryArtist, cleanArtist, StringComparison.OrdinalIgnoreCase))
        {
            Add(cleanTitle, primaryArtist);
        }

        // 针对 YouTube 搬运标题（如 "银翼杀手 - 精卫" 或 "作品名 / 曲名"），尝试拆出真实曲名
        string[] parts = TitleSplitRegex().Split(cleanTitle);
        if (parts.Length >= 2)
        {
            string second = StripLooseSubParens(parts[^1]).Trim();
            string first = StripLooseSubParens(parts[0]).Trim();
            if (second.Length >= 2)
            {
                Add(second, string.Empty);
                if (!string.IsNullOrWhiteSpace(primaryArtist))
                {
                    Add(second, primaryArtist);
                }
            }
            if (first.Length >= 2)
            {
                Add(first, string.Empty);
            }
        }

        // 如果还是带普通圆括号附注（如 "精卫（万物终归向海）"），增加去括号纯歌名候选
        string strippedTitle = StripLooseSubParens(cleanTitle).Trim();
        if (!string.IsNullOrWhiteSpace(strippedTitle) &&
            !string.Equals(strippedTitle, cleanTitle, StringComparison.OrdinalIgnoreCase))
        {
            Add(strippedTitle, primaryArtist);
            Add(strippedTitle, string.Empty);
        }

        return list;
    }

    public static string ExtractPrimaryArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        string[] parts = ArtistSplitRegex().Split(artist.Trim());
        return parts.Length > 0 ? parts[0].Trim() : artist.Trim();
    }

    private static string StripLooseSubParens(string s)
    {
        return AnyChineseOrAsciiParenRegex().Replace(s, " ").Trim(' ', '-', '—', '–');
    }

    public static string FormatTrackDisplay(string title, string artist)
    {
        string cleanT = SanitizeForPixelBar(title);
        string cleanA = SanitizeForPixelBar(artist);
        if (string.IsNullOrWhiteSpace(cleanT)) return string.Empty;
        return string.IsNullOrWhiteSpace(cleanA) ? cleanT : $"{cleanT} - {cleanA}";
    }

    /// <summary>
    /// 判断是否疑似外语歌曲（含日文假名/韩文，或不含汉字）
    /// </summary>
    public static bool LooksForeign(string title, string artist)
    {
        string combined = $"{title} {artist}";
        bool hasCjk = false;
        foreach (char c in combined)
        {
            if ((c >= '\u3040' && c <= '\u30FF') || (c >= '\uAC00' && c <= '\uD7AF'))
            {
                return true; // 含日文假名或韩文谚文
            }
            if (c >= '\u4E00' && c <= '\u9FFF')
            {
                hasCjk = true;
            }
        }
        return !hasCjk;
    }

    private async Task<LyricTimeline?> TryFetchLrcLibExactAsync(string title, string artist, double durationSec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
        {
            return null;
        }

        try
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            if (durationSec > 5)
            {
                url += $"&duration={(int)Math.Round(durationSec)}";
            }

            using HttpResponseMessage resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("syncedLyrics", out JsonElement synced) &&
                synced.ValueKind == JsonValueKind.String)
            {
                List<LyricLine> lines = ParseLrc(synced.GetString());
                if (lines.Count > 0)
                {
                    return new LyricTimeline("lrclib-get", lines, false);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"LRCLIB get 异常: {ex.Message}");
        }
        return null;
    }

    private async Task<LyricTimeline?> TrySearchLrcLibAsync(string title, string artist, double durationSec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            string q = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            string url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(q)}";
            using HttpResponseMessage resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestLrc = null;
            int bestScore = int.MinValue;

            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("syncedLyrics", out JsonElement synced) ||
                    synced.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? lrc = synced.GetString();
                if (string.IsNullOrWhiteSpace(lrc))
                {
                    continue;
                }

                string itemTrack = item.TryGetProperty("trackName", out var tn) ? (tn.GetString() ?? "") : "";
                string itemArtist = item.TryGetProperty("artistName", out var an) ? (an.GetString() ?? "") : "";
                double itemDur = item.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;

                int score = ScoreCandidate(title, artist, durationSec, itemTrack, itemArtist, itemDur);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLrc = lrc;
                }
            }

            if (bestLrc != null && bestScore >= 20)
            {
                List<LyricLine> lines = ParseLrc(bestLrc);
                if (lines.Count > 0)
                {
                    return new LyricTimeline("lrclib-search", lines, false);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"LRCLIB search 异常: {ex.Message}");
        }
        return null;
    }

    private async Task<LyricTimeline?> TrySearchNeteaseAsync(string title, string artist, double durationSec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        try
        {
            string kw = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            string searchUrl = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(kw)}&type=1&limit=8&offset=0";
            using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            req.Headers.Referrer = new Uri("https://music.163.com/");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("result", out var resEl) ||
                !resEl.TryGetProperty("songs", out var songsEl) ||
                songsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var scoredSongs = new List<(long Id, int Score)>();
            foreach (JsonElement song in songsEl.EnumerateArray())
            {
                long id = song.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                if (id <= 0) continue;

                string sName = song.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                double sDur = song.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                    ? dEl.GetDouble() / 1000.0
                    : 0;

                string sArtist = "";
                if (song.TryGetProperty("artists", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
                {
                    sArtist = string.Join(" ", aArr.EnumerateArray()
                        .Select(a => a.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "")
                        .Where(x => x.Length > 0));
                }

                int score = ScoreCandidate(title, artist, durationSec, sName, sArtist, sDur);
                if (score >= 20)
                {
                    scoredSongs.Add((id, score));
                }
            }

            scoredSongs.Sort((a, b) => b.Score.CompareTo(a.Score));

            LyricTimeline? firstWithoutTrans = null;
            foreach (var (songId, _) in scoredSongs.Take(3))
            {
                string lyricUrl = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1";
                using var lreq = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
                lreq.Headers.Referrer = new Uri("https://music.163.com/");

                using HttpResponseMessage lresp = await _http.SendAsync(lreq, ct);
                if (!lresp.IsSuccessStatusCode)
                {
                    continue;
                }

                using JsonDocument ldoc = JsonDocument.Parse(await lresp.Content.ReadAsStringAsync(ct));
                if (!ldoc.RootElement.TryGetProperty("lrc", out var lrcObj) ||
                    !lrcObj.TryGetProperty("lyric", out var lrcText) ||
                    lrcText.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                List<LyricLine> mainLines = ParseLrc(lrcText.GetString());
                if (mainLines.Count == 0)
                {
                    continue;
                }

                bool hasTrans = false;
                if (ldoc.RootElement.TryGetProperty("tlyric", out var tObj) &&
                    tObj.TryGetProperty("lyric", out var tText) &&
                    tText.ValueKind == JsonValueKind.String)
                {
                    List<LyricLine> transLines = ParseLrc(tText.GetString());
                    if (transLines.Count > 0)
                    {
                        (mainLines, hasTrans) = MergeTranslations(mainLines, transLines);
                    }
                }

                var timeline = new LyricTimeline(hasTrans ? "netease+tlyric" : "netease", mainLines, hasTrans);
                if (hasTrans || !_config.PreferTranslation)
                {
                    return timeline;
                }
                firstWithoutTrans ??= timeline;
            }

            return firstWithoutTrans;
        }
        catch (Exception ex)
        {
            _log($"网易云歌词查询异常: {ex.Message}");
        }
        return null;
    }

    private static (List<LyricLine> Merged, bool HasTranslation) MergeTranslations(
        List<LyricLine> mainLines,
        List<LyricLine> transLines)
    {
        var merged = new List<LyricLine>(mainLines.Count);
        int transCount = 0;

        foreach (LyricLine m in mainLines)
        {
            string? bestTrans = null;
            double bestDiff = 0.45;
            foreach (LyricLine t in transLines)
            {
                double diff = Math.Abs(t.TimeSec - m.TimeSec);
                if (diff <= bestDiff &&
                    !string.IsNullOrWhiteSpace(t.Text) &&
                    !string.Equals(t.Text, m.Text, StringComparison.OrdinalIgnoreCase))
                {
                    bestDiff = diff;
                    bestTrans = t.Text;
                }
            }

            if (bestTrans != null)
            {
                transCount++;
            }
            merged.Add(m with { Translation = bestTrans });
        }

        return (merged, transCount > 0);
    }

    private static int ScoreCandidate(
        string targetTitle,
        string targetArtist,
        double targetDur,
        string candTitle,
        string candArtist,
        double candDur)
    {
        int score = 0;
        string t1 = Simplify(targetTitle);
        string t2 = Simplify(candTitle);
        if (string.IsNullOrEmpty(t1) || string.IsNullOrEmpty(t2))
        {
            return -100;
        }

        if (t1 == t2) score += 60;
        else if (t2.Contains(t1, StringComparison.Ordinal) || t1.Contains(t2, StringComparison.Ordinal)) score += 35;

        if (!string.IsNullOrWhiteSpace(targetArtist) && !string.IsNullOrWhiteSpace(candArtist))
        {
            string a1 = Simplify(targetArtist);
            string a2 = Simplify(candArtist);
            if (a1 == a2) score += 40;
            else if (a2.Contains(a1, StringComparison.Ordinal) || a1.Contains(a2, StringComparison.Ordinal)) score += 25;
        }

        if (targetDur > 10 && candDur > 10)
        {
            double diff = Math.Abs(targetDur - candDur);
            if (diff <= 2.0) score += 25;
            else if (diff <= 6.0) score += 15;
            else if (diff > 20.0) score -= 25;
        }

        return score;
    }

    private static string Simplify(string s)
    {
        return NonWordRegex().Replace(s.ToLowerInvariant(), string.Empty);
    }

    /// <summary>
    /// 判断是否为作词/作曲/编曲等制作人员行或无意义装饰行
    /// </summary>
    public static bool IsCreditOrNoiseLine(string text, double timeSec)
    {
        string trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return true;
        }

        if (CreditLineRegex().IsMatch(trimmed) || PureSymbolOrInstrumentalRegex().IsMatch(trimmed))
        {
            return true;
        }

        // 前 12 秒内常见的静态抬头行，如 "歌名 - 歌手" 或 "Powered by ..."
        if (timeSec <= 12.0 && HeaderNoiseRegex().IsMatch(trimmed))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析标准 LRC 文本（支持多时间戳、3位毫秒及 [offset:ms] 标签）
    /// </summary>
    public static List<LyricLine> ParseLrc(string? lrcText)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrcText))
        {
            return result;
        }

        double offsetSec = 0;
        var offsetMatch = LrcOffsetRegex().Match(lrcText);
        if (offsetMatch.Success && int.TryParse(offsetMatch.Groups[1].Value, out int offsetMs))
        {
            offsetSec = offsetMs / 1000.0;
        }

        foreach (string rawLine in lrcText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            MatchCollection matches = LrcTimestampRegex().Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            string text = LrcTimestampRegex().Replace(rawLine, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value);
                int sec = int.Parse(m.Groups[2].Value);
                double frac = 0;
                if (m.Groups[3].Success && m.Groups[3].Value.Length > 0)
                {
                    string f = m.Groups[3].Value.PadRight(3, '0')[..3];
                    frac = int.Parse(f) / 1000.0;
                }

                double timeSec = Math.Max(0, min * 60 + sec + frac - offsetSec);
                result.Add(new LyricLine(timeSec, text));
            }
        }

        result.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
        return result;
    }

    /// <summary>
    /// 净化点阵屏输出字符：剔除 Emoji 代理对、变体选择符、零宽字符，合并多余空格
    /// </summary>
    public static string SanitizeForPixelBar(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsSurrogate(c)) continue;
            if (c is '\uFE0E' or '\uFE0F' or '\uFEFF' or '\u200B' or '\u200C' or '\u200D' or '♪' or '♫' or '♬') continue;
            sb.Append(c == '\u3000' ? ' ' : c);
        }

        return MultiSpaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    private LyricTimeline? TryLoadFromDisk(string cacheKey)
    {
        try
        {
            string file = Path.Combine(_config.GetCacheDirectoryPath(), $"v2_{cacheKey}.json");
            if (!File.Exists(file))
            {
                return null;
            }
            return JsonSerializer.Deserialize<LyricTimeline>(File.ReadAllText(file));
        }
        catch
        {
            return null;
        }
    }

    private void SaveToDisk(string cacheKey, LyricTimeline timeline)
    {
        try
        {
            string dir = _config.GetCacheDirectoryPath();
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"v2_{cacheKey}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(timeline));
        }
        catch
        {
            // 忽略缓存写入失败
        }
    }

    private static string ComputeCacheKey(string title, string artist)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{title.ToLowerInvariant()}|{artist.ToLowerInvariant()}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[]? lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);

    /// <summary>
    /// 将繁体中文转换为简体中文；若包含日文平假名/片假名则原样保留，避免误伤日语汉字
    /// </summary>
    public static string ToSimplifiedIfChinese(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (char c in text)
        {
            if (c >= '\u3040' && c <= '\u30FF')
            {
                return text;
            }
        }

        string mapped = text;
        int len = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (len > 0)
        {
            char[] buf = new char[len];
            int written = LCMapStringEx("zh-CN", LCMAP_SIMPLIFIED_CHINESE, text, text.Length, buf, buf.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (written > 0)
            {
                mapped = new string(buf, 0, written);
            }
        }

        return mapped
            .Replace('妳', '你')
            .Replace('牠', '它')
            .Replace('檯', '台')
            .Replace('臺', '台')
            .Replace('裡', '里')
            .Replace('裏', '里')
            .Replace('著', '着')
            .Replace('麽', '么');
    }

    [GeneratedRegex(@"\s*-\s*Topic$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicSuffixRegex();

    [GeneratedRegex(@"\s+(Official|Music\s+Channel)$", RegexOptions.IgnoreCase)]
    private static partial Regex OfficialWordSuffixRegex();

    [GeneratedRegex(@"[【『「][^】』」]*[】』」]")]
    private static partial Regex DecorativeQuoteRegex();

    [GeneratedRegex(@"[\(\[（](?:official\s*(?:music\s*)?(?:video|audio|lyric\s*video|mv)|lyric\s*video|lyrics?|mv|hd|4k|live|cover|remix|ver\.?|full|remastered(?:\s*\d+)?|动态歌词(?:mv)?|完整版|高清|纯享版?|无损|翻唱)[\)\]）]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBracketRegex();

    [GeneratedRegex(@"[\(\[（]\s*[\)\]）]")]
    private static partial Regex EmptyBracketRegex();

    [GeneratedRegex(@"[\(\（][^\)\）]{1,40}[\)\）]")]
    private static partial Regex AnyChineseOrAsciiParenRegex();

    [GeneratedRegex(@"\s+[-—–|/]\s+")]
    private static partial Regex TitleSplitRegex();

    [GeneratedRegex(@"\s*(?:和|/|、|&|,|feat\.?|ft\.?|×|\s+x\s+)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplitRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[\W_]+")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\[offset:([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LrcOffsetRegex();

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex LrcTimestampRegex();

    [GeneratedRegex(@"^(?:作词|作詞|作曲|编曲|編曲|词|詞|曲|填词|填詞|谱曲|譜曲|演唱|原唱|翻唱|歌手|制作人|製作人|制作|製作|混音|母带|母帶|录音|錄音|和声|和聲|吉他|贝斯|貝斯|鼓|键盘|鍵盤|弦乐|弦樂|小提琴|大提琴|笛子|二胡|古筝|古箏|钢琴|鋼琴|监制|監製|统筹|統籌|策划|策劃|出品|发行|發行|文案|美工|PV|OP|SP|Lyricist|Composer|Arranger|Producer|Vocal|Mixing|Mastering|Guitar|Bass|Drums|Violin|Chorus)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex CreditLineRegex();

    [GeneratedRegex(@"^(?:纯音乐[，,\s]*请欣赏|純音樂[，,\s]*請欣賞|[\-\*\/~=_\s]+)$")]
    private static partial Regex PureSymbolOrInstrumentalRegex();

    [GeneratedRegex(@"^(?:Powered\s+by|歌词由.+提供)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderNoiseRegex();
}
