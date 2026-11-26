using System.Text;
using GameService.Api.Hubs;
using GameService.Api.Middleware;
using GameService.Service.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using GameService.Repository.Extensions;
using GameService.Service.Extensions;
using GameService.Service.Metrics;
using GameService.Repository.State;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;
using GameService.Service.Tracing;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
// ADR-0004: the running games are in Redis (ConnectionStrings__Redis, the same Redis as LobbyService),
// so GameService can run several pods. Without it (local runs, tests) they are kept in memory.
var redis = builder.Configuration.GetConnectionString("Redis");

// /health is liveness (the process is up). /health/ready also checks Redis, so a pod that cannot reach
// the games gets no traffic.
var health = builder.Services.AddHealthChecks();
if (!string.IsNullOrWhiteSpace(redis))
    health.AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

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

// With Redis, the SignalR Redis backplane (as LobbyService, SCRUM-128): every pod publishes its group
// messages on Redis, so RoundStarted reaches a player whose WebSocket is on another pod.
var signalR = builder.Services.AddSignalR();
if (!string.IsNullOrWhiteSpace(redis))
    signalR.AddStackExchangeRedis(redis, o =>
    {
        o.Configuration.ChannelPrefix = RedisChannel.Literal("slopster-game");
        o.Configuration.AbortOnConnectFail = false;
    });
builder.Services.AddSingleton<IGameNotifier, SignalRGameNotifier>();

builder.Services.AddRepositoryLayer(redis);
builder.Services.AddServiceLayer(builder.Configuration);

// SCRUM-119/173: traces, metrics and logs over OTLP. The collector address comes from
// OTEL_EXPORTER_OTLP_ENDPOINT (default http://localhost:4317) and the service name from OTEL_SERVICE_NAME
// (both set by the gitops chart). Logs carry the trace id, so Grafana links a log line to its trace.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddSource("Microsoft.AspNetCore.SignalR.Server") // one span per hub call: JoinGame, Ready, SubmitAnswer
        .AddSource("System.Net.Http")                     // calls to MusicService
        .AddSource("RabbitMQ.Client.Publisher")           // GameFinished, with the trace context in its headers
        .AddSource(GameTracing.SourceName))               // the game's timers, in the game's trace
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation() // http.server.request.duration
        .AddMeter("System.Net.Http", "System.Runtime", "Microsoft.AspNetCore.SignalR.Server")
        .AddMeter(GameMetrics.MeterName))
    .WithLogging(logging => { }, options =>
    {
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
    })
    .UseOtlpExporter();

var app = builder.Build();

// Create the coordinator now, so it handles deadlines from the first poll (with Redis, this pod may
// pick up deadlines of games it has never seen).
app.Services.GetRequiredService<IGameCoordinator>();
app.Logger.LogInformation("GameService keeps its games {Where}",
    string.IsNullOrWhiteSpace(redis) ? "in memory (one instance only)" : "in Redis (any number of pods)");

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

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;

// Ready only when Redis answers.
public sealed class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy("Redis does not answer", e);
        }
    }
}
