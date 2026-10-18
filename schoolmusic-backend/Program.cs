using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using schoolmusic_backend.Models;
using System.Text;
using StackExchange.Redis;
using schoolmusic_backend.Services;

/// TODO: commets to every controller, to make code more readable,
/// since the first controllers (UserController, TracksController) are simple but the more advanced
/// ones will be a lot more complex

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("database");
var redisConnectionString = builder.Configuration.GetConnectionString("redis");
var jwtSettings = builder.Configuration.GetSection("Jwt");
var key = Encoding.UTF8.GetBytes(jwtSettings["Key"]);

builder.Services.AddAuthentication(options=>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options=>
{
   options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidAudience = jwtSettings["Audience"],
        IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(key),
       ClockSkew = TimeSpan.Zero
   };
});

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddHttpClient("Spotify", client => client.Timeout = TimeSpan.FromSeconds(10));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Database and Redis config
builder.Services.AddDbContext<schoolmusicContext>(options=>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
);

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnectionString)
);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000", "http://localhost:5173", "http://localhost:8080"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});


// SignalR configuration
builder.Services.AddSignalR();

builder.Services.AddScoped<IBreakService, BreakService>();
builder.Services.AddScoped<IRadioStatusService, RadioStatusService>();
builder.Services.AddScoped<IQueueService, QueueService>();
builder.Services.AddScoped<IRankingsService, RankingsService>();
builder.Services.AddSingleton<ISpotifyConnectPlaybackService, SpotifyConnectPlaybackService>();
builder.Services.AddSingleton<BreakSchedulerService>();
builder.Services.AddSingleton<IBreakSchedulerService>(sp => sp.GetRequiredService<BreakSchedulerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BreakSchedulerService>());
builder.Services.AddSingleton<ISongSourceService, SpotifySongSource>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ISystemStatusService, SystemStatusService>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Features:SwaggerEnabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors("CorsPolicy");

app.UseHttpsRedirection();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.MapHub<schoolmusic_backend.Hubs.QueueHub>("/hubs/queue");

app.Run();
