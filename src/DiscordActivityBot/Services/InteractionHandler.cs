using System.Reflection;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace DiscordActivityBot.Services;

public sealed class InteractionHandler(
    DiscordSocketClient client,
    InteractionService interactionService,
    IServiceProvider serviceProvider,
    ILogger<InteractionHandler> logger)
{
    private int _commandsRegistered;

    public async Task InitializeAsync()
    {
        interactionService.Log += OnDiscordLogAsync;
        client.InteractionCreated += OnInteractionCreatedAsync;
        client.Ready += OnReadyAsync;

        await interactionService.AddModulesAsync(Assembly.GetExecutingAssembly(), serviceProvider);
    }

    private async Task OnReadyAsync()
    {
        if (Interlocked.Exchange(ref _commandsRegistered, 1) != 0)
        {
            return;
        }

        try
        {
            var commands = await interactionService.RegisterCommandsGloballyAsync(deleteMissing: true);
            logger.LogInformation("Komendy slash zarejestrowano globalnie ({CommandCount}).",
                commands.Count);
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _commandsRegistered, 0);
            logger.LogError(exception, "Nie udało się zarejestrować komend slash.");
        }
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(client, interaction);
            var result = await interactionService.ExecuteCommandAsync(context, serviceProvider);
            if (result.IsSuccess)
            {
                return;
            }

            logger.LogWarning("Komenda użytkownika {UserId} nie powiodła się: {Error} — {Reason}.",
                interaction.User.Id, result.Error, result.ErrorReason);
            if (!interaction.HasResponded)
            {
                await interaction.RespondAsync(
                    "Nie udało się wykonać komendy. Sprawdź jej parametry albo spróbuj ponownie.",
                    ephemeral: true);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Nieobsłużony błąd interakcji {InteractionId}.", interaction.Id);
            if (!interaction.HasResponded)
            {
                await interaction.RespondAsync("Wystąpił nieoczekiwany błąd bota.", ephemeral: true);
            }
        }
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
        logger.Log(level, message.Exception, "Discord.Interactions: {Message}", message.Message);
        return Task.CompletedTask;
    }
}
