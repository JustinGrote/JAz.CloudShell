using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace JAz.CloudShell;

/// <summary>A Cloud Shell, authentication, or bridge failure with a user-facing message.</summary>
public sealed class CloudShellException : Exception
{
    public CloudShellException(string stage, string message, HttpStatusCode? statusCode = null, string? requestId = null, Exception? inner = null)
        : base(message, inner)
    {
        Stage = stage;
        StatusCode = statusCode;
        RequestId = requestId;
    }

    public string Stage { get; }
    public HttpStatusCode? StatusCode { get; }
    public string? RequestId { get; }
}

/// <summary>
/// Provisions an Azure Cloud Shell PowerShell terminal through the public ARM Cloud Shell API.
/// Based on the request sequence used by Devolutions Terminal and Windows Terminal.
/// </summary>
internal sealed class CloudShellClient
{
    internal const string ArmEndpoint = "https://management.azure.com/";
    internal const string ArmScope = "https://management.core.windows.net//.default";
    private const string ApiVersion = "2025-09-01-preview";
    private const int MaxAttempts = 4;

    private static readonly string[] AllowedHostSuffixes = [".console.azure.com", ".servicebus.windows.net"];

    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly Action<string> _log;

    public CloudShellClient(HttpClient http, string accessToken, Action<string> log)
    {
        _http = http;
        _accessToken = accessToken;
        _log = log;
    }

    public async Task<Uri> ProvisionTerminalAsync(int columns, int rows, CancellationToken ct)
    {
        _log("Reading Cloud Shell user settings.");
        using (var settings = await SendAsync("UserSettings", () => NewRequest(HttpMethod.Get, new Uri($"{ArmEndpoint}providers/Microsoft.Portal/userSettings/cloudconsole?api-version={ApiVersion}")), ct, notFoundMessage:
            "Azure Cloud Shell is not set up for this account. Open https://shell.azure.com once to complete Cloud Shell onboarding, then retry.").ConfigureAwait(false))
        {
        }

        _log("Requesting a Cloud Shell console.");
        Uri cloudShellUri;
        using (var console = await SendAsync("Provisioning", () =>
        {
            var r = NewRequest(HttpMethod.Put, new Uri($"{ArmEndpoint}providers/Microsoft.Portal/consoles/default?api-version={ApiVersion}"));
            r.Content = new StringContent("""{"properties":{"osType":"linux"}}""", Encoding.UTF8, "application/json");
            return r;
        }, ct).ConfigureAwait(false))
        {
            var uriText = TryGetString(console.Json, "properties", "uri")
                ?? throw new CloudShellException("Provisioning", "Azure did not return a Cloud Shell console URI.", requestId: console.RequestId);
            cloudShellUri = ValidateServiceUri(uriText, Uri.UriSchemeHttps, "Provisioning");
            if (!cloudShellUri.AbsoluteUri.EndsWith('/'))
            {
                cloudShellUri = new Uri(cloudShellUri.AbsoluteUri + "/");
            }
        }

        _log("Creating a bash terminal.");
        using var terminal = await SendAsync("Terminal", () =>
        {
            var r = NewRequest(HttpMethod.Post, new Uri(cloudShellUri, $"terminals?cols={columns}&rows={rows}&version=2019-01-01&shell=bash"));
            r.Headers.Referrer = cloudShellUri;
            r.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            return r;
        }, ct).ConfigureAwait(false);

        var id = TryGetString(terminal.Json, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new CloudShellException("Terminal", "Azure did not return a Cloud Shell terminal identifier.", requestId: terminal.RequestId);
        }

        var socketUri = BuildWebSocketUri(cloudShellUri, id, TryGetString(terminal.Json, "socketUri"));
        return ValidateServiceUri(socketUri.AbsoluteUri, Uri.UriSchemeWss, "Terminal");
    }

    /// <summary>Builds the terminal WebSocket URI for direct and Azure Relay (Service Bus) Cloud Shell endpoints.</summary>
    internal static Uri BuildWebSocketUri(Uri cloudShellUri, string terminalId, string? socketUri)
    {
        if (!cloudShellUri.Host.Contains("servicebus", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(cloudShellUri)
            {
                Scheme = Uri.UriSchemeWss,
                Port = -1,
                Path = $"{cloudShellUri.AbsolutePath.TrimEnd('/')}/terminals/{Uri.EscapeDataString(terminalId)}",
            }.Uri;
        }

        if (!Uri.TryCreate(socketUri, UriKind.Absolute, out var relay))
        {
            throw new CloudShellException("Terminal", "Azure did not return the relay socket URI required by this Cloud Shell instance.");
        }

        var segments = relay.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new CloudShellException("Terminal", "Azure returned an invalid relay socket URI.");
        }

        return new UriBuilder(relay)
        {
            Scheme = Uri.UriSchemeWss,
            Port = -1,
            Query = string.Empty,
            Fragment = string.Empty,
            Path = $"/$hc/{segments[0]}/terminals/{Uri.EscapeDataString(terminalId)}",
        }.Uri;
    }

    internal static Uri ValidateServiceUri(string value, string scheme, string stage)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != scheme)
        {
            throw new CloudShellException(stage, $"Azure returned an unexpected Cloud Shell endpoint (expected {scheme}).");
        }

        if (!AllowedHostSuffixes.Any(s => uri.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
        {
            throw new CloudShellException(stage, $"Azure returned a Cloud Shell endpoint on an unrecognized host '{uri.Host}'.");
        }

        return uri;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("JAz.CloudShell/0.1");
        return request;
    }

    private sealed class JsonResponse(JsonDocument json, string? requestId) : IDisposable
    {
        public JsonElement Json => json.RootElement;
        public string? RequestId => requestId;
        public void Dispose() => json.Dispose();
    }

    private async Task<JsonResponse> SendAsync(string stage, Func<HttpRequestMessage> createRequest, CancellationToken ct, string? notFoundMessage = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            using var request = createRequest();
            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                _log($"{stage} request failed ({ex.Message}); retrying.");
                await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new CloudShellException(stage, $"Azure Cloud Shell request failed: {ex.Message}", inner: ex);
            }

            using (response)
            {
                var requestId = GetRequestId(response);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        return new JsonResponse(JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body), requestId);
                    }
                    catch (JsonException ex)
                    {
                        throw new CloudShellException(stage, "Azure returned invalid JSON.", response.StatusCode, requestId, ex);
                    }
                }

                var status = response.StatusCode;
                if (IsTransient(status) && attempt < MaxAttempts)
                {
                    var delay = Backoff(attempt, response.Headers.RetryAfter);
                    _log($"{stage} request returned HTTP {(int)status}; retrying in {delay.TotalSeconds:0} s.");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    continue;
                }

                var serviceMessage = ReadServiceError(body);
                var message = status switch
                {
                    HttpStatusCode.NotFound when notFoundMessage is not null => notFoundMessage,
                    HttpStatusCode.Unauthorized => $"Azure rejected the credential for Cloud Shell ({serviceMessage ?? "HTTP 401"}).",
                    HttpStatusCode.Forbidden => $"Cloud Shell access is forbidden for this identity or blocked by policy ({serviceMessage ?? "HTTP 403"}).",
                    _ => $"Azure Cloud Shell {stage} request failed with HTTP {(int)status}: {serviceMessage ?? response.ReasonPhrase}",
                };
                if (requestId is not null)
                {
                    message += $" (request id {requestId})";
                }

                throw new CloudShellException(stage, message, status, requestId);
            }
        }
    }

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Pow(2, attempt)));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.InternalServerError;

    private static string? GetRequestId(HttpResponseMessage response)
    {
        foreach (var name in new[] { "x-ms-request-id", "x-ms-correlation-request-id" })
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                return values.FirstOrDefault();
            }
        }

        return null;
    }

    internal static string? ReadServiceError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                var code = TryGetString(error, "code");
                var message = TryGetString(error, "message");
                return code is not null && message is not null ? $"{code}: {message}" : message ?? code;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
