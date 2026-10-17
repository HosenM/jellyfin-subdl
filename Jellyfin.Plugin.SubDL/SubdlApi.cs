using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SubDL;

/// <summary>
/// subdl API details.
/// </summary>
internal static class SubdlApi
{
    /// <summary>
    /// subdl API base address.
    /// </summary>
    public const string BaseUrl = "https://api.subdl.com";

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

        [JsonPropertyName("remaining")]
        public int? Remaining { get; set; }

        [JsonPropertyName("reset_at")]
        public string? ResetAt { get; set; }
    }
}
