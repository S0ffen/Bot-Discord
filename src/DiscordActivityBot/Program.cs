using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordActivityBot.Configuration;
using DiscordActivityBot.Data;
using DiscordActivityBot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Logging.ClearProviders();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<ShopOptions>(builder.Configuration.GetSection(ShopOptions.SectionName));

var configuredDatabasePath = builder.Configuration[$"{BotOptions.SectionName}:DatabasePath"]
    ?? "data/activity-bot.db";
var databasePath = Path.GetFullPath(configuredDatabasePath, AppContext.BaseDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

builder.Services.AddPooledDbContextFactory<BotDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddSingleton(_ => new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates | GatewayIntents.GuildMessages,
    LogGatewayIntentWarnings = true,
    MessageCacheSize = 0
}));

builder.Services.AddSingleton(serviceProvider =>
{
    var client = serviceProvider.GetRequiredService<DiscordSocketClient>();
    return new InteractionService(client.Rest, new InteractionServiceConfig
    {
        DefaultRunMode = RunMode.Async,
        LogLevel = LogSeverity.Info,
        UseCompiledLambda = true
    });
});

builder.Services.AddSingleton<InteractionHandler>();
builder.Services.AddSingleton<EconomyService>();
builder.Services.AddSingleton<EconomyMutationLock>();
builder.Services.AddSingleton<GuildSettingsService>();
builder.Services.AddSingleton<StatisticsService>();
builder.Services.AddSingleton<JailRoleService>();
builder.Services.AddSingleton<ShopService>();
builder.Services.AddSingleton<VoiceActivityService>();
builder.Services.AddSingleton<MessageActivityService>();

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<VoiceActivityService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MessageActivityService>());
builder.Services.AddHostedService<VoiceRewardEnforcementService>();
builder.Services.AddHostedService<DiscordBotHostedService>();

builder.Services.AddOptions<BotOptions>()
    .Validate(options => options.PointsPerMinute > 0, "Bot:PointsPerMinute musi być większe od zera.")
    .Validate(options => options.HeartbeatSeconds >= 10, "Bot:HeartbeatSeconds nie może być mniejsze niż 10.")
    .ValidateOnStart();

builder.Services.AddOptions<ShopOptions>()
    .Validate(options => options.MutePrice >= 0 && options.JailPrice >= 0 && options.DisconnectPrice >= 0,
        "Ceny w sklepie nie mogą być ujemne.")
    .Validate(options => options.MuteDurationMinutes > 0,
        "Shop:MuteDurationMinutes musi być większe od zera.")
    .Validate(options => options.JailDurationMinutes > 0,
        "Shop:JailDurationMinutes musi być większe od zera.")
    .ValidateOnStart();

await builder.Build().RunAsync();
