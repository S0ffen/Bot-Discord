using DiscordActivityBot.Configuration;
using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DiscordActivityBot.Services;

public sealed class DatabaseInitializer(
    IDbContextFactory<BotDbContext> dbContextFactory,
    IOptions<ShopOptions> shopOptions,
    ILogger<DatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = shopOptions.Value;
        ShopItemDefinition[] currentItems =
        [
            new("wyciszenie", "Wyciszenie użytkownika",
                $"Wycisza wskazaną osobę na {options.MuteDurationMinutes} min.", options.MutePrice,
                ShopItemType.VoiceMute),
            new("wiezienie", "Wysłanie do więzienia",
                $"Nadaje rolę więzienną i wymaga {options.JailDurationMinutes} min obecności na kanale więzienia.",
                options.JailPrice, ShopItemType.VoiceJail),
            new("wyrzucenie", "Wyrzucenie z kanału",
                "Natychmiast rozłącza wskazaną osobę z kanału głosowego.", options.DisconnectPrice,
                ShopItemType.VoiceDisconnect)
        ];

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        // EnsureCreated nie aktualizuje już istniejącej bazy. Poniższe polecenia
        // bezpiecznie dodają nowe tabele użytkownikom wcześniejszej wersji bota.
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "GuildSettings" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_GuildSettings" PRIMARY KEY AUTOINCREMENT,
                "GuildId" TEXT NOT NULL,
                "VoiceLogChannelId" TEXT NULL,
                "JailVoiceChannelId" TEXT NULL,
                "JailRoleId" TEXT NULL,
                "UpdatedAtUtc" TEXT NOT NULL
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_GuildSettings_GuildId\" ON \"GuildSettings\" (\"GuildId\");",
            cancellationToken);
        await AddColumnIfMissingAsync(
            db,
            "GuildSettings",
            "JailRoleId",
            "ALTER TABLE \"GuildSettings\" ADD COLUMN \"JailRoleId\" TEXT NULL;",
            cancellationToken);

        if (await TableExistsAsync(db, "VoiceRewardEffects", cancellationToken))
        {
            await AddColumnIfMissingAsync(
                db,
                "VoiceRewardEffects",
                "JailRoleId",
                "ALTER TABLE \"VoiceRewardEffects\" ADD COLUMN \"JailRoleId\" TEXT NULL;",
                cancellationToken);
            await AddColumnIfMissingAsync(
                db,
                "VoiceRewardEffects",
                "OriginalRoleIdsJson",
                "ALTER TABLE \"VoiceRewardEffects\" ADD COLUMN \"OriginalRoleIdsJson\" TEXT NOT NULL DEFAULT '[]';",
                cancellationToken);
            await AddColumnIfMissingAsync(
                db,
                "VoiceRewardEffects",
                "RequiredDurationSeconds",
                "ALTER TABLE \"VoiceRewardEffects\" ADD COLUMN \"RequiredDurationSeconds\" INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
            await AddColumnIfMissingAsync(
                db,
                "VoiceRewardEffects",
                "ServedDurationSeconds",
                "ALTER TABLE \"VoiceRewardEffects\" ADD COLUMN \"ServedDurationSeconds\" INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
            await AddColumnIfMissingAsync(
                db,
                "VoiceRewardEffects",
                "JailPresenceStartedAtUtc",
                "ALTER TABLE \"VoiceRewardEffects\" ADD COLUMN \"JailPresenceStartedAtUtc\" TEXT NULL;",
                cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "UserMessageStats" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_UserMessageStats" PRIMARY KEY AUTOINCREMENT,
                "BotUserId" INTEGER NOT NULL,
                "MessageCount" INTEGER NOT NULL DEFAULT 0,
                CONSTRAINT "FK_UserMessageStats_Users_BotUserId" FOREIGN KEY ("BotUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
                CONSTRAINT "CK_UserMessageStats_MessageCount" CHECK ("MessageCount" >= 0)
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_UserMessageStats_BotUserId\" ON \"UserMessageStats\" (\"BotUserId\");",
            cancellationToken);
        if (await TableExistsAsync(db, "UserProgress", cancellationToken))
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UserMessageStats" ("BotUserId", "MessageCount")
                SELECT "BotUserId", "MessageCount"
                FROM "UserProgress"
                WHERE "MessageCount" > 0
                ON CONFLICT("BotUserId") DO UPDATE SET
                    "MessageCount" = MAX("UserMessageStats"."MessageCount", excluded."MessageCount");
                """,
                cancellationToken);
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"UserProgress\";", cancellationToken);
        }

        if (await TableExistsAsync(db, "LevelRoleRewards", cancellationToken))
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"LevelRoleRewards\";", cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "ChannelActivityStats" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ChannelActivityStats" PRIMARY KEY AUTOINCREMENT,
                "GuildId" TEXT NOT NULL,
                "ChannelId" TEXT NOT NULL,
                "LastKnownChannelName" TEXT NOT NULL,
                "MessageCount" INTEGER NOT NULL DEFAULT 0,
                "LastMessageAtUtc" TEXT NOT NULL,
                CONSTRAINT "CK_ChannelActivityStats_MessageCount" CHECK ("MessageCount" >= 0)
            );
            """,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_ChannelActivityStats_GuildId_ChannelId\" ON \"ChannelActivityStats\" (\"GuildId\", \"ChannelId\");",
            cancellationToken);
        var existingItems = await db.ShopItems.ToDictionaryAsync(x => x.Key, cancellationToken);
        var currentKeys = currentItems.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var oldItem in existingItems.Values.Where(x => !currentKeys.Contains(x.Key)))
        {
            oldItem.IsEnabled = false;
        }

        foreach (var definition in currentItems)
        {
            if (!existingItems.TryGetValue(definition.Key, out var item))
            {
                db.ShopItems.Add(new ShopItem
                {
                    Key = definition.Key,
                    Name = definition.Name,
                    Description = definition.Description,
                    Price = definition.Price,
                    Type = definition.Type,
                    IsEnabled = true
                });
                continue;
            }

            item.Name = definition.Name;
            item.Description = definition.Description;
            item.Price = definition.Price;
            item.Type = definition.Type;
            item.IsEnabled = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Baza danych jest gotowa. Sklep zawiera {ItemCount} podstawowe przedmioty.",
            currentItems.Length);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<bool> TableExistsAsync(
        BotDbContext db,
        string tableName,
        CancellationToken cancellationToken)
    {
        var count = await db.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS \"Value\" FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = {0}",
                tableName)
            .SingleAsync(cancellationToken);
        return count > 0;
    }

    private static async Task AddColumnIfMissingAsync(
        BotDbContext db,
        string tableName,
        string columnName,
        string migrationSql,
        CancellationToken cancellationToken)
    {
        var count = await db.Database.SqlQueryRaw<long>(
                "SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({0}) WHERE \"name\" = {1}",
                tableName,
                columnName)
            .SingleAsync(cancellationToken);
        if (count == 0)
        {
            await db.Database.ExecuteSqlRawAsync(migrationSql, cancellationToken);
        }
    }

    private sealed record ShopItemDefinition(
        string Key,
        string Name,
        string Description,
        long Price,
        ShopItemType Type);
}
