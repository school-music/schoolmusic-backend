using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using schoolmusic_backend.Models;
using schoolmusic_backend.Configuration;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;
using LibVLCSharp.Shared;
using schoolmusic_backend.Services;


LibVLCSharp.Shared.Core.Initialize();

var builder = WebApplication.CreateBuilder(args);

// W Development generujemy losowy klucz, jeśli nie został ustawiony przez user-secrets.
// Klucz zmienia się po restarcie i nigdy nie może być używany w środowisku produkcyjnym.
if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Key"]))
{
    builder.Configuration["Jwt:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

ProductionConfigurationValidator.Validate(builder.Configuration, builder.Environment);

var connectionString = builder.Configuration.GetConnectionString("database")
    ?? builder.Configuration.GetSection("ConnectionString")["database"];
var redisConnectionString = builder.Configuration.GetConnectionString("redis")
    ?? builder.Configuration.GetSection("ConnectionString")["redis"]
    ?? "localhost:6379";

if (string.IsNullOrWhiteSpace(redisConnectionString) || redisConnectionString == "REDIS_CONNECTION_STRING_HERE")
{
    redisConnectionString = "localhost:6379";
}
var jwtSettings = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var key = Encoding.UTF8.GetBytes(jwtSettings.Key);

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

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
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(key),
       ClockSkew = TimeSpan.Zero
   };
});

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("Spotify", client => client.Timeout = TimeSpan.FromSeconds(10));
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
var securityScheme = new OpenApiSecurityScheme
{
    Name = "Authorization",
    Description = "Podaj token JWT w formacie: Bearer {twoj_token}",
    In = ParameterLocation.Header,
    Type = SecuritySchemeType.Http,
    Scheme = "bearer",
    BearerFormat = "JWT"
};

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, cancellationToken) =>
    {
        document.Info = new OpenApiInfo
        {
            Title = "SchoolMusic Backend API",
            Version = "v1",
            Description = "Inteligentny radiowęzeł szkolny - backend REST API z automatycznym harmonogramem przerw, kolejką utworów, panelem moderacji i integracją audio."
        };

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = securityScheme;

        return Task.CompletedTask;
    });

    options.AddOperationTransformer((operation, context, cancellationToken) =>
    {
        var hasAuthorize = context.Description.ActionDescriptor.EndpointMetadata
            .Any(m => m is Microsoft.AspNetCore.Authorization.IAuthorizeData);

        var allowAnonymous = context.Description.ActionDescriptor.EndpointMetadata
            .Any(m => m is Microsoft.AspNetCore.Authorization.IAllowAnonymous);

        if (hasAuthorize && !allowAnonymous)
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer")] = new List<string>()
            });
        }

        return Task.CompletedTask;
    });
});

// Database and Redis config
builder.Services.AddDbContext<schoolmusicContext>(options=>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
);

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var config = ConfigurationOptions.Parse(redisConnectionString);
    config.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(config);
}
);

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? [
        "http://localhost:3000", "http://127.0.0.1:3000",
        "http://localhost:5173", "http://127.0.0.1:5173",
        "http://localhost:8080", "http://127.0.0.1:8080"
    ];

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
builder.Services.AddSingleton<IQueueMutationLock, QueueMutationLock>();
builder.Services.AddScoped<IRankingsService, RankingsService>();
builder.Services.AddSingleton<IAudioPlayerService, AudioPlayerService>();
builder.Services.AddSingleton<ISpotifyConnectPlaybackService, SpotifyConnectPlaybackService>();
builder.Services.AddSingleton<BreakSchedulerService>();
builder.Services.AddSingleton<IBreakSchedulerService>(sp => sp.GetRequiredService<BreakSchedulerService>());
if (builder.Configuration.GetValue("Scheduler:Enabled", true))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BreakSchedulerService>());
builder.Services.AddSingleton<ISongSourceService, SpotifySongSource>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ISystemStatusService, SystemStatusService>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddSingleton<ILdapService, LdapService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

var app = builder.Build();

if (!builder.Configuration.GetValue("Scheduler:Enabled", true))
    app.Logger.LogWarning("Break scheduler is disabled by configuration; this instance will not control automatic playback.");

// Walidacja konfiguracji LDAP przy starcie aplikacji
var ldapService = app.Services.GetRequiredService<ILdapService>();
if (ldapService is LdapService ldapServiceImpl)
{
    ldapServiceImpl.ValidateConfiguration();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Features:SwaggerEnabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors("CorsPolicy");

// W srodowisku Development wylaczamy HttpsRedirection, aby nie psuc lokalnego HTTP (5170/5200),
// kontenerow Dockera (8080) oraz negocjacji SignalR z przegladarki.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

// Hub udostępnia wyłącznie odczyt publicznego stanu kolejki i kiosku.
app.MapHub<schoolmusic_backend.Hubs.QueueHub>("/hubs/queue").AllowAnonymous();

app.Run();
