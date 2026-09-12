using DiscordActivityBot.Data;
using DiscordActivityBot.Services;
using Xunit;

namespace DiscordActivityBot.Tests;

public sealed class JailSentenceClockTests
{
    [Fact]
    public void PresenceClock_CountsOnlyTimeBetweenEnterAndLeave()
    {
        var startedAt = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var effect = CreateEffect(300);

        JailSentenceClock.StartPresence(effect, startedAt);
        JailSentenceClock.PausePresence(effect, startedAt.AddSeconds(180.8));

        Assert.Equal(180, effect.ServedDurationSeconds);
        Assert.Null(effect.JailPresenceStartedAtUtc);

        JailSentenceClock.CheckpointPresence(effect, startedAt.AddHours(1));
        Assert.Equal(180, effect.ServedDurationSeconds);
        Assert.Equal(120, JailSentenceClock.RemainingSeconds(effect));
    }

    [Fact]
    public void PresenceClock_CompletesAfterAccumulatedSessions()
    {
        var startedAt = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
        var effect = CreateEffect(300);

        JailSentenceClock.StartPresence(effect, startedAt);
        JailSentenceClock.PausePresence(effect, startedAt.AddSeconds(180));
        JailSentenceClock.StartPresence(effect, startedAt.AddMinutes(30));
        JailSentenceClock.CheckpointPresence(effect, startedAt.AddMinutes(32));

        Assert.Equal(300, effect.ServedDurationSeconds);
        Assert.True(JailSentenceClock.IsComplete(effect));
        Assert.Equal(0, JailSentenceClock.RemainingSeconds(effect));
    }

    private static VoiceRewardEffect CreateEffect(long requiredDurationSeconds) => new()
    {
        GuildId = "1",
        TargetDiscordUserId = "2",
        JailChannelId = "3",
        Type = VoiceRewardEffectType.Jail,
        StartsAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow,
        RequiredDurationSeconds = requiredDurationSeconds
    };
}
