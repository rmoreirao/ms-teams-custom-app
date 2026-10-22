using Teams.Core;
using System.Text.Json.Serialization;

namespace Teams.Host;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GraphPolicyOptions
{
    public string ApprovalReference { get; set; } = "";
    public string WebServicePrincipalId { get; set; } = "";
    public string[] AdmittedUserIds { get; set; } = [];
    public string[] AppRoleIds { get; set; } = [Guid.Empty.ToString("D")];
    public Dictionary<string, DateTimeOffset> HistoryFrom { get; set; } = [];
    public string[] SendEnabledResources { get; set; } = [];
    public void Validate(ApprovedResource[] resources, ApprovedDirectChat[] pairs)
    {
        if (AdmittedUserIds is null || AppRoleIds is null || HistoryFrom is null || SendEnabledResources is null ||
            string.IsNullOrWhiteSpace(ApprovalReference) || ApprovalReference.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(WebServicePrincipalId, "D", out var principal) || principal == Guid.Empty ||
            AdmittedUserIds.Length is < 1 or > 100 || AdmittedUserIds.Distinct().Count() != AdmittedUserIds.Length ||
            AdmittedUserIds.Any(value => !Guid.TryParseExact(value, "D", out var user) || user == Guid.Empty) ||
            AppRoleIds.Length is < 1 or > 20 || AppRoleIds.Distinct().Count() != AppRoleIds.Length ||
            AppRoleIds.Any(value => !Guid.TryParseExact(value, "D", out _)) ||
            HistoryFrom.Count != resources.Length || resources.Any(resource => !HistoryFrom.TryGetValue(resource.Id, out var from) ||
                from == default || from > DateTimeOffset.UtcNow) ||
            SendEnabledResources.Distinct().Count() != SendEnabledResources.Length ||
            SendEnabledResources.Any(id => !resources.Any(resource => resource.Id == id)) ||
            pairs.Any(pair => !AdmittedUserIds.Contains(pair.FirstActorId) || !AdmittedUserIds.Contains(pair.SecondActorId)))
            throw new InvalidOperationException("Graph membership policy requires explicit approval, valid direct-user admission/roles and bounded histories for every approved resource.");
    }
}
