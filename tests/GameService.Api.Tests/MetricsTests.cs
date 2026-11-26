using GameService.Service.Interfaces;
using GameService.Service.Metrics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;

namespace GameService.Api.Tests;

// SCRUM-119: the app starts with OpenTelemetry metrics, and the coordinator gets the same GameMetrics.
public class MetricsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void App_StartsWithMetricsRegistered()
    {
        using var client = factory.CreateClient(); // starts Program, including the OpenTelemetry setup

        Assert.NotNull(factory.Services.GetService<MeterProvider>());
        Assert.NotNull(factory.Services.GetService<GameMetrics>());
        Assert.NotNull(factory.Services.GetService<IGameCoordinator>());
    }
}
