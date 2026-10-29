using Teams.Core;

namespace Teams.Host;

public sealed record ProjectionCheckResult(int Actors, int SnapshotReads, int ExcludedPairDenials, int DirectChatsOpened);

public static class ProjectionChecks
{
    public static async Task<ProjectionCheckResult> RunAsync(ILiveStateStore store, LiveOptions options,
        string tenant, TimeProvider clock, ILogger<LiveDiscovery> logger, CancellationToken ct, IResourceRegistry? registry = null)
    {
        var policy = options.GraphPolicy ?? throw new InvalidOperationException("Projection checks require the approved Graph policy.");
        if (options.EntitlementProvider != "Graph" || !options.AdmissionEnabled || !options.SynchronizationEnabled)
            throw new InvalidOperationException("Projection checks require an admitted service-authenticated deployment.");
        var discovery = new LiveDiscovery(store, options, tenant, logger, clock);
        var reads = 0;
        var denials = 0;
        var opened = 0;
        var resources = registry is null ? options.Resources : (await registry.ListAsync(ct)).Select(item => item.Resource).ToArray();
        foreach (var actorId in policy.AdmittedUserIds)
        {
            var actor = new Actor(tenant, actorId, "", "read-only-projection-check");
            var pairs = options.DirectChats.Where(pair => pair.FirstActorId == actorId || pair.SecondActorId == actorId).ToArray();
            var expected = options.Resources.Where(resource => !options.DirectChats.Any(pair => pair.ResourceId == resource.Id) ||
                pairs.Any(pair => pair.ResourceId == resource.Id)).Select(resource => resource.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var resource in resources.Where(resource => !options.Resources.Any(seed => seed.Id == resource.Id)))
            {
                try { await store.GetMemberAsync(actor, resource.Id, false, ct); expected.Add(resource.Id); }
                catch (AppError error) when (error.Status == 404 && error.Code == "access_denied") { }
            }
            List<ConversationView> catalog = [];
            string? cursor = null;
            do
            {
                var page = await discovery.CatalogAsync(actor, cursor, ct);
                catalog.AddRange(page.Items); cursor = page.Cursor;
            } while (cursor is not null);
            if (catalog.Count != expected.Count ||
                !catalog.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expected) ||
                catalog.Any(item => item.IsDirect != pairs.Any(pair => pair.ResourceId == item.Id)))
                throw new InvalidOperationException("The projected catalog differs from the approved member/pair boundary.");
            foreach (var resource in resources)
            {
                if (!expected.Contains(resource.Id))
                {
                    try { await store.GetMemberAsync(actor, resource.Id, false, ct); }
                    catch (AppError error) when (error.Status == 404 && error.Code == "access_denied")
                    {
                        if (options.DirectChats.Any(pair => pair.ResourceId == resource.Id)) denials++;
                        continue;
                    }
                    throw new InvalidOperationException("A user outside an approved direct pair was not denied.");
                }
                var member = await store.GetMemberAsync(actor, resource.Id, false, ct);
                if ((await store.GetAsync(tenant, resource.Id, ct))?.Value.CoverageReady != true)
                    throw new AppError(503, "projection_unhealthy", "Read-only release acceptance requires completed source reconciliation.");
                var cutoff = clock.GetUtcNow().AddDays(-7);
                var messages = await store.MessagesAsync(actor, resource.Id, null, member.HistoryFrom, null, null, ct);
                if (messages.Count > 21 || messages.Any(message =>
                    message.CreatedAt < member.HistoryFrom || message.CreatedAt < cutoff || message.ParentId is not null))
                    throw new InvalidOperationException("The projected snapshot exceeded its paging or history boundary.");
                reads++;
            }
            var people = await discovery.PeopleAsync(actor, ct);
            var expectedPeople = pairs.Select(pair => pair.FirstActorId == actorId ? pair.SecondActorId : pair.FirstActorId).ToHashSet(StringComparer.Ordinal);
            if (people.Count != expectedPeople.Count || !people.Select(person => person.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expectedPeople))
                throw new InvalidOperationException("Projected people discovery differs from the approved exact pairs.");
            foreach (var pair in pairs)
            {
                var other = pair.FirstActorId == actorId ? pair.SecondActorId : pair.FirstActorId;
                var direct = await discovery.OpenAsync(actor, other, ct);
                if (direct.Id != pair.ResourceId || !direct.IsDirect)
                    throw new InvalidOperationException("Opening an approved person did not select its verified existing direct chat.");
                opened++;
            }
        }
        return new(policy.AdmittedUserIds.Length, reads, denials, opened);
    }
}
