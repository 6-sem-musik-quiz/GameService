namespace GameService.Domain.Dtos;

// Messages on the SignalR hub /hubs/game (SCRUM-102). Serialized as camelCase JSON.
// None of them reveal the song of a round that is still being played: RoundStartedDto has no
// title, artists or year, and the audio URL only arrives through PrefetchDto.

// Returned by JoinGame. Can be called again after a reconnect.
// SCRUM-150: everything about earlier rounds is public (every player gets the same songs, revealed in
// RoundEnded), so the state carries everyone's cards, history and the past songs. What is still
// secret, where anyone placed the current round's song, is never in here.
public record GameStateDto(
    Guid GameId,
    GameSettingsDto Settings,
    int Round,                              // 0 before the first round has started
    int TotalRounds,
    IReadOnlyList<PlayerSummaryDto> Players,
    IReadOnlyList<CardDto> MyTimeline,      // sorted by year
    IReadOnlyList<PrefetchDto> Prefetch,    // the files the client should have ready now
    RoundStartedDto? CurrentRound,          // set while a round is being played
    IReadOnlyList<PastSongDto> PastSongs);  // the songs of the rounds that have ended

public record GameSettingsDto(int CategoryId, string CategoryName, int Rounds, int AnswerTimeSeconds);

// Timeline and History cover ended rounds only. AnsweredOrder (1, 2, 3 ...) is set while a round is
// being played and the player has answered it; never the answer itself.
public record PlayerSummaryDto(
    int UserId,
    string UserName,
    int Score,
    IReadOnlyList<CardDto> Timeline,
    IReadOnlyList<RoundHistoryDto> History,
    int? AnsweredOrder);

// AnsweredAfterMs is null when the player did not answer that round.
public record RoundHistoryDto(int Round, bool Correct, int? AnsweredAfterMs);

public record PastSongDto(int Round, CardDto Song);

public record CardDto(string Title, IReadOnlyList<string> Artists, int Year);

public record PrefetchDto(int Round, string AudioUrl);

// SCRUM-103: someone answered. Does not reveal the answer.
// SCRUM-150: AnsweredAfterMs is measured by the server, from when playback started
// (ActiveRound.PlaybackStartsAt) to when the answer arrived, clamped to [0, answer time].
public record PlayerAnsweredDto(int UserId, int AnsweredAfterMs);

// SCRUM-103: the round is over. Reveals the song and everyone's answer and timeline.
public record RoundEndedDto(int Round, CardDto Song, IReadOnlyList<RoundResultDto> Results);

// SlotIndex, AnsweredAfterMs and Range are null when the player did not answer.
// SCRUM-150: Range = [from, to], the years of the two cards the answer was put between
// (in the timeline before this round's card); from is null for "before the first card",
// to is null for "after the last card".
public record RoundResultDto(
    int UserId,
    string UserName,
    int? SlotIndex,
    bool Correct,
    int Score,
    IReadOnlyList<CardDto> Timeline,
    int? AnsweredAfterMs,
    IReadOnlyList<int?>? Range);

// SCRUM-104: after the last RoundEnded. Standings are sorted by score, highest first.
public record GameEndedDto(Guid GameId, IReadOnlyList<StandingDto> Standings);

public record StandingDto(int UserId, string UserName, int Score, int Correct, int Answered, int Placement,
    IReadOnlyList<CardDto> Timeline);

// startsInMs is relative: the client starts a local timer when the message arrives and plays when
// it runs out. No server timestamps, so a wrong clock on the player's device does not matter.
// In GameStateDto.CurrentRound (after a reconnect) the values are what is left of the round:
// startsInMs until playback (0 if it has started), the offset moved forward by the seconds already
// played, and answerTimeSeconds reduced by the same.
public record RoundStartedDto(int Round, int TotalRounds, int StartsInMs, int StartOffsetSeconds, int AnswerTimeSeconds);
