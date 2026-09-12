using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordActivityBot.Services;

public sealed class GuildSettingsService(IDbContextFactory<BotDbContext> dbContextFactory)
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<GuildSettingsSnapshot> GetAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = EconomyService.Id(guildId);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.GuildSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.GuildId == guildIdText, cancellationToken);

        return settings is null
            ? GuildSettingsSnapshot.Empty
            : new GuildSettingsSnapshot(
                ParseId(settings.VoiceLogChannelId),
                ParseId(settings.JailVoiceChannelId),
                ParseId(settings.JailRoleId));
    }

    public Task SetVoiceLogChannelAsync(
        ulong guildId,
        ulong channelId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(guildId, settings => settings.VoiceLogChannelId = EconomyService.Id(channelId), cancellationToken);

    public Task SetJailAsync(
        ulong guildId,
        ulong channelId,
        ulong roleId,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(guildId, settings =>
        {
            settings.JailVoiceChannelId = EconomyService.Id(channelId);
            settings.JailRoleId = EconomyService.Id(roleId);
        }, cancellationToken);

    private async Task UpdateAsync(
        ulong guildId,
        Action<GuildSettings> update,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var guildIdText = EconomyService.Id(guildId);
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var settings = await db.GuildSettings
                .SingleOrDefaultAsync(x => x.GuildId == guildIdText, cancellationToken);
            if (settings is null)
            {
                settings = new GuildSettings
                {
                    GuildId = guildIdText,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                db.GuildSettings.Add(settings);
            }

            update(settings);
            settings.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static ulong? ParseId(string? id) => ulong.TryParse(id, out var parsed) ? parsed : null;
}

public sealed record GuildSettingsSnapshot(
    ulong? VoiceLogChannelId,
    ulong? JailVoiceChannelId,
    ulong? JailRoleId)
{
    public static GuildSettingsSnapshot Empty { get; } = new(null, null, null);
}
