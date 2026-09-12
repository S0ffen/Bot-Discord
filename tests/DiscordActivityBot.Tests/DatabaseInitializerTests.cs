using DiscordActivityBot.Configuration;
using DiscordActivityBot.Data;
using DiscordActivityBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DiscordActivityBot.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task StartAsync_CreatesNewActivityTablesAndSeedsShop()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"discord-activity-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<BotDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new TestDbContextFactory(dbOptions);
            var initializer = new DatabaseInitializer(
                factory,
                Options.Create(new ShopOptions()),
                NullLogger<DatabaseInitializer>.Instance);

            await initializer.StartAsync(CancellationToken.None);

            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(3, await db.ShopItems.CountAsync(x => x.IsEnabled));
            Assert.Equal(0, await db.GuildSettings.CountAsync());
            Assert.Equal(0, await db.UserMessageStats.CountAsync());
            Assert.Equal(0, await db.ChannelActivityStats.CountAsync());
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task StartAsync_MigratesMessageCountAndRemovesUnwantedXpTables()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"discord-activity-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<BotDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new TestDbContextFactory(dbOptions);

            await using (var setupDb = await factory.CreateDbContextAsync())
            {
                await setupDb.Database.EnsureCreatedAsync();
                var user = new BotUser
                {
                    GuildId = "1",
                    DiscordUserId = "2",
                    LastKnownDisplayName = "Tester",
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                setupDb.Users.Add(user);
                await setupDb.SaveChangesAsync();
                await setupDb.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE "UserProgress" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "BotUserId" INTEGER NOT NULL,
                        "Experience" INTEGER NOT NULL DEFAULT 0,
                        "Level" INTEGER NOT NULL DEFAULT 1,
                        "MessageCount" INTEGER NOT NULL DEFAULT 0,
                        "LastMessageXpAtUtc" TEXT NULL
                    );
                    """);
                await setupDb.Database.ExecuteSqlAsync(
                    $"INSERT INTO \"UserProgress\" (\"BotUserId\", \"MessageCount\") VALUES ({user.Id}, 12);");
                await setupDb.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE "LevelRoleRewards" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "GuildId" TEXT NOT NULL,
                        "RequiredLevel" INTEGER NOT NULL,
                        "RoleId" TEXT NOT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL
                    );
                    """);
            }

            var initializer = new DatabaseInitializer(
                factory,
                Options.Create(new ShopOptions()),
                NullLogger<DatabaseInitializer>.Instance);
            await initializer.StartAsync(CancellationToken.None);

            await using var verifyDb = await factory.CreateDbContextAsync();
            Assert.Equal(12, await verifyDb.UserMessageStats.Select(x => x.MessageCount).SingleAsync());
            Assert.Equal(0, await CountTableAsync(verifyDb, "UserProgress"));
            Assert.Equal(0, await CountTableAsync(verifyDb, "LevelRoleRewards"));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task StartAsync_AddsJailRoleAndPresenceColumnsWithoutDeletingExistingRows()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"discord-activity-{Guid.NewGuid():N}.db");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<BotDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var factory = new TestDbContextFactory(dbOptions);

            await using (var setupDb = await factory.CreateDbContextAsync())
            {
                await setupDb.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE "ShopItems" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "Key" TEXT NOT NULL,
                        "Name" TEXT NOT NULL,
                        "Description" TEXT NOT NULL,
                        "Price" INTEGER NOT NULL,
                        "Type" INTEGER NOT NULL,
                        "IsEnabled" INTEGER NOT NULL
                    );
                    """);
                await setupDb.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE "GuildSettings" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "GuildId" TEXT NOT NULL,
                        "VoiceLogChannelId" TEXT NULL,
                        "JailVoiceChannelId" TEXT NULL,
                        "UpdatedAtUtc" TEXT NOT NULL
                    );
                    INSERT INTO "GuildSettings"
                        ("GuildId", "VoiceLogChannelId", "JailVoiceChannelId", "UpdatedAtUtc")
                    VALUES ('1', '10', '20', '2026-09-10 12:00:00');
                    """);
                await setupDb.Database.ExecuteSqlRawAsync(
                    """
                    CREATE TABLE "VoiceRewardEffects" (
                        "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        "PurchaseId" INTEGER NOT NULL,
                        "GuildId" TEXT NOT NULL,
                        "TargetDiscordUserId" TEXT NOT NULL,
                        "Type" INTEGER NOT NULL,
                        "OriginalChannelId" TEXT NULL,
                        "JailChannelId" TEXT NOT NULL,
                        "StartsAtUtc" TEXT NOT NULL,
                        "ExpiresAtUtc" TEXT NOT NULL,
                        "EndedAtUtc" TEXT NULL
                    );
                    INSERT INTO "VoiceRewardEffects"
                        ("PurchaseId", "GuildId", "TargetDiscordUserId", "Type", "OriginalChannelId",
                         "JailChannelId", "StartsAtUtc", "ExpiresAtUtc")
                    VALUES (1, '1', '2', 2, '10', '20', '2026-09-10 12:00:00', '2026-09-10 12:05:00');
                    """);
            }

            var initializer = new DatabaseInitializer(
                factory,
                Options.Create(new ShopOptions()),
                NullLogger<DatabaseInitializer>.Instance);
            await initializer.StartAsync(CancellationToken.None);

            await using var verifyDb = await factory.CreateDbContextAsync();
            var settings = await verifyDb.GuildSettings.SingleAsync();
            Assert.Equal("20", settings.JailVoiceChannelId);
            Assert.Null(settings.JailRoleId);

            var effect = await verifyDb.VoiceRewardEffects.SingleAsync();
            Assert.Equal("[]", effect.OriginalRoleIdsJson);
            Assert.Equal(0, effect.RequiredDurationSeconds);
            Assert.Equal(0, effect.ServedDurationSeconds);
            Assert.Null(effect.JailRoleId);
            Assert.Null(effect.JailPresenceStartedAtUtc);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static Task<long> CountTableAsync(BotDbContext db, string tableName) =>
        db.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS \"Value\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = {0}",
                tableName)
            .SingleAsync();

    private sealed class TestDbContextFactory(DbContextOptions<BotDbContext> options)
        : IDbContextFactory<BotDbContext>
    {
        public BotDbContext CreateDbContext() => new(options);

        public Task<BotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
