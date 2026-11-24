namespace GameService.Service.Options;

// The "Game" section in configuration (env e.g. Game__ReadyTimeoutSeconds).
public class GameOptions
{
    public const string SectionName = "Game";

    // The game starts when every player has called JoinGame, or this long after it was created.
    public int JoinTimeoutSeconds { get; set; } = 15;

    // A round starts when every connected player has sent Ready for it, or after this long.
    public int ReadyTimeoutSeconds { get; set; } = 10;

    // RoundStarted.startsInMs: how long clients wait before they play.
    public int StartDelayMs { get; set; } = 3000;

    // Answers that arrive this long after the answer time still count (network delay).
    // The round ends at endsAt + this, unless everyone has answered earlier.
    public int AnswerGraceSeconds { get; set; } = 1;

    // The next round is prepared this long after RoundEnded.
    public int PauseBetweenRoundsSeconds { get; set; } = 8;

    // How many rounds ahead the clients download (rounds 1-5 at the start, k+5 when round k starts).
    public int PrefetchWindow { get; set; } = 5;
}
