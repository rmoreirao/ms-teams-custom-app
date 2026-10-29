using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teams.Core;
using Teams.Infrastructure;

namespace Teams.Host;

public sealed class ResourceRegistry(LiveOptions options, string tenant, IPartitionDocuments? documents = null) : IResourceRegistry
{
    private sealed record Manifest(IReadOnlyList<ResourceRegistration> Resources);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Partition => $"{tenant}|resource-registry";
    public static string ChannelId(string tenant, string team, string channel) =>
        "channel-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { tenant, team, channel }))));
    private IEnumerable<ResourceRegistration> Seeds => options.Resources.Select(resource => new ResourceRegistration(resource,
        options.GraphPolicy?.HistoryFrom.GetValueOrDefault(resource.Id) ??
        options.SyntheticHistoryFrom.GetValueOrDefault(resource.Id),
        options.GraphPolicy?.SendEnabledResources.Contains(resource.Id) ?? true));
    private async Task<(IReadOnlyList<ResourceRegistration> Resources, string? Version)> DynamicAsync(CancellationToken ct)
    {
        if (!options.ChannelDiscovery.RegistryEnabled) return ([], null);
        if (documents is null) throw new InvalidOperationException("The enabled resource registry requires its provisioned storage.");
        var stored = await documents.ReadCurrentAsync(Partition, "registry", ct);
        if (stored is null) return ([], null);
        var manifest = stored.Value.SchemaVersion == 1
            ? JsonSerializer.Deserialize<Manifest>(stored.Value.Json, Json) : null;
        if (manifest is null || manifest.Resources.Count + options.SubscriptionScopes.Length > 100 ||
            manifest.Resources.Select(r => r.Resource.Id).Distinct().Count() != manifest.Resources.Count ||
            manifest.Resources.Any(r => r.Resource.Kind != "channel" ||
                r.Resource.Id != ChannelId(tenant, r.Resource.GraphTeamId!, r.Resource.GraphChannelId!) ||
                string.IsNullOrWhiteSpace(r.Resource.GraphTeamId) || string.IsNullOrWhiteSpace(r.Resource.GraphChannelId) ||
                r.HistoryFrom == default))
            throw new AppError(503, "resource_registry_invalid", "The registered channel inventory could not be verified.");
        return (manifest.Resources, stored.Version);
    }
    public async Task<IReadOnlyList<ResourceRegistration>> ListAsync(CancellationToken ct) =>
        Seeds.Concat((await DynamicAsync(ct)).Resources).ToArray();
    public async Task<ResourceRegistration?> FindAsync(string id, CancellationToken ct) =>
        Seeds.SingleOrDefault(r => r.Resource.Id == id) ??
        (await DynamicAsync(ct)).Resources.SingleOrDefault(r => r.Resource.Id == id);
    public async Task<IReadOnlyList<SubscriptionScope>> ScopesAsync(CancellationToken ct) =>
        options.SubscriptionScopes.Concat((await DynamicAsync(ct)).Resources
            .Select(r => new SubscriptionScope(r.Resource.Id, r.Resource.Path))).ToArray();
    public async Task<ResourceRegistration> RegisterAsync(ResourceRegistration registration, CancellationToken ct)
    {
        if (!options.ChannelDiscovery.RegistryEnabled || documents is null)
            throw new AppError(403, "channel_discovery_disabled", "New channel preparation is not enabled.");
        var resource = registration.Resource;
        if (resource.Kind != "channel" || resource.Id != ChannelId(tenant, resource.GraphTeamId!, resource.GraphChannelId!) ||
            string.IsNullOrWhiteSpace(resource.Title) || string.IsNullOrWhiteSpace(resource.GraphTeamId) ||
            string.IsNullOrWhiteSpace(resource.GraphChannelId) || registration.HistoryFrom == default)
            throw new AppError(400, "channel_registration_invalid", "The channel mapping or history policy is invalid.");
        var seed = Seeds.SingleOrDefault(r => r.Resource.Path == resource.Path);
        if (seed is not null) return seed;
        if (Seeds.Any(r => r.Resource.Id == resource.Id))
            throw new AppError(409, "resource_mapping_changed", "A configured logical resource cannot be retargeted.");
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await DynamicAsync(ct);
            var existing = current.Resources.SingleOrDefault(r => r.Resource.Id == resource.Id);
            if (existing is not null)
            {
                if (existing.Resource.Path != resource.Path) throw new AppError(409, "resource_mapping_changed", "A registered resource cannot be retargeted.");
                return existing;
            }
            if (current.Resources.Count + options.SubscriptionScopes.Length >= 100)
                throw new AppError(409, "channel_capacity", "This app has reached its channel synchronization capacity. Open the channel in Teams.");
            var next = new Manifest(current.Resources.Append(registration).ToArray());
            var document = new PartitionDocument("registry", Partition, "resource-registry", tenant, "", "",
                DateTimeOffset.UtcNow, 0, -1, JsonSerializer.Serialize(next, Json));
            if (await documents.WriteAsync([new(document, current.Version)], ct)) return registration;
        }
        throw new AppError(409, "resource_registry_conflict", "Another channel registration is in progress. Retry the original request.");
    }
}
