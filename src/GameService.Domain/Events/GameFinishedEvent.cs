namespace GameService.Domain.Events;

public class GameFinishedEvent
{
    public Guid GameId { get; set; }
    public int Rounds { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int CatId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public List<PlayerGameResult> Players { get; set; } = [];
}

public class PlayerGameResult
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public int Score { get; set; }
    public int CorrectAnswers { get; set; }
    public int AnsweredRounds { get; set; }
    public int Placement { get; set; }
}
