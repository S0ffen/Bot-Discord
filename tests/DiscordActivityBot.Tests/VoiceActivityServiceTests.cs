using Discord.WebSocket;
using DiscordActivityBot.Configuration;
using DiscordActivityBot.Data;
using DiscordActivityBot.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DiscordActivityBot.Tests;

public sealed class VoiceActivityServiceTests
{
    [Fact]
    public async Task StopAsync_AddsOnlyRemainingPointsAfterSpendingLiveBalance()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<BotDbContext>().UseSqlite(connection).Options;
        var factory = new TestDbContextFactory(dbOptions);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
        }

        using var client = new DiscordSocketClient();
        var service = new VoiceActivityService(
            client, factory, Options.Create(new BotOptions()), new GuildSettingsService(factory),
            new EconomyMutationLock(), NullLogger<VoiceActivityService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var joinedAt = DateTime.UtcNow.AddSeconds(-190);
            await using (var db = factory.CreateDbContext())
            {
                var session = new VoiceSession
                {
                    GuildId = "1",
                    DiscordUserId = "2",
                    ChannelId = "3",
                    ChannelName = "Test",
                    JoinedAtUtc = joinedAt,
                    LastObservedAtUtc = joinedAt,
                    BotUser = new BotUser
                    {
                        GuildId = "1",
                        DiscordUserId = "2",
                        LastKnownDisplayName = "Test user"
                    }
                };
                db.VoiceSessions.Add(session);
                VoiceSessionClock.CheckpointPresence(session, joinedAt.AddMinutes(2), 1, []);
                await db.SaveChangesAsync();
                Assert.Null(session.LeftAtUtc);
            }

            var economy = new EconomyService(factory);
            Assert.Equal(2, await economy.GetBalanceAsync(1, 2));
            Assert.Equal(2, Assert.Single(await economy.GetLeaderboardAsync(1, 10)).Points);
            await using (var db = factory.CreateDbContext())
            {
                var user = await db.Users.SingleAsync();
                user.Points -= 2;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, await new EconomyService(factory).GetBalanceAsync(1, 2));
        await using var verifyDb = factory.CreateDbContext();
        var completedSession = await verifyDb.VoiceSessions.SingleAsync();
        Assert.NotNull(completedSession.LeftAtUtc);
        Assert.Equal(3, completedSession.AwardedPoints);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task StartAsync_SettlesOnlyUncreditedMinutesUpToLastObservation(
        bool alreadyCreditedAndSpent, long expectedBalance)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<BotDbContext>().UseSqlite(connection).Options;
        var factory = new TestDbContextFactory(dbOptions);
        var joinedAt = DateTime.UtcNow.AddHours(-1);
        var lastObservedAt = joinedAt.AddSeconds(140);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.VoiceSessions.Add(new VoiceSession
            {
                GuildId = "1",
                DiscordUserId = "2",
                ChannelId = "3",
                ChannelName = "Test",
                JoinedAtUtc = joinedAt,
                LastObservedAtUtc = lastObservedAt,
                DurationSeconds = alreadyCreditedAndSpent ? 90 : 0,
                AwardedPoints = alreadyCreditedAndSpent ? 1 : 0,
                BotUser = new BotUser
                {
                    GuildId = "1",
                    DiscordUserId = "2",
                    LastKnownDisplayName = "Test user",
                    Points = 0
                }
            });
            await db.SaveChangesAsync();
        }

        using var client = new DiscordSocketClient();
        var service = new VoiceActivityService(
            client, factory, Options.Create(new BotOptions()), new GuildSettingsService(factory),
            new EconomyMutationLock(), NullLogger<VoiceActivityService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var economy = new EconomyService(factory);
            Assert.Equal(expectedBalance, await economy.GetBalanceAsync(1, 2));
            await using var db = factory.CreateDbContext();
            var session = await db.VoiceSessions.SingleAsync();
            Assert.Equal(lastObservedAt, session.LeftAtUtc);
            Assert.Equal(140, session.DurationSeconds);
            Assert.Equal(2, session.AwardedPoints);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // Kolejny start nie dopisuje nagrody za już zamkniętą sesję.
        await service.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(expectedBalance, await new EconomyService(factory).GetBalanceAsync(1, 2));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<BotDbContext> options)
        : IDbContextFactory<BotDbContext>
    {
        public BotDbContext CreateDbContext() => new(options);

        public Task<BotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
