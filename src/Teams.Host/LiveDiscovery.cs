using Teams.Core;

namespace Teams.Host;

public sealed record ApprovedDirectChat(string ResourceId, string FirstActorId, string SecondActorId);
public sealed record LivePerson(string Id, string Name, string? Email = null);

public sealed class LiveDiscovery(ILiveStateStore store, LiveOptions options, string tenant,
    ILogger<LiveDiscovery> logger, TimeProvider clock)
{
    public async Task<CatalogPage> CatalogAsync(Actor actor, string? cursor, CancellationToken ct)
    {
        var page = await store.CatalogAsync(actor, cursor, ct);
        List<ConversationView> items = [];
        foreach (var conversation in page.Items)
        {
            var pair = conversation.Kind == "chat" ? options.DirectChats.SingleOrDefault(pair =>
                pair.ResourceId == conversation.Id && (pair.FirstActorId == actor.Id || pair.SecondActorId == actor.Id)) : null;
            if (pair is null) { items.Add(conversation); continue; }
            await VerifyPairAsync(pair, ct);
            var otherId = pair.FirstActorId == actor.Id ? pair.SecondActorId : pair.FirstActorId;
            var other = await RecipientAsync(otherId, conversation.Id, ct);
            items.Add(conversation with { Title = other.Name, IsDirect = true });
        }
        return new(items, page.Cursor);
    }

    public async Task<IReadOnlyList<LivePerson>> PeopleAsync(Actor actor, CancellationToken ct)
    {
        ValidateActor(actor);
        List<LivePerson> people = [];
        foreach (var pair in options.DirectChats.Where(pair => pair.FirstActorId == actor.Id || pair.SecondActorId == actor.Id))
        {
            var other = pair.FirstActorId == actor.Id ? pair.SecondActorId : pair.FirstActorId;
            try
            {
                await store.GetMemberAsync(actor, pair.ResourceId, true, ct);
                await VerifyPairAsync(pair, ct);
                var member = await RecipientAsync(other, pair.ResourceId, ct);
                people.Add(new(other, member.Name));
            }
            catch (AppError ex) when (ex.Status is 403 or 404)
            {
                logger.LogInformation("Approved direct-chat discovery entry omitted after current policy denial: {Code}.", ex.Code);
            }
        }
        return people.OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ConversationView> OpenAsync(Actor actor, string personId, CancellationToken ct)
    {
        ValidateActor(actor);
        var pair = options.DirectChats.SingleOrDefault(pair =>
            pair.FirstActorId == actor.Id && pair.SecondActorId == personId ||
            pair.SecondActorId == actor.Id && pair.FirstActorId == personId)
            ?? throw new AppError(404, "recipient_unavailable", "This person has no approved one-to-one chat in this workspace.");
        await store.GetMemberAsync(actor, pair.ResourceId, true, ct);
        await VerifyPairAsync(pair, ct);
        var other = await RecipientAsync(personId, pair.ResourceId, ct);
        var resource = await store.GetAsync(tenant, pair.ResourceId, ct);
        if (resource is null || !resource.Value.CoverageReady)
            throw new AppError(503, "projection_preparing", "This approved chat is still synchronizing. No message was sent. Try again.");
        return new(pair.ResourceId, other.Name, "chat", null, resource.Value.Revision, IsDirect: true);
    }

    private void ValidateActor(Actor actor)
    {
        if (actor.TenantId != tenant) throw new AppError(404, "access_denied", "This workspace is not available.");
        if (!options.AdmissionEnabled || !options.SynchronizationEnabled)
            throw new AppError(503, "admission_disabled", "Production chat admission is not enabled.");
        if (options.DirectChats.Length == 0)
            throw new AppError(404, "people_disabled", "No approved one-to-one chats are configured in this workspace.");
    }

    private async Task<Member> RecipientAsync(string id, string resource, CancellationToken ct)
    {
        try { return await store.GetMemberAsync(new(tenant, id, ""), resource, false, ct); }
        catch (AppError ex) when (ex.Status == 401)
        { throw new AppError(404, "recipient_unavailable", "This person no longer has access."); }
    }

    private async Task VerifyPairAsync(ApprovedDirectChat pair, CancellationToken ct)
    {
        var resource = options.Resources.Single(resource => resource.Id == pair.ResourceId);
        var evidence = await store.DirectChatAuthorityAsync(tenant, pair.ResourceId, ct);
        if (evidence is null || evidence.TenantId != tenant || evidence.ResourceId != pair.ResourceId ||
            evidence.GraphChatId != resource.GraphChatId || evidence.ChatType != "oneOnOne" || evidence.MemberIds.Count != 2 ||
            !evidence.MemberIds.Contains(pair.FirstActorId) || !evidence.MemberIds.Contains(pair.SecondActorId) ||
            evidence.FreshUntil <= clock.GetUtcNow() || evidence.VerifiedAt > clock.GetUtcNow() ||
            evidence.FreshUntil - evidence.VerifiedAt > TimeSpan.FromMinutes(5) || string.IsNullOrWhiteSpace(evidence.AuthorityVersion))
            throw new AppError(503, "direct_chat_authority_required",
                "Current policy evidence must verify this chat as the exact approved one-to-one pair before it can be opened.");
    }
}
