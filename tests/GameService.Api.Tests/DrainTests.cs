using System.Net;
using GameService.Service.Sharding;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.Api.Tests;

// ADR-0004: preStop calls /internal/drain; from then on /health/ready fails (the Service stops
// sending new games here) while /health (liveness) stays OK, so Kubernetes does not kill the drain.
public class DrainTests
{
    [Fact]
    public async Task Drain_MakesThePodNotReady_ButStillAlive()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        var drain = await client.GetAsync("/internal/drain"); // no games: returns at once
        Assert.Equal(HttpStatusCode.OK, drain.StatusCode);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.True(factory.Services.GetRequiredService<GameDrain>().IsDraining);
    }
}
