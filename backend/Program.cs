// File: backend/Program.cs
using AzureOnlinePongGame.Services; // Add using for the service
using Microsoft.AspNetCore.Builder; // Required for WebApplication builder extensions
using Microsoft.Extensions.DependencyInjection; // Required for IServiceCollection extensions
using Microsoft.Extensions.Hosting; // Required for IHostEnvironment extensions
using System;
using Microsoft.Extensions.Configuration; // Required for IConfiguration
using Microsoft.Extensions.Logging; // Required for ILogger
using AzureOnlinePongGame.Models; // Add using for Models
using System.Collections.Generic; // For Dictionary used in HealthCheck
using Microsoft.AspNetCore.Http; // For HttpContext, Request, Response
using Newtonsoft.Json; // For serializing health check response

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

// *** Start Migration Additions ***

// 1. Register GameStateService as a Singleton
builder.Services.AddSingleton<GameStateService>();

// Add memory cache service
builder.Services.AddMemoryCache();

// Register PaddlePositionCache for in-memory paddle position storage
builder.Services.AddSingleton<PaddlePositionCache>();

// 2. Configure Health Checks
builder.Services.AddHealthChecks()
    // Add Redis health check
    .AddRedis(
        builder.Configuration.GetConnectionString("RedisConnection")
            ?? throw new InvalidOperationException("Redis connection string 'RedisConnection' not found for health check."),
        name: "redis-check",
        tags: new string[] { "ready" }); // Tag for readiness probes

// Register SignalR with sticky sessions support for horizontal scaling
builder.Services.AddSignalR(options => {
    // Optimize for less frequent state updates
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
    options.StreamBufferCapacity = 20; // Default is 10
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
})
.AddMessagePackProtocol()
.AddNewtonsoftJsonProtocol(options =>
{
    options.PayloadSerializerSettings.ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();
});

// Configure JSON serialization for the rest of the application to use camelCase
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

// Register GameLoopService as a Hosted Service
builder.Services.AddHostedService<AzureOnlinePongGame.Services.GameLoopService>();

// Add Swagger services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Read CORS origins from configuration
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendCors", policy =>
    {
        if (corsOrigins != null && corsOrigins.Length > 0)
        {
            policy.WithOrigins(corsOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            policy.AllowAnyOrigin()
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        }
    });
});

// *** End Migration Additions ***


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Apply CORS policy globally
app.UseCors("FrontendCors");

// *** Start Migration Additions ***

// 5. Map Health Check Endpoint
app.MapGet("/healthcheck", async (GameStateService gameStateService, ILogger<Program> logger) =>
{
    logger.LogInformation("Health check requested");

    bool redisConnected = gameStateService.IsRedisConnected(out string? redisError);
    long waitingPlayersCount = -1;
    long activeGamesCount = -1;

    if (redisConnected)
    {
        try
        {
            waitingPlayersCount = await gameStateService.GetMatchmakingQueueSizeAsync();
            activeGamesCount = gameStateService.GetActiveGameCount();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HealthCheck failed to get metrics from Redis.");
            redisConnected = false; // Mark as degraded if metrics fail
            redisError = redisError ?? "Failed to get matchmaking/game count metrics.";
        }
    }

    var healthData = new // Use anonymous type for simplicity
    {
        status = redisConnected ? "Healthy" : "Degraded",
        timestamp = DateTime.UtcNow,
        dependencies = new Dictionary<string, object> // Dictionary for dependencies
        {
            { "redisConnected", redisConnected },
            { "redisError", redisError ?? "N/A" }
        },
        metrics = new Dictionary<string, object> // Dictionary for metrics
        {
            { "waitingPlayers", waitingPlayersCount },
            { "activeGames", activeGamesCount },
            { "inMemorySessions", 0 } // Fixed value since we can't access static fields reliably here
        }
    };

    // Return JSON response
    return Results.Ok(healthData);
});

// Map PongHub endpoint
app.MapHub<AzureOnlinePongGame.PongHub>("/pong");

// *** End Migration Additions ***


app.Run();
