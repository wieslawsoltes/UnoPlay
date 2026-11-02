using System.Buffers.Binary;
using System.Text;
using UnoPlay.Metadata;

static void Check(bool condition, string name)
{ if (!condition) throw new InvalidDataException(name); }

var mp3 = new byte[834];
foreach (int pos in new[] { 0,417 }) new byte[] { 0xff,0xfb,0x90,0 }.CopyTo(mp3,pos);
var tags = MetadataReader.Read(mp3);
Check(tags is { Codec: "MP3", SampleRate: 44100, Channels: 2, KilobitsPerSecond: 128 }, "MPEG-1 Layer III header");

byte[] id3 = [.. "ID3"u8, 4, 0, 0, 0, 0, 0, 16, .. "TIT2"u8, 0, 0, 0, 6, 0, 0, 3, .. "Light"u8, .. mp3];
Check(MetadataReader.Read(id3) is { Title: "Light", SampleRate: 44100 }, "ID3v2.4 text + sync-safe sizes");

static byte[] Box(string name, byte[] body)
{
    var bytes = new byte[8+body.Length];
    BinaryPrimitives.WriteInt32BigEndian(bytes,bytes.Length);
    Encoding.ASCII.GetBytes(name).CopyTo(bytes,4); body.CopyTo(bytes,8);
    return bytes;
}
var sample = new byte[28];
BinaryPrimitives.WriteUInt16BigEndian(sample.AsSpan(16),2);
BinaryPrimitives.WriteUInt16BigEndian(sample.AsSpan(24),48000);
var avg = new byte[12];
BinaryPrimitives.WriteInt32BigEndian(avg.AsSpan(8),96000);
byte[] ftyp = Box("ftyp","M4A "u8.ToArray());
byte[] moov = Box("moov", Box("trak", Box("mdia", Box("minf",Box("stbl",Box("stsd", [.. new byte[8], .. Box("mp4a",[..sample,..Box("btrt",avg)])]))))));
Check(MetadataReader.Read([..ftyp,..moov]) is { Channels: 2, SampleRate: 48000, KilobitsPerSecond: 96 }, "MPEG-4 audio sample entry");
Check(MetadataReader.Read(ftyp,[..new byte[22],..moov]).SampleRate == 48000, "moov after media data");
Check(MetadataReader.Read([0xff,0xfb,0x90,0]).Codec is null, "No guessed header without a matching frame");
for (int i=0; i<id3.Length; i++) MetadataReader.Read(id3.AsSpan(0,i));
for (int i=0; i<moov.Length; i++) MetadataReader.Read([..ftyp,..moov.AsSpan(0,i)]);
Console.WriteLine("Metadata headers, ID3, MP4 tail probing and truncation checks passed.");
foreach (var path in args)
{
    using var input = File.OpenRead(path);
    Console.WriteLine($"{path}: {await MetadataSource.FromStreamAsync(input)}");
}
