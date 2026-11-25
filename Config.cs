using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelBarLyrics;

/// <summary>
/// 应用配置模型，保存于程序同目录下的 config.json
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// 协议加密模式：auto（自动探测）、none（明文）、xor_a5（0x10 异或）、invert（0x30 按位取反）、xtea（0x31 XTEA）
    /// </summary>
    public string EncryptionMode { get; set; } = "none";

    /// <summary>
    /// 当 auto 模式未能抢到设备回包时使用的默认加密模式
    /// </summary>
    public string FallbackEncryptionMode { get; set; } = "none";

    /// <summary>
    /// 优先匹配的播放器进程/SMTC AppId 关键字列表（按顺序优先）
    /// </summary>
    public List<string> PreferredApps { get; set; } =
    [
        "limusic"
    ];

    /// <summary>
    /// 是否仅监听 PreferredApps 中的播放器（false 表示优先 PreferredApps，但也兼容其他正在播放的音乐软件）
    /// </summary>
    public bool WhitelistOnly { get; set; } = true;

    /// <summary>
    /// 找不到同步歌词时是否在音响屏幕上显示“歌名 - 歌手”
    /// </summary>
    public bool FallbackToTitle { get; set; } = true;

    /// <summary>
    /// 是否自动将繁体中文歌词转为简体中文（自动跳过含日文假名的歌词行）
    /// </summary>
    public bool ConvertToSimplified { get; set; } = true;

    /// <summary>
    /// 外语歌是否优先显示中文翻译歌词
    /// </summary>
    public bool PreferTranslation { get; set; } = true;

    /// <summary>
    /// 是否过滤“作词/作曲/编曲/混音”等制作人员元信息行
    /// </summary>
    public bool FilterCreditLines { get; set; } = true;

    /// <summary>
    /// 切歌与前奏空白期是否在屏幕上先显示“歌名 - 歌手”
    /// </summary>
    public bool ShowTitleOnIntro { get; set; } = true;

    /// <summary>
    /// 全局歌词时间偏移（毫秒），正数表示歌词提前，负数表示延后（默认 +350ms 抵消屏幕滚出延迟）
    /// </summary>
    public int LyricOffsetMs { get; set; } = 350;

    /// <summary>
    /// 播放进度轮询间隔（毫秒）
    /// </summary>
    public int PollIntervalMs { get; set; } = 150;

    /// <summary>
    /// 歌词缓存目录（相对路径基于程序所在目录，禁止落在 C 盘）
    /// </summary>
    public string CacheDir { get; set; } = "cache";

    /// <summary>
    /// 日志文件名
    /// </summary>
    public string LogFile { get; set; } = "pixelbar-lyrics.log";

    [JsonIgnore]
    public string BaseDirectory { get; private set; } = AppContext.BaseDirectory;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 加载配置；若不存在则在程序目录自动生成默认配置文件
    /// </summary>
    public static AppConfig LoadOrCreate(string? baseDir = null)
    {
        string dir = baseDir ?? AppContext.BaseDirectory;
        string path = Path.Combine(dir, "config.json");
        AppConfig cfg;
        if (File.Exists(path))
        {
            try
            {
                string raw = File.ReadAllText(path);
                cfg = JsonSerializer.Deserialize<AppConfig>(raw, JsonOpts) ?? new AppConfig();
            }
            catch
            {
                cfg = new AppConfig();
            }
        }
        else
        {
            cfg = new AppConfig();
            cfg.BaseDirectory = dir;
            cfg.Save();
        }

        cfg.BaseDirectory = dir;
        return cfg;
    }

    /// <summary>
    /// 将当前配置写回 config.json
    /// </summary>
    public void Save()
    {
        try
        {
            string path = Path.Combine(BaseDirectory, "config.json");
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // 忽略只读目录等写入异常
        }
    }

    public string GetCacheDirectoryPath()
    {
        return Path.IsPathRooted(CacheDir)
            ? CacheDir
            : Path.Combine(BaseDirectory, CacheDir);
    }

    public string GetLogFilePath()
    {
        return Path.IsPathRooted(LogFile)
            ? LogFile
            : Path.Combine(BaseDirectory, LogFile);
    }
}
