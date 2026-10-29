using System.Text.Json;
using Azure.Core;
using Microsoft.AspNetCore.WebUtilities;
using Teams.Core;

namespace Teams.Host;

public static class GraphUrls
{
    public static string SubscriptionResource(string canonicalPath) =>
        canonicalPath.Replace("%3A", ":", StringComparison.OrdinalIgnoreCase)
            .Replace("%40", "@", StringComparison.OrdinalIgnoreCase);
    public static bool IsReauthorization(HttpMethod method, Uri uri) =>
        method == HttpMethod.Post && ResourcePath(uri).Split('/')
            is ["subscriptions", _, "reauthorize"];
    public static Uri Validate(string value, string? expectedPath = null)
    {
        var uri = Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            ? absolute : new Uri(new Uri("https://graph.microsoft.com/v1.0/"), value);
        if (uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal))
            throw new AppError(400, "graph_url_invalid", "Graph URL leaves the configured authority/version.");
        if (expectedPath is not null && ResourcePath(uri) != ResourcePath(Validate(expectedPath)))
            throw new AppError(400, "graph_cursor_invalid", "Graph continuation leaves the approved resource.");
        return uri;
    }
    public static Uri ChannelMetadata(string teamId, string? channelId, string? continuation = null)
    {
        if (string.IsNullOrWhiteSpace(teamId) || teamId.Length > 512 || teamId is "." or ".." ||
            channelId is not null && (string.IsNullOrWhiteSpace(channelId) || channelId.Length > 512 || channelId is "." or ".."))
            throw new AppError(400, "graph_url_invalid", "Select an available team and channel.");
        var root = $"teams/{Uri.EscapeDataString(teamId)}/channels" +
            (channelId is null ? "" : "/" + Uri.EscapeDataString(channelId));
        const string fields = "id,displayName,membershipType,isArchived,moderationSettings";
        var value = continuation ?? $"https://graph.microsoft.com/beta/{root}?$select={fields}";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "graph.microsoft.com" || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || !uri.AbsolutePath.StartsWith("/beta/", StringComparison.Ordinal))
            throw new AppError(400, "graph_url_invalid", "Channel metadata must use the configured Graph authority and beta version.");
        if (GraphMessages.NormalizeResource(uri.AbsolutePath["/beta/".Length..]) !=
            ResourcePath(Validate(root)))
            throw new AppError(400, "graph_cursor_invalid", "Channel metadata continuation leaves the selected channel collection.");
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("$select", out var selected) || selected.Count != 1 || selected[0] != fields ||
            query.Any(pair => pair.Value.Count != 1 || pair.Key is not ("$select" or "$skiptoken" or "$top")) ||
            query.TryGetValue("$top", out var top) && (!int.TryParse(top[0], out var size) || size is < 1 or > 1000))
            throw new AppError(400, "graph_cursor_invalid", "Channel metadata continuations cannot select content or expand relationships.");
        return uri;
    }
    public static string ResourcePath(Uri uri) =>
        GraphMessages.NormalizeResource(Validate(uri.AbsoluteUri).AbsolutePath["/v1.0/".Length..]);
    public static string BudgetResource(Uri uri)
    {
        var path = ResourcePath(uri);
        var segments = path.Split('/');
        if (segments is ["chats", _, ..]) return "v1.0/" + string.Join('/', segments.Take(2));
        if (segments is ["teams", _, "channels", _, ..]) return "v1.0/" + string.Join('/', segments.Take(4));
        return "/v1.0/" + path;
    }
}
public sealed class GraphBackground(HttpClient http, TokenCredential credential, GraphRetry? retry = null) : IGraphBackground
{
    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => RequestAsync(HttpMethod.Get, path, null, ct);
    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => RequestAsync(HttpMethod.Post, path, body, ct);
    public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => RequestAsync(HttpMethod.Patch, path, body, ct);
    public Task<JsonElement> GetChannelMetadataAsync(string teamId, string? channelId, string? continuation, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, GraphUrls.ChannelMetadata(teamId, channelId, continuation), null, ct);
    private Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var uri = GraphUrls.Validate(path);
        var canonical = GraphUrls.ResourcePath(uri);
        var control = canonical == "subscriptions" || canonical.StartsWith("subscriptions/", StringComparison.Ordinal);
        if (!control && method != HttpMethod.Get) throw new InvalidOperationException("Background Graph clients cannot send user messages.");
        return SendAsync(method, uri, body, ct);
    }
    private async Task<JsonElement> SendAsync(HttpMethod method, Uri uri, object? body, CancellationToken ct)
    {
        using var measurement = LiveTelemetry.Measure("graph-background");
        return await GraphRequests.SendAsync(() => http, async token =>
            (await credential.GetTokenAsync(new TokenRequestContext(["https://graph.microsoft.com/.default"]), token)).Token,
            method, uri, body, "application", retry ?? new(), ct);
    }
}
