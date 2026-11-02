using UnoPlay.Core;

static void Check(bool value, string reason)
{
    if (!value) throw new InvalidOperationException(reason);
}
var queue = new PlayQueue(new Random(42));
var a = new Track("a", "duplicate");
queue.Tracks.Add(a);
queue.Tracks.Add(a);
queue.Tracks.Add(new Track("b", "different"));
queue.Shuffle = true;
queue.Select(1);
int first = queue.CurrentIndex;
queue.Next(false);
int second = queue.CurrentIndex;
Check(first != second, "shuffle repeated a slot");
queue.Previous();
Check(queue.CurrentIndex == first, "previous did not rewind history");
queue.Next();
Check(queue.CurrentIndex == second, "next did not replay history");
queue.Next(false);
Check(queue.CurrentIndex != first && queue.CurrentIndex != second, "shuffle omitted a slot");
Check(queue.Next(false) == null, "single cycle did not end");
queue.Repeat = true;
Check(queue.Next(false) != null, "repeat did not start another shuffle cycle");
var selected = queue.Current;
queue.Move(queue.CurrentIndex, 2);
Check(queue.CurrentIndex == 2 && ReferenceEquals(queue.Current, selected), "move changed active track");
queue.RemoveAt(2);
Check(queue.Current == null, "removed track remains selected");
queue.Clear();
Check(queue.Next() == null && queue.Previous() == null, "empty navigation");

var source = new Uri("https://station.example/radio/playlist.m3u");
var tracks = PlaylistFile.Read("\uFEFF#EXTM3U\r\n#EXTINF:12.5,First transmission\r\none.mp3\r\n#EXTINF:-1,Live\r\n/live\r\n", ".m3u", source);
Check(tracks.Count == 2 && tracks[0].Location == "https://station.example/radio/one.mp3", "relative URL resolution");
Check(tracks[0].Duration == TimeSpan.FromSeconds(12.5) && tracks[1].Duration == null, "EXTINF lengths");
Check(PlaylistFile.Read(PlaylistFile.WriteM3u(tracks), "M3U").SequenceEqual(tracks), "extended playlist round trip");
var pls = PlaylistFile.Read("[playlist]\nFile2=two.mp3\nTITLE1=One\nLength1=30\nFile1=one.mp3", "pls", source);
Check(pls.Count == 2 && pls[0].Title == "One" && pls[1].Title == "two.mp3", "PLS ordering");
try { PlaylistFile.Read("#EXTM3U\n#EXT-X-VERSION:4\nseg.ts", "m3u8"); throw new Exception("HLS decoded as a queue"); }
catch (FormatException) { }
Console.WriteLine("Queue identity, shuffle history, repeat, editing and playlist codecs passed.");
