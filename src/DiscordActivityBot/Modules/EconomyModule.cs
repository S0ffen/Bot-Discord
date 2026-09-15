using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordActivityBot.Services;

namespace DiscordActivityBot.Modules;

[RequireContext(ContextType.Guild)]
public sealed class EconomyModule(
    EconomyService economyService,
    ShopService shopService) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("shop", "Wyświetla sklep z nagrodami za aktywność głosową.")]
    public async Task ShopAsync()
    {
        var items = await shopService.GetCatalogAsync(Context.Guild.Id);
        var embed = new EmbedBuilder()
            .WithTitle("Sklep serwera")
            .WithDescription(
                "Kup nagrodę komendą `/kup przedmiot:<nazwa> cel:<osoba>`. Saldo sprawdzisz przez `/balance`.")
            .WithColor(new Color(88, 101, 242));

        foreach (var item in items)
        {
            var availability = item.IsAvailable ? string.Empty : $"\n⚠️ {item.AvailabilityMessage}";
            embed.AddField($"{item.Name} — {item.Price:N0} pkt (`{item.Key}`)",
                $"{item.Description}{availability}");
        }

        await RespondAsync(embed: embed.Build());
    }

    [SlashCommand("kup", "Kupuje wybrany przedmiot ze sklepu.")]
    public async Task BuyAsync(
        [Summary("przedmiot", "Identyfikator przedmiotu z /shop"), Autocomplete]
        string itemKey,
        [Summary("cel", "Osoba aktualnie obecna na kanale głosowym"), Autocomplete]
        string targetUserId)
    {
        if (Context.Guild is null || Context.User is not SocketGuildUser buyer)
        {
            await RespondAsync("Tej komendy można użyć tylko na serwerze.", ephemeral: true);
            return;
        }

        await DeferAsync(ephemeral: true);
        var target = ulong.TryParse(targetUserId, out var parsedTargetId)
            ? Context.Guild.GetUser(parsedTargetId)
            : null;
        var result = await shopService.BuyAsync(Context.Guild, buyer, target, itemKey);
        await Context.Interaction.ModifyOriginalResponseAsync(properties => properties.Content = result.Message);
    }

    [AutocompleteCommand("przedmiot", "kup")]
    public async Task BuyAutocompleteAsync()
    {
        if (Context.Interaction is not SocketAutocompleteInteraction autocomplete)
        {
            return;
        }

        var query = autocomplete.Data.Current.Value?.ToString() ?? string.Empty;
        var catalog = await shopService.GetCatalogAsync(Context.Guild.Id);
        var results = catalog
            .Where(x => x.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(x => new AutocompleteResult($"{x.Name} — {x.Price:N0} pkt", x.Key));
        await autocomplete.RespondAsync(results);
    }

    [AutocompleteCommand("cel", "kup")]
    public async Task TargetAutocompleteAsync()
    {
        if (Context.Interaction is not SocketAutocompleteInteraction autocomplete)
        {
            return;
        }

        var query = autocomplete.Data.Current.Value?.ToString() ?? string.Empty;
        var results = Context.Guild.Users
            .Where(user => !user.IsBot
                           && user.Id != Context.User.Id
                           && user.VoiceChannel is not null
                           && (user.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || user.Username.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(user => user.DisplayName)
            .Take(25)
            .Select(user => new AutocompleteResult(
                $"{user.DisplayName} — {user.VoiceChannel!.Name}",
                user.Id.ToString()));

        await autocomplete.RespondAsync(results);
    }

    [SlashCommand("balance", "Pokazuje liczbę posiadanych punktów.")]
    public async Task BalanceAsync(
        [Summary("uzytkownik", "Opcjonalnie: użytkownik, którego saldo chcesz sprawdzić")]
        IUser? selectedUser = null)
    {
        var user = selectedUser ?? Context.User;
        var balance = await economyService.GetBalanceAsync(Context.Guild.Id, user.Id);
        await RespondAsync($"{user.Mention} ma **{balance:N0} pkt**.");
    }

    [SlashCommand("leaderboard", "Pokazuje 10 najaktywniejszych użytkowników serwera.")]
    public async Task LeaderboardAsync()
    {
        var entries = await economyService.GetLeaderboardAsync(Context.Guild.Id, 10);
        if (entries.Count == 0)
        {
            await RespondAsync("Ranking jest jeszcze pusty.");
            return;
        }

        var lines = entries.Select((entry, index) =>
            $"**{index + 1}.** <@{entry.DiscordUserId}> — **{entry.Points:N0} pkt**");
        var embed = new EmbedBuilder()
            .WithTitle("Ranking aktywności głosowej")
            .WithDescription(string.Join('\n', lines))
            .WithColor(new Color(87, 242, 135))
            .Build();
        await RespondAsync(embed: embed);
    }

    [SlashCommand("voice-stats", "Pokazuje zakończone sesje i czas na kanałach głosowych.")]
    public async Task VoiceStatsAsync(
        [Summary("uzytkownik", "Opcjonalnie: użytkownik, którego statystyki chcesz sprawdzić")]
        IUser? selectedUser = null)
    {
        var user = selectedUser ?? Context.User;
        var stats = await economyService.GetVoiceStatsAsync(Context.Guild.Id, user.Id);
        var duration = TimeSpan.FromSeconds(stats.TotalSeconds);
        var durationText = duration.TotalHours >= 1
            ? $"{(long)duration.TotalHours} godz. {duration.Minutes} min"
            : $"{duration.Minutes} min";

        await RespondAsync(
            $"Statystyki {user.Mention}: **{stats.Sessions:N0} sesji**, **{durationText}**, zdobyto **{stats.AwardedPoints:N0} pkt**.");
    }
}
