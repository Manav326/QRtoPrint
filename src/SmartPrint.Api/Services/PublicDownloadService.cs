using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace SmartPrint.Api.Services;

public sealed record PublicDownloadStatus(string? Version, bool WindowsAvailable, bool AndroidAvailable);

/// <summary>
/// Resolves stable release assets on the server and streams them through the
/// public application origin. GitHub asset redirects are followed server-side;
/// no GitHub URL or redirect is returned to a customer browser.
/// </summary>
public sealed class PublicDownloadService(HttpClient httpClient, IConfiguration configuration, IMemoryCache cache)
{
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan ReleaseCacheDuration = TimeSpan.FromMinutes(1);

    private static readonly IReadOnlyDictionary<string, string> PlatformAssets =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "SmartPrint-Windows-Setup.exe",
            ["android"] = "SmartPrint-Android.apk"
        };

    private readonly string _repository = configuration["PublicDownloads:GitHubRepository"]?.Trim() ?? "";

    public async Task<PublicDownloadStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        return new PublicDownloadStatus(
            release?.TagName,
            release?.Assets.ContainsKey(PlatformAssets["windows"]) == true,
            release?.Assets.ContainsKey(PlatformAssets["android"]) == true);
    }

    public async Task StreamDownloadAsync(string platform, HttpContext context, CancellationToken cancellationToken)
    {
        if (!PlatformAssets.TryGetValue(platform, out var assetName))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var release = await GetLatestReleaseAsync(cancellationToken);
        if (release is null || !release.Assets.TryGetValue(assetName, out var assetUri))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        if (!TryGetRange(context.Request, out var range))
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        using var upstreamResponse = await SendAssetRequestAsync(
            assetUri, range, context.Request.Headers["If-Range"].ToString(), cancellationToken);

        if (upstreamResponse.StatusCode == HttpStatusCode.NotFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        if (upstreamResponse.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            CopyContentRange(upstreamResponse, context.Response);
            context.Response.Headers["Cache-Control"] = "no-store";
            return;
        }

        if (upstreamResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            throw new HttpRequestException(
                $"The release asset provider returned HTTP {(int)upstreamResponse.StatusCode}.");
        }

        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers["Content-Disposition"] = new ContentDispositionHeaderValue("attachment")
        {
            FileName = assetName,
            FileNameStar = assetName
        }.ToString();
        context.Response.Headers["Cache-Control"] = "private, no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Accept-Ranges"] =
            upstreamResponse.Headers.AcceptRanges.FirstOrDefault() ?? "bytes";

        if (upstreamResponse.Content.Headers.ContentLength is { } contentLength)
        {
            context.Response.ContentLength = contentLength;
        }

        CopyContentRange(upstreamResponse, context.Response);

        if (upstreamResponse.Content.Headers.LastModified is { } lastModified)
        {
            context.Response.Headers["Last-Modified"] = lastModified.ToString("R");
        }

        if (upstreamResponse.Headers.ETag is { } etag)
        {
            context.Response.Headers["ETag"] = etag.ToString();
        }

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        await upstreamStream.CopyToAsync(context.Response.Body, 128 * 1024, cancellationToken);
    }

    private async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_repository))
        {
            return null;
        }

        if (!Regex.IsMatch(_repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
        {
            throw new InvalidOperationException(
                "PublicDownloads:GitHubRepository must use the owner/repository format.");
        }

        var cacheKey = $"public-download-release:{_repository}";
        if (cache.TryGetValue<CachedRelease>(cacheKey, out var cached))
        {
            return cached?.Release;
        }

        var apiUri = new Uri($"https://api.github.com/repos/{_repository}/releases/latest");
        using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        metadataTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, metadataTimeout.Token);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            cache.Set(cacheKey, new CachedRelease(null), ReleaseCacheDuration);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The release metadata provider returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(metadataTimeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: metadataTimeout.Token);
        var root = document.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagProperty)
            ? tagProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(tagName) ||
            !Regex.IsMatch(tagName, @"^v[0-9]+\.[0-9]+\.[0-9]+$"))
        {
            cache.Set(cacheKey, new CachedRelease(null), ReleaseCacheDuration);
            return null;
        }

        var assets = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (root.TryGetProperty("assets", out var assetsProperty) &&
            assetsProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProperty.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameProperty) ||
                    nameProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameProperty.GetString();
                if (name is null || !PlatformAssets.Values.Contains(name, StringComparer.Ordinal) ||
                    !asset.TryGetProperty("browser_download_url", out var urlProperty) ||
                    urlProperty.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (TryValidateAssetUri(urlProperty.GetString(), name, out var validatedUri))
                {
                    assets[name] = validatedUri;
                }
            }
        }

        var release = new ReleaseInfo(tagName, assets);
        cache.Set(cacheKey, new CachedRelease(release), ReleaseCacheDuration);
        return release;
    }

    private bool TryValidateAssetUri(string? value, string assetName, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !candidate.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            !candidate.IsDefaultPort ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        var path = Uri.UnescapeDataString(candidate.AbsolutePath);
        var expectedPrefix = $"/{_repository}/releases/download/";
        if (!path.StartsWith(expectedPrefix, StringComparison.Ordinal) ||
            !path.EndsWith("/" + assetName, StringComparison.Ordinal))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private async Task<HttpResponseMessage> SendAssetRequestAsync(
        Uri initialUri,
        RangeHeaderValue? range,
        string ifRangeHeader,
        CancellationToken cancellationToken)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (range is not null)
            {
                request.Headers.Range = range;
            }

            if (RangeConditionHeaderValue.TryParse(ifRangeHeader, out var ifRange))
            {
                request.Headers.IfRange = ifRange;
            }

            var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode is not (
                HttpStatusCode.MovedPermanently or
                HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or
                HttpStatusCode.PermanentRedirect))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null ||
                !Uri.TryCreate(currentUri, location, out var nextUri) ||
                !IsAllowedRedirect(nextUri) ||
                redirectCount == MaximumRedirects)
            {
                throw new HttpRequestException("The release asset redirect was invalid or exceeded its limit.");
            }

            currentUri = nextUri;
        }

        throw new HttpRequestException("The release asset redirect limit was exceeded.");
    }

    private static bool IsAllowedRedirect(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool TryGetRange(HttpRequest request, out RangeHeaderValue? range)
    {
        range = null;
        var rangeHeader = request.Headers["Range"].ToString();
        if (string.IsNullOrWhiteSpace(rangeHeader))
        {
            return true;
        }

        if (!RangeHeaderValue.TryParse(rangeHeader, out range) || range.Ranges.Count != 1)
        {
            return false;
        }

        return true;
    }

    private static void CopyContentRange(HttpResponseMessage upstream, HttpResponse response)
    {
        if (upstream.Content.Headers.ContentRange is { } contentRange)
        {
            response.Headers["Content-Range"] = contentRange.ToString();
        }
    }

    private sealed record ReleaseInfo(string TagName, IReadOnlyDictionary<string, Uri> Assets);
    private sealed record CachedRelease(ReleaseInfo? Release);
}
