using Teams.Core;
using Teams.Host;

namespace Teams.Tests;

public sealed class BoundedJsonTests
{
    [Fact]
    public async Task DependencyPayloadIsBoundedEvenWithoutAContentLengthHeader()
    {
        using var content = new UnknownLengthContent(System.Text.Encoding.UTF8.GetBytes(new string('x', 100)));
        Assert.Null(content.Headers.ContentLength);
        Assert.Equal("dependency_payload_too_large", (await Assert.ThrowsAsync<AppError>(() =>
            BoundedJson.ReadAsync(content, 32, default))).Code);
    }
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(data).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(data));
    }
    [Fact]
    public async Task DependencyPayloadHonorsCancellationAndReturnsOwnedJson()
    {
        using var content = new StringContent("""{"value":42}""");
        var result = await BoundedJson.ReadAsync(content, 100, default);
        Assert.Equal(42, result.GetProperty("value").GetInt32());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var other = new StringContent("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BoundedJson.ReadAsync(other, 100, canceled.Token));
    }
}
