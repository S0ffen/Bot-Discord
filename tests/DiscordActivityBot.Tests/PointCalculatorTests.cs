using DiscordActivityBot.Data;
using DiscordActivityBot.Services;
using Xunit;

namespace DiscordActivityBot.Tests;

public sealed class PointCalculatorTests
{
    private static readonly DateTime JoinedAt = new(2026, 9, 9, 18, 3, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 59, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(44, 0, 44)]
    [InlineData(44, 59, 44)]
    public void Calculate_AwardsOnlyFullMinutes(int minutes, int seconds, long expectedPoints)
    {
        var leftAt = JoinedAt.AddMinutes(minutes).AddSeconds(seconds);

        var result = PointCalculator.Calculate(JoinedAt, leftAt, 1, []);

        Assert.Equal(expectedPoints, result);
    }

    [Fact]
    public void Calculate_AppliesBoostOnlyToMinutesEarnedInsideItsWindow()
    {
        var boosts = new[]
        {
            new PointBoost
            {
                Multiplier = 2,
                StartsAtUtc = JoinedAt.AddMinutes(7),
                ExpiresAtUtc = JoinedAt.AddMinutes(17)
            }
        };

        var result = PointCalculator.Calculate(JoinedAt, JoinedAt.AddMinutes(44), 1, boosts);

        Assert.Equal(54, result);
    }

    [Fact]
    public void Calculate_UsesHighestMultiplierInsteadOfStackingBoosts()
    {
        var boosts = new[]
        {
            new PointBoost
            {
                Multiplier = 2,
                StartsAtUtc = JoinedAt,
                ExpiresAtUtc = JoinedAt.AddMinutes(10)
            },
            new PointBoost
            {
                Multiplier = 3,
                StartsAtUtc = JoinedAt,
                ExpiresAtUtc = JoinedAt.AddMinutes(10)
            }
        };

        var result = PointCalculator.Calculate(JoinedAt, JoinedAt.AddMinutes(5), 1, boosts);

        Assert.Equal(15, result);
    }
}
