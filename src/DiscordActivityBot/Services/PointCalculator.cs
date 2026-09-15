using DiscordActivityBot.Data;

namespace DiscordActivityBot.Services;

public static class PointCalculator
{
    public static long Calculate(
        DateTime joinedAtUtc,
        DateTime leftAtUtc,
        long pointsPerMinute,
        IReadOnlyCollection<PointBoost> boosts,
        long previouslyAwardedMinutes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(previouslyAwardedMinutes);
        var fullMinutes = (long)Math.Floor((leftAtUtc - joinedAtUtc).TotalMinutes);
        if (fullMinutes <= previouslyAwardedMinutes)
        {
            return 0;
        }

        long total = 0;
        for (long minute = previouslyAwardedMinutes + 1; minute <= fullMinutes; minute++)
        {
            var earnedAt = joinedAtUtc.AddMinutes(minute);
            var multiplier = boosts
                .Where(x => x.StartsAtUtc <= earnedAt && earnedAt < x.ExpiresAtUtc)
                .Select(x => x.Multiplier)
                .DefaultIfEmpty(1)
                .Max();

            total = checked(total + checked(pointsPerMinute * multiplier));
        }

        return total;
    }
}
