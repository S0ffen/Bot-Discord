using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordActivityBot.Services;

namespace DiscordActivityBot.Modules;

[Group("ustawienia", "Konfiguracja bota na tym serwerze.")]
[RequireContext(ContextType.Guild)]
[DefaultMemberPermissions(GuildPermission.ManageGuild)]
[RequireUserPermission(GuildPermission.ManageGuild)]
public sealed class SettingsModule(GuildSettingsService settingsService)
    : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("logi", "Ustawia kanał tekstowy dla logów aktywności i zakupów.")]
    public async Task SetLogsAsync(
        [Summary("kanal", "Kanał tekstowy, na który bot ma wysyłać logi")]
        [ChannelTypes(ChannelType.Text)]
        IChannel selectedChannel)
    {
        if (selectedChannel is not SocketTextChannel channel || channel.Guild.Id != Context.Guild.Id)
        {
            await RespondAsync("Wskaż kanał tekstowy z tego serwera.", ephemeral: true);
            return;
        }

        var permissions = Context.Guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.SendMessages || !permissions.EmbedLinks)
        {
            await RespondAsync(
                "Bot potrzebuje na tym kanale uprawnień: `Wyświetlanie kanału`, `Wysyłanie wiadomości` i `Osadzanie linków`.",
                ephemeral: true);
            return;
        }

        await settingsService.SetVoiceLogChannelAsync(Context.Guild.Id, channel.Id);
        await RespondAsync(
            $"Logi aktywności głosowej i zakupów będą wysyłane na {channel.Mention}.",
            ephemeral: true);
    }

    [SlashCommand("wiezienie", "Ustawia kanał głosowy oraz rolę używane podczas kary więzienia.")]
    public async Task SetJailAsync(
        [Summary("kanal", "Kanał głosowy przeznaczony na więzienie")]
        [ChannelTypes(ChannelType.Voice)]
        IChannel selectedChannel,
        [Summary("rola", "Rola nadawana osobie osadzonej w więzieniu")]
        IRole selectedRole)
    {
        if (selectedChannel is not SocketVoiceChannel channel || channel.Guild.Id != Context.Guild.Id)
        {
            await RespondAsync("Wskaż kanał głosowy z tego serwera.", ephemeral: true);
            return;
        }

        if (Context.Guild.AFKChannel?.Id == channel.Id)
        {
            await RespondAsync("Systemowy kanał AFK nie może być jednocześnie więzieniem.", ephemeral: true);
            return;
        }

        var permissions = Context.Guild.CurrentUser.GetPermissions(channel);
        if (!permissions.ViewChannel || !permissions.Connect
            || !Context.Guild.CurrentUser.GuildPermissions.MoveMembers)
        {
            await RespondAsync(
                "Bot potrzebuje uprawnień `Wyświetlanie kanału`, `Łączenie` oraz `Przenoszenie członków`.",
                ephemeral: true);
            return;
        }

        var role = Context.Guild.GetRole(selectedRole.Id);
        if (role is null || role.IsManaged || role.Id == Context.Guild.EveryoneRole.Id)
        {
            await RespondAsync("Wskaż zwykłą, niezarządzaną rolę z tego serwera.", ephemeral: true);
            return;
        }

        if (!Context.Guild.CurrentUser.GuildPermissions.ManageRoles
            || Context.Guild.CurrentUser.Hierarchy <= role.Position)
        {
            await RespondAsync(
                "Bot potrzebuje `Zarządzania rolami`, a jego najwyższa rola musi znajdować się nad rolą więzienną.",
                ephemeral: true);
            return;
        }

        await settingsService.SetJailAsync(Context.Guild.Id, channel.Id, role.Id);
        await RespondAsync(
            $"Kanał {channel.Mention} i rola {role.Mention} zostały ustawione dla więzienia. " +
            "Kara liczy się tylko podczas faktycznej obecności na tym kanale.",
            ephemeral: true);
    }

    [SlashCommand("pokaz", "Pokazuje aktualne ustawienia kanałów bota.")]
    public async Task ShowAsync()
    {
        var settings = await settingsService.GetAsync(Context.Guild.Id);
        var logs = settings.VoiceLogChannelId is { } logChannelId
            ? $"<#{logChannelId}>"
            : "nie ustawiono";
        var jail = settings.JailVoiceChannelId is { } jailChannelId
            ? $"<#{jailChannelId}>"
            : "nie ustawiono";
        var jailRole = settings.JailRoleId is { } jailRoleId
            ? $"<@&{jailRoleId}>"
            : "nie ustawiono";

        await RespondAsync(
            $"**Kanał logów:** {logs}\n**Więzienie:** {jail}\n**Rola więzienna:** {jailRole}",
            ephemeral: true);
    }
}
