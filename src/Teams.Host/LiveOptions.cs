using Teams.Core;
using System.Text.Json.Serialization;

namespace Teams.Host;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LiveOptions
{
    public string ApprovalReference { get; set; } = "";
    public string ReadConsistency { get; set; } = "";
    public string AuthorityMode { get; set; } = "Policy";
    public string EntitlementProvider { get; set; } = "External";
    public GraphPolicyOptions? GraphPolicy { get; set; }
    public ChannelDiscoveryOptions ChannelDiscovery { get; set; } = new();
    public Dictionary<string, string> RoleIdentityClientIds { get; set; } = [];
    public string[] HostedRoles { get; set; } = [];
    public string[] SyntheticActorIds { get; set; } = [];
    public DateTimeOffset SyntheticNotBefore { get; set; }
    public Dictionary<string, DateTimeOffset> SyntheticHistoryFrom { get; set; } = [];
    public Dictionary<string, string> SyntheticResourceOwners { get; set; } = [];
    [JsonIgnore] public bool DelegatedSynthetic => AuthorityMode == "DelegatedSynthetic";
    public bool Hosts(string role, string hostRole) => HostedRoles.Length == 0 ? hostRole == role : HostedRoles.Contains(role);
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
    public int? GraphIntervalMilliseconds { get; set; }
    public int? DataIntervalMilliseconds { get; set; }
    public GraphRetryOptions GraphRetry { get; set; } = new();
    public int QueueConcurrency { get; set; } = 8;
    public ApprovedResource[] Resources { get; set; } = [];
    public ApprovedDirectChat[] DirectChats { get; set; } = [];
    public SubscriptionScope[] SubscriptionScopes { get; set; } = [];
    public void Validate(string tenant, bool local, bool localAuth, string role)
    {
        if (ReadConsistency != "Session")
            throw new InvalidOperationException("Live ReadConsistency must explicitly select Session; migrate legacy profiles before deployment.");
        if (!Guid.TryParse(tenant, out var tenantId) || tenantId == Guid.Empty ||
            !Guid.TryParse(SyncClientId, out var sync) || sync == Guid.Empty ||
            !Guid.TryParse(ControlIdentityClientId, out var identity) || identity == Guid.Empty ||
            string.IsNullOrWhiteSpace(ApprovalReference) || WriterEpoch <= 0 || local || localAuth)
            throw new InvalidOperationException("Live mode requires recorded approvals, Entra/Cosmos, explicit client identities and a positive writer epoch.");
        if (role is not ("Api" or "Receiver" or "Projector" or "Delivery" or "Control"))
            throw new InvalidOperationException("Live mode requires an independently deployed supported role.");
        ValidateHosting(role, tenant);
        ChannelDiscovery.Validate(this);
        if (EntitlementProvider is not ("External" or "Graph") || DelegatedSynthetic && EntitlementProvider != "External")
            throw new InvalidOperationException("Select Graph or External policy under service authentication; synthetic hosting cannot select a policy provider.");
        foreach (var url in DelegatedSynthetic || EntitlementProvider == "Graph" ? new[] { NotificationUrl, LifecycleUrl } :
            new[] { NotificationUrl, LifecycleUrl, EntitlementAuthority })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.Query.Length != 0 || uri.IsLoopback)
                throw new InvalidOperationException("Live callbacks and policy authority require approved public HTTPS URLs.");
        if (!DelegatedSynthetic && EntitlementProvider == "External" && string.IsNullOrWhiteSpace(EntitlementScope) ||
            string.IsNullOrWhiteSpace(NotificationCertificateId) ||
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
        if (GraphRetry is null) throw new InvalidOperationException("Live GraphRetry settings cannot be null.");
        GraphRetry.Validate();
        if (QueueConcurrency is < 1 or > 64 || Resources.Length is < 1 or > 10000 ||
            Resources.Select(r => r.Id).Distinct().Count() != Resources.Length ||
            Resources.Select(r => r.Path).Distinct().Count() != Resources.Length ||
            Resources.Any(r => !System.Text.RegularExpressions.Regex.IsMatch(r.Id ?? "",
                "^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new InvalidOperationException("Live resource registry or worker concurrency is invalid.");
        if (SubscriptionScopes.Length is < 1 or > 100 || SubscriptionScopes.Select(s => s.Id).Distinct().Count() != SubscriptionScopes.Length ||
            SubscriptionScopes.Any(s => string.IsNullOrWhiteSpace(s.Id) ||
                (s.Path is not ("chats/getAllMessages" or "teams/getAllMessages") && !Resources.Any(r => r.Path == s.Path))) ||
            Resources.Any(r => !SubscriptionScopes.Any(s => s.Covers(r))))
            throw new InvalidOperationException("Every approved resource needs an explicit supported subscription scope.");
        ValidateDirectChats();
        if (EntitlementProvider == "Graph")
        {
            if (GraphPolicy is null) throw new InvalidOperationException("Graph policy requires its explicit approved source/admission contract.");
            if (SyntheticActorIds.Length != 0 || SyntheticHistoryFrom.Count != 0 || SyntheticResourceOwners.Count != 0 || SyntheticNotBefore != default)
                throw new InvalidOperationException("Remove legacy synthetic actor, owner and bootstrap settings from a service-authenticated configuration.");
            GraphPolicy.Validate(Resources, DirectChats);
            var roles = new[] { "Api", "Receiver", "Projector", "Delivery", "Control" };
            if (RoleIdentityClientIds is null || RoleIdentityClientIds.Count != roles.Length || roles.Any(value => !RoleIdentityClientIds.ContainsKey(value)) ||
                RoleIdentityClientIds.Values.Any(value => !Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) ||
                RoleIdentityClientIds.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roles.Length ||
                RoleIdentityClientIds["Control"] != ControlIdentityClientId ||
                SubscriptionScopes.Any(scope => !Resources.Any(resource => resource.Path == scope.Path)))
                throw new InvalidOperationException("Graph policy requires five distinct role identities and only approved per-resource subscriptions.");
        }
        if (DelegatedSynthetic && (Resources.Length is < 2 or > 5 || Resources.Count(r => r.Kind == "channel") != 1 ||
            Resources.Any(r => !r.Id.StartsWith("graph-pilot-", StringComparison.Ordinal)) ||
            SubscriptionScopes.Any(scope => !Resources.Any(resource => resource.Path == scope.Path)) ||
            Resources.Any(resource => !SyntheticResourceOwners.TryGetValue(resource.Id, out var owner) ||
                !SyntheticActorIds.Contains(owner) || !SyntheticHistoryFrom.TryGetValue(resource.Id, out var from) ||
                from < SyntheticNotBefore || from > DateTimeOffset.UtcNow) ||
            DirectChats.Any(pair => !SyntheticActorIds.Contains(pair.FirstActorId) || !SyntheticActorIds.Contains(pair.SecondActorId))))
            throw new InvalidOperationException("DelegatedSynthetic is restricted to its explicit test resources, per-resource owners and history cutoffs.");
        if (DelegatedSynthetic && Resources.Where(resource => !DirectChats.Any(pair => pair.ResourceId == resource.Id))
            .Any(resource => !resource.Title.StartsWith(DevelopmentTeamsScope.ResourcePrefix, StringComparison.Ordinal)))
            throw new InvalidOperationException("The synthetic group/channel must use the dedicated test-resource title prefix.");
    }
    public bool Admits(string actorId) => DelegatedSynthetic ? SyntheticActorIds.Contains(actorId) :
        EntitlementProvider != "Graph" || GraphPolicy?.AdmittedUserIds.Contains(actorId) == true;
    public void ValidateIdentityBindings(string? webClientId, string? runtimeClientId, string role)
    {
        if (EntitlementProvider != "Graph") return;
        if (!Guid.TryParseExact(webClientId, "D", out var web) || web == Guid.Empty ||
            SyncClientId == webClientId || !RoleIdentityClientIds.TryGetValue(role, out var expected) ||
            runtimeClientId != expected)
            throw new InvalidOperationException("Service synchronization must use a separate sync application and the workload's own managed identity.");
    }
    public void ValidateHosting(string hostRole, string tenant)
    {
        if (AuthorityMode is not ("Policy" or "DelegatedSynthetic"))
            throw new InvalidOperationException("Select the explicit Policy or DelegatedSynthetic authority mode.");
        var roles = HostedRoles.Length == 0 ? new[] { hostRole } : HostedRoles;
        if (!roles.Contains(hostRole) || roles.Distinct().Count() != roles.Length ||
            roles.Any(value => value is not ("Api" or "Receiver" or "Projector" or "Delivery" or "Control")) ||
            !DelegatedSynthetic && roles.Length != 1)
            throw new InvalidOperationException("Combined live hosting is allowed only in the explicit bounded synthetic profile.");
        if (DelegatedSynthetic && (ReadConsistency != "Session" ||
            !Guid.TryParse(tenant, out _) || SyntheticActorIds.Length != 3 ||
            SyntheticActorIds.Distinct(StringComparer.Ordinal).Count() != 3 ||
            SyntheticActorIds.Any(actor => !Guid.TryParseExact(actor, "D", out var value) || value == Guid.Empty) ||
            SyntheticNotBefore == default || SyntheticNotBefore > DateTimeOffset.UtcNow ||
            roles.Contains("Api") && !roles.Order().SequenceEqual(new[] { "Api", "Control", "Receiver" }) ||
            !roles.Contains("Api") && !roles.Order().SequenceEqual(new[] { "Delivery", "Projector" })))
            throw new InvalidOperationException("DelegatedSynthetic requires exactly three real test identities and the fixed API/control/receiver or projector/delivery topology.");
    }
    public void ValidateDirectChats()
    {
        if (DirectChats.Length > 10000 ||
            DirectChats.Select(pair => pair.ResourceId).Distinct(StringComparer.Ordinal).Count() != DirectChats.Length ||
            DirectChats.Select(pair => string.Join(":", new[] { pair.FirstActorId, pair.SecondActorId }.Order(StringComparer.Ordinal)))
                .Distinct(StringComparer.Ordinal).Count() != DirectChats.Length ||
            DirectChats.Any(pair => !Guid.TryParseExact(pair.FirstActorId, "D", out var first) || first == Guid.Empty ||
                !Guid.TryParseExact(pair.SecondActorId, "D", out var second) || second == Guid.Empty || first == second ||
                !Resources.Any(resource => resource.Id == pair.ResourceId && resource.Kind == "chat")) ||
            DirectChats.SelectMany(pair => new[] { pair.FirstActorId, pair.SecondActorId }).GroupBy(id => id)
                .Any(group => group.Count() > 100))
            throw new InvalidOperationException("Direct-chat discovery requires unique approved chat/pair mappings and at most 100 contacts per actor.");
    }
    public void ValidateStorageProfile(string accountConsistency, int readableRegions, int writableRegions)
    {
        if (ReadConsistency != "Session" || accountConsistency is not ("Session" or "Strong") ||
            readableRegions != 1 || writableRegions != 1)
            throw new InvalidOperationException("Explicit Session reads require one readable/writable region and Session or Strong account consistency.");
    }
}
public sealed class ChannelDiscoveryOptions
{
    public bool RegistryEnabled { get; set; }
    public bool Enabled { get; set; }
    public bool SendEnabled { get; set; }
    public string ApprovalReference { get; set; } = "";
    public DateTimeOffset HistoryFrom { get; set; }
    public void Validate(LiveOptions options)
    {
        if ((Enabled || SendEnabled) && !RegistryEnabled || RegistryEnabled && (options.AuthorityMode != "Policy" ||
            options.EntitlementProvider != "Graph" || string.IsNullOrWhiteSpace(ApprovalReference) ||
            ApprovalReference.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase) ||
            HistoryFrom == default || HistoryFrom > DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Channel discovery requires the approved service Graph registry and explicit history policy.");
    }
}
