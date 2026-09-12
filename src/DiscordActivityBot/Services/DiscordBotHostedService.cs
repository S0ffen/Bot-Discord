using Discord;
using Discord.WebSocket;
using DiscordActivityBot.Configuration;
using Microsoft.Extensions.Options;

namespace DiscordActivityBot.Services;

public sealed class DiscordBotHostedService(
    DiscordSocketClient client,
    InteractionHandler interactionHandler,
    IOptions<BotOptions> botOptions,
    ILogger<DiscordBotHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = botOptions.Value.Token;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Brak tokenu bota. Ustaw zmienną środowiskową DISCORD_TOKEN albo Bot:Token w appsettings.Local.json.");
        }

        client.Log += OnDiscordLogAsync;
        await interactionHandler.InitializeAsync();
        await client.LoginAsync(TokenType.Bot, token.Trim());
        await client.StartAsync();
        logger.LogInformation("Uruchomiono klienta Discord. Oczekiwanie na zdarzenie Ready...");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (client.LoginState == LoginState.LoggedIn)
        {
            await client.StopAsync();
            await client.LogoutAsync();
        }

        client.Log -= OnDiscordLogAsync;
    }

    private Task OnDiscordLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Trace,
            LogSeverity.Debug => LogLevel.Debug,
            _ => LogLevel.Information
        };
        logger.Log(level, message.Exception, "Discord: {Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}
