namespace GameService.Domain.Dtos;

// Messages on the SignalR hub /hubs/game (SCRUM-102). Serialized as camelCase JSON.
// None of them reveal the song of a round that is still being played: RoundStartedDto has no
// title, artists or year, and the audio URL only arrives through PrefetchDto.

// Returned by JoinGame. Can be called again after a reconnect.
public record GameStateDto(
    Guid GameId,
    GameSettingsDto Settings,
    int Round,                              // 0 before the first round has started
    int TotalRounds,
    IReadOnlyList<PlayerSummaryDto> Players,
    IReadOnlyList<CardDto> MyTimeline,      // sorted by year
    IReadOnlyList<PrefetchDto> Prefetch,    // the files the client should have ready now
    RoundStartedDto? CurrentRound);         // set while a round is being played

public record GameSettingsDto(int CategoryId, string CategoryName, int Rounds, int AnswerTimeSeconds);

public record PlayerSummaryDto(int UserId, string UserName, int Score);

public record CardDto(string Title, IReadOnlyList<string> Artists, int Year);

public record PrefetchDto(int Round, string AudioUrl);

// startsInMs is relative: the client starts a local timer when the message arrives and plays when
// it runs out. No server timestamps, so a wrong clock on the player's device does not matter.
// In GameStateDto.CurrentRound (after a reconnect) the values are what is left of the round:
// startsInMs until playback (0 if it has started), the offset moved forward by the seconds already
// played, and answerTimeSeconds reduced by the same.
public record RoundStartedDto(int Round, int TotalRounds, int StartsInMs, int StartOffsetSeconds, int AnswerTimeSeconds);
