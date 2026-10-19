using Teams.Core;
using System.Text.Json.Serialization;

namespace Teams.Host;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LiveOptions
{
    public string ApprovalReference { get; set; } = "";
    public string SyncClientId { get; set; } = "";
    public string ControlIdentityClientId { get; set; } = "";
    public string NotificationUrl { get; set; } = "";
    public string LifecycleUrl { get; set; } = "";
    public string NotificationCertificateId { get; set; } = "";
    public string NotificationCertificate { get; set; } = "";
    public Dictionary<string, string> NotificationKeys { get; set; } = [];
    public string EntitlementAuthority { get; set; } = "";
    public string EntitlementScope { get; set; } = "";
    public long WriterEpoch { get; set; }
    public bool AdmissionEnabled { get; set; }
    public bool SynchronizationEnabled { get; set; }
    public int GraphIntervalMilliseconds { get; set; } = 100;
    public int DataIntervalMilliseconds { get; set; } = 250;
    public int QueueConcurrency { get; set; } = 8;
    public ApprovedResource[] Resources { get; set; } = [];
    public SubscriptionScope[] SubscriptionScopes { get; set; } = [];
    public void Validate(string tenant, bool local, bool localAuth, string role)
    {
        if (!Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(SyncClientId, out var sync) || sync == Guid.Empty ||
            !Guid.TryParse(ControlIdentityClientId, out var identity) || identity == Guid.Empty ||
            string.IsNullOrWhiteSpace(ApprovalReference) || WriterEpoch <= 0 || local || localAuth)
            throw new InvalidOperationException("Live mode requires recorded approvals, Entra/Cosmos, explicit client identities and a positive writer epoch.");
        if (role is not ("Api" or "Receiver" or "Projector" or "Delivery" or "Control"))
            throw new InvalidOperationException("Live mode requires an independently deployed supported role.");
        foreach (var url in new[] { NotificationUrl, LifecycleUrl, EntitlementAuthority })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || uri.IsLoopback)
                throw new InvalidOperationException("Live callbacks and policy authority require approved public HTTPS URLs.");
        if (string.IsNullOrWhiteSpace(EntitlementScope) || string.IsNullOrWhiteSpace(NotificationCertificateId) ||
            NotificationCertificate.Length == 0 || !NotificationKeys.ContainsKey(NotificationCertificateId))
            throw new InvalidOperationException("Live policy scope and notification encryption references are required.");
        if (AdmissionEnabled && !SynchronizationEnabled)
            throw new InvalidOperationException("User admission cannot precede approved synchronization.");
        if (new Uri(NotificationUrl).AbsolutePath != "/callbacks/graph/notifications" ||
            new Uri(LifecycleUrl).AbsolutePath != "/callbacks/graph/lifecycle")
            throw new InvalidOperationException("Callback URLs must match the implemented notification/lifecycle routes.");
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(NotificationCertificate));
        using var rsa = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(certificate);
        if (rsa is null || rsa.KeySize < 2048 || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("Notification encryption requires an unexpired RSA certificate of at least 2048 bits.");
        foreach (var key in NotificationKeys.Values)
            if (!Uri.TryCreate(key, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["keys", _, _])
                throw new InvalidOperationException("Notification keys must use explicit versioned Key Vault key references.");
        if (GraphIntervalMilliseconds < 100 || DataIntervalMilliseconds < GraphIntervalMilliseconds * 2 ||
            QueueConcurrency is < 1 or > 64 || Resources.Length is < 1 or > 10000 ||
            Resources.Select(r => r.Id).Distinct().Count() != Resources.Length ||
            Resources.Select(r => r.Path).Distinct().Count() != Resources.Length ||
            Resources.Any(r => !System.Text.RegularExpressions.Regex.IsMatch(r.Id ?? "",
                "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("Live resource registry, concurrency or conservative Graph budgets are invalid.");
        if (SubscriptionScopes.Length is < 1 or > 100 || SubscriptionScopes.Select(s => s.Id).Distinct().Count() != SubscriptionScopes.Length ||
            SubscriptionScopes.Any(s => string.IsNullOrWhiteSpace(s.Id) ||
                (s.Path is not ("chats/getAllMessages" or "teams/getAllMessages") && !Resources.Any(r => r.Path == s.Path))) ||
            Resources.Any(r => !SubscriptionScopes.Any(s => s.Covers(r))))
            throw new InvalidOperationException("Every approved resource needs an explicit supported subscription scope.");
    }
}
public sealed record SubscriptionScope(string Id, string Path)
{
    public bool Covers(ApprovedResource resource) => Path == resource.Path ||
        Path == "chats/getAllMessages" && resource.Kind == "chat" ||
        Path == "teams/getAllMessages" && resource.Kind == "channel";
}
