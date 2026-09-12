using System.Text.Json;
using Discord.WebSocket;

namespace DiscordActivityBot.Services;

public sealed class JailRoleService(ILogger<JailRoleService> logger)
{
    public IReadOnlyList<ulong> CaptureRoles(SocketGuildUser user) =>
        user.Roles
            .Where(role => role.Id != user.Guild.EveryoneRole.Id && !role.IsManaged)
            .Select(role => role.Id)
            .Distinct()
            .ToArray();

    public static string SerializeRoles(IEnumerable<ulong> roleIds) =>
        JsonSerializer.Serialize(roleIds.Distinct().ToArray());

    public IReadOnlyList<ulong> DeserializeRoles(string? roleIdsJson)
    {
        if (string.IsNullOrWhiteSpace(roleIdsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<ulong[]>(roleIdsJson) ?? [];
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "Nie udało się odczytać zapisanej listy ról więźnia.");
            return [];
        }
    }

    public async Task EnsureJailRoleOnlyAsync(SocketGuildUser user, ulong jailRoleId)
    {
        var jailRole = user.Guild.GetRole(jailRoleId);
        if (jailRole is null || jailRole.IsManaged || user.Guild.CurrentUser.Hierarchy <= jailRole.Position)
        {
            throw new InvalidOperationException("Rola więzienna nie istnieje albo bot nie może jej przydzielać.");
        }

        if (user.Roles.All(role => role.Id != jailRoleId))
        {
            await user.AddRoleAsync(jailRole);
        }

        var rolesToRemove = user.Roles
            .Where(role => role.Id != user.Guild.EveryoneRole.Id
                           && role.Id != jailRoleId
                           && !role.IsManaged)
            .ToArray();
        foreach (var role in rolesToRemove)
        {
            await user.RemoveRoleAsync(role);
        }
    }

    public async Task RestoreRolesAsync(
        SocketGuildUser user,
        ulong jailRoleId,
        IEnumerable<ulong> originalRoleIds)
    {
        var rolesToRestore = new List<SocketRole>();
        foreach (var roleId in originalRoleIds.Distinct())
        {
            var role = user.Guild.GetRole(roleId);
            if (role is null || role.IsManaged || role.Id == user.Guild.EveryoneRole.Id)
            {
                logger.LogWarning(
                    "Nie można przywrócić nieistniejącej albo zarządzanej roli {RoleId} użytkownikowi {UserId}.",
                    roleId,
                    user.Id);
                continue;
            }

            if (user.Guild.CurrentUser.Hierarchy <= role.Position)
            {
                throw new InvalidOperationException(
                    $"Nie można przywrócić roli {role.Id}, ponieważ rola bota jest zbyt nisko.");
            }

            rolesToRestore.Add(role);
        }

        // Nie opieramy się tutaj na user.Roles. Bez intentu GuildMembers lokalny cache
        // może nadal zawierać role usunięte przy rozpoczęciu kary. Discordowe operacje
        // dodania i usunięcia roli są idempotentne, więc można wykonać je bezwarunkowo.
        foreach (var role in rolesToRestore)
        {
            await user.AddRoleAsync(role);
        }

        var jailRole = user.Guild.GetRole(jailRoleId);
        if (jailRole is not null && !jailRole.IsManaged)
        {
            if (user.Guild.CurrentUser.Hierarchy <= jailRole.Position)
            {
                throw new InvalidOperationException(
                    $"Nie można usunąć roli więziennej {jailRole.Id}, ponieważ rola bota jest zbyt nisko.");
            }

            await user.RemoveRoleAsync(jailRole);
        }
    }
}
