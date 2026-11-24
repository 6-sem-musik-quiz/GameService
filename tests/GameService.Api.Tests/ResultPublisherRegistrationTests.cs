using GameService.Service.Interfaces;
using GameService.Service.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
    public void ResultPublisher_IsSingleton_AcrossMultipleScopes()
    {
        using var scope1 = factory.Services.CreateScope();
        using var scope2 = factory.Services.CreateScope();

        var publisher1 = scope1.ServiceProvider.GetRequiredService<IResultPublisher>();
        var publisher2 = scope2.ServiceProvider.GetRequiredService<IResultPublisher>();

        Assert.Same(publisher1, publisher2);
    }

    [Fact]
    public void RabbitMqOptions_AreBoundFromConfiguration()
    {
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        Assert.NotNull(options);
        Assert.Equal("localhost", options.Host);
        Assert.Equal(5672, options.Port);
        Assert.Equal("guest", options.Username);
        Assert.Equal("guest", options.Password);
        Assert.Equal("game-events", options.ExchangeName);
        Assert.Equal("game.finished.v1", options.RoutingKey);
    }

    [Fact]
    public void RabbitMqOptions_OverridesCanBeConfiguredViaEnvironmentOrCustomFactory()
    {
        // Arrange - custom factory with overridden config like cluster secrets
        var customFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RabbitMQ:Host", "rabbitmq.quiz.svc.cluster.local" },
                    { "RabbitMQ:Port", "5673" },
                    { "RabbitMQ:Username", "quiz-cluster-user" },
                    { "RabbitMQ:Password", "secret-rabbit-pwd" },
                    { "RabbitMQ:VirtualHost", "/prod" },
                    { "RabbitMQ:ExchangeName", "cluster-events" },
                    { "RabbitMQ:RoutingKey", "game.finished.cluster" }
                });
            });
        });

        // Act
        using var scope = customFactory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<RabbitMqOptions>>().Value;

        // Assert
        Assert.Equal("rabbitmq.quiz.svc.cluster.local", options.Host);
        Assert.Equal(5673, options.Port);
        Assert.Equal("quiz-cluster-user", options.Username);
        Assert.Equal("secret-rabbit-pwd", options.Password);
        Assert.Equal("/prod", options.VirtualHost);
        Assert.Equal("cluster-events", options.ExchangeName);
        Assert.Equal("game.finished.cluster", options.RoutingKey);
    }

    [Fact]
    public void ResultPublisher_CanBeResolvedFromCustomFactoryWithClusterSettings()
    {
        var customFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "RabbitMQ:Host", "rabbitmq.quiz.svc.cluster.local" }
                });
            });
        });

        using var scope = customFactory.Services.CreateScope();
        var publisher = scope.ServiceProvider.GetService<IResultPublisher>();

        Assert.NotNull(publisher);
    }
}
