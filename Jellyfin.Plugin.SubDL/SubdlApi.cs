using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubDL;

/// <summary>
/// Shared subdl HTTP details.
/// </summary>
internal static class SubdlApi
{
    /// <summary>
    /// Name of the configured <see cref="HttpClient"/>.
    /// </summary>
    public const string HttpClientName = "SubDL";

    /// <summary>
    /// subdl API base address.
    /// </summary>
    public const string BaseUrl = "https://api.subdl.com";

    /// <summary>
    /// Serializer options for subdl responses.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Builds an authenticated GET request.
    /// </summary>
    /// <param name="url">Absolute url.</param>
    /// <param name="apiKey">subdl API key.</param>
    /// <returns>The request message.</returns>
    public static HttpRequestMessage CreateRequest(string url, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", "Bearer " + apiKey);
        return request;
    }

    /// <summary>
    /// The slice of <c>/api/v2/me</c> used by the status report.
    /// </summary>
    internal sealed class MeResponse
    {
        [JsonPropertyName("plan")]
        public PlanInfo? Plan { get; set; }

        [JsonPropertyName("usage")]
        public UsageInfo? Usage { get; set; }
    }

    internal sealed class PlanInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    internal sealed class UsageInfo
    {
        [JsonPropertyName("search")]
        public Counter? Search { get; set; }

        [JsonPropertyName("downloads")]
        public Counter? Downloads { get; set; }
    }

    internal sealed class Counter
    {
        [JsonPropertyName("used")]
        public int Used { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }

        [JsonPropertyName("reset_at")]
        public string? ResetAt { get; set; }
    }
}
