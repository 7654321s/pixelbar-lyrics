# PixelBarLyrics

PixelBarLyrics 是用于漫步者花再 Halo PixelBar（`VID 0x2D99`，`PID 0xA160`）的 Windows 歌词同步工具。它通过 Windows 系统媒体控制（SMTC）读取 Limusic、YouTube Music 客户端和 Spotify 等播放器的曲目与进度，再经 USB HID 接口将歌词实时推送到音响正面屏幕。

## 功能说明

- **通用媒体监听**：读取 Windows SMTC 会话，支持不开放本地歌词接口的桌面和网页播放器。
- **双词源与翻译合并**：按歌名、歌手和时长从 LRCLIB 与网易云音乐匹配 LRC 时间轴。外语歌曲支持优先显示网易云 `tlyric` 中文翻译。
- **长标题清洗**：自动去掉 YouTube 标题中的 `Official Video`、`【...】` 和 `『...』` 等装饰文字，支持按主歌手和副标题拆词重搜。
- **繁简转换**：调用 Windows 系统 API 将繁体中文歌词转为简体，遇到含日文假名的行自动跳过。
- **元信息过滤与前奏显示**：过滤“作词”“作曲”“编曲”等制作人员行，切歌和前奏期间先显示干净的“歌名 - 歌手”。
- **后台与硬件共存**：以共享读写方式打开 USB HID 控制端点，可与官方 EDIFIER TempoHub 同时运行，不占用设备，也不修改官方程序。
- **系统托盘控制**：提供右下角托盘图标，支持切换中文翻译、繁简转换、元信息过滤、微调时间偏移和暂停推送。

## 环境要求

- Windows 10 或 Windows 11。
- 花再 Halo PixelBar 音响（通过 USB 线连接电脑作为声卡，且在官方软件中开启“情绪歌词”开关）。
- .NET 10 SDK（仅从源码编译时需要）。

## 编译与运行

在项目根目录执行以下命令编译并发布到 `app` 目录：

```powershell
dotnet publish -c Release -o app
```

发布后直接运行 `app\PixelBarLyrics.exe`，程序会驻留在右下角托盘并自动监听音乐播放。

### 命令行参数

| 参数 | 说明 |
|---|---|
| `--follow-process <进程名>` | 等待并跟随指定播放器进程（例如 `limusic-app`），播放器关闭后自动退出 |
| `--check-smtc` | 列出当前 Windows 检测到所有媒体会话的标题、歌手和进度 |
| `--probe` | 枚举 `VID 0x2D99` HID 接口并尝试发送 `CM_65` 和 `CM_E7` 探测包 |
| `--test-text "<文本>"` | 向音响屏幕发送单条测试文字（可配合 `--enc none\|xor_a5\|invert\|xtea`） |
| `--test-lyric "<歌名\|歌手>"` | 测试歌名清洗、歌词搜索、翻译匹配和繁简转换结果 |
| `--console` | 在控制台前台输出实时日志，不显示托盘图标 |

## 配置说明

首次运行会在程序同目录生成 `config.json`，也可以复制 `config.example.json` 修改后使用。

| 配置项 | 默认值 | 说明 |
|---|---|---|
| `encryptionMode` | `"none"` | 协议加密方式，支持 `"none"`、`"xor_a5"`、`"invert"`、`"xtea"` 和 `"auto"` |
| `fallbackEncryptionMode` | `"none"` | `"auto"` 模式未收到设备回包时使用的默认加密方式 |
| `preferredApps` | `["limusic"]` | 优先匹配的播放器 `AppId` 关键字列表 |
| `whitelistOnly` | `true` | 设为 `true` 时只监听 `preferredApps` 中的应用，避免看浏览器视频时触发推送 |
| `fallbackToTitle` | `true` | 未找到同步歌词时显示“歌名 - 歌手” |
| `convertToSimplified` | `true` | 自动将繁体中文转为简体 |
| `preferTranslation` | `true` | 外语歌曲优先显示中文翻译 |
| `filterCreditLines` | `true` | 过滤作词、作曲、编曲等制作人员信息行 |
| `showTitleOnIntro` | `true` | 切歌和前奏空白期显示“歌名 - 歌手” |
| `lyricOffsetMs` | `350` | 歌词时间偏移量（毫秒），正数表示提前显示 |
| `pollIntervalMs` | `150` | 播放进度轮询间隔（毫秒） |

如果你使用独立安装的 YouTube Music 客户端或 PWA，可以把 `"youtube"` 或 `"ytmusic"` 加进 `preferredApps`。普通浏览器所有标签页共用同一个进程标识，如果同时在浏览器看视频，不建议把 `"chrome"` 或 `"msedge"` 加进白名单。

## 协议备忘

以下参数来自对 `VID 0x2D99`、`PID 0xA160` 设备的实测抓包记录：

- **HID 控制接口**：`UsagePage = 0xFF14`，`Usage = 0x0001`，输入和输出报告长度均为 64 字节。
- **64 字节帧结构**：`[0x2E, 0xAA, 0xEC, Cmd, LenHi, LenLo, ...Payload, Checksum, 0x00...]`，其中校验和为第 1 字节到负载末尾字节累加和的低 8 位（不含首字节 `0x2E`）。
- **`CM_65` 查询回包**：实测返回 `Version = 2`、`Encryption = 0x00`（明文无加密）、`MaxLength = 64`。
- **`CM_E7` 查询回包**：对 `source_id = 0x0E` 返回 `TextMaxLength = 80`、`PackageMaxLength = 50`。
- **`CM_E8` 歌词推送**：负载为 `[0x0E, TotalUtf8Len, ...Chunk]`，单包文本切片不超过 50 字节，整句截断至 80 字节 UTF-8 以内。

## 开源协议

本项目基于 [MIT License](LICENSE) 开源。
