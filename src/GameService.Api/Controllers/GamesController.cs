using GameService.Domain.Dtos;
using GameService.Service.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace GameService.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/games")]
public class GamesController(IGameEngine gameEngine) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(CreateGameResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] CreateGameRequest request, CancellationToken cancellationToken)
    {
        var header = Request.Headers[HeaderNames.Authorization].ToString();
        const string prefix = "Bearer ";
        var token = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;

        var response = await gameEngine.CreateGameAsync(request, token, cancellationToken);
        return Created($"/api/games/{response.GameId}", response);
    }
}
