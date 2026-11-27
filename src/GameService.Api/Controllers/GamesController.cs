using GameService.Domain.Dtos;
using GameService.Repository.State;
using GameService.Service.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace GameService.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/games")]
public class GamesController(IGameEngine gameEngine, IMaintenanceSwitch maintenance) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(CreateGameResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Create([FromBody] CreateGameRequest request, CancellationToken cancellationToken)
    {
        // SCRUM-179: switched off by an admin -> no new games (running games go on). Same answer as the
        // edge gives for a switched-off service (SCRUM-194), so LobbyService and the frontend can show it.
        if (await maintenance.OffMessageAsync(cancellationToken) is { } message)
        {
            Response.Headers.RetryAfter = "30";
            var problem = new ProblemDetails
            {
                Type = "https://quiz.munkcloud.dk/problems/maintenance",
                Title = "Game is under maintenance",
                Status = StatusCodes.Status503ServiceUnavailable,
                Detail = string.IsNullOrWhiteSpace(message) ? "Game is under maintenance. Try again later." : message,
            };
            problem.Extensions["service"] = "gameservice";
            return new ObjectResult(problem) { StatusCode = StatusCodes.Status503ServiceUnavailable, ContentTypes = { "application/problem+json" } };
        }

        var header = Request.Headers[HeaderNames.Authorization].ToString();
        const string prefix = "Bearer ";
        var token = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;

        var response = await gameEngine.CreateGameAsync(request, token, cancellationToken);
        return Created($"/api/games/{response.GameId}", response);
    }
}
