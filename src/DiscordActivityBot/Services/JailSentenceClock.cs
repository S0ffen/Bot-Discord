using DiscordActivityBot.Data;

namespace DiscordActivityBot.Services;

public static class JailSentenceClock
{
    public static void StartPresence(VoiceRewardEffect effect, DateTime startedAtUtc)
    {
        effect.JailPresenceStartedAtUtc = startedAtUtc;
    }

    public static void CheckpointPresence(VoiceRewardEffect effect, DateTime observedAtUtc)
    {
        if (effect.JailPresenceStartedAtUtc is not { } startedAtUtc || observedAtUtc <= startedAtUtc)
        {
            return;
        }

        var wholeSeconds = (long)Math.Floor((observedAtUtc - startedAtUtc).TotalSeconds);
        if (wholeSeconds <= 0)
        {
            return;
        }

        effect.ServedDurationSeconds = checked(effect.ServedDurationSeconds + wholeSeconds);
        effect.JailPresenceStartedAtUtc = startedAtUtc.AddSeconds(wholeSeconds);
    }

    public static void PausePresence(VoiceRewardEffect effect, DateTime leftAtUtc)
    {
        CheckpointPresence(effect, leftAtUtc);
        effect.JailPresenceStartedAtUtc = null;
    }

    public static bool IsComplete(VoiceRewardEffect effect) =>
        effect.RequiredDurationSeconds > 0
        && effect.ServedDurationSeconds >= effect.RequiredDurationSeconds;

    public static long RemainingSeconds(VoiceRewardEffect effect) =>
        Math.Max(0, effect.RequiredDurationSeconds - effect.ServedDurationSeconds);
}
