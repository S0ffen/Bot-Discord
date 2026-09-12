namespace DiscordActivityBot.Configuration;

public sealed class ShopOptions
{
    public const string SectionName = "Shop";

    public long MutePrice { get; set; } = 500;
    public int MuteDurationMinutes { get; set; } = 1;
    public long JailPrice { get; set; } = 1500;
    public int JailDurationMinutes { get; set; } = 5;
    public long DisconnectPrice { get; set; } = 750;
}
