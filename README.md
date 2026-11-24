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

**State is in memory** (`IGameStore`, no database), so **only one GameService instance is supported**.
Finished games are removed 30 minutes after they end.

## Playing a game (SignalR, `/hubs/game`)

The hub needs a JWT; browsers send it as `?access_token=`. Through the edge it is
`https://quiz.munkcloud.dk/api/gameservice/hubs/game` (the edge forwards it as `/api/hubs/game`, which is mapped too).

| Direction | Message | |
|---|---|---|
| client → server | `JoinGame(gameId)` → `GameStateDto` | Only players in the game. Joins group `game:{id}`. Call again after a reconnect: the state has the files to have ready (`prefetch`) and what is left of a running round (`currentRound`). |
| client → server | `Ready(gameId, round)` | The file for the round is downloaded. May be sent before the round is next. |
| server → group | `Prefetch([{ round, audioUrl }])` | Rounds 1-5 when the game starts, then round k+5 when round k starts. |
| server → group | `RoundStarted({ round, totalRounds, startsInMs, startOffsetSeconds, answerTimeSeconds })` | No song info. Clients play after `startsInMs` from `startOffsetSeconds`. |

- The game starts when every player has called `JoinGame`, or `Game:JoinTimeoutSeconds` (15) after it was created.
- A round starts when every **connected** player has sent `Ready`, or after `Game:ReadyTimeoutSeconds` (10).
- `startOffsetSeconds` is random in `[0, max(0, duration - answerTime)]`. The server keeps
  `endsAt = now + startsInMs + answerTime` to reject late answers (SCRUM-103).
- Ending a round and starting the next is SCRUM-103: it calls `IGameCoordinator.PrepareRoundAsync(gameId, round + 1)`.

| Setting | Default | |
|---|---|---|
| `Game:JoinTimeoutSeconds` | 15 | |
| `Game:ReadyTimeoutSeconds` | 10 | |
| `Game:StartDelayMs` | 3000 | `RoundStarted.startsInMs` |
| `Game:PrefetchWindow` | 5 | |

## Tests

```bash
dotnet test
```

## Service ports

| Service | API | Postgres |
|---|---|---|
| UserService | 5001 | 5441 |
| MusicService | 5002 | 5442 |
| StatisticService | 5003 | 5443 |
| LobbyService | 5004 | – |
| GameService | 5005 | – |
