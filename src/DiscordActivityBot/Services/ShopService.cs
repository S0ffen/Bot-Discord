using System.Data;
using Discord;
using Discord.WebSocket;
using DiscordActivityBot.Configuration;
using DiscordActivityBot.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DiscordActivityBot.Services;

public sealed class ShopService(
    IDbContextFactory<BotDbContext> dbContextFactory,
    IOptions<ShopOptions> shopOptions,
    GuildSettingsService guildSettingsService,
    JailRoleService jailRoleService,
    EconomyMutationLock mutationLock,
    ILogger<ShopService> logger)
{
    private readonly ShopOptions _options = shopOptions.Value;
    private readonly SemaphoreSlim _mutationGate = mutationLock.Gate;

    public async Task<IReadOnlyList<ShopCatalogItem>> GetCatalogAsync(
        ulong guildId,
        CancellationToken cancellationToken = default)
    {
        var guildSettings = await guildSettingsService.GetAsync(guildId, cancellationToken);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.ShopItems
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Price)
            .ToListAsync(cancellationToken);

        return items.Select(item => new ShopCatalogItem(
                item.Key,
                item.Name,
                item.Description,
                item.Price,
                IsConfigured(item, guildSettings),
                AvailabilityMessage(item, guildSettings)))
            .ToList();
    }

    public async Task<PurchaseResult> BuyAsync(
        SocketGuild guild,
        SocketGuildUser buyer,
        SocketGuildUser? target,
        string itemKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = itemKey.Trim().ToLowerInvariant();
        var guildSettings = await guildSettingsService.GetAsync(guild.Id, cancellationToken);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var item = await db.ShopItems.SingleOrDefaultAsync(
                x => x.Key == normalizedKey && x.IsEnabled,
                cancellationToken);
            if (item is null)
            {
                return PurchaseResult.Failure("Nie ma takiego przedmiotu. Użyj `/shop`, aby zobaczyć listę.");
            }

            if (!IsConfigured(item, guildSettings))
            {
                return PurchaseResult.Failure(AvailabilityMessage(item, guildSettings));
            }

            var validationError = ValidateTarget(guild, buyer, target, item, guildSettings);
            if (validationError is not null)
            {
                return PurchaseResult.Failure(validationError);
            }

            var validatedTarget = target!;
            if (item.Type == ShopItemType.VoiceJail)
            {
                var guildId = EconomyService.Id(guild.Id);
                var targetId = EconomyService.Id(validatedTarget.Id);
                var alreadyJailed = await db.VoiceRewardEffects.AnyAsync(
                    x => x.GuildId == guildId
                         && x.TargetDiscordUserId == targetId
                         && x.Type == VoiceRewardEffectType.Jail
                         && x.EndedAtUtc == null,
                    cancellationToken);
                if (alreadyJailed)
                {
                    return PurchaseResult.Failure("Wskazana osoba odbywa już karę więzienia.");
                }
            }

            var originalChannelId = validatedTarget.VoiceChannel!.Id;
            var originalRoleIds = item.Type == ShopItemType.VoiceJail
                ? jailRoleService.CaptureRoles(validatedTarget)
                : [];

            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            var now = DateTime.UtcNow;
            var botUser = await GetOrCreateUserAsync(
                db,
                guild.Id,
                buyer.Id,
                buyer.DisplayName,
                now,
                cancellationToken);
            if (botUser.Points < item.Price)
            {
                await transaction.RollbackAsync(cancellationToken);
                return PurchaseResult.Failure(
                    $"Masz **{botUser.Points:N0} pkt**, a `{item.Key}` kosztuje **{item.Price:N0} pkt**.");
            }

            botUser.Points -= item.Price;
            botUser.UpdatedAtUtc = now;
            var purchase = new Purchase
            {
                BotUser = botUser,
                ShopItem = item,
                TargetDiscordUserId = EconomyService.Id(validatedTarget.Id),
                PurchasedAtUtc = now,
                PricePaid = item.Price,
                Status = PurchaseStatus.Pending
            };
            db.Purchases.Add(purchase);

            if (item.Type == ShopItemType.VoiceJail)
            {
                db.VoiceRewardEffects.Add(new VoiceRewardEffect
                {
                    Purchase = purchase,
                    GuildId = EconomyService.Id(guild.Id),
                    TargetDiscordUserId = EconomyService.Id(validatedTarget.Id),
                    Type = VoiceRewardEffectType.Jail,
                    OriginalChannelId = EconomyService.Id(originalChannelId),
                    JailChannelId = EconomyService.Id(guildSettings.JailVoiceChannelId!.Value),
                    JailRoleId = EconomyService.Id(guildSettings.JailRoleId!.Value),
                    OriginalRoleIdsJson = JailRoleService.SerializeRoles(originalRoleIds),
                    RequiredDurationSeconds = checked(_options.JailDurationMinutes * 60L),
                    ServedDurationSeconds = 0,
                    StartsAtUtc = now,
                    // Nowe kary używają licznika faktycznej obecności. Pole pozostaje
                    // uzupełnione dla zgodności ze starszym schematem i indeksem.
                    ExpiresAtUtc = now.AddMinutes(_options.JailDurationMinutes)
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            try
            {
                await ApplyDiscordEffectAsync(
                    guild,
                    validatedTarget,
                    item,
                    guildSettings,
                    originalRoleIds);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Discord odrzucił nagrodę {ItemKey} kupioną przez {BuyerId} przeciwko {TargetId}.",
                    item.Key,
                    buyer.Id,
                    validatedTarget.Id);
                await RefundPurchaseAsync(purchase.Id, exception.Message, cancellationToken);
                return PurchaseResult.Failure(
                    "Discord odrzucił wykonanie tej nagrody. Zakup anulowano, a punkty zostały zwrócone.");
            }

            return await FinalizePurchaseAsync(
                guild,
                validatedTarget,
                item,
                purchase.Id,
                guildSettings,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Nie udało się zrealizować zakupu {ItemKey} dla użytkownika {UserId}.",
                normalizedKey,
                buyer.Id);
            return PurchaseResult.Failure(
                "Zakup nie powiódł się z powodu błędu bota. Punkty nie powinny zostać utracone.");
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task ApplyDiscordEffectAsync(
        SocketGuild guild,
        SocketGuildUser target,
        ShopItem item,
        GuildSettingsSnapshot guildSettings,
        IReadOnlyList<ulong> originalRoleIds)
    {
        switch (item.Type)
        {
            case ShopItemType.VoiceMute:
                await target.ModifyAsync(properties => properties.Mute = true);
                break;

            case ShopItemType.VoiceJail:
                var jailChannel = guild.GetVoiceChannel(guildSettings.JailVoiceChannelId!.Value)
                                  ?? throw new InvalidOperationException("Kanał więzienny nie istnieje.");
                var jailRoleId = guildSettings.JailRoleId!.Value;
                try
                {
                    await jailRoleService.EnsureJailRoleOnlyAsync(target, jailRoleId);
                    await target.ModifyAsync(properties => properties.Channel = jailChannel);
                }
                catch
                {
                    try
                    {
                        await jailRoleService.RestoreRolesAsync(target, jailRoleId, originalRoleIds);
                    }
                    catch (Exception restoreException)
                    {
                        logger.LogCritical(
                            restoreException,
                            "Nie udało się cofnąć częściowo wykonanej kary więzienia użytkownika {UserId}.",
                            target.Id);
                    }

                    throw;
                }

                break;

            case ShopItemType.VoiceDisconnect:
                await target.ModifyAsync(properties => properties.Channel = null);
                break;

            default:
                throw new InvalidOperationException("Nieobsługiwany typ nagrody głosowej.");
        }
    }

    private async Task<PurchaseResult> FinalizePurchaseAsync(
        SocketGuild guild,
        SocketGuildUser target,
        ShopItem item,
        int purchaseId,
        GuildSettingsSnapshot guildSettings,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var purchase = await db.Purchases
                .Include(x => x.BotUser)
                .SingleAsync(x => x.Id == purchaseId, cancellationToken);
            var now = DateTime.UtcNow;
            purchase.Status = PurchaseStatus.Completed;

            string rewardMessage;
            if (item.Type == ShopItemType.VoiceMute)
            {
                var expiresAt = now.AddMinutes(_options.MuteDurationMinutes);
                db.VoiceRewardEffects.Add(new VoiceRewardEffect
                {
                    PurchaseId = purchase.Id,
                    GuildId = EconomyService.Id(guild.Id),
                    TargetDiscordUserId = EconomyService.Id(target.Id),
                    Type = VoiceRewardEffectType.Mute,
                    JailChannelId = string.Empty,
                    StartsAtUtc = now,
                    ExpiresAtUtc = expiresAt
                });
                rewardMessage = $"{target.Mention} został wyciszony na **{_options.MuteDurationMinutes} min**.";
                purchase.RewardSummary = $"Wyciszenie {target.Id} do {expiresAt:u}.";
            }
            else if (item.Type == ShopItemType.VoiceJail)
            {
                var effect = await db.VoiceRewardEffects.SingleAsync(
                    x => x.PurchaseId == purchase.Id,
                    cancellationToken);
                if (target.VoiceChannel?.Id == guildSettings.JailVoiceChannelId)
                {
                    effect.JailPresenceStartedAtUtc = now;
                }

                rewardMessage =
                    $"{target.Mention} trafił do więzienia na **{_options.JailDurationMinutes} min faktycznej obecności**.";
                purchase.RewardSummary =
                    $"Więzienie {target.Id}: do odsiedzenia {effect.RequiredDurationSeconds} s.";
            }
            else
            {
                rewardMessage = $"{target.Mention} został wyrzucony z kanału głosowego.";
                purchase.RewardSummary = $"Rozłączono {target.Id} z kanału głosowego.";
            }

            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Użytkownik {BuyerId} kupił {ItemKey} wobec {TargetId} za {Price} pkt. Saldo: {Balance} pkt.",
                purchase.BotUser.DiscordUserId,
                item.Key,
                target.Id,
                purchase.PricePaid,
                purchase.BotUser.Points);
            return PurchaseResult.Successful(
                $"Kupiono **{item.Name}** za **{item.Price:N0} pkt**. {rewardMessage}\n" +
                $"Saldo: **{purchase.BotUser.Points:N0} pkt**.",
                purchase.BotUser.Points);
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                exception,
                "Nagroda {ItemKey} została wykonana wobec {TargetId}, ale nie zapisano finalizacji zakupu {PurchaseId}.",
                item.Key,
                target.Id,
                purchaseId);
            return PurchaseResult.Failure(
                "Nagroda została wykonana, ale zapis transakcji wymaga uwagi administratora. Punkty zostały pobrane.");
        }
    }

    private async Task RefundPurchaseAsync(int purchaseId, string failureReason, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var purchase = await db.Purchases
            .Include(x => x.BotUser)
            .SingleAsync(x => x.Id == purchaseId, cancellationToken);

        if (purchase.Status == PurchaseStatus.Pending)
        {
            purchase.BotUser.Points = checked(purchase.BotUser.Points + purchase.PricePaid);
            purchase.BotUser.UpdatedAtUtc = DateTime.UtcNow;
            purchase.Status = PurchaseStatus.FailedAndRefunded;
            purchase.FailureReason = failureReason.Length <= 500 ? failureReason : failureReason[..500];

            var effect = await db.VoiceRewardEffects.SingleOrDefaultAsync(
                x => x.PurchaseId == purchaseId,
                cancellationToken);
            if (effect is not null)
            {
                effect.EndedAtUtc = DateTime.UtcNow;
                effect.JailPresenceStartedAtUtc = null;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private string? ValidateTarget(
        SocketGuild guild,
        SocketGuildUser buyer,
        SocketGuildUser? target,
        ShopItem item,
        GuildSettingsSnapshot guildSettings)
    {
        if (target is null)
        {
            return "Wskaż osobę w parametrze `cel`, np. `/buy przedmiot:wyciszenie cel:@osoba`.";
        }

        if (target.Id == buyer.Id)
        {
            return "Nie możesz użyć tej nagrody na sobie.";
        }

        if (target.IsBot)
        {
            return "Nie można używać nagród głosowych na botach.";
        }

        if (target.Hierarchy >= guild.CurrentUser.Hierarchy)
        {
            return "Rola bota jest zbyt nisko, aby wykonać tę akcję na wskazanej osobie.";
        }

        if (target.VoiceChannel is null)
        {
            return "Wskazana osoba nie znajduje się teraz na kanale głosowym.";
        }

        if (item.Type == ShopItemType.VoiceMute)
        {
            if (!guild.CurrentUser.GuildPermissions.MuteMembers)
            {
                return "Bot potrzebuje uprawnienia `Wyciszanie członków`.";
            }

            if (target.IsMuted)
            {
                return "Wskazana osoba jest już wyciszona przez serwer.";
            }
        }

        if (item.Type is ShopItemType.VoiceJail or ShopItemType.VoiceDisconnect
            && !guild.CurrentUser.GuildPermissions.MoveMembers)
        {
            return "Bot potrzebuje uprawnienia `Przenoszenie członków`.";
        }

        if (item.Type == ShopItemType.VoiceJail)
        {
            var jailChannel = guildSettings.JailVoiceChannelId is { } channelId
                ? guild.GetVoiceChannel(channelId)
                : null;
            var jailRole = guildSettings.JailRoleId is { } roleId
                ? guild.GetRole(roleId)
                : null;
            if (jailChannel is null || jailRole is null || jailRole.IsManaged)
            {
                return "Kanał lub rola więzienna nie zostały poprawnie skonfigurowane.";
            }

            if (!guild.CurrentUser.GuildPermissions.ManageRoles
                || guild.CurrentUser.Hierarchy <= jailRole.Position)
            {
                return "Bot potrzebuje `Zarządzania rolami`, a jego rola musi być nad rolą więzienną.";
            }

            if (target.VoiceChannel.Id == jailChannel.Id || target.Roles.Any(role => role.Id == jailRole.Id))
            {
                return "Wskazana osoba już znajduje się w więzieniu albo ma rolę więzienną.";
            }
        }

        return item.Type is ShopItemType.VoiceMute or ShopItemType.VoiceJail or ShopItemType.VoiceDisconnect
            ? null
            : "Ten typ nagrody nie jest obecnie dostępny.";
    }

    private static bool IsConfigured(ShopItem item, GuildSettingsSnapshot settings) =>
        item.Type != ShopItemType.VoiceJail
        || settings.JailVoiceChannelId is not null && settings.JailRoleId is not null;

    private static string AvailabilityMessage(ShopItem item, GuildSettingsSnapshot settings) =>
        item.Type == ShopItemType.VoiceJail
        && (settings.JailVoiceChannelId is null || settings.JailRoleId is null)
            ? "Niedostępne: administrator musi użyć `/ustawienia wiezienie kanal:... rola:...`."
            : "Dostępne";

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
        var user = await db.Users.SingleOrDefaultAsync(
            x => x.GuildId == guildIdText && x.DiscordUserId == userIdText,
            cancellationToken);
        if (user is not null)
        {
            user.LastKnownDisplayName = displayName;
            user.UpdatedAtUtc = now;
            return user;
        }

        user = new BotUser
        {
            GuildId = guildIdText,
            DiscordUserId = userIdText,
            LastKnownDisplayName = displayName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.Users.Add(user);
        return user;
    }
}

public sealed record ShopCatalogItem(
    string Key,
    string Name,
    string Description,
    long Price,
    bool IsAvailable,
    string AvailabilityMessage);

public sealed record PurchaseResult(bool IsSuccess, string Message, long? Balance)
{
    public static PurchaseResult Successful(string message, long balance) => new(true, message, balance);
    public static PurchaseResult Failure(string message) => new(false, message, null);
}
