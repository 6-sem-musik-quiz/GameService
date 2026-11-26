namespace GameService.Domain.Models;

// ADR-0004: everything GameService knows about one running game. It is saved after every change
// (in Redis in the cluster, so every GameService pod can continue the game; in memory when no Redis
// is configured), and only changed under the game's lock.
public class GameSession
{
    public Game Game { get; set; } = new();

    // Players who have called JoinGame at least once.
    public HashSet<int> Joined { get; set; } = [];

    // Open hub connections per player (connection ids, from any pod).
    public Dictionary<int, HashSet<string>> Connections { get; set; } = [];

    // Players who are ready for a round, by round.
    public Dictionary<int, HashSet<int>> Ready { get; set; } = [];

    // Answers for the active round, by userId. Judged when the round ends.
    public Dictionary<int, PlayerAnswer> Answers { get; set; } = [];

    // The round the server is waiting for Ready for; 0 when it is not waiting.
    public int PreparingRound { get; set; }

    // What happens next if nothing else does (the game's only timer). A new deadline replaces the
    // old one; a deadline whose token is no longer this one is ignored when it comes due.
    public GameDeadline? Deadline { get; set; }

    // SCRUM-173: the W3C trace context of the request that created the game, so the game's timers and
    // its GameFinished message continue that trace. Null when the game was created without tracing.
    public string? TraceParent { get; set; }
}

// Order is 1 for the first answer of the round, 2 for the next, and so on.
public record PlayerAnswer(int Slot, int AfterMs, int Order);

public enum DeadlineKind
{
    Join,  // the join timeout: start with the players who have joined
    Ready, // the ready timeout for Round: start it anyway
    Round, // the answer time (+ grace) of Round is over: end it
    Pause  // the pause after Round is over: next round, or finish the game
}

public record GameDeadline(DeadlineKind Kind, int Round, DateTimeOffset DueAt, string Token);
