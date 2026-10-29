using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Teams.Core;

namespace Teams.Host;

public sealed class GraphPolicyFeed(IGraphBackground graph, IGraphPolicyReader policyReader, ILiveStateStore store,
    LiveOptions options, string tenant, string webClientId, TimeProvider clock, ILogger<GraphPolicyFeed> logger,
    IResourceRegistry? registry = null) : IEntitlementFeed
{
    private sealed record Account(string Name, bool Enabled);
    private sealed record IdentitySnapshot(IReadOnlyDictionary<string, Account> Accounts, IReadOnlySet<string> Assigned,
        DateTimeOffset FreshUntil, string Version);
    private readonly SemaphoreSlim identitiesGate = new(1);
    private readonly ConcurrentDictionary<string, AuthorityPage> snapshots = new();
    private IdentitySnapshot? identities;
    private readonly ConcurrentDictionary<string, (HashSet<string> Teams, DateTimeOffset Until)> joinedTeams = new();
    private GraphPolicyOptions Policy => options.GraphPolicy ?? throw new InvalidOperationException("Graph membership policy is missing.");
    public async Task<AuthorityPage> ReadAsync(ApprovedResource resource, string? cursor, CancellationToken ct)
    {
        var registration = registry is null ? null : await registry.FindAsync(resource.Id, ct);
        if (cursor is not null || (registry is null ? !options.Resources.Any(approved => approved == resource) : registration?.Resource != resource))
            throw new AppError(400, "policy_scope_invalid", "Policy reads must select an approved resource without a client continuation.");
        if (snapshots.TryGetValue(resource.Id, out var cached) &&
            cached.Entries.All(entry => entry.FreshUntil > clock.GetUtcNow().AddSeconds(60)))
            return cached;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        ct = deadline.Token;
        var identity = await IdentitiesAsync(ct);
        var source = graph.ForResource(resource.Path);
        var root = resource.Path[..resource.Path.LastIndexOf("/messages", StringComparison.Ordinal)];
        var metadata = resource.Kind == "channel" ? await policyReader.ReadChannelAsync(resource, ct) :
            await source.GetAsync(root, ct);
        var histories = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        var supported = true;
        string? denial = null;
        var pair = options.DirectChats.SingleOrDefault(pair => pair.ResourceId == resource.Id);
        var historyFloor = registration?.HistoryFrom ?? Policy.HistoryFrom[resource.Id];
        if (resource.Kind == "chat")
        {
            if (Text(metadata, "id") != resource.GraphChatId)
                throw InvalidEvidence();
            var type = Text(metadata, "chatType");
            supported = pair is null ? type == "group" : type == "oneOnOne";
            var members = await CollectionAsync(path => source.GetAsync(path, ct), root + "/members", ct);
            foreach (var member in members)
            {
                var id = Text(member, "userId");
                if (!memberIds.Add(id)) throw InvalidEvidence();
                if (!Policy.AdmittedUserIds.Contains(id) ||
                    member.TryGetProperty("tenantId", out var tid) && tid.GetString() != tenant)
                    supported = false;
                if (member.TryGetProperty("visibleHistoryStartDateTime", out var history) &&
                    history.ValueKind == JsonValueKind.String && history.TryGetDateTimeOffset(out var from))
                    histories[id] = from;
                else if (type == "oneOnOne")
                    histories[id] = Date(metadata, "createdDateTime");
            }
            if (pair is not null && (memberIds.Count != 2 ||
                !memberIds.SetEquals(new[] { pair.FirstActorId, pair.SecondActorId }))) supported = false;
            if (memberIds.Count > 100) supported = false;
            if (!supported) denial = "policy_chat_scope_unsupported";
        }
        else
        {
            var teamRoot = $"teams/{Uri.EscapeDataString(resource.GraphTeamId!)}";
            var team = await policyReader.ReadAsync(teamRoot + "?$select=id,isArchived", ct);
            if (Text(team, "id") != resource.GraphTeamId || Text(metadata, "id") != resource.GraphChannelId) throw InvalidEvidence();
            supported = ChannelEligibility.Supported(metadata) && !Boolean(team, "isArchived");
            if (options.ChannelDiscovery.RegistryEnabled && !options.Resources.Any(seed => seed.Id == resource.Id))
            {
                foreach (var actor in Policy.AdmittedUserIds)
                    if ((await JoinedTeamsAsync(actor, identity.FreshUntil, ct)).Contains(resource.GraphTeamId!))
                    {
                        memberIds.Add(actor);
                        histories[actor] = historyFloor;
                    }
            }
            else
                foreach (var member in await CollectionAsync(path => policyReader.ReadAsync(path, ct), teamRoot + "/members", ct))
                {
                    var id = Text(member, "userId");
                    if (!memberIds.Add(id)) throw InvalidEvidence();
                    if (member.TryGetProperty("tenantId", out var tid) && tid.GetString() != tenant) supported = false;
                    histories[id] = historyFloor;
                }
            if (!supported) denial = "policy_channel_scope_unsupported";
        }
        var verifiedAt = clock.GetUtcNow();
        if (identity.FreshUntil <= verifiedAt) throw new AppError(503, "policy_snapshot_expired", "Source identity evidence expired before the policy decision completed.");
        var freshUntil = identity.FreshUntil < verifiedAt.AddMinutes(2) ? identity.FreshUntil : verifiedAt.AddMinutes(2);
        List<Entitlement> entries = [];
        var version = Hash($"{Policy.ApprovalReference}:{identity.Version}:{resource.Id}:{verifiedAt:O}:{supported}:{string.Join(",", memberIds.Order())}");
        foreach (var actor in Policy.AdmittedUserIds)
        {
            var account = identity.Accounts[actor];
            var active = supported && account.Enabled && identity.Assigned.Contains(actor) &&
                memberIds.Contains(actor) && histories.ContainsKey(actor) && !await store.IsBlockedAsync(tenant, actor, ct);
            var cutoff = new[] { historyFloor, histories.GetValueOrDefault(actor), verifiedAt.AddDays(-7) }.Max();
            entries.Add(new(tenant, resource.Id, actor, account.Name, cutoff, verifiedAt, freshUntil,
                version, active, active && (registration?.CanSend ?? Policy.SendEnabledResources.Contains(resource.Id)) &&
                    (options.Resources.Any(seed => seed.Id == resource.Id) || options.ChannelDiscovery.SendEnabled), options.WriterEpoch));
        }
        if (denial is not null)
            logger.LogWarning("Source-backed policy denied unsupported resource conditions; resource={ResourceId}, code={Code}.", resource.Id, denial);
        if (supported && entries.Any(entry => memberIds.Contains(entry.ActorId) && !histories.ContainsKey(entry.ActorId)))
            throw new AppError(503, "policy_history_unproven", "The source did not establish the admitted member's visible history boundary.");
        var direct = pair is not null && supported ? new DirectChatAuthority(tenant, resource.Id, resource.GraphChatId!,
            "oneOnOne", memberIds.Order().ToArray(), verifiedAt, freshUntil, version) : null;
        var page = new AuthorityPage(tenant, resource.Id, entries, null, direct);
        snapshots[resource.Id] = page;
        return page;
    }
    public async Task VerifyActorAsync(Actor actor, CancellationToken ct)
    {
        if (actor.TenantId != tenant || !Policy.AdmittedUserIds.Contains(actor.Id))
            throw new AppError(403, "access_denied", "This workspace is not available.");
        var snapshot = await IdentitiesAsync(ct);
        if (!snapshot.Accounts[actor.Id].Enabled || !snapshot.Assigned.Contains(actor.Id) ||
            await store.IsBlockedAsync(tenant, actor.Id, ct))
            throw new AppError(403, "access_denied", "This account no longer has access.");
    }
    private async Task<HashSet<string>> JoinedTeamsAsync(string actor, DateTimeOffset until, CancellationToken ct)
    {
        if (joinedTeams.TryGetValue(actor, out var cached) && cached.Until > clock.GetUtcNow().AddSeconds(30)) return cached.Teams;
        var values = await CollectionAsync(path => policyReader.ReadAsync(path, ct), $"users/{Uri.EscapeDataString(actor)}/joinedTeams", ct);
        var teams = values.Select(value => Text(value, "id")).ToHashSet(StringComparer.Ordinal);
        joinedTeams[actor] = (teams, until);
        return teams;
    }
    private async Task<IdentitySnapshot> IdentitiesAsync(CancellationToken ct)
    {
        await identitiesGate.WaitAsync(ct);
        try
        {
            if (identities is { } cached && cached.FreshUntil > clock.GetUtcNow().AddSeconds(60)) return cached;
            var started = clock.GetUtcNow();
            var appRoot = $"servicePrincipals/{Policy.WebServicePrincipalId}";
            var app = await policyReader.ReadAsync(appRoot + "?$select=id,appId,appRoleAssignmentRequired,accountEnabled", ct);
            if (Text(app, "id") != Policy.WebServicePrincipalId || Text(app, "appId") != webClientId ||
                !Boolean(app, "appRoleAssignmentRequired")) throw InvalidEvidence();
            var assignments = await CollectionAsync(path => policyReader.ReadAsync(path, ct), appRoot + "/appRoleAssignedTo", ct);
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assignment in assignments)
            {
                if (Text(assignment, "resourceId") != Policy.WebServicePrincipalId) throw InvalidEvidence();
                if (Boolean(app, "accountEnabled") && Text(assignment, "principalType") == "User" && Policy.AppRoleIds.Contains(Text(assignment, "appRoleId")) &&
                    Policy.AdmittedUserIds.Contains(Text(assignment, "principalId")))
                    assigned.Add(Text(assignment, "principalId"));
            }
            var accounts = new Dictionary<string, Account>(StringComparer.Ordinal);
            foreach (var actor in Policy.AdmittedUserIds)
            {
                JsonElement user;
                try { user = await policyReader.ReadAsync($"users/{actor}?$select=id,displayName,accountEnabled,userType", ct); }
                catch (AppError error) when (error.Code == "graph_404")
                {
                    accounts[actor] = new(actor, false);
                    logger.LogInformation("Policy account is absent from the approved tenant; actor={ActorId}.", actor);
                    continue;
                }
                if (Text(user, "id") != actor) throw InvalidEvidence();
                accounts[actor] = new(Text(user, "displayName"), Boolean(user, "accountEnabled") && Text(user, "userType") == "Member");
            }
            if (started.AddMinutes(2) <= clock.GetUtcNow()) throw new AppError(503, "policy_snapshot_expired", "Source identity enumeration exceeded its evidence window.");
            identities = new(accounts, assigned, started.AddMinutes(2),
                Hash(JsonSerializer.Serialize(new { started, accounts, assigned = assigned.Order().ToArray() })));
            return identities;
        }
        finally { identitiesGate.Release(); }
    }
    private static async Task<IReadOnlyList<JsonElement>> CollectionAsync(Func<string, Task<JsonElement>> read, string path, CancellationToken ct)
    {
        List<JsonElement> items = [];
        string? next = path;
        for (var page = 0; page < 20 && next is not null; page++)
        {
            ct.ThrowIfCancellationRequested();
            GraphUrls.Validate(next, path);
            var result = await read(next);
            if (!result.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array ||
                values.GetArrayLength() > 1000) throw InvalidEvidence();
            items.AddRange(values.EnumerateArray());
            if (items.Count > 1000) throw new AppError(503, "policy_page_budget", "Source policy enumeration exceeded its bounded scope.");
            next = result.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind != JsonValueKind.Null ? link.GetString() : null;
        }
        if (next is not null) throw new AppError(503, "policy_page_budget", "Source policy enumeration did not complete within its page budget.");
        return items;
    }
    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        field.GetString() is { Length: > 0 } text ? text : throw InvalidEvidence();
    private static bool Boolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? field.GetBoolean() : throw InvalidEvidence();
    private static DateTimeOffset Date(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String &&
        field.TryGetDateTimeOffset(out var date) ? date : throw InvalidEvidence();
    private static AppError InvalidEvidence() => new(503, "policy_evidence_invalid", "The Graph policy source returned missing or mismatched authoritative evidence.");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
