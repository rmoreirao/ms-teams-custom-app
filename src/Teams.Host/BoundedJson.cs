using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Teams.Core;

namespace Teams.Host;

public static class BoundedJson
{
    public static async Task<JsonElement> ReadAsync(HttpContent content, int maximumBytes, CancellationToken ct)
    {
        if (maximumBytes <= 0 || maximumBytes == int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (content.Headers.ContentLength > maximumBytes)
            throw new AppError(503, "dependency_payload_too_large", "Dependency payload exceeds the approved processing limit.");
        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)output.Length)), ct);
                if (count == 0) break;
                output.Write(buffer, 0, count);
                if (output.Length > maximumBytes)
                    throw new AppError(503, "dependency_payload_too_large", "Dependency payload exceeds the approved processing limit.");
            }
            using var document = JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length), new() { MaxDepth = 32 });
            return document.RootElement.Clone();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            if (output.TryGetBuffer(out var plaintext)) CryptographicOperations.ZeroMemory(plaintext.AsSpan());
        }
    }
}
