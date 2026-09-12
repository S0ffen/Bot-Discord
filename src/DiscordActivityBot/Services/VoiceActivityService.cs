using Discord;
using Discord.WebSocket;
using DiscordActivityBot.Configuration;
using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DiscordActivityBot.Services;

public sealed class VoiceActivityService(
    DiscordSocketClient client,
    IDbContextFactory<BotDbContext> dbContextFactory,
    IOptions<BotOptions> botOptions,
    GuildSettingsService guildSettingsService,
    EconomyMutationLock mutationLock,
    ILogger<VoiceActivityService> logger) : IHostedService
{
    private readonly BotOptions _options = botOptions.Value;
    private readonly SemaphoreSlim _operationGate = mutationLock.Gate;
    private CancellationTokenSource? _loopCancellation;
    private Task? _heartbeatTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated += OnUserVoiceStateUpdatedAsync;
        client.Ready += OnReadyAsync;

        _loopCancellation = new CancellationTokenSource();
        _heartbeatTask = RunHeartbeatAsync(_loopCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        client.UserVoiceStateUpdated -= OnUserVoiceStateUpdatedAsync;
        client.Ready -= OnReadyAsync;

        if (_loopCancellation is not null)
        {
            await _loopCancellation.CancelAsync();
        }

        if (_heartbeatTask is not null)
        {
            try
            {
                await _heartbeatTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Normalne zakończenie pętli przy wyłączaniu aplikacji.
            }
        }

        await CloseAllSessionsAsync(DateTime.UtcNow, cancellationToken);
        _loopCancellation?.Dispose();
    }

    private async Task OnReadyAsync()
    {
        try
        {
            await RepairSessionsAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Nie udało się odtworzyć aktywnych sesji głosowych po połączeniu.");
        }
    }

    private async Task OnUserVoiceStateUpdatedAsync(
        SocketUser socketUser,
        SocketVoiceState before,
        SocketVoiceState after)
    {
        if (socketUser is not SocketGuildUser user || user.IsBot || before.VoiceChannel?.Id == after.VoiceChannel?.Id)
        {
            return;
        }

        IReadOnlyList<CompletedVoiceSessionLog> completedSessions = [];
        StartedVoiceSessionLog? startedSession = null;
        var changesSaved = false;
        await _operationGate.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var guildSettings = await guildSettingsService.GetAsync(user.Guild.Id);
            await using var db = await dbContextFactory.CreateDbContextAsync();

            if (before.VoiceChannel is not null)
            {
                completedSessions = await CloseActiveSessionAsync(db, user.Guild.Id, user.Id, now);
                // Utrwal zamknięcie przed sprawdzeniem, czy istnieje aktywna sesja
                // dla kanału docelowego (istotne przy przenoszeniu między kanałami).
                await db.SaveChangesAsync();
            }

            if (after.VoiceChannel is not null
                && ShouldTrack(user, after.VoiceChannel, guildSettings.JailVoiceChannelId))
            {
                startedSession = await OpenSessionAsync(db, user, after.VoiceChannel, now);
            }

            await db.SaveChangesAsync();
            changesSaved = true;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Błąd obsługi zmiany kanału głosowego użytkownika {UserId}.", socketUser.Id);
        }
        finally
        {
            _operationGate.Release();
        }

        if (!changesSaved)
        {
            return;
        }

        foreach (var completedSession in completedSessions)
        {
            await SendCompletedSessionLogAsync(completedSession);
        }

        if (startedSession is not null)
        {
            await SendStartedSessionLogAsync(startedSession);
        }
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    if (client.ConnectionState == Discord.ConnectionState.Connected)
                    {
                        await RepairSessionsAsync(cancellationToken);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception,
                        "Nie udało się wykonać kontroli sesji głosowych; następna próba odbędzie się automatycznie.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normalne zakończenie usługi.
        }
    }

    private async Task RepairSessionsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var guilds = client.Guilds;
        var settingsByGuild = new Dictionary<ulong, GuildSettingsSnapshot>();
        foreach (var guild in guilds)
        {
            settingsByGuild[guild.Id] = await guildSettingsService.GetAsync(guild.Id, cancellationToken);
        }

        var connectedUsers = guilds
            .SelectMany(guild => guild.Users)
            .Where(user => !user.IsBot
                           && user.VoiceChannel is not null
                           && ShouldTrack(
                               user,
                               user.VoiceChannel,
                               settingsByGuild[user.Guild.Id].JailVoiceChannelId))
            .ToDictionary(
                user => (GuildId: user.Guild.Id, UserId: user.Id),
                user => new ConnectedVoiceUser(user, user.VoiceChannel!));

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var activeSessions = await db.VoiceSessions
                .Include(x => x.BotUser)
                .Where(x => x.LeftAtUtc == null)
                .ToListAsync(cancellationToken);

            foreach (var session in activeSessions)
            {
                if (!ulong.TryParse(session.GuildId, out var guildId)
                    || !ulong.TryParse(session.DiscordUserId, out var userId))
                {
                    await FinishSessionAsync(db, session, session.LastObservedAtUtc, cancellationToken);
                    continue;
                }

                var key = (GuildId: guildId, UserId: userId);
                if (connectedUsers.TryGetValue(key, out var connected)
                    && session.ChannelId == EconomyService.Id(connected.Channel.Id))
                {
                    session.LastObservedAtUtc = now;
                    session.ChannelName = connected.Channel.Name;
                    session.BotUser.LastKnownDisplayName = connected.User.DisplayName;
                    session.BotUser.UpdatedAtUtc = now;
                    connectedUsers.Remove(key);
                    continue;
                }

                // Gdy zdarzenie wyjścia zaginęło lub bot był offline, nie naliczamy czasu
                // po ostatnim potwierdzonym heartbeatcie.
                await FinishSessionAsync(db, session, session.LastObservedAtUtc, cancellationToken);
            }

            // Zapytanie w OpenSessionAsync musi widzieć zamknięte powyżej rekordy.
            await db.SaveChangesAsync(cancellationToken);

            foreach (var connected in connectedUsers.Values)
            {
                await OpenSessionAsync(db, connected.User, connected.Channel, now, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<StartedVoiceSessionLog?> OpenSessionAsync(
        BotDbContext db,
        SocketGuildUser user,
        SocketVoiceChannel channel,
        DateTime joinedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var guildId = EconomyService.Id(user.Guild.Id);
        var userId = EconomyService.Id(user.Id);
        var alreadyActive = await db.VoiceSessions
            .AnyAsync(x => x.GuildId == guildId && x.DiscordUserId == userId && x.LeftAtUtc == null,
                cancellationToken);
        if (alreadyActive)
        {
            return null;
        }

        var botUser = await GetOrCreateUserAsync(db, user.Guild.Id, user.Id, user.DisplayName, joinedAtUtc,
            cancellationToken);
        db.VoiceSessions.Add(new VoiceSession
        {
            BotUser = botUser,
            GuildId = guildId,
            DiscordUserId = userId,
            ChannelId = EconomyService.Id(channel.Id),
            ChannelName = channel.Name,
            JoinedAtUtc = joinedAtUtc,
            LastObservedAtUtc = joinedAtUtc
        });

        logger.LogInformation("Użytkownik {User} ({UserId}) wszedł na kanał {Channel} o {JoinedAtUtc:u}.",
            user.DisplayName, user.Id, channel.Name, joinedAtUtc);
        return new StartedVoiceSessionLog(
            user.Guild.Id,
            user.Id,
            user.DisplayName,
            channel.Id,
            channel.Name,
            joinedAtUtc);
    }

    private async Task<IReadOnlyList<CompletedVoiceSessionLog>> CloseActiveSessionAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        DateTime leftAtUtc,
        CancellationToken cancellationToken = default)
    {
        var guildIdText = EconomyService.Id(guildId);
        var userIdText = EconomyService.Id(userId);
        var sessions = await db.VoiceSessions
            .Include(x => x.BotUser)
            .Where(x => x.GuildId == guildIdText && x.DiscordUserId == userIdText && x.LeftAtUtc == null)
            .ToListAsync(cancellationToken);

        var completedSessions = new List<CompletedVoiceSessionLog>(sessions.Count);
        foreach (var session in sessions)
        {
            completedSessions.Add(await FinishSessionAsync(db, session, leftAtUtc, cancellationToken));
        }

        return completedSessions;
    }

    private async Task<CompletedVoiceSessionLog> FinishSessionAsync(
        BotDbContext db,
        VoiceSession session,
        DateTime requestedLeftAtUtc,
        CancellationToken cancellationToken)
    {
        var leftAtUtc = requestedLeftAtUtc < session.JoinedAtUtc ? session.JoinedAtUtc : requestedLeftAtUtc;
        var boosts = await db.PointBoosts
            .Where(x => x.BotUserId == session.BotUserId
                        && x.ExpiresAtUtc > session.JoinedAtUtc
                        && x.StartsAtUtc <= leftAtUtc)
            .ToListAsync(cancellationToken);
        var awardedPoints = PointCalculator.Calculate(session.JoinedAtUtc, leftAtUtc, _options.PointsPerMinute, boosts);

        session.LeftAtUtc = leftAtUtc;
        session.LastObservedAtUtc = leftAtUtc;
        session.DurationSeconds = Math.Max(0, (long)Math.Floor((leftAtUtc - session.JoinedAtUtc).TotalSeconds));
        session.AwardedPoints = awardedPoints;
        session.BotUser.Points = checked(session.BotUser.Points + awardedPoints);
        session.BotUser.UpdatedAtUtc = DateTime.UtcNow;
        logger.LogInformation(
            "Użytkownik {User} opuścił kanał {Channel}. Wejście: {JoinedAtUtc:u}, wyjście: {LeftAtUtc:u}, czas: {Minutes} min, nagroda: {Points} pkt.",
            session.BotUser.LastKnownDisplayName,
            session.ChannelName,
            session.JoinedAtUtc,
            leftAtUtc,
            session.DurationSeconds / 60,
            awardedPoints);
        return new CompletedVoiceSessionLog(
            ulong.TryParse(session.GuildId, out var guildId) ? guildId : 0,
            ulong.TryParse(session.DiscordUserId, out var userId) ? userId : 0,
            session.BotUser.LastKnownDisplayName,
            ulong.TryParse(session.ChannelId, out var channelId) ? channelId : 0,
            session.ChannelName,
            session.JoinedAtUtc,
            leftAtUtc,
            session.DurationSeconds,
            awardedPoints,
            session.BotUser.Points);
    }

    private async Task CloseAllSessionsAsync(DateTime leftAtUtc, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var sessions = await db.VoiceSessions
                .Include(x => x.BotUser)
                .Where(x => x.LeftAtUtc == null)
                .ToListAsync(cancellationToken);

            foreach (var session in sessions)
            {
                await FinishSessionAsync(db, session, leftAtUtc, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private bool ShouldTrack(SocketGuildUser user, SocketVoiceChannel channel, ulong? jailVoiceChannelId)
    {
        if (_options.IgnoredVoiceChannelIds.Contains(channel.Id))
        {
            return false;
        }

        if (jailVoiceChannelId is { } jailChannelId && channel.Id == jailChannelId)
        {
            return false;
        }

        return !_options.IgnoreAfkChannel || user.Guild.AFKChannel?.Id != channel.Id;
    }

    private async Task SendStartedSessionLogAsync(StartedVoiceSessionLog session)
    {
        var embed = new EmbedBuilder()
            .WithTitle("🎙️ Wejście na kanał głosowy")
            .WithColor(new Color(87, 242, 135))
            .AddField("Użytkownik", $"<@{session.UserId}> (`{session.DisplayName}`)", true)
            .AddField("Kanał", $"<#{session.ChannelId}> (`{session.ChannelName}`)", true)
            .AddField("Wejście", DiscordTimestamp(session.JoinedAtUtc), true)
            .WithCurrentTimestamp()
            .Build();

        await SendVoiceLogAsync(session.GuildId, embed);
    }

    private async Task SendCompletedSessionLogAsync(CompletedVoiceSessionLog session)
    {
        var duration = TimeSpan.FromSeconds(session.DurationSeconds);
        var durationText = $"{(long)duration.TotalMinutes} min {duration.Seconds} s";
        var embed = new EmbedBuilder()
            .WithTitle("👋 Wyjście z kanału głosowego")
            .WithColor(new Color(237, 66, 69))
            .AddField("Użytkownik", $"<@{session.UserId}> (`{session.DisplayName}`)", true)
            .AddField("Kanał", $"<#{session.ChannelId}> (`{session.ChannelName}`)", true)
            .AddField("Wejście", DiscordTimestamp(session.JoinedAtUtc), true)
            .AddField("Wyjście", DiscordTimestamp(session.LeftAtUtc), true)
            .AddField("Czas", durationText, true)
            .AddField("Nagroda", $"{session.AwardedPoints:N0} pkt", true)
            .AddField("Nowe saldo", $"{session.NewBalance:N0} pkt", true)
            .WithCurrentTimestamp()
            .Build();

        await SendVoiceLogAsync(session.GuildId, embed);
    }

    private async Task SendVoiceLogAsync(ulong guildId, Embed embed)
    {
        try
        {
            var guildSettings = await guildSettingsService.GetAsync(guildId);
            if (guildSettings.VoiceLogChannelId is not { } voiceLogChannelId)
            {
                return;
            }

            if (client.GetChannel(voiceLogChannelId) is not SocketTextChannel channel
                || channel.Guild.Id != guildId)
            {
                logger.LogWarning(
                    "Bot:VoiceLogChannelId={ChannelId} nie wskazuje kanału tekstowego na serwerze {GuildId}.",
                    voiceLogChannelId, guildId);
                return;
            }

            await channel.SendMessageAsync(embed: embed);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Nie udało się odczytać ustawień lub wysłać logu głosowego. Sprawdź bazę i uprawnienia bota.");
        }
    }

    private static string DiscordTimestamp(DateTime utcTime) =>
        $"<t:{new DateTimeOffset(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc)).ToUnixTimeSeconds()}:T>";

    private static async Task<BotUser> GetOrCreateUserAsync(
        BotDbContext db,
        ulong guildId,
        ulong userId,
        string displayName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var guildIdText = EconomyService.Id(guildId);
        var userIdText = EconomyService.Id(userId);
        var botUser = await db.Users.SingleOrDefaultAsync(
            x => x.GuildId == guildIdText && x.DiscordUserId == userIdText,
            cancellationToken);

        if (botUser is null)
        {
            botUser = new BotUser
            {
                GuildId = guildIdText,
                DiscordUserId = userIdText,
                LastKnownDisplayName = displayName,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.Users.Add(botUser);
        }
        else
        {
            botUser.LastKnownDisplayName = displayName;
            botUser.UpdatedAtUtc = now;
        }

        return botUser;
    }

    private sealed record ConnectedVoiceUser(SocketGuildUser User, SocketVoiceChannel Channel);

    private sealed record StartedVoiceSessionLog(
        ulong GuildId,
        ulong UserId,
        string DisplayName,
        ulong ChannelId,
        string ChannelName,
        DateTime JoinedAtUtc);

    private sealed record CompletedVoiceSessionLog(
        ulong GuildId,
        ulong UserId,
        string DisplayName,
        ulong ChannelId,
        string ChannelName,
        DateTime JoinedAtUtc,
        DateTime LeftAtUtc,
        long DurationSeconds,
        long AwardedPoints,
        long NewBalance);
}
