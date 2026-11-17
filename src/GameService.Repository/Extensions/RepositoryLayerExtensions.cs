using GameService.Repository.Data;
using GameService.Repository.Interfaces;
using GameService.Repository.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.Repository.Extensions;

public static class RepositoryLayerExtensions
{
    public static IServiceCollection AddRepositoryLayer(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IExampleRepository, ExampleRepository>();

        return services;
    }
}
