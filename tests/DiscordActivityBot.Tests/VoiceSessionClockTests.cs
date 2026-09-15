using DiscordActivityBot.Data;
using DiscordActivityBot.Services;
using Xunit;

namespace DiscordActivityBot.Tests;

public sealed class VoiceSessionClockTests
{
    private static readonly DateTime JoinedAt = new(2026, 9, 15, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CheckpointPresence_CreditsFullMinutesWhileSessionIsOpen()
    {
        var session = CreateSession();

        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddSeconds(59), 1, []);
        Assert.Equal(0, session.BotUser.Points);

        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddSeconds(60), 1, []);
        Assert.Equal(1, session.BotUser.Points);
        Assert.Null(session.LeftAtUtc);

        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddSeconds(90), 1, []);
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddSeconds(120), 1, []);
        Assert.Equal(2, session.BotUser.Points);
        Assert.Equal(2, session.AwardedPoints);
        Assert.Equal(120, session.DurationSeconds);
    }

    [Fact]
    public void CheckpointPresence_DoesNotCreditSpentPointsAgain()
    {
        var session = CreateSession();
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(2), 1, []);
        session.BotUser.Points -= 2;

        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(2), 1, []);
        Assert.Equal(0, session.BotUser.Points);

        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(3).AddSeconds(59), 1, []);
        Assert.Equal(1, session.BotUser.Points);
        Assert.Equal(3, session.AwardedPoints);
    }

    [Fact]
    public void CheckpointPresence_PreservesBoostWindowsAcrossCheckpoints()
    {
        var session = CreateSession();
        PointBoost[] boosts =
        [
            new()
            {
                Multiplier = 3,
                StartsAtUtc = JoinedAt.AddSeconds(90),
                ExpiresAtUtc = JoinedAt.AddMinutes(3)
            }
        ];

        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(1), 1, boosts);
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(2), 1, boosts);
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(3), 1, boosts);

        Assert.Equal(5, session.BotUser.Points);
        Assert.Equal(PointCalculator.Calculate(JoinedAt, JoinedAt.AddMinutes(3), 1, boosts),
            session.AwardedPoints);
    }

    [Fact]
    public void CheckpointPresence_ChangedRateAffectsOnlyNewMinutes()
    {
        var session = CreateSession();
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(2), 5, []);
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(3), 1, []);

        Assert.Equal(11, session.BotUser.Points);
        Assert.Equal(11, session.AwardedPoints);
    }

    [Fact]
    public void CheckpointPresence_IgnoresOlderObservations()
    {
        var session = CreateSession();
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(2), 1, []);
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(1), 1, []);
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(2), 1, []);

        Assert.Equal(2, session.BotUser.Points);
        Assert.Equal(120, session.DurationSeconds);
        Assert.Equal(JoinedAt.AddMinutes(2), session.LastObservedAtUtc);
    }

    [Fact]
    public void CheckpointPresence_DoesNotChangeCompletedSessions()
    {
        var session = CreateSession();
        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(1), 1, []);
        session.LeftAtUtc = JoinedAt.AddMinutes(1);

        VoiceSessionClock.CheckpointPresence(session, JoinedAt.AddMinutes(10), 1, []);

        Assert.Equal(1, session.BotUser.Points);
        Assert.Equal(60, session.DurationSeconds);
    }

    private static VoiceSession CreateSession() => new()
    {
        GuildId = "1",
        DiscordUserId = "2",
        ChannelId = "3",
        ChannelName = "Test",
        JoinedAtUtc = JoinedAt,
        LastObservedAtUtc = JoinedAt,
        BotUser = new BotUser
        {
            GuildId = "1",
            DiscordUserId = "2",
            LastKnownDisplayName = "Test user"
        }
    };
}
