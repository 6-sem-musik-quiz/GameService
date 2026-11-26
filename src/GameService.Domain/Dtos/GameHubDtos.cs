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
// SCRUM-151 (gameservice.gamehub v1.2): Awards ("fastest" then "streak") and Songs (every round's song).
// The bonus is added to the normal score: Standings' Score and Placement include it.
public record GameEndedDto(
    Guid GameId,
    IReadOnlyList<StandingDto> Standings,
    IReadOnlyList<AwardDto> Awards,
    IReadOnlyList<PastSongDto> Songs);

// Kind is "fastest" or "streak". Winners got +1 (empty if nobody qualified). Contenders are every
// player, best first, so the client can reveal the values one at a time.
public record AwardDto(string Kind, IReadOnlyList<int> Winners, IReadOnlyList<AwardContenderDto> Contenders);

// Value: "fastest" = average answer time in ms over correct answers (null with none);
// "streak" = longest run of correct answers in a row.
public record AwardContenderDto(int UserId, int? Value, bool Qualified);

public record StandingDto(int UserId, string UserName, int Score, int Correct, int Answered, int Placement,
    IReadOnlyList<CardDto> Timeline);

// startsInMs is relative: a v1 client starts a local timer when the message arrives and plays when
// it runs out, so its network delay is added on top.
// SCRUM-168 (gameservice.gamehub v1.1): PlaybackStartsAt is the server time (UTC) at which the song is
// at StartOffsetSeconds; the answer time ends at PlaybackStartsAt + AnswerTimeSeconds. A client that
// knows its offset to the server clock (GameHub.ServerTime) plays at that instant, so every player
// starts together whatever their network delay.
// In GameStateDto.CurrentRound (after a reconnect) the values are what is left of the round:
// startsInMs until playback (0 if it has started), the offset moved forward by the whole seconds
// already played, answerTimeSeconds reduced by the same, and PlaybackStartsAt moved by the same, so
// the client gets the exact (ms) position from PlaybackStartsAt.
public record RoundStartedDto(int Round, int TotalRounds, int StartsInMs, int StartOffsetSeconds, int AnswerTimeSeconds,
    DateTimeOffset? PlaybackStartsAt = null);
