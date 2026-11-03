using System.Net;
using System.Net.Sockets;
using System.Text;
using UnoPlay.Spotify;
using UnoPlay.Spotify.Uno;

const string clientId = "0123456789abcdef0123456789abcdef";
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
async Task<T> Throws<T>(Func<Task> operation) where T : Exception
{
    try { await operation(); }
    catch (T error) { assertions++; return error; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static Dictionary<string, string> Query(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part.ElementAtOrDefault(1) ?? ""));

async Task<string> SendAsync(Uri redirect, string target, string? raw = null)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, redirect.Port);
    using var stream = client.GetStream();
    string request = raw ?? "GET " + target + " HTTP/1.1\r\nHost: 127.0.0.1:" + redirect.Port + "\r\nConnection: close\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var body = new MemoryStream();
    await stream.CopyToAsync(body, timeout.Token);
    return Encoding.UTF8.GetString(body.ToArray());
}

using var tokenHandler = new TokenHandler();
using var tokenHttp = new HttpClient(tokenHandler);
var session = new SpotifySession(tokenHttp, clientId);
var browserOpened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
Task signIn = SpotifyDesktopAuthorization.SignInAsync(session, authorization =>
{
    browserOpened.SetResult(authorization);
    return Task.FromResult(true);
});
var auth = await browserOpened.Task;
var authQuery = Query(auth.Query);
var redirect = new Uri(authQuery["redirect_uri"]);
Check(auth.Host == "accounts.spotify.com" && authQuery["code_challenge_method"] == "S256", "System browser opens official PKCE authorization");
Check(redirect.Host == "127.0.0.1" && redirect.Port > 0 && redirect.AbsolutePath == "/spotify/callback", "Per-sign-in loopback port");
Check(SpotifyDesktopAuthorization.RegisteredRedirectUri.AbsoluteUri == "http://127.0.0.1/spotify/callback", "Registered loopback redirect omits dynamic port");
string wrongState = await SendAsync(redirect, "/spotify/callback?state=incorrect&code=unused");
Check(wrongState.StartsWith("HTTP/1.1 400") && !signIn.IsCompleted && tokenHandler.Count == 0, "Wrong OAuth state ignored without exchange");
string malformed = await SendAsync(redirect, "/spotify/callback", "POST /spotify/callback HTTP/1.1\r\nHost: localhost\r\n\r\n");
Check(malformed.StartsWith("HTTP/1.1 404") && tokenHandler.Count == 0, "Unexpected methods cannot exchange a token");
string oversized = await SendAsync(redirect, "", "GET /spotify/callback?state=" + new string('x', 9000) + " HTTP/1.1\r\nHost: localhost\r\n\r\n");
Check(oversized.StartsWith("HTTP/1.1 404") && tokenHandler.Count == 0, "Oversized requests cannot exchange a token");
string duplicateState = await SendAsync(redirect, "/spotify/callback?state=" + authQuery["state"] + "&state=" + authQuery["state"] + "&code=unused");
Check(duplicateState.StartsWith("HTTP/1.1 400") && tokenHandler.Count == 0, "Duplicate OAuth state rejected");
// A client can disappear while the listener is reading. The next valid callback must still be accepted.
using (var abandoned = new TcpClient())
{
    await abandoned.ConnectAsync(IPAddress.Loopback, redirect.Port);
    abandoned.Client.LingerState = new LingerOption(true, 0);
}
string valid = await SendAsync(redirect, "/spotify/callback?state=" + authQuery["state"] + "&code=valid-code");
await signIn.WaitAsync(TimeSpan.FromSeconds(5));
Check(valid.StartsWith("HTTP/1.1 200") && valid.Contains("Spotify is connected"), "Successful desktop callback response");
Check(session.IsSignedIn && tokenHandler.Count == 1 && tokenHandler.RedirectUri == redirect.AbsoluteUri, "Correct verifier exchanged once for matching redirect");
Check(valid.Contains("Cache-Control: no-store") && valid.Contains("Referrer-Policy: no-referrer"), "Callback response avoids credential caching and referrers");

var deniedSession = new SpotifySession(tokenHttp, clientId);
var deniedOpened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
Task deniedSignIn = SpotifyDesktopAuthorization.SignInAsync(deniedSession, authorization =>
{
    deniedOpened.SetResult(authorization);
    return Task.FromResult(true);
});
var deniedQuery = Query((await deniedOpened.Task).Query);
string deniedReply = await SendAsync(new Uri(deniedQuery["redirect_uri"]), "/spotify/callback?state=" + deniedQuery["state"] + "&error=access_denied");
var denied = await Throws<SpotifyAuthenticationException>(() => deniedSignIn);
Check(denied.Error == "access_denied" && deniedReply.StartsWith("HTTP/1.1 400"), "Denied consent returns controlled failure");
Check(!deniedSession.IsSignedIn && tokenHandler.Count == 1, "Denied consent never exchanges tokens");

using var cancellation = new CancellationTokenSource();
var cancelledOpened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
Task cancelledSignIn = SpotifyDesktopAuthorization.SignInAsync(new SpotifySession(tokenHttp, clientId), authorization =>
{
    cancelledOpened.SetResult(authorization);
    return Task.FromResult(true);
}, cancellation.Token);
var cancelledRedirect = new Uri(Query((await cancelledOpened.Task).Query)["redirect_uri"]);
cancellation.Cancel();
await Throws<OperationCanceledException>(() => cancelledSignIn);
using (var closedClient = new TcpClient())
    await Throws<SocketException>(() => closedClient.ConnectAsync(IPAddress.Loopback, cancelledRedirect.Port));
Check(tokenHandler.Count == 1, "Cancellation closes the loopback listener without exchanging a token");

using var launchingCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
var neverOpened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
await Throws<OperationCanceledException>(() => SpotifyDesktopAuthorization.SignInAsync(new SpotifySession(tokenHttp, clientId), _ => neverOpened.Task, launchingCancellation.Token));
await Throws<SpotifyAuthenticationException>(() => SpotifyDesktopAuthorization.SignInAsync(new SpotifySession(tokenHttp, clientId), _ => Task.FromResult(false)));
Check(tokenHandler.Count == 1, "Launch failure and stalled launch remain cancellable");
Console.WriteLine($"Spotify desktop loopback PKCE callbacks, invalid requests, consent denial, cancellation and listener cleanup passed ({assertions} assertions).");

sealed class TokenHandler : HttpMessageHandler
{
    public int Count { get; private set; }
    public string? RedirectUri { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Count++;
        if (request.RequestUri!.AbsoluteUri != "https://accounts.spotify.com/api/token") throw new InvalidOperationException("Unexpected endpoint");
        string body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var fields = body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part[1]));
        if (fields["code"] != "valid-code" || fields["code_verifier"].Length is < 43 or > 128 || fields.ContainsKey("client_secret"))
            throw new InvalidOperationException("Invalid PKCE exchange");
        RedirectUri = fields["redirect_uri"];
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"desktop-access\",\"refresh_token\":\"desktop-refresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}", Encoding.UTF8, "application/json")
        };
    }
}
