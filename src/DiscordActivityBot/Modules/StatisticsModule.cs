using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordActivityBot.Services;

namespace DiscordActivityBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class StatisticsModule(StatisticsService statisticsService)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("message-stats", "Pokazuje liczbę wiadomości wysłanych przez użytkownika.")]
    public async Task MessageStatsAsync(
        [Summary("uzytkownik", "Opcjonalnie: użytkownik, którego statystyki chcesz sprawdzić")]
        IUser? selectedUser = null)
    {
        var user = selectedUser ?? Context.User;
        var messageCount = await statisticsService.GetMessageCountAsync(Context.Guild.Id, user.Id);
        await RespondAsync($"{user.Mention} wysłał(a) **{messageCount:N0} wiadomości**.");
    }

    [SlashCommand("voice-leaderboard", "Pokazuje ranking łącznego czasu na kanałach głosowych.")]
    public async Task VoiceLeaderboardAsync()
    {
        var entries = await statisticsService.GetVoiceLeaderboardAsync(Context.Guild.Id, 10);
        if (entries.Count == 0)
        {
            await RespondAsync("Ranking czasu głosowego jest jeszcze pusty.");
            return;
        }

        var lines = entries.Select((entry, index) =>
            $"**{index + 1}.** <@{entry.DiscordUserId}> — **{FormatDuration(entry.TotalSeconds)}**");
        var embed = new EmbedBuilder()
            .WithTitle("Ranking czasu na voice")
            .WithDescription(string.Join('\n', lines))
            .WithColor(new Color(88, 101, 242))
            .Build();
        await RespondAsync(embed: embed);
    }

    [SlashCommand("server-stats", "Pokazuje statystyki wiadomości i aktywności kanałów serwera.")]
    public async Task ServerStatsAsync()
    {
        var stats = await statisticsService.GetServerStatisticsAsync(Context.Guild.Id, 5);
        var topUsers = stats.TopUsers.Count == 0
            ? "Brak danych"
            : string.Join('\n', stats.TopUsers.Select((entry, index) =>
                $"**{index + 1}.** <@{entry.DiscordUserId}> — {entry.MessageCount:N0}"));
        var topChannels = stats.TopChannels.Count == 0
            ? "Brak danych"
            : string.Join('\n', stats.TopChannels.Select((entry, index) =>
                $"**{index + 1}.** <#{entry.ChannelId}> — {entry.MessageCount:N0}"));

        var embed = new EmbedBuilder()
            .WithTitle("Statystyki serwera")
            .WithDescription("Statystyki wiadomości są liczone od uruchomienia tej funkcji.")
            .WithColor(new Color(88, 101, 242))
            .AddField("Wiadomości", $"{stats.TotalMessages:N0}", true)
            .AddField("Aktywni użytkownicy", $"{stats.ActiveUsers:N0}", true)
            .AddField("Aktywne kanały", $"{stats.ActiveChannels:N0}", true)
            .AddField("Najaktywniejsi użytkownicy", topUsers)
            .AddField("Najaktywniejsze kanały", topChannels)
            .Build();
        await RespondAsync(embed: embed);
    }

    private static string FormatDuration(long totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(totalSeconds);
        return duration.TotalHours >= 1
            ? $"{(long)duration.TotalHours} godz. {duration.Minutes} min"
            : $"{duration.Minutes} min {duration.Seconds} s";
    }
}
