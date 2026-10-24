using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Cpfs.Core.Updates;

namespace Cpfs.Windows;

// The update check's one request: GET of GitHub's latest release for cyclone9/CPFS, with only the User-Agent GitHub
// requires and its Accept type. No cookies, no credentials, no other data; nothing is downloaded but this answer.
internal static class UpdateClient
{
    private const int TimeoutMs = 10000;
    private const int MaxBody = 1 << 20;

    private static readonly object Gate = new();
    private static Task<UpdateResult>? _atStart;

    // At most once per run, for the start-up check.
    public static Task<UpdateResult> CheckOnceAsync()
    {
        lock (Gate)
        {
            return _atStart ??= CheckAsync("start");
        }
    }

    public static Task<UpdateResult> CheckAsync(string why) => Task.Run(() =>
    {
        var version = AppLog.VersionNumber();
        int? status = null;
        string? body = null;
        string? error = null;
        try
        {
            // TLS 1.2 or newer only. SecurityProtocolType.Tls13 is 12288 [Learn: SecurityProtocolType, .NET Framework 4.8].
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            var request = (HttpWebRequest)WebRequest.Create(UpdateCheck.ApiUrl);
            request.Method = "GET";
            request.UserAgent = UpdateCheck.UserAgent(version);
            request.Accept = UpdateCheck.Accept;
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.CookieContainer = null;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            // A redirect could lead to another host or to plain http; a 3xx is returned as is and counts as Failed
            // [Learn: HttpWebRequest.AllowAutoRedirect, .NET Framework 4.8].
            request.AllowAutoRedirect = false;
            using var response = (HttpWebResponse)request.GetResponse();
            status = (int)response.StatusCode;
            body = status == 200 ? Read(response) : null;
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse response)
        {
            using (response)
            {
                status = (int)response.StatusCode;
            }

            error = ex.Status.ToString();
        }
        catch (Exception ex) when (ex is WebException or IOException or NotSupportedException or System.Security.SecurityException or InvalidOperationException)
        {
            error = ex is WebException web ? web.Status.ToString() : ex.GetType().Name;
        }

        var result = UpdateCheck.FromResponse(status, body, version);
        AppLog.Write("update", "Update check (" + why + "): " + result.Status + (result.Version is null ? "" : " " + result.Version)
            + (status is null ? "" : ", HTTP " + status) + (error is null ? "" : ", " + error));
        return result;
    });

    private static string? Read(HttpWebResponse response)
    {
        using var stream = response.GetResponseStream();
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > MaxBody)
            {
                return null;
            }

            buffer.Write(chunk, 0, n);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
