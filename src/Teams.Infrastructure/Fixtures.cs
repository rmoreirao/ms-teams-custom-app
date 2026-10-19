using Teams.Core;

namespace Teams.Infrastructure;

public static class Fixtures
{
    public const string Version = "split-view-v2";
    public static readonly string[] Names = ["Alex Morgan", "Sam Rivera", "Jordan Lee"];
    public static IReadOnlyList<Conversation> Create(string tenant, string[] ids, DateTimeOffset now)
    {
        if (ids.Length != 3 || ids.Distinct().Count() != 3 || ids.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Exactly three distinct fixture actor IDs are required.");
        var members = ids.Take(2).Select((id, i) => new Member(id, Names[i], now.AddDays(-7))).ToList();
        Message M(int i, string body, string? parent = null) => new($"fixture-{i}", ids[i % 2], Names[i % 2], body,
            now.AddMinutes(-120 + i), now.AddMinutes(-120 + i), parent);
        var design = Enumerable.Range(1, 45).Select(i => M(i, i switch
        {
            43 => "I have updated the handover notes. Can you review the opening checklist?",
            44 => "Yes. The new checklist makes the next shift much easier to follow.",
            45 => "Thank you! Let's keep the conversation here so everyone has the same context.",
            _ => $"Synthetic conversation context {i}. This workspace contains test data only."
        })).ToList();
        var channel = new List<Message>
        {
            M(50, "Welcome to the team channel. Post updates here and keep replies in a thread."),
            M(51, "Tomorrow's opening checklist is ready.\nPlease review the stock and safety sections."),
            M(52, "I have checked the stock section. Everything is ready.", "fixture-51"),
            M(53, "Thanks for checking. I will review the safety section.", "fixture-51"),
            M(54, "Synthetic text-safety fixture: <img src=x onerror=alert('unsafe')> & <script>alert(1)</script>"),
            M(55, "Accessibility fixture: café, 日本語, مرحبا. Long references should wrap: " + new string('x', 180))
        };
        return
        [
            new("chat-handover", tenant, "Sam Rivera", "chat", null, [..members], design, [], 1),
            new("chat-project", tenant, "Opening team", "chat", null, [..members, new(ids[2], Names[2], now.AddMinutes(-75))],
                [M(5, "Earlier context, visible only to existing members."), M(60, "The team plan is ready for review."),
                    M(61, "Let's discuss the remaining questions here.")], [], 1),
            new("channel-general", tenant, "General", "channel", "Northwind test workspace", [..members], channel, [], 1),
            new("channel-updates", tenant, "Updates", "channel", "Northwind test workspace", [..members],
                [M(70, "A quiet place for important team updates.")], [], 1),
            new("chat-empty", tenant, "New conversation", "chat", null, [..members], [], [], 1),
            new("chat-restricted", tenant, "Restricted fixture", "chat", null,
                [new(ids[2], Names[2], now.AddDays(-1))], [M(80, "Only the restricted test persona may read this fixture.")], [], 1)
        ];
    }
    public static async Task<int> SeedAsync(IStateStore store, string tenant, string[] ids, TimeProvider clock, CancellationToken ct)
    {
        var created = 0;
        foreach (var value in Create(tenant, ids, clock.GetUtcNow()))
            if (await store.CreateAsync(value, ct)) created++;
        return created;
    }
    public static async Task VerifyAsync(IStateStore store, string tenant, string[] ids, CancellationToken ct)
    {
        var expected = Create(tenant, ids, DateTimeOffset.UtcNow);
        foreach (var item in expected)
        {
            var actual = await store.GetAsync(tenant, item.Id, ct)
                ?? throw new InvalidOperationException($"Fixture {item.Id} is missing.");
            if (actual.Value.TenantId != tenant || actual.Value.Kind != item.Kind ||
                actual.Value.Members.Any(m => !ids.Contains(m.Id)) || actual.Value.Messages.Count > 200)
                throw new InvalidOperationException($"Fixture {item.Id} does not match its three-user data boundary. Migrate the retired fixture actor explicitly before rollout.");
        }
    }
    public static async Task RetireActorAsync(IStateStore store, string tenant, string[] ids, string retiredActorId,
        TimeProvider clock, CancellationToken ct)
    {
        var fixtures = Create(tenant, ids, clock.GetUtcNow());
        if (string.IsNullOrWhiteSpace(retiredActorId) || ids.Contains(retiredActorId))
            throw new InvalidOperationException("The retired fixture actor must be nonempty and distinct from the three active actors.");
        var updates = new List<Versioned<Conversation>>();
        foreach (var fixture in fixtures)
        {
            var current = await store.GetAsync(tenant, fixture.Id, ct)
                ?? throw new InvalidOperationException($"Seed and verify the existing fixture {fixture.Id} before migrating actors.");
            if (current.Value.TenantId != tenant || current.Value.Kind != fixture.Kind ||
                current.Value.Members.Any(m => !ids.Contains(m.Id) && m.Id != retiredActorId) ||
                current.Value.GraphChatId is not null || current.Value.GraphTeamId is not null ||
                current.Value.GraphChannelId is not null)
                throw new InvalidOperationException("Fixture migration refuses unknown actors or live Teams resource mappings.");
            var retired = current.Value.Members.SingleOrDefault(m => m.Id == retiredActorId);
            if (retired is null) continue;
            var members = current.Value.Members.Where(m => m.Id != retiredActorId).ToList();
            if (fixture.Id == "chat-project")
            {
                if (members.Any(m => m.Id == ids[2]))
                    throw new InvalidOperationException("The late-joiner fixture already contains the replacement actor; review its history boundary.");
                members.Add(new(ids[2], Names[2], retired.HistoryFrom, retired.Active));
            }
            var revision = current.Value.Revision + 1;
            updates.Add(current with { Value = current.Value with
            {
                Members = members,
                Epoch = current.Value.Epoch + 1,
                Revision = revision,
                Changes = [..current.Value.Changes, new(revision, "fixture-personas-migrated", clock.GetUtcNow())]
            } });
        }
        foreach (var update in updates)
            if (!await store.SaveAsync(update, ct))
                throw new InvalidOperationException("Fixture changed during actor migration. Quiesce the simulator and retry.");
        await store.SetBlockedAsync(tenant, retiredActorId, true, ct);
    }
    public static async Task ResetAsync(IStateStore store, string tenant, string[] ids, TimeProvider clock, CancellationToken ct)
    {
        foreach (var item in Create(tenant, ids, clock.GetUtcNow()))
        {
            var actual = await store.GetAsync(tenant, item.Id, ct)
                ?? throw new InvalidOperationException("Seed and verify the allowlisted dataset before resetting it.");
            var updated = item with
            {
                Epoch = actual.Value.Epoch + 1,
                Revision = actual.Value.Revision + 1,
                Changes = [new(actual.Value.Revision + 1, "dataset-reset", clock.GetUtcNow())]
            };
            if (!await store.SaveAsync(actual with { Value = updated }, ct))
                throw new InvalidOperationException("Fixture changed during reset. Quiesce the dev app and retry.");
        }
        foreach (var id in ids) await store.SetBlockedAsync(tenant, id, false, ct);
    }
}
