using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Cpfs.Core.Updates;

namespace Cpfs.Windows;

// The update check's one request: GET of GitHub's latest release for cyclone9/CPFS, with only the User-Agent GitHub
// requires and its Accept type. No cookies, no credentials, no other data. The check downloads nothing but this answer;
// the exe is fetched only by Update.UpdateEngine, after the user pressed "Update now".
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
#if CPFS_UPDATE_TEST
            if (Update.UpdateTestHooks.SourceDir is { } dir)
            {
                body = File.ReadAllText(Path.Combine(dir, Update.UpdateTestHooks.LatestFile), Encoding.UTF8);
                status = 200;
                var fake = UpdateCheck.FromResponse(status, body, version);
                AppLog.Write("update", "Update check (" + why + ", test source " + dir + "): " + fake.Status + " " + (fake.Version ?? "") + ", offer " + fake.Refusal);
                return fake;
            }
#endif
            Update.UpdateNet.UseModernTls();
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
            + (status is null ? "" : ", HTTP " + status) + (error is null ? "" : ", " + error)
            + (result.Status == UpdateStatus.Newer ? ", install " + (result.Offer is null ? "not offered: " + result.Refusal : "offered") : ""));
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
