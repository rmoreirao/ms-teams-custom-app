using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Teams.Core;

namespace Teams.Host;

public static partial class GraphMessages
{
    public static string NormalizeResource(string path)
    {
        if (path.Length > 4096 || path.Contains('?') || path.Contains('#') || path.Contains('\\'))
            throw new AppError(400, "notification_resource_invalid", "Notification resource is invalid.");
        var normalized = ODataSegment().Replace(path.TrimStart('/'), m =>
            $"{m.Groups[1].Value}/{Uri.EscapeDataString(Uri.UnescapeDataString(m.Groups[2].Value.Replace("''", "'")))}");
        return string.Join('/', normalized.Split('/').Select(part => Uri.EscapeDataString(Uri.UnescapeDataString(part))));
    }
    public static Message Parse(JsonElement data, string? parent = null)
    {
        var id = data.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id)) throw new JsonException("Graph message ID is required.");
        var created = data.GetProperty("createdDateTime").GetDateTimeOffset();
        var modified = data.TryGetProperty("lastModifiedDateTime", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetDateTimeOffset() : created;
        var deleted = data.TryGetProperty("deletedDateTime", out var deletedAt) && deletedAt.ValueKind == JsonValueKind.String;
        if (deleted && deletedAt.GetDateTimeOffset() > modified) modified = deletedAt.GetDateTimeOffset();
        var actor = "";
        var author = "Teams";
        if (data.TryGetProperty("from", out var from) && from.ValueKind == JsonValueKind.Object &&
            from.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            actor = user.TryGetProperty("id", out var actorId) ? actorId.GetString() ?? "" : "";
            author = user.TryGetProperty("displayName", out var name) ? name.GetString() ?? author : author;
        }
        var content = "";
        string? html = null;
        if (!deleted && data.TryGetProperty("body", out var body))
        {
            content = body.GetProperty("content").GetString() ?? "";
            if (content.Length > 65536) throw new JsonException("Message exceeds the approved text projection limit.");
            if (body.TryGetProperty("contentType", out var type) && type.GetString() == "html")
            {
                html = content;
                content = WebUtility.HtmlDecode(HtmlTags().Replace(LineBreaks().Replace(content, "\n"), ""));
            }
        }
        var version = data.TryGetProperty("etag", out var etag) && etag.GetString() is { Length: > 0 } tag ? tag :
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText())));
        var reply = data.TryGetProperty("replyToId", out var replyId) && replyId.ValueKind == JsonValueKind.String ? replyId.GetString() : null;
        if (parent is not null && reply is not null && parent != reply) throw new JsonException("Reply context does not match.");
        var attachments = data.TryGetProperty("attachments", out var attached) && attached.ValueKind == JsonValueKind.Array &&
            attached.GetArrayLength() > 0;
        var notice = deleted ? null : attachments ? "Attachments are available in Teams." :
            html is not null && EmbeddedContent().IsMatch(html) ? "Embedded content is available in Teams." : null;
        var url = !deleted && data.TryGetProperty("webUrl", out var webUrl) && webUrl.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(webUrl.GetString(), UriKind.Absolute, out var source) && source.Scheme == "https" &&
            source.Port == 443 && source.UserInfo.Length == 0 && source.Host is ("teams.microsoft.com" or "teams.cloud.microsoft")
            ? source.AbsoluteUri : null;
        return new(id, actor, author, content, created, modified, parent ?? reply, deleted, version, notice, url, html);
    }
    [GeneratedRegex(@"([A-Za-z]+)\('((?:[^']|'')*)'\)", RegexOptions.CultureInvariant)]
    private static partial Regex ODataSegment();
    [GeneratedRegex(@"<br\s*/?>|</(?:p|div)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex LineBreaks();
    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant, 100)]
    private static partial Regex HtmlTags();
    [GeneratedRegex(@"<(?:img|attachment|video|audio|iframe)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex EmbeddedContent();
}
