namespace GameService.Service.Exceptions;

// Invalid input from the caller -> 400.
public class GameValidationException(string message) : Exception(message);

// MusicService returned too few songs -> 502.
public class InsufficientSongsException(int expected, int actual)
    : Exception($"MusicService returned {actual} songs, expected {expected}.");

// MusicService unreachable or returned an error -> 502.
public class MusicServiceException(string message, Exception? inner = null) : Exception(message, inner);
