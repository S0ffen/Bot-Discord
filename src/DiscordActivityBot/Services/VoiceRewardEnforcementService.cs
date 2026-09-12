using Discord.WebSocket;
using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordActivityBot.Services;

public sealed class VoiceRewardEnforcementService(
    DiscordSocketClient client,
    IDbContextFactory<BotDbContext> dbContextFactory,
    JailRoleService jailRoleService,
    EconomyMutationLock mutationLock,
    ILogger<VoiceRewardEnforcementService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _mutationGate = mutationLock.Gate;
    private bool _presenceInitialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                try
                {
                    if (client.ConnectionState == Discord.ConnectionState.Connected)
                    {
                        await EnforceEffectsAsync(stoppingToken);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Nie udało się sprawdzić nagród głosowych; następna próba odbędzie się automatycznie.");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normalne zatrzymanie usługi.
        }
        finally
        {
            client.UserVoiceStateUpdated -= OnUserVoiceStateUpdatedAsync;
        }
    }

    private async Task OnUserVoiceStateUpdatedAsync(
        SocketUser socketUser,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        if (socketUser is not SocketGuildUser user
            || user.IsBot
            || before.VoiceChannel?.Id == after.VoiceChannel?.Id)
        {
            return;
        }

        await _mutationGate.WaitAsync();
        try
        {
            var guildId = EconomyService.Id(user.Guild.Id);
            var userId = EconomyService.Id(user.Id);
            await using var db = await dbContextFactory.CreateDbContextAsync();
            var effects = await db.VoiceRewardEffects
                .Where(x => x.GuildId == guildId
                            && x.TargetDiscordUserId == userId
                            && x.Type == VoiceRewardEffectType.Jail
                            && x.RequiredDurationSeconds > 0
                            && x.EndedAtUtc == null)
                .ToListAsync();
            var now = DateTime.UtcNow;

            foreach (var effect in effects)
            {
                if (!ulong.TryParse(effect.JailChannelId, out var jailChannelId))
                {
                    continue;
                }

                var leftJail = before.VoiceChannel?.Id == jailChannelId
                               && after.VoiceChannel?.Id != jailChannelId;
                var enteredJail = before.VoiceChannel?.Id != jailChannelId
                                  && after.VoiceChannel?.Id == jailChannelId;
                if (leftJail)
                {
                    JailSentenceClock.PausePresence(effect, now);
                    if (JailSentenceClock.IsComplete(effect)
                        && ulong.TryParse(effect.JailRoleId, out var jailRoleId))
                    {
                        await CompleteJailAsync(effect, user.Guild, user, jailRoleId, now);
                    }
                }
                else if (enteredJail)
                {
                    JailSentenceClock.StartPresence(effect, now);
                }
            }

            await db.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Nie udało się zapisać zmiany obecności więźnia {UserId}; kontrola okresowa spróbuje ponownie.",
                socketUser.Id);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task EnforceEffectsAsync(CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTime.UtcNow;
            var effects = await db.VoiceRewardEffects
                .Where(x => x.EndedAtUtc == null)
                .OrderBy(x => x.Id)
                .Take(100)
                .ToListAsync(cancellationToken);

            if (!_presenceInitialized)
            {
                NormalizePresenceAfterRestart(effects);
                _presenceInitialized = true;
            }

            foreach (var effect in effects)
            {
                if (!TryResolve(effect, out var guild, out var target, out var jailChannel))
                {
                    if (!HasValidIdentifiers(effect))
                    {
                        effect.EndedAtUtc = now;
                        logger.LogError(
                            "Niepoprawne identyfikatory w rekordzie nagrody głosowej {EffectId}.",
                            effect.Id);
                    }

                    continue;
                }

                try
                {
                    if (effect.Type == VoiceRewardEffectType.Mute)
                    {
                        await EnforceMuteAsync(effect, target, now);
                    }
                    else if (effect.RequiredDurationSeconds > 0
                             && ulong.TryParse(effect.JailRoleId, out var jailRoleId))
                    {
                        await EnforcePresenceBasedJailAsync(
                            effect,
                            guild!,
                            target,
                            jailChannel!,
                            jailRoleId,
                            now);
                    }
                    else
                    {
                        await EnforceLegacyJailAsync(effect, guild!, target, jailChannel!, now);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogWarning(
                        exception,
                        "Nie udało się obsłużyć czasowej nagrody głosowej {EffectId}; operacja zostanie ponowiona.",
                        effect.Id);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static void NormalizePresenceAfterRestart(IEnumerable<VoiceRewardEffect> effects)
    {
        foreach (var effect in effects.Where(x =>
                     x.Type == VoiceRewardEffectType.Jail && x.RequiredDurationSeconds > 0))
        {
            effect.JailPresenceStartedAtUtc = null;
            if (!ulong.TryParse(effect.GuildId, out _)
                || !ulong.TryParse(effect.TargetDiscordUserId, out _)
                || !ulong.TryParse(effect.JailChannelId, out _))
            {
                continue;
            }

            // Czas, gdy bot był offline, nie jest doliczany. Bieżąca obecność
            // zostanie rozpoczęta w tej samej kontroli okresowej.
        }
    }

    private static async Task EnforceMuteAsync(
        VoiceRewardEffect effect,
        SocketGuildUser? target,
        DateTime now)
    {
        if (target is null)
        {
            return;
        }

        if (now >= effect.ExpiresAtUtc)
        {
            if (target.IsMuted)
            {
                await target.ModifyAsync(properties => properties.Mute = false);
            }

            effect.EndedAtUtc = now;
            return;
        }

        if (target.VoiceChannel is not null && !target.IsMuted)
        {
            await target.ModifyAsync(properties => properties.Mute = true);
        }
    }

    private async Task EnforcePresenceBasedJailAsync(
        VoiceRewardEffect effect,
        SocketGuild guild,
        SocketGuildUser? target,
        SocketVoiceChannel jailChannel,
        ulong jailRoleId,
        DateTime now)
    {
        if (target is null)
        {
            return;
        }

        // Dokończona kara może oczekiwać na ponowną próbę przywrócenia ról.
        // Nie nakładaj wtedy ponownie roli więziennej ani nie zabieraj nowych ról.
        if (JailSentenceClock.IsComplete(effect))
        {
            await CompleteJailAsync(effect, guild, target, jailRoleId, now);
            return;
        }

        await jailRoleService.EnsureJailRoleOnlyAsync(target, jailRoleId);
        var isInJail = target.VoiceChannel?.Id == jailChannel.Id;
        if (!isInJail)
        {
            JailSentenceClock.PausePresence(effect, now);
            if (JailSentenceClock.IsComplete(effect))
            {
                await CompleteJailAsync(effect, guild, target, jailRoleId, now);
                return;
            }

            if (target.VoiceChannel is not null)
            {
                await target.ModifyAsync(properties => properties.Channel = jailChannel);
                JailSentenceClock.StartPresence(effect, DateTime.UtcNow);
                logger.LogInformation(
                    "Użytkownik {UserId} wrócił do aktywnego więzienia {ChannelId}.",
                    target.Id,
                    jailChannel.Id);
            }

            return;
        }

        if (effect.JailPresenceStartedAtUtc is null)
        {
            JailSentenceClock.StartPresence(effect, now);
            return;
        }

        JailSentenceClock.CheckpointPresence(effect, now);
        if (!JailSentenceClock.IsComplete(effect))
        {
            return;
        }

        await CompleteJailAsync(effect, guild, target, jailRoleId, now);
    }

    private static async Task EnforceLegacyJailAsync(
        VoiceRewardEffect effect,
        SocketGuild guild,
        SocketGuildUser? target,
        SocketVoiceChannel jailChannel,
        DateTime now)
    {
        if (target is null)
        {
            return;
        }

        if (now >= effect.ExpiresAtUtc)
        {
            await MoveOutOfJailAsync(effect, guild, target);
            effect.EndedAtUtc = now;
            return;
        }

        if (target.VoiceChannel is not null && target.VoiceChannel.Id != jailChannel.Id)
        {
            await target.ModifyAsync(properties => properties.Channel = jailChannel);
        }
    }

    private static async Task MoveOutOfJailAsync(
        VoiceRewardEffect effect,
        SocketGuild guild,
        SocketGuildUser target)
    {
        if (target.VoiceChannel is null)
        {
            return;
        }

        SocketVoiceChannel? originalChannel = null;
        if (ulong.TryParse(effect.OriginalChannelId, out var originalChannelId))
        {
            originalChannel = guild.GetVoiceChannel(originalChannelId);
        }

        await target.ModifyAsync(properties => properties.Channel = originalChannel);
    }

    private async Task CompleteJailAsync(
        VoiceRewardEffect effect,
        SocketGuild guild,
        SocketGuildUser target,
        ulong jailRoleId,
        DateTime endedAtUtc)
    {
        effect.ServedDurationSeconds = effect.RequiredDurationSeconds;
        effect.JailPresenceStartedAtUtc = null;
        await jailRoleService.RestoreRolesAsync(
            target,
            jailRoleId,
            jailRoleService.DeserializeRoles(effect.OriginalRoleIdsJson));
        await MoveOutOfJailAsync(effect, guild, target);
        effect.EndedAtUtc = endedAtUtc;
        logger.LogInformation(
            "Zakończono więzienie użytkownika {UserId} po odsiedzeniu {DurationSeconds} s.",
            target.Id,
            effect.RequiredDurationSeconds);
    }

    private static bool HasValidIdentifiers(VoiceRewardEffect effect) =>
        ulong.TryParse(effect.GuildId, out _)
        && ulong.TryParse(effect.TargetDiscordUserId, out _)
        && (effect.Type != VoiceRewardEffectType.Jail || ulong.TryParse(effect.JailChannelId, out _));

    private bool TryResolve(
        VoiceRewardEffect effect,
        out SocketGuild? guild,
        out SocketGuildUser? target,
        out SocketVoiceChannel? jailChannel)
    {
        guild = null;
        target = null;
        jailChannel = null;

        if (!ulong.TryParse(effect.GuildId, out var guildId)
            || !ulong.TryParse(effect.TargetDiscordUserId, out var userId))
        {
            return false;
        }

        guild = client.GetGuild(guildId);
        if (guild is null)
        {
            return false;
        }

        target = guild.GetUser(userId);
        if (effect.Type == VoiceRewardEffectType.Jail)
        {
            if (!ulong.TryParse(effect.JailChannelId, out var jailChannelId))
            {
                return false;
            }

            jailChannel = guild.GetVoiceChannel(jailChannelId);
            return jailChannel is not null;
        }

        return true;
    }
}
