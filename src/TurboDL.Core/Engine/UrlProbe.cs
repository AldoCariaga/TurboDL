using System.Net;
using System.Net.Http.Headers;

namespace TurboDL.Core.Engine;

public sealed record ProbeResult(
    Uri FinalUrl,
    long? Length,
    bool SupportsRanges,
    string FileName,
    string? ETag,
    string? LastModified,
    string? ContentType);

public static class UrlProbe
{
    /// <summary>
    /// Asks for byte 0 only. A 206 tells us the size and that ranges work; a 200 means one connection.
    /// Many servers reject HEAD, so a tiny ranged GET is the reliable probe.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(
        HttpClient http, string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        TurboHttp.ApplyHeaders(request, headers);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var code = (int)response.StatusCode;

        long? length;
        bool ranges;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            length = response.Content.Headers.ContentRange?.Length;
            ranges = length is not null;
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                 && response.Content.Headers.ContentRange?.Length == 0)
        {
            length = 0; // empty file
            ranges = true;
        }
        else if (response.IsSuccessStatusCode)
        {
            length = response.Content.Headers.ContentLength;
            ranges = false;
        }
        else if (code is 408 or 429 or >= 500)
        {
            throw TransientHttpException.From(response);
        }
        else
        {
            throw new ServerRefusedException(code, $"Server returned HTTP {code} {response.ReasonPhrase}.");
        }

        var finalUrl = response.RequestMessage?.RequestUri ?? new Uri(url);
        return new ProbeResult(
            finalUrl,
            length,
            ranges,
            PickFileName(response, finalUrl),
            response.Headers.ETag?.ToString(),
            response.Content.Headers.LastModified?.ToString("R"),
            response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>text/html or application/xhtml+xml: a web page, not a file.</summary>
    public static bool IsWebPage(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var media = contentType.Split(';', 2)[0].Trim();
        return media.Equals("text/html", StringComparison.OrdinalIgnoreCase)
               || media.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string PickFileName(HttpResponseMessage response, Uri finalUrl)
    {
        var cd = response.Content.Headers.ContentDisposition;
        var fromHeader = cd?.FileNameStar ?? cd?.FileName;
        if (!string.IsNullOrWhiteSpace(fromHeader)) return FileNames.Sanitize(fromHeader.Trim('"'));

        var last = finalUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return FileNames.Sanitize(last is null ? null : Uri.UnescapeDataString(last));
    }
}
