using Discord;
using Discord.WebSocket;
using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;

namespace DiscordActivityBot.Services;

public sealed class MessageActivityService(
    DiscordSocketClient client,
    IDbContextFactory<BotDbContext> dbContextFactory,
    EconomyMutationLock mutationLock,
    ILogger<MessageActivityService> logger) : IHostedService
{
    private readonly SemaphoreSlim _mutationGate = mutationLock.Gate;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        client.MessageReceived += OnMessageReceivedAsync;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        client.MessageReceived -= OnMessageReceivedAsync;
        return Task.CompletedTask;
    }

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Source != MessageSource.User
            || message.Author is not SocketGuildUser user
            || message.Channel is not SocketGuildChannel channel
            || user.IsBot)
        {
            return;
        }

        await _mutationGate.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            await using var db = await dbContextFactory.CreateDbContextAsync();
            var botUser = await BotUserStore.GetOrCreateAsync(
                db,
                user.Guild.Id,
                user.Id,
                user.DisplayName,
                now);
            var userStats = botUser.Id == 0
                ? null
                : await db.UserMessageStats.SingleOrDefaultAsync(x => x.BotUserId == botUser.Id);
            if (userStats is null)
            {
                userStats = new UserMessageStat { BotUser = botUser };
                db.UserMessageStats.Add(userStats);
            }

            userStats.MessageCount = checked(userStats.MessageCount + 1);

            var guildId = EconomyService.Id(user.Guild.Id);
            var channelId = EconomyService.Id(channel.Id);
            var channelStats = await db.ChannelActivityStats.SingleOrDefaultAsync(
                x => x.GuildId == guildId && x.ChannelId == channelId);
            if (channelStats is null)
            {
                channelStats = new ChannelActivityStat
                {
                    GuildId = guildId,
                    ChannelId = channelId,
                    LastKnownChannelName = channel.Name,
                    LastMessageAtUtc = now
                };
                db.ChannelActivityStats.Add(channelStats);
            }

            channelStats.LastKnownChannelName = channel.Name;
            channelStats.LastMessageAtUtc = now;
            channelStats.MessageCount = checked(channelStats.MessageCount + 1);

            await db.SaveChangesAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Nie udało się zapisać aktywności wiadomości użytkownika {UserId}.", user.Id);
        }
        finally
        {
            _mutationGate.Release();
        }
    }
}
