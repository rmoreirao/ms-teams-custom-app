using Teams.Core;
using Teams.Infrastructure;
using Teams.Host;

namespace Teams.Tests;

public sealed class PrimaryReadTests
{
    private static PartitionDocument Document => new("member", "tenant|resource", "member", "tenant", "resource",
        "actor", DateTimeOffset.UtcNow, 1, -1, """{"active":false}""");
    [Fact]
    public void PrimaryReadReturnsTheAuthoritativeValueAndETag()
    {
        var result = new PrimaryReadResult(1, true, Document, "etag");
        var value = result.Validate("tenant|resource", "member", "opaque-session-token");
        Assert.Equal(Document.Json, value!.Value.Json);
        Assert.Equal("etag", value.Version);
    }
    [Fact]
    public void PrimaryConfirmedAbsenceIsNotAStoredProcedureFailure()
    {
        Assert.Null(new PrimaryReadResult(1, false, null, null).Validate("pk", "id", null));
    }
    [Fact]
    public void SchemaIdentityVersionAndSessionEvidenceMustBeValidBeforeServingContent()
    {
        foreach (var result in new[] {
            new PrimaryReadResult(0, true, Document, "etag"),
            new PrimaryReadResult(1, true, Document with { Pk = "other" }, "etag"),
            new PrimaryReadResult(1, true, Document with { Id = "other" }, "etag"),
            new PrimaryReadResult(1, true, null, "etag"),
            new PrimaryReadResult(1, true, Document, null),
            new PrimaryReadResult(1, false, Document, null)
        })
            Assert.Equal("primary_read_invalid", Assert.Throws<AppError>(() =>
                result.Validate("tenant|resource", "member", "opaque-token")).Code);
        Assert.Equal("primary_read_invalid", Assert.Throws<AppError>(() =>
            new PrimaryReadResult(1, true, Document, "etag").Validate("tenant|resource", "member", null)).Code);
    }
    [Theory]
    [InlineData("Session", "Session", 1, 1, true)]
    [InlineData("Session", "Strong", 1, 1, true)]
    [InlineData("", "Session", 1, 1, false)]
    [InlineData("Strong", "Strong", 1, 1, false)]
    [InlineData("Strong", "Session", 1, 1, false)]
    [InlineData("SessionPrimary", "Session", 1, 1, false)]
    [InlineData("SessionPrimary", "Strong", 1, 1, false)]
    [InlineData("Session", "Eventual", 1, 1, false)]
    [InlineData("Session", "Session", 2, 1, false)]
    [InlineData("Session", "Session", 2, 2, false)]
    [InlineData("Unsupported", "Session", 1, 1, false)]
    public void StorageProfileIsValidatedAtRuntimeNotOnlyByTheDeploymentScript(string profile, string account,
        int reads, int writes, bool accepted)
    {
        var options = new LiveOptions { ReadConsistency = profile };
        if (accepted) options.ValidateStorageProfile(account, reads, writes);
        else Assert.Throws<InvalidOperationException>(() => options.ValidateStorageProfile(account, reads, writes));
    }
}
