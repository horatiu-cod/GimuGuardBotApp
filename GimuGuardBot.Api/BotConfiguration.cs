namespace GimuGuardBot.Api;

public class BotConfiguration
{
    public const string ConfigurationSectionName = "BotConfiguration";
    public string BotToken { get; init; } = default!;
    public string PublicChannelUsername { get; init; } = default!;
    public long PrivateGroupId { get; init; } // The ID of the group/supergroup where verification happens
}
