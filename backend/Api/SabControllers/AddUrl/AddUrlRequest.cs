using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.AddUrl;

public class AddUrlRequest() : AddFileRequest
{
    private static readonly HttpClient HttpClientInstance = InitializeHttpClient();
    private const int MaxAutomaticRedirections = 10;

    private static readonly string? SabReplayUrl =
        EnvironmentUtil.GetEnvironmentVariable("SAB_REPLAY_URL");

    // When replaying through a SAB proxy, every hop MUST go through the
    // proxy — otherwise a 3xx redirect that the proxy surfaces (rather
    // than following internally) would be auto-followed by .NET directly
    // to the indexer, leaking the request out of the proxy.
    private static readonly HttpClient ReplayHttpClient = InitializeReplayHttpClient();

    public static async Task<AddUrlRequest> New(HttpContext context, ConfigManager configManager)
    {
        var nzbUrl = context.GetRequestParam("name");
        var nzbName = context.GetRequestParam("nzbname");
        var userAgent = configManager.GetUserAgent();
        var nzbFile = await GetNzbFile(nzbUrl, nzbName, userAgent).ConfigureAwait(false);
        return new AddUrlRequest()
        {
            FileName = nzbFile.FileName,
            ContentType = nzbFile.ContentType,
            NzbFileStream = nzbFile.FileStream,
            Category = context.GetRequestParam("cat") ?? configManager.GetManualUploadCategory(),
            Priority = MapPriorityOption(context.GetRequestParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetRequestParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    private static async Task<NzbFileResponse> GetNzbFile(string? url, string? nzbName, string userAgent)
    {
        try
        {
            // validate url
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception($"The url is invalid.");

            // fetch url
            var response = await GetAsync(url, userAgent).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Received status code {response.StatusCode}.");

            // read the content type
            var contentType = response.Content.Headers.ContentType?.MediaType;

            // determine the filename
            var fileName = AddNzbExtension(nzbName)
                           ?? GetFilenameFromResponseHeader(response)
                           ?? GetFilenameFromUrl(url)
                           ?? throw new Exception("Nzb filename could not be determined.");

            // read the file contents
            var fileStream = await response.Content.ReadAsStreamAsync();

            // return response
            return new NzbFileResponse
            {
                FileName = fileName,
                ContentType = contentType,
                FileStream = fileStream
            };
        }
        catch (Exception ex)
        {
            throw new BadHttpRequestException($"Failed to fetch nzb-file url `{url}`: {ex.Message}");
        }
    }

    private static string? AddNzbExtension(string? nzbName)
    {
        return nzbName == null ? null
            : nzbName.ToLower().EndsWith("nzb") ? nzbName
            : $"{nzbName}.nzb";
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, string userAgent)
    {
        var inReplayMode = !string.IsNullOrWhiteSpace(SabReplayUrl);
        var httpClient = inReplayMode ? ReplayHttpClient : HttpClientInstance;
        httpClient.DefaultRequestHeaders.Remove("User-Agent");
        httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
        var response = await httpClient.GetAsync(WrapWithReplay(url));
        var remainingRedirects = MaxAutomaticRedirections;
        // In replay mode we always iterate redirects manually so each hop
        // is re-wrapped through the proxy. Outside replay mode the original
        // ALLOW_HTTPS_TO_HTTP_REDIRECTS env-gated behaviour applies.
        while
        (
            (int)response.StatusCode is >= 300 and < 400
            && remainingRedirects > 0
            && response.Headers.Location is not null
            && (inReplayMode || EnvironmentUtil.IsVariableTrue("ALLOW_HTTPS_TO_HTTP_REDIRECTS"))
        )
        {
            var redirect = response.Headers.Location;
            var redirectUri = redirect.IsAbsoluteUri ? redirect : new Uri(new Uri(url), redirect);
            response = await httpClient.GetAsync(WrapWithReplay(redirectUri.ToString()));
            remainingRedirects--;
        }

        return response;
    }

    private static string WrapWithReplay(string url)
    {
        return string.IsNullOrWhiteSpace(SabReplayUrl)
            ? url
            : $"{SabReplayUrl.TrimEnd('/')}?url={Uri.EscapeDataString(url)}";
    }

    private static HttpClient InitializeHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxAutomaticRedirections,
        };
        return new HttpClient(handler);
    }

    private static HttpClient InitializeReplayHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            MaxAutomaticRedirections = MaxAutomaticRedirections,
        };
        return new HttpClient(handler);
    }

    private static string? GetFilenameFromResponseHeader(HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var filename = contentDisposition?.FileName?.Trim('"');
        return StringUtil.EmptyToNull(filename);
    }

    private static string? GetFilenameFromUrl(string url)
    {
        try
        {
            var filename = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(filename)) return null;
            filename = Uri.UnescapeDataString(filename);
            filename = AddNzbExtension(filename);
            return filename;
        }
        catch
        {
            return null;
        }
    }

    private class NzbFileResponse
    {
        public required string FileName { get; init; }
        public required string? ContentType { get; init; }
        public required Stream FileStream { get; init; }
    }
}