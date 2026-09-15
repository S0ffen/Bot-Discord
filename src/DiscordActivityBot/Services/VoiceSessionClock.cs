using DiscordActivityBot.Data;

namespace DiscordActivityBot.Services;

public static class VoiceSessionClock
{
    public static long CheckpointPresence(
        VoiceSession session,
        DateTime observedAtUtc,
        long pointsPerMinute,
        IReadOnlyCollection<PointBoost> boosts)
    {
        if (session.LeftAtUtc is not null || observedAtUtc < session.LastObservedAtUtc)
        {
            return 0;
        }

        var durationSeconds = Math.Max(0, (long)Math.Floor((observedAtUtc - session.JoinedAtUtc).TotalSeconds));
        var newPoints = PointCalculator.Calculate(
            session.JoinedAtUtc, observedAtUtc, pointsPerMinute, boosts,
            previouslyAwardedMinutes: session.DurationSeconds / 60);
        var awardedPoints = checked(session.AwardedPoints + newPoints);
        var balance = checked(session.BotUser.Points + newPoints);

        session.LastObservedAtUtc = observedAtUtc;
        session.DurationSeconds = durationSeconds;
        session.AwardedPoints = awardedPoints;
        session.BotUser.Points = balance;
        session.BotUser.UpdatedAtUtc = DateTime.UtcNow;
        return newPoints;
    }
}
