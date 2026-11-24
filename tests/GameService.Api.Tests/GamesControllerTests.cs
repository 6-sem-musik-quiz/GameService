using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using GameService.Domain.Dtos;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Service.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameService.Api.Tests;

public class GamesControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string SigningKey = "test-signing-key-at-least-32-bytes-long!!";
    private readonly IMusicServiceClient _music = Substitute.For<IMusicServiceClient>();
    private readonly WebApplicationFactory<Program> _factory;

    public GamesControllerTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = SigningKey }));
            builder.ConfigureServices(services => services.AddScoped(_ => _music));
        });
    }

    private static string Token(string key = SigningKey, string audience = "slopster")
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "slopster-userservice",
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private HttpClient Client(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static CreateGameRequest Body(int players = 2) => new()
    {
        LobbyId = Guid.NewGuid(),
        CategoryId = 3,
        CategoryName = "Rock",
        Rounds = 2,
        AnswerTimeSeconds = 15,
        Players = Enumerable.Range(1, players).Select(i => new PlayerDto { UserId = i, UserName = $"p{i}" }).ToList()
    };

    private static List<Song> Songs(int n) =>
        Enumerable.Range(1, n).Select(i => new Song { Id = $"s{i}", Year = 1970 + i }).ToList();

    [Fact]
    public async Task Post_ValidRequest_Returns201WithGameId_AndForwardsToken()
    {
        var token = Token();
        _music.GetRandomSongsAsync(3, 3, token, Arg.Any<CancellationToken>()).Returns(Songs(3));

        var response = await Client(token).PostAsJsonAsync("/api/games", Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateGameResponse>();
        Assert.NotEqual(Guid.Empty, created!.GameId);
    }

    [Fact]
    public async Task Post_TooManyPlayers_Returns400()
    {
        var response = await Client(Token()).PostAsJsonAsync("/api/games", Body(9));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_MissingOrInvalidToken_Returns401(bool wrongKey)
    {
        var token = wrongKey ? Token("another-signing-key-that-is-32-bytes-long!!") : null;

        var response = await Client(token).PostAsJsonAsync("/api/games", Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_WrongAudience_Returns401()
    {
        var response = await Client(Token(audience: "other")).PostAsJsonAsync("/api/games", Body());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_MusicServiceFails_Returns502()
    {
        _music.GetRandomSongsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new MusicServiceException("down"));

        var response = await Client(Token()).PostAsJsonAsync("/api/games", Body());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Post_TooFewSongs_Returns502()
    {
        _music.GetRandomSongsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Songs(1));

        var response = await Client(Token()).PostAsJsonAsync("/api/games", Body());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
}
