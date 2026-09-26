using System.Text;

namespace MCLCS.Core.Toolbox;

/// <summary>音频文件的标签信息（标题 / 歌手 / 专辑 / 时长）。</summary>
public sealed class AudioTag
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }

    /// <summary>时长（秒）；无法解析时为 0。</summary>
    public double DurationSec { get; init; }

    public static AudioTag Empty { get; } = new();
}

/// <summary>
/// 音频元数据读取器（bug #10：此前播放列表只显示文件名，歌手/专辑/时长全为空）。
/// 采用零依赖的手写解析，覆盖常见容器：
/// <list type="bullet">
///   <item>mp3：ID3v2（TIT2 / TPE1 / TALB）+ ID3v1 尾部，时长按首帧比特率估算（CBR 精确，VBR 近似）；</item>
///   <item>flac：STREAMINFO 计算时长 + Vorbis Comment 读标签；</item>
///   <item>ogg：Vorbis Comment 读标签（时长需解码，留 0 由播放器补充）；</item>
///   <item>wav：RIFF 的 fmt/data 块计算时长。</item>
/// </list>
/// 解析失败一律返回空标签，调用方回退到文件名，绝不影响导入。
/// </summary>
public static class AudioMetadata
{
    public static AudioTag Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var ext = Path.GetExtension(path);
            if (ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) return ReadMp3(fs);
            if (ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)) return ReadFlac(fs);
            if (ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)) return ReadOgg(fs);
            if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)) return ReadWav(fs);
            return AudioTag.Empty;
        }
        catch
        {
            return AudioTag.Empty;
        }
    }

    // ---- mp3 ----

    private static AudioTag ReadMp3(Stream s)
    {
        var length = s.Length;
        string? title = null, artist = null, album = null;
        long audioStart = 0, audioEnd = length;

        using (var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true))
        {
            // ID3v2：位于文件头
            if (length > 10)
            {
                s.Position = 0;
                var head = br.ReadBytes(10);
                if (head.Length == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
                {
                    var major = head[3];
                    var size = ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
                    var end = 10L + size;
                    if (end <= length)
                    {
                        audioStart = end;
                        s.Position = 10;
                        while (s.Position + 10 <= end)
                        {
                            if (major == 2)
                            {
                                var id = Encoding.ASCII.GetString(br.ReadBytes(3));
                                var sz = br.ReadBytes(3);
                                if (sz.Length < 3) break;
                                var frameSize = (sz[0] << 16) | (sz[1] << 8) | sz[2];
                                if (frameSize <= 0 || s.Position + frameSize > end) break;
                                Assign(id, DecodeId3Text(br.ReadBytes(frameSize)), ref title, ref artist, ref album);
                            }
                            else
                            {
                                var id = Encoding.ASCII.GetString(br.ReadBytes(4));
                                if (id.Length < 4 || id[0] == 0) break;
                                var sz = br.ReadBytes(4);
                                if (sz.Length < 4) break;
                                var frameSize = major >= 4
                                    ? ((sz[0] & 0x7F) << 21) | ((sz[1] & 0x7F) << 14) | ((sz[2] & 0x7F) << 7) | (sz[3] & 0x7F)
                                    : (sz[0] << 24) | (sz[1] << 16) | (sz[2] << 8) | sz[3];
                                br.ReadBytes(2); // flags
                                if (frameSize <= 0 || s.Position + frameSize > end) break;
                                Assign(id, DecodeId3Text(br.ReadBytes(frameSize)), ref title, ref artist, ref album);
                            }
                        }
                    }
                }
            }

            // ID3v1：位于文件尾 128 字节
            if (length >= 128)
            {
                s.Position = length - 128;
                var tail = br.ReadBytes(128);
                if (tail.Length == 128 && tail[0] == 'T' && tail[1] == 'A' && tail[2] == 'G')
                {
                    title ??= Latin(tail, 3, 30);
                    artist ??= Latin(tail, 33, 30);
                    album ??= Latin(tail, 63, 30);
                    audioEnd = length - 128;
                }
            }
        }

        double duration = 0;
        var kbps = ReadMp3Bitrate(s, audioStart, audioEnd);
        if (kbps > 0 && audioEnd > audioStart)
            duration = (audioEnd - audioStart) * 8.0 / (kbps * 1000.0);

        return new AudioTag { Title = title, Artist = artist, Album = album, DurationSec = duration };
    }

    private static readonly int[] Mpeg1Layer3Bitrates =
        { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };

    private static readonly int[] Mpeg2Layer3Bitrates =
        { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };

    /// <summary>从第一个有效的 MPEG 帧头读出比特率（kbps），用于把文件长度换算成时长。</summary>
    private static int ReadMp3Bitrate(Stream s, long start, long end)
    {
        try
        {
            using var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
            var window = Math.Min(end - start, 256 * 1024);
            for (long i = 0; i + 4 <= window; i++)
            {
                s.Position = start + i;
                if (br.ReadByte() != 0xFF) continue;
                var b1 = br.ReadByte();
                if ((b1 & 0xE0) != 0xE0) continue;              // 帧同步位
                var versionBits = (b1 >> 3) & 0x03;              // 3=MPEG1
                var layerBits = (b1 >> 1) & 0x03;                // 1=Layer III
                var bitrateIndex = (br.ReadByte() >> 4) & 0x0F;
                if (bitrateIndex is 0 or 15) continue;           // 0=free, 15=bad
                if (layerBits != 1) return 128;                  // 非 Layer III：给个常用值估算
                return versionBits == 3
                    ? Mpeg1Layer3Bitrates[bitrateIndex]
                    : Mpeg2Layer3Bitrates[bitrateIndex];
            }
        }
        catch
        {
            // 读不到就交给调用方按 0 处理
        }
        return 0;
    }

    private static void Assign(string id, string? value, ref string? title, ref string? artist, ref string? album)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (id is "TIT2" or "TT2") title ??= value;
        else if (id is "TPE1" or "TP1") artist ??= value;
        else if (id is "TALB" or "TAL") album ??= value;
    }

    private static string? DecodeId3Text(byte[] data)
    {
        if (data.Length < 2) return null;
        var encoding = data[0];
        var body = data.Skip(1).ToArray();
        try
        {
            var text = encoding switch
            {
                1 => Encoding.Unicode.GetString(body),   // UTF-16 带 BOM
                2 => Encoding.BigEndianUnicode.GetString(body),
                3 => Encoding.UTF8.GetString(body),
                _ => Encoding.Latin1.GetString(body)
            };
            return Clean(text);
        }
        catch
        {
            return null;
        }
    }

    private static string? Latin(byte[] buf, int offset, int count)
    {
        var text = Encoding.Latin1.GetString(buf, offset, count);
        return Clean(text);
    }

    private static string? Clean(string text)
    {
        var idx = text.IndexOf('\0');
        if (idx >= 0) text = text[..idx];
        text = text.Trim();
        return text.Length == 0 ? null : text;
    }

    // ---- flac ----

    private static AudioTag ReadFlac(Stream s)
    {
        string? title = null, artist = null, album = null;
        double duration = 0;

        using var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        var magic = br.ReadBytes(4);
        if (magic.Length < 4 || magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C')
            return AudioTag.Empty;

        while (s.Position + 4 <= s.Length)
        {
            var header = br.ReadBytes(4);
            if (header.Length < 4) break;
            var isLast = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var size = (header[1] << 16) | (header[2] << 8) | header[3];
            if (size < 0 || s.Position + size > s.Length) break;

            if (type == 0 && size >= 34)
            {
                // STREAMINFO：采样率(20bit) 与总采样数(36bit)
                var block = br.ReadBytes(size);
                var bitBuffer = new System.Collections.BitArray(block);
                var sampleRate = ReadBits(bitBuffer, 44, 20);
                var totalSamples = ReadBitsLong(bitBuffer, 84, 36);
                if (sampleRate > 0) duration = totalSamples / (double)sampleRate;
            }
            else if (type == 4)
            {
                var block = br.ReadBytes(size);
                ReadVorbisComment(block, ref title, ref artist, ref album);
            }
            else
            {
                s.Position += size;
            }

            if (isLast) break;
        }

        return new AudioTag { Title = title, Artist = artist, Album = album, DurationSec = duration };
    }

    private static long ReadBits(System.Collections.BitArray bits, int start, int count)
    {
        long value = 0;
        for (var i = 0; i < count; i++)
        {
            if (start + i >= bits.Length) break;
            if (bits[start + i]) value |= 1L << (count - 1 - i);
        }
        return value;
    }

    private static long ReadBitsLong(System.Collections.BitArray bits, int start, int count)
    {
        long value = 0;
        for (var i = 0; i < count; i++)
        {
            if (start + i >= bits.Length) break;
            if (bits[start + i]) value |= 1L << (count - 1 - i);
        }
        return value;
    }

    // ---- ogg ----

    private static AudioTag ReadOgg(Stream s)
    {
        string? title = null, artist = null, album = null;
        using var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        var scanEnd = Math.Min(s.Length, 256 * 1024);
        s.Position = 0;
        while (s.Position + 27 <= scanEnd)
        {
            var capture = br.ReadBytes(27);
            if (capture[0] != 'O' || capture[1] != 'g' || capture[2] != 'g' || capture[3] != 'S')
            {
                s.Position -= 26; // 逐字节滑动找页头
                continue;
            }
            var segmentCount = capture[26];
            var segments = br.ReadBytes(segmentCount);
            if (segments.Length < segmentCount) break;
            var payloadLen = segments.Sum(x => (int)x);
            if (s.Position + payloadLen > s.Length) break;

            // 第一页的第一个包必然是 Vorbis 标识头，注释头紧随其后
            if (payloadLen > 7 && capture[0] == 'O')
            {
                var payload = br.ReadBytes(payloadLen);
                if (payload.Length > 8 && payload[1] == 'v' && payload[2] == 'o' && payload[3] == 'r' && payload[7] == 3)
                    ReadVorbisComment(payload, ref title, ref artist, ref album);
            }
            else
            {
                s.Position += payloadLen;
            }

            if (title is not null && artist is not null && album is not null) break;
        }

        return new AudioTag { Title = title, Artist = artist, Album = album, DurationSec = 0 };
    }

    /// <summary>解析 Vorbis Comment（flac / ogg 共用）：vendor 长度 + 条目数 + KEY=VALUE。</summary>
    private static void ReadVorbisComment(byte[] data, ref string? title, ref string? artist, ref string? album)
    {
        try
        {
            var pos = 0;
            if (data.Length < 8) return;
            // ogg 注释包有 1 字节类型 + "vorbis" 前缀
            if (data[0] == 3 || data[0] == 1) pos = 7;
            if (pos + 4 > data.Length) return;

            var vendorLen = BitConverter.ToInt32(data, pos);
            pos += 4 + vendorLen;
            if (pos + 4 > data.Length) return;
            var count = BitConverter.ToInt32(data, pos);
            pos += 4;

            for (var i = 0; i < count && pos + 4 <= data.Length; i++)
            {
                var len = BitConverter.ToInt32(data, pos);
                pos += 4;
                if (len < 0 || pos + len > data.Length) break;
                var entry = Encoding.UTF8.GetString(data, pos, len);
                pos += len;

                var eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                var key = entry[..eq].Trim().ToUpperInvariant();
                var value = Clean(entry[(eq + 1)..]);
                if (value is null) continue;

                if (key is "TITLE") title ??= value;
                else if (key is "ARTIST" or "PERFORMER") artist ??= value;
                else if (key is "ALBUM") album ??= value;
            }
        }
        catch
        {
            // 注释块损坏时忽略标签
        }
    }

    // ---- wav ----

    private static AudioTag ReadWav(Stream s)
    {
        double duration = 0;
        using var br = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        var riff = br.ReadBytes(12);
        if (riff.Length < 12 || riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F')
            return AudioTag.Empty;

        int byteRate = 0, dataSize = 0;
        while (s.Position + 8 <= s.Length)
        {
            var id = Encoding.ASCII.GetString(br.ReadBytes(4));
            var sizeBytes = br.ReadBytes(4);
            if (sizeBytes.Length < 4) break;
            var size = BitConverter.ToInt32(sizeBytes, 0);

            if (id == "fmt " && size >= 16)
            {
                var fmt = br.ReadBytes(size);
                if (fmt.Length >= 16)
                {
                    byteRate = BitConverter.ToInt32(fmt, 8); // 字节/秒
                    if (byteRate <= 0) break;
                }
            }
            else if (id == "data")
            {
                dataSize = size;
                if (byteRate > 0) duration = dataSize / (double)byteRate;
                break;
            }
            else
            {
                if (size < 0 || s.Position + size > s.Length) break;
                s.Position += size;
                if (size % 2 == 1) s.Position++; // RIFF 块按偶数字节对齐
            }
        }

        return new AudioTag { DurationSec = duration };
    }
}
