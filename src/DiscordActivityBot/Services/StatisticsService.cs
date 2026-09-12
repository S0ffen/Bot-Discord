using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordActivityBot.Services;

public sealed class StatisticsService(IDbContextFactory<BotDbContext> dbContextFactory)
{
    public async Task<long> GetMessageCountAsync(
        ulong guildId,
        ulong userId,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = EconomyService.Id(guildId);
        var userIdText = EconomyService.Id(userId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserMessageStats
            .Where(x => x.BotUser.GuildId == guildIdText && x.BotUser.DiscordUserId == userIdText)
            .Select(x => (long?)x.MessageCount)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
    }

    public async Task<IReadOnlyList<VoiceLeaderboardEntry>> GetVoiceLeaderboardAsync(
        ulong guildId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = EconomyService.Id(guildId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.VoiceSessions
            .Where(x => x.GuildId == guildIdText && x.LeftAtUtc != null)
            .GroupBy(x => new
            {
                x.DiscordUserId,
                x.BotUser.LastKnownDisplayName
            })
            .Select(group => new VoiceLeaderboardEntry(
                group.Key.DiscordUserId,
                group.Key.LastKnownDisplayName,
                group.Sum(x => x.DurationSeconds)))
            .OrderByDescending(x => x.TotalSeconds)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<ServerStatistics> GetServerStatisticsAsync(
        ulong guildId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = EconomyService.Id(guildId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var channelQuery = db.ChannelActivityStats.Where(x => x.GuildId == guildIdText);
        var userQuery = db.UserMessageStats.Where(x => x.BotUser.GuildId == guildIdText && x.MessageCount > 0);
        var topChannels = await channelQuery
            .OrderByDescending(x => x.MessageCount)
            .ThenBy(x => x.Id)
            .Take(limit)
            .Select(x => new ChannelMessageEntry(x.ChannelId, x.LastKnownChannelName, x.MessageCount))
            .ToListAsync(cancellationToken);
        var topUsers = await userQuery
            .OrderByDescending(x => x.MessageCount)
            .ThenBy(x => x.Id)
            .Take(limit)
            .Select(x => new UserMessageEntry(
                x.BotUser.DiscordUserId,
                x.BotUser.LastKnownDisplayName,
                x.MessageCount))
            .ToListAsync(cancellationToken);

        return new ServerStatistics(
            await channelQuery.SumAsync(x => (long?)x.MessageCount, cancellationToken) ?? 0,
            await userQuery.LongCountAsync(cancellationToken),
            await channelQuery.LongCountAsync(cancellationToken),
            topChannels,
            topUsers);
    }
}

public sealed record VoiceLeaderboardEntry(string DiscordUserId, string DisplayName, long TotalSeconds);
public sealed record ChannelMessageEntry(string ChannelId, string ChannelName, long MessageCount);
public sealed record UserMessageEntry(string DiscordUserId, string DisplayName, long MessageCount);
public sealed record ServerStatistics(
    long TotalMessages,
    long ActiveUsers,
    long ActiveChannels,
    IReadOnlyList<ChannelMessageEntry> TopChannels,
    IReadOnlyList<UserMessageEntry> TopUsers);
