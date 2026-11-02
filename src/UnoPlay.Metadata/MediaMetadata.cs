using System.Buffers.Binary;
using System.Text;

namespace UnoPlay.Metadata;

/// <summary>Encoded-stream properties. A zero numeric value means the source does not declare that property.</summary>
public sealed record MediaMetadata(string? Title = null, string? Artist = null, int SampleRate = 0, int Channels = 0,
    int KilobitsPerSecond = 0, string? Codec = null)
{
    public string? DisplayTitle => string.IsNullOrWhiteSpace(Title) ? null : string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
}

/// <summary>A bounded reader for MPEG audio, ISO BMFF, WAVE, ADTS and native FLAC streams.</summary>
public static class MetadataReader
{
    public const int ProbeSize = 128 * 1024;
    public static MediaMetadata Read(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> tail = default)
    {
        var metadata = new MediaMetadata();
        int audio = 0;
        if (bytes.StartsWith("ID3"u8) && bytes.Length >= 10)
            (metadata, audio) = ReadId3(bytes);
        var data = bytes[Math.Min(bytes.Length, audio)..];
        if (data.Length >= 12 && data.Slice(4,4).SequenceEqual("ftyp"u8))
        {
            var value = ReadBoxes(data, metadata, 0);
            if (!tail.IsEmpty && (value.SampleRate == 0 || value.Title is null))
            {
                // A moov box can follow gigabytes of mdat. Search only the bounded tail,
                // accepting a candidate only when its complete declared box is present.
                int at = 4;
                while (at < tail.Length)
                {
                    int index = tail[at..].IndexOf("moov"u8);
                    if (index < 0) break;
                    int boxAt = at + index - 4;
                    int size = ReadSize(tail[boxAt..]);
                    if (size >= 8 && size <= tail.Length - boxAt)
                        return ReadBoxes(tail.Slice(boxAt, size), value, 0);
                    at = boxAt + 8;
                }
            }
            return value;
        }
        if (data.Length >= 42 && data.StartsWith("fLaC"u8) && (data[4] & 127) == 0)
        {
            int rate = (data[18] << 12) | (data[19] << 4) | (data[20] >> 4);
            return metadata with { Codec = "FLAC", SampleRate = rate, Channels = ((data[20] >> 1) & 7) + 1 };
        }
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8,4).SequenceEqual("WAVE"u8))
        {
            for (int pos = 12; pos <= data.Length - 8;)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 4)..]);
                if (size < 0 || size > data.Length - pos - 8) break;
                if (size >= 16 && data.Slice(pos,4).SequenceEqual("fmt "u8))
                {
                    var fmt = data[(pos + 8)..];
                    return metadata with { Codec = "WAVE", Channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]),
                        SampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]),
                        KilobitsPerSecond = (int)(BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..]) * 8L / 1000) };
                }
                pos += 8 + size + (size & 1);
            }
            return metadata;
        }
        // A free-form/unknown file must not acquire technical properties from a random sync pattern.
        for (int at = 0; at + 4 <= Math.Min(data.Length, 4096); at++)
        {
            var p = data[at..];
            if (p[0] != 0xff || (p[1] & 0xe0) != 0xe0) continue;
            if ((p[1] & 0xf6) == 0xf0 && p.Length >= 7)
            {
                ReadOnlySpan<int> rates = [96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350];
                int idx = p[2] >> 2 & 15, cfg = (p[2] & 1) * 4 + (p[3] >> 6);
                int length = ((p[3] & 3) << 11) | (p[4] << 3) | (p[5] >> 5);
                if (idx < rates.Length && length >= 7 && length < p.Length - 1 && p[length] == 255 && (p[length+1] & 246) == 240)
                    return metadata with { Codec = "AAC", SampleRate = rates[idx], Channels = cfg == 7 ? 8 : cfg };
                continue;
            }
            int version = p[1] >> 3 & 3, layer = p[1] >> 1 & 3, rateIndex = p[2] >> 2 & 3, bitrateIndex = p[2] >> 4;
            if (version == 1 || layer == 0 || rateIndex == 3 || bitrateIndex is 0 or 15) continue;
            ReadOnlySpan<int> sampleRates = [44100,48000,32000];
            int rate = sampleRates[rateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            int kbps = Bitrate(version, layer, bitrateIndex);
            int lengthBytes = layer == 3 ? (12 * kbps * 1000 / rate + ((p[2] >> 1) & 1)) * 4
                : (version == 3 || layer == 2 ? 144000 : 72000) * kbps / rate + ((p[2] >> 1) & 1);
            if (lengthBytes + 2 > p.Length || p[lengthBytes] != 255 || (p[lengthBytes + 1] & 0xfe) != (p[1] & 0xfe)) continue;
            int channels = (p[3] >> 6) == 3 ? 1 : 2;
            int xingAt = 4 + ((p[1] & 1) == 0 ? 2 : 0) + (version == 3 ? (channels == 1 ? 17 : 32) : (channels == 1 ? 9 : 17));
            if (layer == 1 && xingAt + 16 <= p.Length && (p.Slice(xingAt,4).SequenceEqual("Xing"u8) || p.Slice(xingAt,4).SequenceEqual("Info"u8)) &&
                (BinaryPrimitives.ReadUInt32BigEndian(p[(xingAt+4)..]) & 3) == 3)
            {
                uint frames = BinaryPrimitives.ReadUInt32BigEndian(p[(xingAt+8)..]), bytesCount = BinaryPrimitives.ReadUInt32BigEndian(p[(xingAt+12)..]);
                if (frames > 0 && bytesCount > 0)
                    kbps = (int)Math.Clamp(Math.Round(bytesCount * 8d * rate / (frames * (version == 3 ? 1152d : 576d) * 1000)), 1, 640);
            }
            return metadata with { SampleRate = rate, Channels = channels, KilobitsPerSecond = kbps, Codec = layer == 1 ? "MP3" : $"MPEG Layer {4 - layer}" };
        }
        return metadata;
    }

    private static int Bitrate(int version, int layer, int i)
    {
        ReadOnlySpan<int> table = (version == 3, layer) switch
        {
            (true,3) => [0,32,64,96,128,160,192,224,256,288,320,352,384,416,448],
            (true,2) => [0,32,48,56,64,80,96,112,128,160,192,224,256,320,384],
            (true,1) => [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320],
            (false,3) => [0,32,48,56,64,80,96,112,128,144,160,176,192,224,256],
            _ => [0,8,16,24,32,40,48,56,64,80,96,112,128,144,160]
        };
        return table[i];
    }

    private static int SyncSafe(ReadOnlySpan<byte> data) => (data[0] & 127) << 21 | (data[1] & 127) << 14 | (data[2] & 127) << 7 | data[3] & 127;
    private static int ReadSize(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadInt32BigEndian(p);
    private static (MediaMetadata, int) ReadId3(ReadOnlySpan<byte> p)
    {
        var value = new MediaMetadata();
        int ver = p[3], size = SyncSafe(p[6..]), end = Math.Min(p.Length, size + 10), pos = 10;
        int audio = 10 + size + (ver == 4 && (p[5] & 16) != 0 ? 10 : 0);
        if (ver is < 2 or > 4 || (p[5] & 128) != 0) return (value,audio);
        if (ver >= 3 && (p[5] & 64) != 0 && pos + 4 <= end)
        {
            int extended = ver == 4 ? SyncSafe(p[pos..]) : ReadSize(p[pos..]);
            if (extended < 0 || extended > end - pos) return (value,audio);
            pos += extended + (ver == 3 ? 4 : 0);
        }
        int header = ver == 2 ? 6 : 10;
        while (pos + header <= end && p[pos] != 0)
        {
            var frame = p[pos..];
            int len = ver == 2 ? frame[3] << 16 | frame[4] << 8 | frame[5] : ver == 4 ? SyncSafe(frame[4..]) : ReadSize(frame[4..]);
            if (len <= 0 || len > end - pos - header) break;
            bool title = ver == 2 ? frame.StartsWith("TT2"u8) : frame.StartsWith("TIT2"u8);
            bool artist = ver == 2 ? frame.StartsWith("TP1"u8) : frame.StartsWith("TPE1"u8);
            if ((title || artist) && (ver == 2 || frame[9] == 0))
            {
                string text = DecodeTag(frame.Slice(header, Math.Min(len, 8192)));
                if (text.Length > 0) value = title ? value with { Title = text } : value with { Artist = text };
            }
            pos += header + len;
        }
        return (value,audio);
    }
    private static string DecodeTag(ReadOnlySpan<byte> p)
    {
        if (p.IsEmpty) return "";
        var body = p[1..];
        var encoding = p[0] switch { 0 => Encoding.Latin1, 1 => body.StartsWith(new byte[] { 0xfe,0xff }) ? Encoding.BigEndianUnicode : Encoding.Unicode,
            2 => Encoding.BigEndianUnicode, 3 => Encoding.UTF8, _ => null };
        return encoding?.GetString(body).Trim('\0','\uFEFF',' ','\r','\n').Replace('\0','/') ?? "";
    }

    private static MediaMetadata ReadDescriptors(ReadOnlySpan<byte> data, MediaMetadata value, int depth)
    {
        if (depth > 4) return value;
        for (int pos = 0; pos + 2 <= data.Length;)
        {
            int tag = data[pos++], len = 0, count = 0;
            byte b;
            do
            {
                if (pos >= data.Length || count++ == 4) return value;
                b = data[pos++];
                len = len * 128 + (b & 127);
            } while ((b & 128) != 0);
            if (len > data.Length - pos) return value;
            var block = data.Slice(pos,len);
            if (tag == 3 && len >= 3)
            {
                int start = 3;
                byte flags = block[2];
                if ((flags & 128) != 0) start += 2;
                if ((flags & 64) != 0 && start < len) start += 1 + block[start];
                if ((flags & 32) != 0) start += 2;
                if (start < len) value = ReadDescriptors(block[start..],value,depth+1);
            }
            else if (tag == 4 && len >= 13 && block[0] == 0x40)
            {
                value = value with { Codec = "AAC", KilobitsPerSecond = (int)(BinaryPrimitives.ReadUInt32BigEndian(block[9..]) / 1000) };
                value = ReadDescriptors(block[13..],value,depth+1);
            }
            else if (tag == 5 && len >= 2 && value.Codec == "AAC")
            {
                int index = (block[0] & 7) * 2 + (block[1] >> 7), channelConfig = (block[1] >> 3) & 15;
                ReadOnlySpan<int> rates = [96000,88200,64000,48000,44100,32000,24000,22050,16000,12000,11025,8000,7350];
                if (index < rates.Length)
                    value = value with { SampleRate = rates[index], Channels = channelConfig is > 0 and <= 7 ? (channelConfig == 7 ? 8 : channelConfig) : value.Channels };
            }
            pos += len;
        }
        return value;
    }

    private static MediaMetadata ReadBoxes(ReadOnlySpan<byte> data, MediaMetadata value, int depth)
    {
        if (depth > 12) return value;
        for (int pos = 0; pos <= data.Length - 8;)
        {
            var box = data[pos..]; uint len32 = BinaryPrimitives.ReadUInt32BigEndian(box);
            ulong size = len32; int head = 8;
            if (len32 == 1) { if (box.Length < 16) break; size = BinaryPrimitives.ReadUInt64BigEndian(box[8..]); head = 16; }
            else if (len32 == 0) size = (uint)box.Length;
            if (size < (ulong)head) break;
            var tag = box.Slice(4,4);
            int available = (int)Math.Min(size,(ulong)box.Length);
            var body = box.Slice(head,available-head);
            uint id = BinaryPrimitives.ReadUInt32BigEndian(tag);
            switch (id)
            {
                case 0x6d6f6f76: case 0x7472616b: case 0x6d646961: case 0x6d696e66: case 0x7374626c: // moov, trak, mdia, minf, stbl
                case 0x75647461: case 0x696c7374: // udta, ilst
                    value = ReadBoxes(body, value, depth + 1); break;
                case 0x6d657461 when body.Length >= 4: // meta, skip FullBox header
                    value = ReadBoxes(body[4..], value, depth + 1); break;
                case 0x73747364 when body.Length >= 8:
                    value = ReadBoxes(body[8..], value, depth + 1); break;
                case 0x6d703461: case 0x616c6163: case 0x61632d33: case 0x65632d33: case 0x4f707573: // audio sample entries
                    if (body.Length >= 28)
                    {
                        var codec = Encoding.ASCII.GetString(tag);
                        var channels = BinaryPrimitives.ReadUInt16BigEndian(body[16..]);
                        int sampleRate = BinaryPrimitives.ReadUInt16BigEndian(body[24..]);
                        value = value with { Codec = codec == "mp4a" ? "MPEG-4 Audio" : codec, Channels = channels, SampleRate = sampleRate };
                        int version = BinaryPrimitives.ReadUInt16BigEndian(body[8..]);
                        int extended = version == 1 ? 44 : 28;
                        if (body.Length > extended && version <= 1) value = ReadBoxes(body[extended..], value, depth + 1);
                    }
                    break;
                case 0x65736473 when body.Length >= 6: // esds FullBox
                    value = ReadDescriptors(body[4..], value, 0); break;
                case 0x62747274 when body.Length >= 12 && value.Codec is not null:
                    value = value with { KilobitsPerSecond = (int)(BinaryPrimitives.ReadUInt32BigEndian(body[8..]) / 1000) }; break;
                case 0xa96e616d: case 0xa9415254:
                    if (body.Length >= 16 && body.Slice(4,4).SequenceEqual("data"u8))
                    {
                        int count = ReadSize(body);
                        if (count >= 16 && count <= body.Length)
                        {
                            string text = Encoding.UTF8.GetString(body.Slice(16,Math.Min(count-16,8192))).Trim('\0',' ');
                            value = id == 0xa96e616d ? value with { Title = text } : value with { Artist = text };
                        }
                    }
                    break;
            }
            if (size > (ulong)box.Length) break;
            pos += (int)size;
        }
        return value;
    }
}
