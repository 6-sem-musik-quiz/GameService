using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Service.Interfaces;

namespace GameService.Service.Clients;

public class MusicServiceClient(HttpClient httpClient) : IMusicServiceClient
{
    public async Task<IReadOnlyList<Song>> GetRandomSongsAsync(int categoryId, int count, string? bearerToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/playlists/{categoryId}/songs/random?count={count}");
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new MusicServiceException($"MusicService returned {(int)response.StatusCode}.");

            var songs = await response.Content.ReadFromJsonAsync<List<Song>>(cancellationToken);
            return songs ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new MusicServiceException("MusicService request failed.", ex);
        }
    }
}
