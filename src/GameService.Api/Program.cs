using System.Text;
using GameService.Api.Hubs;
using GameService.Api.Middleware;
using GameService.Service.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using GameService.Repository.Extensions;
using GameService.Service.Extensions;
using GameService.Service.Metrics;
using GameService.Service.Sharding;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// /health is liveness (the process is up). /health/ready also fails while the pod drains (ADR-0004),
// so the Service sends new games to the other pods; liveness must not, or the drain would be killed.
builder.Services.AddHealthChecks()
    .AddCheck<DrainHealthCheck>("drain", tags: ["ready"]);

// Same validation as UserService (SCRUM-88). Options are resolved lazily so the signing key
// can come from env (Jwt__SigningKey) or test configuration.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((options, config) =>
    {
        var key = config["Jwt:SigningKey"];
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config["Jwt:Issuer"] ?? "slopster-userservice",
            ValidateAudience = true,
            ValidAudience = config["Jwt:Audience"] ?? "slopster",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = string.IsNullOrEmpty(key) ? null : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateLifetime = true
        };
        // Browsers cannot set headers on WebSockets, so the hub takes the token from ?access_token=.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken)
                    && (path.StartsWithSegments(GameHub.Path) || path.StartsWithSegments(GameHub.EdgePath)))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSignalR();
builder.Services.AddSingleton<IGameNotifier, SignalRGameNotifier>();

builder.Services.AddRepositoryLayer();
builder.Services.AddServiceLayer(builder.Configuration);

// SCRUM-119: metrics over OTLP. The collector address comes from OTEL_EXPORTER_OTLP_ENDPOINT (default
// http://localhost:4317) and the service name from OTEL_SERVICE_NAME (both set by the gitops chart).
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation() // http.server.request.duration
        .AddMeter(GameMetrics.MeterName)
        .AddOtlpExporter());

var app = builder.Build();

// ADR-0004: fail at startup, not at the first game, if the shard settings are wrong.
var shards = app.Services.GetRequiredService<ShardedGameIds>();
app.Logger.LogInformation("GameService shard {ShardIndex}: new games get ids starting with {Prefix}",
    shards.ShardIndex, shards.ShardIndex.ToString("x2"));

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// The edge forwards /api/gameservice/hubs/game as /api/hubs/game, so the hub is mapped there too.
app.MapHub<GameHub>(GameHub.Path);
app.MapHub<GameHub>(GameHub.EdgePath);
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => !check.Tags.Contains("ready") });
app.MapHealthChecks("/health/ready");

// ADR-0004: Kubernetes' preStop hook. Not under /api, so the edge cannot reach it.
// Returns when no game in this pod is Waiting or Playing any more.
app.MapGet("/internal/drain", async (GameDrain drain, ILogger<GameDrain> logger, CancellationToken cancellationToken) =>
{
    logger.LogInformation("Draining: no new games; waiting for {ActiveGames} active games", drain.ActiveGames);
    await drain.DrainAsync(TimeSpan.FromSeconds(2), cancellationToken);
    logger.LogInformation("Drained: no active games left");
    return Results.Ok(new { drained = true });
});

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;

// ADR-0004: not ready while the pod drains before it stops.
public sealed class DrainHealthCheck(GameDrain drain) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(drain.IsDraining
            ? HealthCheckResult.Unhealthy($"Draining: {drain.ActiveGames} active games left, no new games")
            : HealthCheckResult.Healthy());
}
