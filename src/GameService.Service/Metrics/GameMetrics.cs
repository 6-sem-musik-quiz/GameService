using System.Diagnostics.Metrics;
using GameService.Repository.State;

namespace GameService.Service.Metrics;

// SCRUM-119: OpenTelemetry instruments for GameService (meter "Slopster.Game"), exported over OTLP.
//   slopster_games_active                       observable gauge, games with Status = Playing in all pods
//                                               (from the shared store, so every pod reports the same
//                                               number: use max(), not sum(), in Grafana)
//   slopster_games_finished_total               counter, one per finished game
//   slopster_rounds_played_total                counter, one per ended round
//   slopster_answers_total{result=correct|wrong} counter, one per answer judged at the end of a round
// HTTP response times come from ASP.NET Core's http.server.request.duration (set up in Program).
public sealed class GameMetrics : IDisposable
{
    public const string MeterName = "Slopster.Game";
    public const string GamesActiveName = "slopster_games_active";
    public const string GamesFinishedName = "slopster_games_finished_total";
    public const string RoundsPlayedName = "slopster_rounds_played_total";
    public const string AnswersName = "slopster_answers_total";

    private readonly Counter<long> _gamesFinished;
    private readonly Counter<long> _roundsPlayed;
    private readonly Counter<long> _answers;

    public GameMetrics(IGameSessionStore store)
    {
        Meter = new Meter(MeterName);
        Meter.CreateObservableGauge(GamesActiveName, store.CountPlaying,
            description: "Games being played right now.");
        _gamesFinished = Meter.CreateCounter<long>(GamesFinishedName, description: "Finished games.");
        _roundsPlayed = Meter.CreateCounter<long>(RoundsPlayedName, description: "Ended rounds.");
        _answers = Meter.CreateCounter<long>(AnswersName, description: "Answers, by result (correct or wrong).");
    }

    public Meter Meter { get; }

    public void GameFinished() => _gamesFinished.Add(1);

    public void RoundPlayed() => _roundsPlayed.Add(1);

    public void Answer(bool correct) =>
        _answers.Add(1, new KeyValuePair<string, object?>("result", correct ? "correct" : "wrong"));

    public void Dispose() => Meter.Dispose();
}
