using System.Globalization;
using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordActivityBot.Services;

public sealed class EconomyService(IDbContextFactory<BotDbContext> dbContextFactory)
{
    public async Task<long> GetBalanceAsync(ulong guildId, ulong discordUserId, CancellationToken cancellationToken = default)
    {
        var guildIdText = Id(guildId);
        var userIdText = Id(discordUserId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Users
            .Where(x => x.GuildId == guildIdText && x.DiscordUserId == userIdText)
            .Select(x => (long?)x.Points)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(
        ulong guildId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = Id(guildId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Users
            .Where(x => x.GuildId == guildIdText)
            .OrderByDescending(x => x.Points)
            .ThenBy(x => x.Id)
            .Take(limit)
            .Select(x => new LeaderboardEntry(x.DiscordUserId, x.LastKnownDisplayName, x.Points))
            .ToListAsync(cancellationToken);
    }

    public async Task<VoiceStats> GetVoiceStatsAsync(
        ulong guildId,
        ulong discordUserId,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = Id(guildId);
        var userIdText = Id(discordUserId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var sessions = db.VoiceSessions
            .Where(x => x.GuildId == guildIdText && x.DiscordUserId == userIdText && x.LeftAtUtc != null);

        return new VoiceStats(
            await sessions.LongCountAsync(cancellationToken),
            await sessions.SumAsync(x => (long?)x.DurationSeconds, cancellationToken) ?? 0,
            await sessions.SumAsync(x => (long?)x.AwardedPoints, cancellationToken) ?? 0);
    }

    internal static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);
}

public sealed record LeaderboardEntry(string DiscordUserId, string DisplayName, long Points);
public sealed record VoiceStats(long Sessions, long TotalSeconds, long AwardedPoints);
