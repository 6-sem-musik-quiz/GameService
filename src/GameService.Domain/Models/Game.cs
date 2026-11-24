namespace GameService.Domain.Models;

public enum GameStatus
{
    Waiting,
    Playing,
    Finished
}

public class Game
{
    public Guid Id { get; set; }
    public Guid LobbyId { get; set; }
    public GameSettings Settings { get; set; } = new();
    // Songs[0] is the shared start card; Songs[1..] are one per round.
    public List<Song> Songs { get; set; } = [];
    public List<GamePlayer> Players { get; set; } = [];
    // 0 until the first round starts; then the number of the round being played (or last played).
    public int CurrentRound { get; set; }
    // The round being played right now, or null between rounds. Set by GameCoordinator (SCRUM-102).
    public ActiveRound? ActiveRound { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Waiting;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    // Songs[0] is the start card, so round r plays Songs[r].
    public Song SongForRound(int round) => Songs[round];
}

// Server-side timing of one round. Never sent to clients as timestamps.
public class ActiveRound
{
    public int Round { get; init; }
    public int StartOffsetSeconds { get; init; }
    // UTC time when the clients start playing (when RoundStarted was sent + startsInMs).
    public DateTimeOffset PlaybackStartsAt { get; init; }
    // PlaybackStartsAt + answer time. Answers after this are rejected (SCRUM-103).
    public DateTimeOffset EndsAt { get; init; }
}

public class GameSettings
{
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public int Rounds { get; set; }
    public int AnswerTimeSeconds { get; set; }
}

public class GamePlayer
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int Score { get; set; }
    public int Correct { get; set; }
    public int Answered { get; set; }
    // Sorted by year, ascending.
    public List<TimelineCard> Timeline { get; set; } = [];
    // One entry per ended round (SCRUM-150): right or not, and how fast.
    public List<RoundHistory> History { get; set; } = [];
}

public class RoundHistory
{
    public int Round { get; init; }
    public bool Correct { get; init; }
    // Null when the player did not answer.
    public int? AnsweredAfterMs { get; init; }
}

public class TimelineCard
{
    public Song Song { get; set; } = new();
    public int Year => Song.Year;
}

// Same shape as MusicService's SongDto (GET /api/playlists/{categoryId}/songs/random).
// AudioUrl is the public CDN address of the whole mp3.
public class Song
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<string> Artists { get; set; } = [];
    public int Year { get; set; }
    public int DurationSeconds { get; set; }
    public string AudioUrl { get; set; } = string.Empty;
}
