using System.Diagnostics;

namespace GameService.Service.Tracing;

// SCRUM-173: GameService's own spans (ActivitySource "Slopster.Game"), next to the ones ASP.NET Core,
// SignalR (hub calls), HttpClient and RabbitMQ.Client make by themselves.
// A game is one trace: the trace context of the request that created it (LobbyService's POST /api/games)
// is kept in the GameSession, and every timer of the game (deadline) runs as a span in that trace. The
// last one publishes GameFinished, and RabbitMQ.Client carries the context in the message headers to
// StatisticService and LobbyService, so the whole game reads as one trace in Tempo.
public static class GameTracing
{
    public const string SourceName = "Slopster.Game";

    public static readonly ActivitySource Source = new(SourceName);

    // The current trace context as a W3C traceparent, to keep in the session; null when not tracing.
    public static string? CurrentTraceParent() => Activity.Current?.Id;

    // Starts a span in the game's trace (or a new trace when the game has none). Null when nobody listens.
    public static Activity? StartInGame(string name, Guid gameId, string? traceParent)
    {
        var parent = traceParent is not null && ActivityContext.TryParse(traceParent, null, out var context)
            ? context
            : default;
        var activity = Source.StartActivity(name, ActivityKind.Internal, parent);
        activity?.SetTag("game.id", gameId.ToString());
        return activity;
    }
}
