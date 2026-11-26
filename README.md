# GameService

Part of the **6-sem-musik-quiz** system. ASP.NET Core (.NET 10) Web API using MVC controllers and a layered architecture.

## Architecture

```
Api  ──►  Service  ──►  Repository
  └─────────┴──────────────┴──►  Domain
```

| Project | Responsibility |
|---|---|
| `src/GameService.Api` | Controllers, middleware, `Program.cs`. Thin: validate input, call a service, return HTTP result. |
| `src/GameService.Service` | Business logic. Talks to repositories through interfaces, returns DTOs. |
| `src/GameService.Repository` | Data access. No SQL database (see the C4 design); empty for now. |
| `src/GameService.Domain` | Entities and DTOs. No dependencies. |
| `tests/GameService.Service.Tests` | Unit tests for services (xUnit + NSubstitute). |
| `tests/GameService.Api.Tests` | Integration tests using `WebApplicationFactory`. |


## Running locally

**Everything in Docker:**

```bash
docker compose up --build
```

**API from your IDE:**

```bash
dotnet run --project src/GameService.Api
```

- API: http://localhost:5005
- Swagger: http://localhost:5005/swagger
- Health: http://localhost:5005/health

## Creating a game

`POST /api/games` (called by LobbyService, requires a JWT) with
`{ lobbyId, categoryId, categoryName, rounds, answerTimeSeconds, players: [{ userId, userName }] }`
returns `201 { gameId }`. `400` on invalid input (2-8 players, unique userIds, rounds in {5, 10, 15, 20}
and answerTimeSeconds in {15, 30, 45, 60}, the same choices as the lobby), `401` without a valid token, `502` if MusicService fails or returns too few songs.

GameService fetches `rounds + 1` songs from MusicService (`GET /api/playlists/{categoryId}/songs/random?count=rounds+1`)
with the forwarded token. The first song is the shared start card in every player's timeline; the rest are one per round.

| Setting | Env var | Notes |
|---|---|---|
| `Services:MusicService` | `Services__MusicService` | MusicService base URL |
| `Jwt:SigningKey` | `Jwt__SigningKey` | Same key as UserService; never commit it |
| `Jwt:Issuer` / `Jwt:Audience` | `Jwt__Issuer` / `Jwt__Audience` | `slopster-userservice` / `slopster` |

**The running games are in Redis** ([ADR-0004](https://github.com/6-sem-musik-quiz/Platform/blob/main/docs/decisions/0004-gameservice-scaling.md), SCRUM-129),
so GameService scales like the other services (X-axis, HPA): any pod can serve any call for any game, and a pod
can die or be replaced without ending its games. Every hub call and timer does: lock the game, load its
`GameSession`, change it and send the messages, save it, unlock.

| In Redis (`slopster:` prefix) | |
|---|---|
| `game:{id}` | The `GameSession` (game, joined players, connections, ready, answers, next deadline) as JSON. Kept 30 min after the game finished, else 3 h |
| `game:{id}:lock` | The game's lock (`SET NX PX 30s`), so one pod at a time changes it |
| `deadlines` | The games' timers (join, ready, round and pause timeouts) as a sorted set by due time. Every pod polls it, every 100 ms while there are deadlines and every second when there are none; the pod whose `ZREM` succeeds handles the deadline |
| `games:playing` | Playing games, for `slopster_games_active` |
| SignalR channels `slopster-game*` | Redis backplane: group messages reach players on every pod |

| Setting | Env var | |
|---|---|---|
| `ConnectionStrings:Redis` | `ConnectionStrings__Redis` | e.g. `redis-master:6379,password=...`. Empty (local runs, tests): the games are kept in memory, one instance only |

`/health` is liveness; `/health/ready` also checks that Redis answers.
Code: `GameService.Repository/State` (store, lock, deadlines; Redis and in-memory), `GameCoordinator` (the game flow).

## Playing a game (SignalR, `/hubs/game`)

The hub needs a JWT; browsers send it as `?access_token=`. Through the edge it is
`https://quiz.munkcloud.dk/api/gameservice/hubs/game` (the edge forwards it as `/api/hubs/game`, which is mapped too).

| Direction | Message | |
|---|---|---|
| client → server | `JoinGame(gameId)` → `GameStateDto` | Only players in the game. Joins group `game:{id}`. Call again after a reconnect: the state has the files to have ready (`prefetch`), what is left of a running round (`currentRound`), every player's `timeline`, `history: [{ round, correct, answeredAfterMs? }]` and `answeredOrder?` for the running round, and `pastSongs: [{ round, song }]` (SCRUM-150). |
| client → server | `Ready(gameId, round)` | The file for the round is downloaded. May be sent before the round is next. |
| server → group | `Prefetch([{ round, audioUrl }])` | Rounds 1-5 when the game starts, then round k+5 when round k starts. |
| server → group | `RoundStarted({ round, totalRounds, startsInMs, startOffsetSeconds, answerTimeSeconds })` | No song info. Clients play after `startsInMs` from `startOffsetSeconds`. |
| client → server | `SubmitAnswer(gameId, round, slotIndex)` | `slotIndex` 0..n in the caller's timeline (0 = before the first card, n = after the last). Rejected if the round is not the current one, the time is up, the player already answered or the slot is out of range. |
| server → group | `PlayerAnswered({ userId, answeredAfterMs })` | Someone answered; the answer stays hidden. `answeredAfterMs` is measured by the server from playback start, clamped to the answer time. |
| server → group | `RoundEnded({ round, song: { title, artists, year }, results: [{ userId, userName, slotIndex?, correct, score, timeline, answeredAfterMs?, range? }] })` | When every connected player has answered, or at `endsAt + 1 s`. `range` = `[from, to]`, the years the answer was put between (`null` for an open end). |
| server → group | `GameEnded({ gameId, standings: [{ userId, userName, score, correct, answered, placement, timeline }], awards: [{ kind, winners, contenders: [{ userId, value, qualified }] }], songs: [{ round, song }] })` | After the last round's reveal. Score includes the bonus awards; sorted by score; equal scores share a placement (1, 1, 3). |

- The game starts when every player has called `JoinGame`, or `Game:JoinTimeoutSeconds` (15) after it was created.
- A round starts when every **connected** player has sent `Ready`, or after `Game:ReadyTimeoutSeconds` (10).
- `startOffsetSeconds` is random in `[0, max(0, duration - answerTime)]`. The server keeps
  `endsAt = now + startsInMs + answerTime` to reject late answers (SCRUM-103).
- **Right answer:** (slot = 0 or year of card[slot-1] ≤ song year) and (slot = n or song year ≤ year of card[slot]).
  Right → the card goes into the timeline, +1 score, +1 correct. Wrong → nothing. `answered` +1 either way;
  no answer does not count as answered. Answers are judged when the round ends, so nobody's timeline
  reveals the song early.
- The next round is prepared `Game:PauseBetweenRoundsSeconds` (8) after `RoundEnded`.
- **Game end:** after the last round the game is `Finished`, `GameEnded` goes to the players, and
  `GameFinished` is published once to RabbitMQ (exchange `game-events`, routing key `game.finished.v1`)
  for StatisticService and LobbyService. The message keeps the SCRUM-83 field names (`catId`, `startTime`, `endTime`,
  `correctAnswers`, `answeredRounds`, `placement`, ...) and, since v1.1 (SCRUM-172), has `lobbyId`, so
  LobbyService can reopen the lobby for "Spil igen" (contract `gameservice.game-finished@1.1` in Platform).
  If publishing fails it is logged; players still get `GameEnded`.
- **Bonus awards (SCRUM-151, gamehub v1.2):** at the end, +1 point each, added to the normal score (also in
  `GameFinished`): `fastest` = lowest average answer time on correct answers (needs ⌈rounds/3⌉ correct),
  `streak` = longest run of correct answers in a row (at least 2). Ties share; times within 100 ms are equal.

| Setting | Default | |
|---|---|---|
| `Game:JoinTimeoutSeconds` | 15 | |
| `Game:ReadyTimeoutSeconds` | 10 | |
| `Game:StartDelayMs` | 3000 | `RoundStarted.startsInMs` |
| `Game:PrefetchWindow` | 5 | |
| `Game:AnswerGraceSeconds` | 1 | Answers this long after `endsAt` still count |
| `Game:PauseBetweenRoundsSeconds` | 8 | |

## Traces and logs (SCRUM-173)

Traces, metrics and logs go over OTLP (`UseOtlpExporter`, endpoint `OTEL_EXPORTER_OTLP_ENDPOINT`, name
`OTEL_SERVICE_NAME`, both from the gitops chart) to Alloy, and on to Tempo, Prometheus and Loki.

- **Spans:** HTTP requests (not `/health`), every hub call (`Microsoft.AspNetCore.SignalR.Server`: `JoinGame`,
  `Ready`, `SubmitAnswer`), calls to MusicService, the `GameFinished` publish (`RabbitMQ.Client.Publisher`, which
  puts the trace context in the message headers) and the game's timers (`Slopster.Game`: `game.join`,
  `game.ready`, `game.round`, `game.pause`).
- **One game is one trace:** the trace context of the request that created the game (LobbyService's
  `POST /api/games`) is kept in the `GameSession`, and every timer of the game runs in that trace, on whichever pod
  handles it. The last one publishes `GameFinished`, so StatisticService and LobbyService continue the same trace.
- **Logs** (`ILogger`) carry the trace id, so Grafana links a log line to its trace.

## Metrics (SCRUM-119)

OpenTelemetry meter `Slopster.Game`, exported over OTLP to `OTEL_EXPORTER_OTLP_ENDPOINT` (default
`http://localhost:4317`; the gitops chart points it at Grafana Alloy) and on to Prometheus.

| Metric | Type | |
|---|---|---|
| `slopster_games_active` | gauge | Games with `Status = Playing`, in all pods (every pod reports the same number: use `max`) |
| `slopster_games_finished_total` | counter | Finished games |
| `slopster_rounds_played_total` | counter | Ended rounds |
| `slopster_answers_total{result="correct"\|"wrong"}` | counter | Answers judged when a round ends (no answer is not counted) |
| `http_server_request_duration_seconds` | histogram | ASP.NET Core response times, by `http_route` |

Example PromQL:

```promql
max(slopster_games_active)                                   # games being played now
sum(increase(slopster_games_finished_total[1h]))             # games finished in the last hour
sum(rate(slopster_rounds_played_total[5m])) * 60             # rounds per minute
sum(rate(slopster_answers_total{result="correct"}[1h]))
  / sum(rate(slopster_answers_total[1h]))                    # share of correct answers
histogram_quantile(0.95, sum by (le, http_route)
  (rate(http_server_request_duration_seconds_bucket[5m])))   # p95 response time per endpoint
```

## Tests

```bash
dotnet test
```

`RedisGameStateTests` start a real Redis in Docker (Testcontainers), so Docker must be running. They include two
GameService instances playing one game over a shared Redis, and a game that one instance starts and another finishes.

## Service ports

| Service | API | Postgres |
|---|---|---|
| UserService | 5001 | 5441 |
| MusicService | 5002 | 5442 |
| StatisticService | 5003 | 5443 |
| LobbyService | 5004 | – |
| GameService | 5005 | – |
