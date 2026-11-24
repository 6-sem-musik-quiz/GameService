using System.Net;
using System.Text;
using GameService.Service.Clients;
using GameService.Service.Exceptions;

namespace GameService.Service.Tests.Clients;

// Runs the real HTTP/JSON path against a fake handler, so a mismatch with MusicService's SongDto is caught.
public class MusicServiceClientTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (MusicServiceClient, FakeHandler) Create(HttpStatusCode status, string body)
    {
        var handler = new FakeHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://music/") };
        return (new MusicServiceClient(http), handler);
    }

    // Exactly what MusicService serializes for SongDto (camelCase web defaults).
    private const string MusicServiceJson = """
        [
          { "id": 12, "title": "Take On Me", "artists": ["a-ha"], "year": 1985, "durationSeconds": 225,
            "audioUrl": "https://cdn.example/songs/abc.mp3" },
          { "id": 7, "title": "Under Pressure", "artists": ["Queen", "David Bowie"], "year": 1981, "durationSeconds": 248,
            "audioUrl": "https://cdn.example/songs/def.mp3" }
        ]
        """;

    [Fact]
    public async Task GetRandomSongs_ReadsMusicServiceSongDto_AndForwardsToken()
    {
        var (client, handler) = Create(HttpStatusCode.OK, MusicServiceJson);

        var songs = await client.GetRandomSongsAsync(3, 2, "tok");

        Assert.Equal("http://music/api/playlists/3/songs/random?count=2", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer tok", handler.Request.Headers.Authorization!.ToString());

        Assert.Equal(2, songs.Count);
        var song = songs[1];
        Assert.Equal(7, song.Id);
        Assert.Equal("Under Pressure", song.Title);
        Assert.Equal(["Queen", "David Bowie"], song.Artists);
        Assert.Equal(1981, song.Year);
        Assert.Equal(248, song.DurationSeconds);
        Assert.Equal("https://cdn.example/songs/def.mp3", song.AudioUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]   // playlist has fewer songs than requested
    [InlineData(HttpStatusCode.NotFound)]     // unknown playlist
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetRandomSongs_ErrorStatus_ThrowsMusicServiceException(HttpStatusCode status)
    {
        var (client, _) = Create(status, """{ "title": "error" }""");

        await Assert.ThrowsAsync<MusicServiceException>(() => client.GetRandomSongsAsync(3, 6, "tok"));
    }

    [Fact]
    public async Task GetRandomSongs_InvalidJson_ThrowsMusicServiceException()
    {
        var (client, _) = Create(HttpStatusCode.OK, "not json");

        await Assert.ThrowsAsync<MusicServiceException>(() => client.GetRandomSongsAsync(3, 6, "tok"));
    }
}
