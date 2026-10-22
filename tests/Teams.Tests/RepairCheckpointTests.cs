using System.Text.Json;
using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class RepairCheckpointTests
{
    private const string Tenant = "tenant";
    private readonly Clock clock = new();
    private readonly MemoryPartitionDocuments documents = new();
    private readonly ApprovedResource resource = new("chat", "chat", "Chat", null, "graph-chat", null, null);
    [Theory]
    [InlineData(true, "runnable")]
    [InlineData(false, "completed")]
    public void OriginalCheckpointsRemainReadableWithoutNewStateFields(bool pending, string status)
    {
        var json = JsonSerializer.Serialize(new { resourceId = "chat", path = resource.Path, parentId = (string?)null,
            nextLink = (string?)null, pending, updatedAt = clock.GetUtcNow(), verifying = !pending,
            scanCursor = (string?)null, generation = (long?)0 });
        var value = JsonSerializer.Deserialize<RepairState>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(status, value.Status);
        Assert.Equal(pending, value.IsRunnable(clock.GetUtcNow()));
        Assert.Null(value.Failure);
        Assert.Null(value.LastProgressAt);
    }
    [Fact]
    public async Task BlockedJobsDoNotStarveRunnableWorkBehindTheFirstPage()
    {
        var control = new LiveControlStore(documents, clock, Tenant);
        for (var index = 0; index < 51; index++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var blockedResource = resource with { Id = $"blocked-{index}" };
            await control.EnqueueRepairAsync(blockedResource, null, null, default);
            var current = (await control.RepairAsync(blockedResource.Id, null, default))!;
            Assert.NotNull(await control.FailRepairAsync(blockedResource, current, 0, "graph_cursor_invalid", true, false, default));
        }
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await control.EnqueueRepairAsync(resource, null, null, default);
        Assert.Empty(await control.PendingRepairsAsync(default));
        Assert.Equal(resource.Id, Assert.Single(await control.PendingRepairsAsync(default)).ResourceId);
        Assert.True(await control.HasPendingRepairAsync("blocked-0", default));
        Assert.Equal("blocked", Assert.Single(await control.UnresolvedRepairsAsync("blocked-0", default)).Status);
    }
    [Fact]
    public async Task RecoveryAndFailureWritesCannotOverwriteAnAdvancedCheckpoint()
    {
        var control = new LiveControlStore(documents, clock, Tenant);
        await control.EnqueueRepairAsync(resource, null, null, default);
        var old = (await control.RepairAsync(resource.Id, null, default))!;
        var next = old.Value with { NextLink = resource.Path + "?$skiptoken=advanced", Generation = 3 };
        Assert.True(await control.SaveRepairAsync(old with { Value = next }, default));
        Assert.False(await control.RestartRepairAsync(resource, old, 0, "obsolete-recovery", default));
        Assert.Null(await control.FailRepairAsync(resource, old, 0, "graph_400", false, true, default));
        Assert.Equal(next, (await control.RepairAsync(resource.Id, null, default))!.Value);
    }
    [Fact]
    public async Task RecoveryCannotRetargetAJobToAnotherApprovedResource()
    {
        var control = new LiveControlStore(documents, clock, Tenant);
        await control.EnqueueRepairAsync(resource, null, null, default);
        var current = (await control.RepairAsync(resource.Id, null, default))!;
        var other = resource with { Id = "other", GraphChatId = "other-graph-chat" };
        Assert.Equal("repair_identity_mismatch", (await Assert.ThrowsAsync<AppError>(() =>
            control.RestartRepairAsync(other, current, 0, "recovery", default))).Code);
        Assert.Equal("repair_identity_mismatch", (await Assert.ThrowsAsync<AppError>(() =>
            control.FailRepairAsync(other, current, 0, "graph_400", false, true, default))).Code);
        Assert.Equal(current, await control.RepairAsync(resource.Id, null, default));
    }
    [Fact]
    public async Task BackoffIsBoundedAndDoesNotRefreshVerificationEvidence()
    {
        var control = new LiveControlStore(documents, clock, Tenant);
        await control.EnqueueRepairAsync(resource, null, null, default);
        var original = (await control.RepairAsync(resource.Id, null, default))!.Value;
        for (var attempt = 0; attempt < 35; attempt++)
        {
            var current = (await control.RepairAsync(resource.Id, null, default))!;
            var failed = (await control.FailRepairAsync(resource, current, 0, "graph_429", false, false, default))!;
            Assert.InRange(failed.Failure!.NextAttemptAt!.Value - clock.GetUtcNow(), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(61));
            Assert.Equal(original.UpdatedAt, failed.UpdatedAt);
            Assert.Null(failed.LastProgressAt);
            clock.Advance(TimeSpan.FromSeconds(61));
        }
        Assert.Equal(30, (await control.RepairAsync(resource.Id, null, default))!.Value.Failure!.Attempts);
    }
    [Fact]
    public void DependencyLoggingRetainsWarningsWithoutChangingApplicationLogging()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Teams.Host", "appsettings.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var levels = json.RootElement.GetProperty("Logging").GetProperty("LogLevel");
        Assert.Equal("Information", levels.GetProperty("Default").GetString());
        foreach (var category in new[] { "Microsoft.Identity.Web", "Microsoft.Identity.Client", "Azure.Identity", "Azure.Core", "System.Net.Http.HttpClient" })
            Assert.Equal("Warning", levels.GetProperty(category).GetString());
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 20, 6, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
