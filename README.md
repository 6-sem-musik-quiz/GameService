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
