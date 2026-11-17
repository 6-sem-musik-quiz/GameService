using GameService.Service.Interfaces;
using GameService.Service.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.Service.Extensions;

public static class ServiceLayerExtensions
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services)
    {
        services.AddScoped<IExampleService, ExampleService>();

        return services;
    }
}
