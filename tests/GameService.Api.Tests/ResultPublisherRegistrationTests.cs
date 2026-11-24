using GameService.Service.Interfaces;
using GameService.Service.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameService.Api.Tests;

public class ResultPublisherRegistrationTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void ResultPublisher_IsRegisteredInDependencyInjection()
    {
        using var scope = factory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetService<IResultPublisher>();

        Assert.NotNull(publisher);
    }

    [Fact]
    public void RabbitMqOptions_AreBoundFromConfiguration()
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        Assert.NotNull(options);
        Assert.Equal("game-events", options.ExchangeName);
        Assert.Equal("game.finished.v1", options.RoutingKey);
    }
}
