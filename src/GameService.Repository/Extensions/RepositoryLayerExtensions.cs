using Microsoft.Extensions.DependencyInjection;

namespace GameService.Repository.Extensions;

public static class RepositoryLayerExtensions
{
    // GameService has no SQL database in the C4 design (only UserDB, StatsDB and MusicDB do).
    // Finished games go to RabbitMQ (ResultPublisher) when that is built.
    public static IServiceCollection AddRepositoryLayer(this IServiceCollection services)
    {
        return services;
    }
}
