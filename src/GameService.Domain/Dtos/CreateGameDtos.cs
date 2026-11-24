namespace GameService.Domain.Dtos;

public class CreateGameRequest
{
    public Guid LobbyId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public int Rounds { get; set; }
    public int AnswerTimeSeconds { get; set; }
    public List<PlayerDto> Players { get; set; } = [];
}

public class PlayerDto
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
}

public record CreateGameResponse(Guid GameId);
