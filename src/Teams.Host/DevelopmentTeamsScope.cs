namespace Teams.Host;

public static class DevelopmentTeamsScope
{
    public const string ResourcePrefix = "Teams Reference Validation";
    public static readonly string[] Scopes = ["Chat.Read", "ChatMessage.Send", "Channel.ReadBasic.All", "ChannelMessage.Read.All", "ChannelMessage.Send"];
}

public sealed record OpenDirectChat(string PersonId);
