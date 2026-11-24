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
