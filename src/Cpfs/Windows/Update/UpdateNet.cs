using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using Cpfs.Core.Updates;

namespace Cpfs.Windows.Update;

// Why an update stopped. Each has its own sentence for the user; the log gets the name and no URL query.
internal enum UpdateError
{
    Cancelled,
    Network,
    RateLimited,
    WrongHost,
    Missing,
    TooLarge,
    Damaged,
    WrongVersion,
    ProbeFailed,
    CannotWrite,
    Leftover,
    SwapFailed,

    // The previous exe could not be put back under the exe's name. Detail is the exe path.
    RollbackFailed,

    // Rolled back, but the previous exe could not be started to say so. Detail is the version that did not start.
    PreviousNotStarted,
    SaveFailed,
}

internal sealed class UpdateException : Exception
{
    public UpdateException(UpdateError error, string? detail = null)
        : base(error.ToString())
    {
        Error = error;
        Detail = detail;
    }

    public UpdateError Error { get; }

    // For the user's sentence where it has a placeholder (a file name, a Windows error text); never a URL.
    public string? Detail { get; }
}

// The update's HTTP: GET only, https only, redirects followed by hand under UpdateRules.IsAllowedHop, no cookies,
// no credentials, no token, sizes capped, timeouts on the headers, every read and the whole download.
internal static class UpdateNet
{
    // TLS 1.2 or newer only. SecurityProtocolType.Tls13 is 12288 [Learn: SecurityProtocolType, .NET Framework 4.8].
    // The certificate is checked by .NET's default rules: no validation callback is ever set.
    public static void UseModernTls() =>
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;

    public static HttpWebRequest NewRequest(Uri uri, string accept, int timeoutMs, int readTimeoutMs)
    {
        UseModernTls();
        var request = (HttpWebRequest)WebRequest.Create(uri);
        request.Method = "GET";
        request.UserAgent = UpdateCheck.UserAgent(AppLog.VersionNumber());
        request.Accept = accept;
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = readTimeoutMs;
        request.CookieContainer = null;
        request.UseDefaultCredentials = false;
        request.Credentials = null;
        request.PreAuthenticate = false;

        // A 3xx comes back as a response [Learn: HttpWebRequest.AllowAutoRedirect]; Download follows it by hand.
        request.AllowAutoRedirect = false;
        return request;
    }

    // One asset into memory. url must be one UpdateRules built; max caps the body; progress gets the bytes so far.
    public static byte[] Download(string url, long max, Action<long>? progress, DateTime deadline, CancellationToken cancel)
    {
        var uri = new Uri(url);
        if (!UpdateRules.IsAllowedHop(uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(UpdateError.WrongHost);
        }

        for (var hop = 0; hop <= UpdateRules.MaxRedirects; hop++)
        {
            cancel.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            var request = NewRequest(uri, "application/octet-stream", UpdateRules.HeadersTimeoutMs, UpdateRules.ReadTimeoutMs);
            using var abort = cancel.Register(request.Abort);
            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse error)
            {
                response = error;
            }
            catch (WebException ex)
            {
                AppLog.Write("update", "GET " + uri.Host + ": " + ex.Status + ", " + Ms(watch));
                throw cancel.IsCancellationRequested ? new UpdateException(UpdateError.Cancelled) : new UpdateException(UpdateError.Network);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    var next = UpdateRules.NextHop(uri, response.Headers[HttpResponseHeader.Location]);
                    AppLog.Write("update", "GET " + uri.Host + ": HTTP " + Num(status) + ", redirect to " + (next?.Host ?? "(unusable)") + ", " + Ms(watch));
                    if (!UpdateRules.IsAllowedHop(next))
                    {
                        throw new UpdateException(UpdateError.WrongHost);
                    }

                    uri = next!;
                    continue;
                }

                if (status != 200)
                {
                    AppLog.Write("update", "GET " + uri.Host + ": HTTP " + Num(status) + ", " + Ms(watch));
                    throw new UpdateException(status switch
                    {
                        404 => UpdateError.Missing,
                        403 or 429 => UpdateError.RateLimited,
                        _ => UpdateError.Network,
                    });
                }

                if (response.ContentLength > max)
                {
                    AppLog.Write("update", "GET " + uri.Host + ": HTTP 200, Content-Length " + Num(response.ContentLength) + " over the cap");
                    throw new UpdateException(UpdateError.TooLarge);
                }

                var bytes = Read(response, max, progress, deadline, cancel);
                AppLog.Write("update", "GET " + uri.Host + ": HTTP 200, " + Num(bytes.LongLength) + " bytes, " + Ms(watch));
                return bytes;
            }
        }

        AppLog.Write("update", "More than " + Num(UpdateRules.MaxRedirects) + " redirects");
        throw new UpdateException(UpdateError.WrongHost);
    }

    private static byte[] Read(HttpWebResponse response, long max, Action<long>? progress, DateTime deadline, CancellationToken cancel)
    {
        try
        {
            using var stream = response.GetResponseStream() ?? throw new UpdateException(UpdateError.Network);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (buffer.Length + n > max)
                {
                    throw new UpdateException(UpdateError.TooLarge);
                }

                if (DateTime.UtcNow > deadline)
                {
                    AppLog.Write("update", "The download took longer than " + UpdateRules.TotalLimit.TotalMinutes.ToString(CultureInfo.InvariantCulture) + " minutes");
                    throw new UpdateException(UpdateError.Network);
                }

                buffer.Write(chunk, 0, n);
                progress?.Invoke(buffer.Length);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is WebException or IOException)
        {
            AppLog.Write("update", "Reading the download failed: " + (ex is WebException web ? web.Status.ToString() : ex.GetType().Name));
            throw cancel.IsCancellationRequested ? new UpdateException(UpdateError.Cancelled) : new UpdateException(UpdateError.Network);
        }
        catch (OperationCanceledException)
        {
            throw new UpdateException(UpdateError.Cancelled);
        }
    }

    private static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static string Ms(Stopwatch watch) => Num(watch.ElapsedMilliseconds) + " ms";
}
