namespace GameService.Service.Exceptions;

// Invalid input from the caller -> 400.
public class GameValidationException(string message) : Exception(message);

// MusicService returned too few songs -> 502.
public class InsufficientSongsException(int expected, int actual)
    : Exception($"MusicService returned {actual} songs, expected {expected}.");

// Unknown game, or the user is not a player in it. The hub turns it into a HubException.
public class GameAccessException(string message) : Exception(message);

// A hub call that breaks the game's rules (e.g. Ready for a round that does not exist).
public class GameRuleException(string message) : Exception(message);

// ADR-0004: this pod is draining before it stops and takes no new games -> 503.
// LobbyService can retry; the Service no longer sends it here once the pod is not ready.
public class GameServiceDrainingException()
    : Exception("This GameService pod is shutting down and takes no new games. Try again.");

// MusicService unreachable or returned an error -> 502.
public class MusicServiceException(string message, Exception? inner = null) : Exception(message, inner);
