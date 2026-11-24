using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GameService.Domain.Dtos;
using GameService.Domain.Models;
using GameService.Service.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace GameService.Api.Tests;

// SCRUM-102 end to end against the TestServer: create a game over REST, then play over SignalR.
public class GameHubTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string SigningKey = "test-signing-key-at-least-32-bytes-long!!";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IMusicServiceClient _music = Substitute.For<IMusicServiceClient>();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<HubConnection> _connections = [];

    public GameHubTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = SigningKey }));
            builder.ConfigureServices(services => services.AddScoped(_ => _music));
        });
        _music.GetRandomSongsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Enumerable.Range(0, call.ArgAt<int>(1)).Select(i => new Song
            {
                Id = i, Title = $"Song {i}", Artists = ["Artist"], Year = 1970 + i, DurationSeconds = 180,
                AudioUrl = $"https://media.example/songs/{i}.mp3"
            }).ToList());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
    }

    private static string Token(int userId, string userName) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "slopster-userservice",
            Audience = "slopster",
            Subject = new ClaimsIdentity([new Claim("sub", userId.ToString()), new Claim("unique_name", userName)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256)
        });

    private async Task<Guid> CreateGame(params int[] userIds)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(userIds[0], "host"));
        var response = await client.PostAsJsonAsync("/api/games", new CreateGameRequest
        {
            LobbyId = Guid.NewGuid(),
            CategoryId = 3,
            CategoryName = "Rock",
            Rounds = 5,
            AnswerTimeSeconds = 15,
            Players = userIds.Select(id => new PlayerDto { UserId = id, UserName = $"p{id}" }).ToList()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreateGameResponse>())!.GameId;
    }

    // WebSockets like a browser: the token goes in ?access_token=.
    private HubConnection ConnectionFor(string? token, string path = "/hubs/game")
    {
        var server = _factory.Server;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult(token);
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri);
                    if (token is not null)
                        uri.Query = $"{uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}";
                    return await server.CreateWebSocketClient().ConnectAsync(uri.Uri, cancellationToken);
                };
            })
            .Build();
        _connections.Add(connection);
        return connection;
    }

    private static TaskCompletionSource<T> Listen<T>(HubConnection connection, string method)
    {
        var received = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<T>(method, value => received.TrySetResult(value));
        return received;
    }

    [Fact]
    public async Task TwoPlayers_Join_GetTheSamePrefetch_SendReady_AndBothGetRoundStarted()
    {
        var gameId = await CreateGame(11, 12);
        var alice = ConnectionFor(Token(11, "alice"));
        var bob = ConnectionFor(Token(12, "bob"), path: "/api/hubs/game"); // the path the edge forwards to
        var alicePrefetch = Listen<List<PrefetchDto>>(alice, "Prefetch");
        var bobPrefetch = Listen<List<PrefetchDto>>(bob, "Prefetch");
        var aliceRound = Listen<RoundStartedDto>(alice, "RoundStarted");
        var bobRound = Listen<RoundStartedDto>(bob, "RoundStarted");
        await alice.StartAsync();
        await bob.StartAsync();

        var aliceState = await alice.InvokeAsync<GameStateDto>("JoinGame", gameId);
        Assert.Equal(0, aliceState.Round);
        Assert.Equal("Song 0", Assert.Single(aliceState.MyTimeline).Title);
        await bob.InvokeAsync<GameStateDto>("JoinGame", gameId); // everyone joined -> the game starts

        var files = await alicePrefetch.Task.WaitAsync(Timeout);
        Assert.Equal(files, await bobPrefetch.Task.WaitAsync(Timeout));
        Assert.Equal([1, 2, 3, 4, 5], files.Select(f => f.Round));
        Assert.Equal("https://media.example/songs/1.mp3", files[0].AudioUrl);

        await alice.InvokeAsync("Ready", gameId, 1);
        Assert.False(aliceRound.Task.IsCompleted); // still waiting for Bob
        await bob.InvokeAsync("Ready", gameId, 1);

        var started = await aliceRound.Task.WaitAsync(Timeout);
        Assert.Equal(started, await bobRound.Task.WaitAsync(Timeout));
        Assert.Equal(1, started.Round);
        Assert.Equal(5, started.TotalRounds);
        Assert.Equal(3000, started.StartsInMs);
        Assert.Equal(15, started.AnswerTimeSeconds);
        Assert.InRange(started.StartOffsetSeconds, 0, 180 - 15);
    }

    [Fact]
    public async Task RoundStarted_OnTheWire_HasNoSongInformation()
    {
        var gameId = await CreateGame(21, 22);
        var alice = ConnectionFor(Token(21, "alice"));
        var bob = ConnectionFor(Token(22, "bob"));
        var raw = Listen<JsonElement>(alice, "RoundStarted");
        await alice.StartAsync();
        await bob.StartAsync();
        await alice.InvokeAsync<GameStateDto>("JoinGame", gameId);
        await bob.InvokeAsync<GameStateDto>("JoinGame", gameId);

        await alice.InvokeAsync("Ready", gameId, 1);
        await bob.InvokeAsync("Ready", gameId, 1);

        var json = await raw.Task.WaitAsync(Timeout);
        Assert.Equal(["round", "totalRounds", "startsInMs", "startOffsetSeconds", "answerTimeSeconds"],
            json.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task JoinGame_ByAUserWhoIsNotAPlayer_FailsWithAReadableMessage()
    {
        var gameId = await CreateGame(31, 32);
        var stranger = ConnectionFor(Token(99, "stranger"));
        await stranger.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => stranger.InvokeAsync<GameStateDto>("JoinGame", gameId));

        Assert.Contains("not a player", error.Message);
    }

    [Fact]
    public async Task Connecting_WithoutToken_IsRejected()
    {
        var anonymous = ConnectionFor(null);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());

        Assert.Contains("401", error.Message);
    }
}
