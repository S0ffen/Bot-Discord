using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordActivityBot.Services;

internal static class BotUserStore
{
    public static async Task<BotUser> GetOrCreateAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        string displayName,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = EconomyService.Id(guildId);
        var userIdText = EconomyService.Id(userId);
        var user = await db.Users.SingleOrDefaultAsync(
            x => x.GuildId == guildIdText && x.DiscordUserId == userIdText,
            cancellationToken);
        if (user is not null)
        {
            user.LastKnownDisplayName = displayName;
            user.UpdatedAtUtc = now;
            return user;
        }

        user = new BotUser
        {
            GuildId = guildIdText,
            DiscordUserId = userIdText,
            LastKnownDisplayName = displayName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Users.Add(user);
        return user;
    }
}
