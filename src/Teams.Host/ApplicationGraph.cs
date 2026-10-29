using System.Text.Json;
using Teams.Core;

namespace Teams.Host;

public interface IGraphPolicyReader
{
    Task<JsonElement> ReadAsync(string path, CancellationToken ct);
    Task<JsonElement> ReadChannelAsync(ApprovedResource resource, CancellationToken ct);
}

public sealed class ApplicationGraph(GraphBackground transport, LiveOptions options, LiveControlStore control, IResourceRegistry? registry = null)
    : IGraphBackground, IGraphPolicyReader
{
    public IGraphBackground ForResource(string resourcePath)
    {
        var uri = GraphUrls.Validate(resourcePath);
        if (uri.Query.Length != 0)
            throw new AppError(403, "graph_scope_denied", "Select an explicitly approved resource without a continuation.");
        return new Scoped(this, GraphUrls.ResourcePath(uri));
    }
    public Task<JsonElement> GetAsync(string path, CancellationToken ct) => throw new InvalidOperationException("Select an approved Graph resource context.");
    public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("Select an approved Graph resource context.");
    public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => throw new InvalidOperationException("Select an approved Graph resource context.");
    public async Task<JsonElement> ReadChannelAsync(ApprovedResource resource, CancellationToken ct)
    {
        var approved = registry is null ? options.Resources.SingleOrDefault(item => item.Id == resource.Id) :
            (await registry.FindAsync(resource.Id, ct))?.Resource;
        if (resource.Kind != "channel" || approved != resource)
            throw new AppError(403, "graph_policy_scope_denied", "Channel metadata reads require the original registered channel.");
        return await transport.GetChannelMetadataAsync(resource.GraphTeamId!, resource.GraphChannelId!, null, ct);
    }
    public async Task<JsonElement> ReadAsync(string path, CancellationToken ct)
    {
        var policy = options.GraphPolicy ?? throw new InvalidOperationException("Graph policy is not configured.");
        var canonical = GraphUrls.ResourcePath(GraphUrls.Validate(path));
        var parts = canonical.Split('/');
        var user = parts is ["users", var id] && policy.AdmittedUserIds.Contains(Uri.UnescapeDataString(id));
        var joined = options.ChannelDiscovery.RegistryEnabled && parts is ["users", var actor, "joinedTeams"] &&
            policy.AdmittedUserIds.Contains(Uri.UnescapeDataString(actor));
        var app = canonical == $"servicePrincipals/{policy.WebServicePrincipalId}" ||
            canonical == $"servicePrincipals/{policy.WebServicePrincipalId}/appRoleAssignedTo";
        var resources = registry is null ? options.Resources : (await registry.ListAsync(ct)).Select(r => r.Resource);
        var team = resources.Where(resource => resource.Kind == "channel").Any(resource =>
            canonical == $"teams/{Uri.EscapeDataString(resource.GraphTeamId!)}" ||
            canonical == $"teams/{Uri.EscapeDataString(resource.GraphTeamId!)}/members");
        if (!user && !app && !team && !joined)
            throw new AppError(403, "graph_policy_scope_denied", "Policy reads cannot leave the admitted users, Web application or approved teams.");
        return await transport.GetAsync(path, ct);
    }
    private async Task<JsonElement> ResolveAsync(string resourcePath, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var resources = registry is null ? options.Resources : (await registry.ListAsync(ct)).Select(r => r.Resource);
        var resource = resources.SingleOrDefault(value => value.Path == resourcePath)
            ?? throw new AppError(403, "graph_scope_denied", "This source resource is not registered.");
        return await RequestAsync(resource, method, path, body, ct);
    }
    private async Task<JsonElement> RequestAsync(ApprovedResource resource, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var canonical = GraphUrls.ResourcePath(GraphUrls.Validate(path));
        var root = resource.Path[..resource.Path.LastIndexOf("/messages", StringComparison.Ordinal)];
        var isControl = canonical == "subscriptions" || canonical.StartsWith("subscriptions/", StringComparison.Ordinal);
        if (isControl)
        {
            if (canonical == "subscriptions")
            {
                if (method != HttpMethod.Get && method != HttpMethod.Post)
                    throw new AppError(403, "graph_scope_denied", "Unsupported subscription operation.");
                if (method == HttpMethod.Post)
                {
                    var value = JsonSerializer.SerializeToElement(body);
                    if (GraphMessages.NormalizeResource(value.GetProperty("resource").GetString() ?? "") != resource.Path ||
                        value.GetProperty("notificationUrl").GetString() != options.NotificationUrl ||
                        value.GetProperty("lifecycleNotificationUrl").GetString() != options.LifecycleUrl ||
                        !value.GetProperty("includeResourceData").GetBoolean() ||
                        value.GetProperty("encryptionCertificateId").GetString() != options.NotificationCertificateId)
                        throw new AppError(403, "graph_scope_denied", "Subscription creation left its approved resource or encrypted callback contract.");
                }
            }
            else
            {
                var parts = canonical.Split('/');
                var validShape = parts is ["subscriptions", _] && (method == HttpMethod.Get || method == HttpMethod.Patch) ||
                    parts is ["subscriptions", _, "reauthorize"] && method == HttpMethod.Post;
                var registered = parts.Length >= 2 ? await control.FindSubscriptionAsync(Uri.UnescapeDataString(parts[1]), ct) : null;
                if (!validShape || registered?.Resource != resource.Path)
                    throw new AppError(403, "graph_scope_denied", "The subscription is not registered for this approved resource.");
            }
        }
        else if (method != HttpMethod.Get || !(canonical == root || canonical == root + "/members" ||
            canonical == resource.Path || canonical.StartsWith(resource.Path + "/", StringComparison.Ordinal)))
            throw new AppError(403, "graph_scope_denied", "Background Graph access cannot send messages or leave its approved resource.");
        return method == HttpMethod.Get ? await transport.GetAsync(path, ct)
            : method == HttpMethod.Post ? await transport.PostAsync(path, body!, ct)
            : await transport.PatchAsync(path, body!, ct);
    }
    private sealed class Scoped(ApplicationGraph owner, string resourcePath) : IGraphBackground
    {
        public Task<JsonElement> GetAsync(string path, CancellationToken ct) => owner.ResolveAsync(resourcePath, HttpMethod.Get, path, null, ct);
        public Task<JsonElement> PostAsync(string path, object body, CancellationToken ct) => owner.ResolveAsync(resourcePath, HttpMethod.Post, path, body, ct);
        public Task<JsonElement> PatchAsync(string path, object body, CancellationToken ct) => owner.ResolveAsync(resourcePath, HttpMethod.Patch, path, body, ct);
    }
}
