using Teams.Core;

namespace Teams.Host;

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
    public int GraphIntervalMilliseconds { get; set; } = 100;
    public int DataIntervalMilliseconds { get; set; } = 250;
    public int QueueConcurrency { get; set; } = 8;
    public ApprovedResource[] Resources { get; set; } = [];
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
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback)
                throw new InvalidOperationException("Live callbacks and policy authority require approved public HTTPS URLs.");
        if (string.IsNullOrWhiteSpace(EntitlementScope) || string.IsNullOrWhiteSpace(NotificationCertificateId) ||
            NotificationCertificate.Length == 0 || !NotificationKeys.ContainsKey(NotificationCertificateId))
            throw new InvalidOperationException("Live policy scope and notification encryption references are required.");
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(NotificationCertificate));
        using var rsa = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPublicKey(certificate);
        if (rsa is null || rsa.KeySize < 2048 || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("Notification encryption requires an unexpired RSA certificate of at least 2048 bits.");
        foreach (var key in NotificationKeys.Values)
            if (!Uri.TryCreate(key, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is not ["keys", _, _])
                throw new InvalidOperationException("Notification keys must use explicit versioned Key Vault key references.");
        if (GraphIntervalMilliseconds < 100 || DataIntervalMilliseconds < GraphIntervalMilliseconds * 2 ||
            QueueConcurrency is < 1 or > 64 || Resources.Length is < 1 or > 10000 ||
            Resources.Select(r => r.Id).Distinct().Count() != Resources.Length ||
            Resources.Select(r => r.Path).Distinct().Count() != Resources.Length ||
            Resources.Any(r => string.IsNullOrWhiteSpace(r.Id)))
            throw new InvalidOperationException("Live resource registry, concurrency or conservative Graph budgets are invalid.");
    }
}
