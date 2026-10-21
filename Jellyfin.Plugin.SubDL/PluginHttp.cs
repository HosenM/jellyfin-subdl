using System.Text.Json;

namespace Jellyfin.Plugin.SubDL;

/// <summary>
/// Shared HTTP details for the plugin's outbound calls.
/// </summary>
internal static class PluginHttp
{
    /// <summary>
    /// Name of the configured <see cref="HttpClient"/>, which sets a User-Agent both
    /// services want.
    /// </summary>
    public const string ClientName = "SubDL";

    /// <summary>
    /// Serializer options for provider responses.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Builds a GET request authenticated with a bearer token.
    /// </summary>
    /// <param name="url">Absolute url.</param>
    /// <param name="apiKey">API key.</param>
    /// <returns>The request message.</returns>
    public static HttpRequestMessage CreateBearerRequest(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", "Bearer " + apiKey);
        return request;
    }

    /// <summary>
    /// Reads the start of a response body for a log line, without ever throwing.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>At most 300 characters of the body.</returns>
    public static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return body.Length > 300 ? body[..300] : body;
        }
        catch (Exception)
        {
            return "<unreadable>";
        }
    }
}
