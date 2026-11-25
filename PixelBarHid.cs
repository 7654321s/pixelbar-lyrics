using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PixelBarLyrics;

/// <summary>
/// 花再 Halo PixelBar 加密模式
/// </summary>
public enum PixelEncryptionMode
{
    None = 0x00,
    XorA5 = 0x10,
    Invert = 0x30,
    Xtea = 0x31
}

/// <summary>
/// 发现到的 HID 设备信息
/// </summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    ushort InputReportLength,
    ushort OutputReportLength);

/// <summary>
/// 花再 Halo PixelBar USB HID 通讯客户端（兼容与 TempoHub 共享打开）
/// </summary>
public sealed class PixelBarHidClient : IDisposable
{
    public const ushort TargetVid = 0x2D99;
    public const ushort TargetPid = 0xA160;
    public const ushort TargetUsagePage = 0xFF14;
    public const ushort TargetUsage = 0x0001;

    private const byte HidReportId = 0x2E;
    private const byte HeaderByte = 0xAA;
    private const byte DeviceTypeEc = 0xEC;
    private const byte CmdGetBaseInfo = 0x65;
    private const byte CmdTextQuery = 0xE7;
    private const byte CmdSetText = 0xE8;
    private const byte LyricSourceId = 0x0E;
    private const int ReportSize = 64;

    private SafeFileHandle? _writeHandle;
    private string? _devicePath;
    private PixelEncryptionMode _encryptionMode;
    private readonly uint[] _xteaKey;
    private readonly Action<string> _log;

    public PixelEncryptionMode CurrentEncryptionMode => _encryptionMode;
    public string? ConnectedPath => _devicePath;
    public bool IsConnected => _writeHandle is { IsInvalid: false, IsClosed: false };

    public PixelBarHidClient(PixelEncryptionMode initialMode, Action<string>? log = null)
    {
        _encryptionMode = initialMode;
        _xteaKey = DeriveXteaKey(TargetVid, TargetPid);
        _log = log ?? (_ => { });
    }

    public static PixelEncryptionMode ParseMode(string? text, PixelEncryptionMode fallback = PixelEncryptionMode.Xtea)
    {
        return (text ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "none" or "0" or "plain" => PixelEncryptionMode.None,
            "xor_a5" or "xor" or "16" or "0x10" => PixelEncryptionMode.XorA5,
            "invert" or "48" or "0x30" => PixelEncryptionMode.Invert,
            "xtea" or "49" or "0x31" => PixelEncryptionMode.Xtea,
            _ => fallback
        };
    }

    /// <summary>
    /// 枚举系统中所有 VID_2D99 & PID_A160 的 HID 接口
    /// </summary>
    public static List<HidDeviceInfo> EnumerateDevices()
    {
        var results = new List<HidDeviceInfo>();
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr devInfoSet = SetupDiGetClassDevsW(
            ref hidGuid,
            null,
            IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
        {
            return results;
        }

        try
        {
            uint index = 0;
            var ifaceData = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            while (SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref hidGuid, index, ref ifaceData))
            {
                index++;
                SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, out uint reqSize, IntPtr.Zero);
                if (reqSize <= 4)
                {
                    continue;
                }

                IntPtr detailBuf = Marshal.AllocHGlobal((int)reqSize);
                try
                {
                    // x64 下 SP_DEVICE_INTERFACE_DETAIL_DATA_W 的 cbSize 固定为 8
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, detailBuf, reqSize, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    string path = Marshal.PtrToStringUni(detailBuf + 4) ?? string.Empty;
                    if (!path.Contains("vid_2d99", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using SafeFileHandle probeHandle = CreateFileW(
                        path,
                        0, // 仅查询属性不需要读写权限
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        0,
                        IntPtr.Zero);

                    if (probeHandle.IsInvalid)
                    {
                        continue;
                    }

                    var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(probeHandle, ref attrs))
                    {
                        continue;
                    }

                    ushort usagePage = 0;
                    ushort usage = 0;
                    ushort inLen = 0;
                    ushort outLen = 0;
                    if (HidD_GetPreparsedData(probeHandle, out IntPtr preparsed))
                    {
                        try
                        {
                            if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) >= 0)
                            {
                                usagePage = caps.UsagePage;
                                usage = caps.Usage;
                                inLen = caps.InputReportByteLength;
                                outLen = caps.OutputReportByteLength;
                            }
                        }
                        finally
                        {
                            HidD_FreePreparsedData(preparsed);
                        }
                    }

                    results.Add(new HidDeviceInfo(
                        path,
                        attrs.VendorID,
                        attrs.ProductID,
                        usagePage,
                        usage,
                        inLen,
                        outLen));
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return results;
    }

    /// <summary>
    /// 连接到花再 Halo PixelBar 的 0xFF14 控制接口（共享读写）
    /// </summary>
    public bool Connect()
    {
        Disconnect();
        List<HidDeviceInfo> devices = EnumerateDevices();
        HidDeviceInfo? target = devices.FirstOrDefault(d =>
            d.VendorId == TargetVid &&
            d.ProductId == TargetPid &&
            d.UsagePage == TargetUsagePage);

        if (target == null)
        {
            return false;
        }

        SafeFileHandle writeHandle = CreateFileW(
            target.Path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (writeHandle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            _log($"打开 HID 写入句柄失败: Win32Error={err}, Path={target.Path}");
            writeHandle.Dispose();
            return false;
        }

        _writeHandle = writeHandle;
        _devicePath = target.Path;
        return true;
    }

    public void Disconnect()
    {
        _writeHandle?.Dispose();
        _writeHandle = null;
        _devicePath = null;
    }

    public void SetEncryptionMode(PixelEncryptionMode mode) => _encryptionMode = mode;

    /// <summary>
    /// 仅在探测期间临时打开重叠读句柄，避免常驻占用与 TempoHub 冲突
    /// </summary>
    private SafeFileHandle? OpenTempReadHandle()
    {
        if (string.IsNullOrEmpty(_devicePath))
        {
            return null;
        }

        SafeFileHandle readHandle = CreateFileW(
            _devicePath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (readHandle.IsInvalid)
        {
            readHandle.Dispose();
            return null;
        }
        return readHandle;
    }

    /// <summary>
    /// 发送 CM_65 查询设备基础信息与加密位（仅在探测期间临时打开读句柄）
    /// </summary>
    public (bool GotReply, byte Version, byte EncryptionByte, int MaxLength, byte[]? RawReply) ProbeBaseInfo(int timeoutMs = 800)
    {
        if (!IsConnected && !Connect())
        {
            return (false, 0, 0, 0, null);
        }

        using SafeFileHandle? readHandle = OpenTempReadHandle();
        byte[] queryReport = BuildReport(CmdGetBaseInfo, [], PixelEncryptionMode.None);
        if (!WriteReport(queryReport))
        {
            return (false, 0, 0, 0, null);
        }

        if (readHandle == null)
        {
            return (false, 0, 0, 0, null);
        }

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            int remain = Math.Max(50, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            byte[]? reply = ReadReportOverlapped(readHandle, remain);
            if (reply == null)
            {
                break;
            }

            _log($"[RX] {Convert.ToHexString(reply)}");

            // 严格按 CmdReport.__init__ 偏移：首字节为 0x2F 或 0x55 时 off=1，否则 off=0
            int off = (reply[0] == 0x2F || reply[0] == 0x55) ? 1 : 0;
            if (reply.Length >= off + 6 &&
                (reply[off + 1] == 0xEC || reply[off + 1] == 0xED) &&
                reply[off + 2] == CmdGetBaseInfo)
            {
                int len = (reply[off + 3] << 8) | reply[off + 4];
                if (len >= 4 && off + 5 + len <= reply.Length)
                {
                    byte ver = reply[off + 5];
                    byte enc = reply[off + 6];
                    int maxLen = (reply[off + 7] << 8) | reply[off + 8];
                    return (true, ver, enc, maxLen, reply);
                }
            }
        }

        return (false, 0, 0, 0, null);
    }

    /// <summary>
    /// 发送 CM_E7 查询设备文字传输长度上限与分包参数（source_id=0x0E）
    /// </summary>
    public (bool GotReply, byte[]? RawReply, byte[]? DecryptedPayload) ProbeTextLimits(PixelEncryptionMode mode, int timeoutMs = 800)
    {
        if (!IsConnected && !Connect())
        {
            return (false, null, null);
        }

        using SafeFileHandle? readHandle = OpenTempReadHandle();
        byte[] queryReport = BuildReport(CmdTextQuery, [LyricSourceId], mode);
        if (!WriteReport(queryReport))
        {
            return (false, null, null);
        }

        if (readHandle == null)
        {
            return (false, null, null);
        }

        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            int remain = Math.Max(50, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            byte[]? reply = ReadReportOverlapped(readHandle, remain);
            if (reply == null)
            {
                break;
            }

            _log($"[RX] {Convert.ToHexString(reply)}");

            int off = (reply[0] == 0x2F || reply[0] == 0x55) ? 1 : 0;
            if (reply.Length >= off + 6 &&
                (reply[off + 1] == 0xEC || reply[off + 1] == 0xED) &&
                reply[off + 2] == CmdTextQuery)
            {
                int len = (reply[off + 3] << 8) | reply[off + 4];
                if (len > 0 && off + 5 + len <= reply.Length)
                {
                    byte[] encPayload = reply[(off + 5)..(off + 5 + len)];
                    byte[] decPayload = DecryptPayload(encPayload, mode);
                    return (true, reply, decPayload);
                }
            }
        }

        return (false, null, null);
    }

    /// <summary>
    /// 向花再 PixelBar 屏幕推送一行歌词（遵循 CM_E8 协议及分包规范）
    /// </summary>
    public bool SendLyricLine(string lyricLine, out List<byte[]> sentFrames)
    {
        sentFrames = [];
        if (string.IsNullOrWhiteSpace(lyricLine))
        {
            return false;
        }

        if (!IsConnected && !Connect())
        {
            return false;
        }

        // 按硬件 CM_E7 回报上限截断至 <= 80 字节，防止超出设备缓冲区
        byte[] utf8 = TruncateUtf8(lyricLine.Trim(), 80);
        int totalLen = utf8.Length;
        if (totalLen == 0)
        {
            return false;
        }

        // XTEA 模式需前置 2 字节长度并 8 字节对齐（含 0 补齐），单包可用文本上限为 44 字节
        // 非 XTEA 模式按硬件 CM_E7 回报上限 50 字节分包
        int chunkMaxBytes = _encryptionMode == PixelEncryptionMode.Xtea ? 44 : 50;

        for (int offset = 0; offset < totalLen; offset += chunkMaxBytes)
        {
            int count = Math.Min(chunkMaxBytes, totalLen - offset);
            byte[] payload = new byte[2 + count];
            payload[0] = LyricSourceId;
            payload[1] = (byte)totalLen;
            Buffer.BlockCopy(utf8, offset, payload, 2, count);

            byte[] report = BuildReport(CmdSetText, payload, _encryptionMode);
            sentFrames.Add(report);

            if (!WriteReport(report))
            {
                // 写入失败时关闭句柄，下次触发自动重连
                Disconnect();
                return false;
            }
        }

        return true;
    }

    private byte[] BuildReport(byte cmdIndex, byte[] rawPayload, PixelEncryptionMode encMode)
    {
        byte[] edPayload = (cmdIndex == CmdGetBaseInfo)
            ? rawPayload
            : EncryptPayload(rawPayload, encMode);

        // 帧结构：[0]=0x2E, [1]=0xAA, [2]=0xEC, [3]=cmd, [4..5]=len(BE), [6..]=edPayload, [end]=crc
        int bodyLen = 5 + edPayload.Length; // buf[1..6+L-1] 共 5+L 字节
        if (1 + bodyLen + 1 > ReportSize)
        {
            throw new InvalidOperationException($"HID 帧超长: {1 + bodyLen + 1} > {ReportSize}");
        }

        byte[] report = new byte[ReportSize];
        report[0] = HidReportId;
        report[1] = HeaderByte;
        report[2] = DeviceTypeEc;
        report[3] = cmdIndex;
        report[4] = (byte)((edPayload.Length >> 8) & 0xFF);
        report[5] = (byte)(edPayload.Length & 0xFF);
        Buffer.BlockCopy(edPayload, 0, report, 6, edPayload.Length);

        // 校验和从 buf[1] 开始累加，不包含 report[0]=0x2E
        int sum = 0;
        for (int i = 1; i < 6 + edPayload.Length; i++)
        {
            sum += report[i];
        }
        report[6 + edPayload.Length] = (byte)(sum & 0xFF);
        return report;
    }

    private byte[] EncryptPayload(byte[] payload, PixelEncryptionMode mode)
    {
        if (payload.Length == 0)
        {
            return payload;
        }

        switch (mode)
        {
            case PixelEncryptionMode.None:
                return payload;

            case PixelEncryptionMode.XorA5:
            {
                byte[] res = new byte[payload.Length];
                for (int i = 0; i < payload.Length; i++)
                {
                    res[i] = (byte)(payload[i] ^ 0xA5);
                }
                return res;
            }

            case PixelEncryptionMode.Invert:
            {
                byte[] res = new byte[payload.Length];
                for (int i = 0; i < payload.Length; i++)
                {
                    res[i] = (byte)(~payload[i]);
                }
                return res;
            }

            case PixelEncryptionMode.Xtea:
                return EncryptXtea(payload);

            default:
                return payload;
        }
    }

    private byte[] DecryptPayload(byte[] payload, PixelEncryptionMode mode)
    {
        if (payload.Length == 0)
        {
            return payload;
        }

        switch (mode)
        {
            case PixelEncryptionMode.None:
                return payload;

            case PixelEncryptionMode.XorA5:
            {
                byte[] res = new byte[payload.Length];
                for (int i = 0; i < payload.Length; i++)
                {
                    res[i] = (byte)(payload[i] ^ 0xA5);
                }
                return res;
            }

            case PixelEncryptionMode.Invert:
            {
                byte[] res = new byte[payload.Length];
                for (int i = 0; i < payload.Length; i++)
                {
                    res[i] = (byte)(~payload[i]);
                }
                return res;
            }

            case PixelEncryptionMode.Xtea:
                return DecryptXtea(payload);

            default:
                return payload;
        }
    }

    private byte[] DecryptXtea(byte[] payload)
    {
        if (payload.Length < 8 || payload.Length % 8 != 0)
        {
            return payload;
        }

        byte[] buf = new byte[payload.Length];
        Buffer.BlockCopy(payload, 0, buf, 0, payload.Length);

        const uint delta = 0x9E3779B9;
        for (int i = 0; i < buf.Length; i += 8)
        {
            uint v0 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i, 4));
            uint v1 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i + 4, 4));
            uint sum = unchecked(delta * 32);
            for (int r = 0; r < 32; r++)
            {
                v1 -= (((v0 << 4) ^ (v0 >> 5)) + v0) ^ (sum + _xteaKey[(sum >> 11) & 3]);
                sum -= delta;
                v0 -= (((v1 << 4) ^ (v1 >> 5)) + v1) ^ (sum + _xteaKey[sum & 3]);
            }
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(i, 4), v0);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(i + 4, 4), v1);
        }

        if (buf.Length < 2)
        {
            return buf;
        }
        int origLen = (buf[0] << 8) | buf[1];
        if (2 + origLen > buf.Length)
        {
            return buf;
        }
        return buf[2..(2 + origLen)];
    }

    /// <summary>
    /// 与 TempoHub driver.common.get_encryption31_encrypt_payload 完全一致的 XTEA 加密实现
    /// </summary>
    private byte[] EncryptXtea(byte[] payload)
    {
        int origLen = payload.Length;
        int rawLen = 2 + origLen;
        int pad = 8 - (rawLen % 8); // 注意：与原版相同，余 0 时也会补 8 字节 0x00
        byte[] buf = new byte[rawLen + pad];
        buf[0] = (byte)((origLen >> 8) & 0xFF);
        buf[1] = (byte)(origLen & 0xFF);
        Buffer.BlockCopy(payload, 0, buf, 2, origLen);

        const uint delta = 0x9E3779B9;
        for (int i = 0; i < buf.Length; i += 8)
        {
            uint v0 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i, 4));
            uint v1 = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(i + 4, 4));
            uint sum = 0;
            for (int r = 0; r < 32; r++)
            {
                v0 += (((v1 << 4) ^ (v1 >> 5)) + v1) ^ (sum + _xteaKey[sum & 3]);
                sum += delta;
                v1 += (((v0 << 4) ^ (v0 >> 5)) + v0) ^ (sum + _xteaKey[(sum >> 11) & 3]);
            }
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(i, 4), v0);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(i + 4, 4), v1);
        }

        return buf;
    }

    /// <summary>
    /// 使用 AES-128-CBC 从 VID/PID 派生 XTEA 4 字密钥
    /// </summary>
    private static uint[] DeriveXteaKey(ushort vid, ushort pid)
    {
        byte[] plaintext = Convert.FromHexString($"4809{pid:X4}1a4811e9ab14d663bd873d93");
        byte[] aesKey = Convert.FromHexString($"0000{vid:X4}00001000800000805f9b34fb");
        byte[] aesIv = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];

        using var aes = Aes.Create();
        aes.Key = aesKey;
        aes.IV = aesIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        byte[] encrypted = new byte[16];
        using ICryptoTransform enc = aes.CreateEncryptor();
        enc.TransformBlock(plaintext, 0, 16, encrypted, 0);

        return
        [
            BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(12, 4))
        ];
    }

    private bool WriteReport(byte[] report)
    {
        if (_writeHandle == null || _writeHandle.IsInvalid)
        {
            return false;
        }

        bool ok = WriteFile(_writeHandle, report, (uint)report.Length, out uint written, IntPtr.Zero);
        if (!ok || written != report.Length)
        {
            int err = Marshal.GetLastWin32Error();
            _log($"HID WriteFile 失败: ok={ok}, written={written}, Win32Error={err}");
            return false;
        }
        return true;
    }

    private static byte[]? ReadReportOverlapped(SafeFileHandle handle, int timeoutMs)
    {
        byte[] buf = new byte[ReportSize];
        using var evt = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped
        {
            EventHandle = evt.SafeWaitHandle.DangerousGetHandle()
        };

        GCHandle pinned = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            bool ok = ReadFile(handle, pinned.AddrOfPinnedObject(), (uint)buf.Length, out uint bytesRead, ref overlapped);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING)
                {
                    return null;
                }

                if (!evt.WaitOne(timeoutMs))
                {
                    CancelIoEx(handle, ref overlapped);
                    return null;
                }

                if (!GetOverlappedResult(handle, ref overlapped, out bytesRead, false))
                {
                    return null;
                }
            }

            if (bytesRead == 0)
            {
                return null;
            }
            return buf[..(int)bytesRead];
        }
        finally
        {
            pinned.Free();
        }
    }

    private static byte[] TruncateUtf8(string text, int maxBytes)
    {
        byte[] raw = Encoding.UTF8.GetBytes(text);
        if (raw.Length <= maxBytes)
        {
            return raw;
        }

        int len = maxBytes;
        // 避开 UTF-8 续字节（10xxxxxx），防止截断半个汉字或表情
        while (len > 0 && (raw[len] & 0xC0) == 0x80)
        {
            len--;
        }
        return raw[..len];
    }

    public void Dispose() => Disconnect();

    #region Win32 SetupAPI & HID P/Invoke

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int ERROR_IO_PENDING = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);

    #endregion
}
