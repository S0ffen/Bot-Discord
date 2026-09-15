namespace DiscordActivityBot.Configuration;

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string Token { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = "data/activity-bot.db";
    public long PointsPerMinute { get; set; } = 1;
    public int HeartbeatSeconds { get; set; } = 30;
    public bool IgnoreAfkChannel { get; set; } = true;
    public List<ulong> IgnoredVoiceChannelIds { get; set; } = [];
}
