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
    public int CurrentRound { get; set; }
    public GameStatus Status { get; set; } = GameStatus.Waiting;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
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
}

public class TimelineCard
{
    public Song Song { get; set; } = new();
    public int Year => Song.Year;
}

// Shape of a song as returned by MusicService (GET /api/playlists/{categoryId}/songs/random).
public class Song
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int Year { get; set; }
    public string? PreviewUrl { get; set; }
    public int DurationSeconds { get; set; }
}
