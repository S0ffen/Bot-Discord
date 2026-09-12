namespace DiscordActivityBot.Services;

/// <summary>
/// Serializuje zmiany salda w obrębie jednej instancji bota. SQLite nadal zapewnia
/// transakcje w bazie, a ta blokada zapobiega nadpisaniu salda przez dwa równoległe zdarzenia.
/// </summary>
public sealed class EconomyMutationLock
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
}
