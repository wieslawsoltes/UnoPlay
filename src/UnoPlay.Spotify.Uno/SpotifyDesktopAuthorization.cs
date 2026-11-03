#if !UNOPLAY_BROWSER
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace UnoPlay.Spotify.Uno;

/// <summary>RFC 8252 loopback redirect, bound before the system browser opens.</summary>
public static class SpotifyDesktopAuthorization
{
    public static readonly Uri RegisteredRedirectUri = new("http://127.0.0.1/spotify/callback");

    public static async Task SignInAsync(SpotifySession session, Func<Uri, Task<bool>> openBrowser,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(4);
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = new UriBuilder(RegisteredRedirectUri) { Port = port }.Uri;
            var request = session.CreateAuthorization(redirect);
            if (!await openBrowser(request.AuthorizationUri).WaitAsync(timeout.Token).ConfigureAwait(false))
                throw new SpotifyAuthenticationException("Open your default browser to sign in to Spotify.");
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var stream = client.GetStream();
                Uri? callback;
                try
                {
                    string? target = await ReadTargetAsync(stream, connectionTimeout.Token).ConfigureAwait(false);
                    if (target is null || !target.StartsWith("/spotify/callback?", StringComparison.Ordinal)
                        || !Uri.TryCreate(redirect.GetLeftPart(UriPartial.Authority) + target, UriKind.Absolute, out callback))
                    {
                        await ReplyAsync(stream, 404, "This address handles UnoPlay Spotify sign-in.", connectionTimeout.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (!MatchesState(callback.Query, request.State))
                    {
                        await ReplyAsync(stream, 400, "The sign-in request does not match. Return to UnoPlay and try again.", connectionTimeout.Token).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested) { continue; }
                catch (IOException) { continue; }
                catch (SocketException) { continue; }
                try
                {
                    await session.CompleteAuthorizationAsync(request, callback, timeout.Token).ConfigureAwait(false);
                }
                catch (SpotifyAuthenticationException)
                {
                    await TryReplyAsync(stream, 400, "Spotify sign-in could not be completed. Return to UnoPlay to try again.", timeout.Token).ConfigureAwait(false);
                    throw;
                }
                // Closing the browser tab after granting access must not turn a successful exchange into a failure.
                await TryReplyAsync(stream, 200, "Spotify is connected. You can close this tab and return to UnoPlay.", timeout.Token).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SpotifyAuthenticationException("Spotify sign-in timed out. Start sign-in again.");
        }
        finally { listener.Stop(); }
    }

    private static bool MatchesState(string query, string expected)
    {
        var values = query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0] == "state").ToArray();
        if (values.Length != 1) return false;
        string actual = Uri.UnescapeDataString(values[0][1].Replace('+', ' '));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    }

    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[8192];
        int length = 0;
        while (length < header.Length)
        {
            int count = await stream.ReadAsync(header.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) return null;
            length += count;
            string text = Encoding.ASCII.GetString(header, 0, length);
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) continue;
            var parts = text[..text.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');
            return parts.Length == 3 && parts[0] == "GET" && parts[2] is "HTTP/1.1" or "HTTP/1.0"
                ? parts[1] : null;
        }
        return null;
    }

    private static async Task TryReplyAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        replyTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await ReplyAsync(stream, status, message, replyTimeout.Token).ConfigureAwait(false); }
        catch (IOException) { }
        catch (SocketException) { }
        catch (OperationCanceledException) { }
    }

    private static async Task ReplyAsync(NetworkStream stream, int status, string message, CancellationToken cancellationToken)
    {
        string html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>UnoPlay Spotify</title>"
            + "<h1>UnoPlay</h1><p>" + WebUtility.HtmlEncode(message) + "</p></html>";
        byte[] body = Encoding.UTF8.GetBytes(html);
        string header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request")}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n"
            + "Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; base-uri 'none'\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }
}
#endif
