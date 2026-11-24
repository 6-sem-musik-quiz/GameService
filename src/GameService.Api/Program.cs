using System.Text;
using GameService.Api.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using GameService.Repository.Extensions;
using GameService.Service.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHealthChecks();

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
    });
builder.Services.AddAuthorization();

builder.Services.AddRepositoryLayer();
builder.Services.AddServiceLayer(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Exposed so integration tests can use WebApplicationFactory<Program>.
public partial class Program;
