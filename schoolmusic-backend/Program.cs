using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Extensions;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// W Development i Testing generujemy losowy klucz, jeśli nie został ustawiony przez user-secrets/konfigurację.
// Klucz zmienia się po restarcie i nigdy nie może być używany w środowisku produkcyjnym.
if ((builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")) &&
    string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Key"]))
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

builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.AddOptions<AudioOptions>()
    .Bind(builder.Configuration.GetSection(AudioOptions.SectionName));

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        if (!context.ProblemDetails.Extensions.ContainsKey("message"))
        {
            context.ProblemDetails.Extensions["message"] =
                context.ProblemDetails.Detail ?? context.ProblemDetails.Title ?? "Wystąpił błąd.";
        }
    };
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptionsAccessor) =>
    {
        var jwtSettings = jwtOptionsAccessor.Value;
        var key = Encoding.UTF8.GetBytes(jwtSettings.Key);

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var sub = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
                if (!int.TryParse(sub, out var userId))
                {
                    context.Fail("Nieprawidłowy identyfikator użytkownika w tokenie.");
                    return;
                }

                var stampService = context.HttpContext.RequestServices.GetRequiredService<IAuthSecurityStampService>();
                var jti = principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                if (!string.IsNullOrWhiteSpace(jti) &&
                    await stampService.IsAccessTokenRevokedAsync(jti, context.HttpContext.RequestAborted))
                {
                    context.Fail("Token dostępu został unieważniony.");
                    return;
                }

                var state = await stampService.GetUserSecurityStateAsync(userId, context.HttpContext.RequestAborted);
                if (!state.Exists || state.IsBanned)
                {
                    context.Fail("Konto użytkownika nie istnieje lub zostało zablokowane.");
                    return;
                }

                var tvClaim = principal?.FindFirstValue(JwtTokenService.TokenVersionClaimType);
                if (!int.TryParse(tvClaim, out var tokenVersion) || tokenVersion != state.TokenVersion)
                {
                    context.Fail("Sesja została unieważniona po zmianie uprawnień lub hasła.");
                    return;
                }

                if (principal?.Identity is ClaimsIdentity identity)
                {
                    var existingRoleClaims = identity.FindAll(ClaimTypes.Role)
                        .Concat(identity.FindAll("role"))
                        .ToArray();
                    foreach (var roleClaim in existingRoleClaims)
                    {
                        identity.RemoveClaim(roleClaim);
                    }
                    identity.AddClaim(new Claim(ClaimTypes.Role, state.RoleName));
                }
            },
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuthentication");
                logger.LogWarning(
                    "Odrzucono token JWT z powodu błędu {ExceptionType}.",
                    context.Exception.GetType().Name);
                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                if (context.HttpContext.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = ApiProblemResults.MediaType;
                    if (!string.IsNullOrWhiteSpace(context.ErrorDescription))
                    {
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                    }
                    var detail = "Wymagane jest ważne uwierzytelnienie JWT.";
                    var problem = ApiProblemResults.BuildProblemDetails(
                        context.HttpContext,
                        StatusCodes.Status401Unauthorized,
                        detail);
                    await context.Response.WriteAsJsonAsync(problem, options: null, contentType: ApiProblemResults.MediaType);
                }
            },
            OnForbidden = async context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("JwtAuthorization");
                logger.LogWarning("Odrzucono uwierzytelnione żądanie z powodu niewystarczających uprawnień.");

                if (context.HttpContext.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = ApiProblemResults.MediaType;
                    var problem = ApiProblemResults.BuildProblemDetails(
                        context.HttpContext,
                        StatusCodes.Status403Forbidden,
                        "Nie masz uprawnień do wykonania tej operacji.");
                    await context.Response.WriteAsJsonAsync(problem, options: null, contentType: ApiProblemResults.MediaType);
                }
            }
        };
    });

builder.Services.AddControllers(options =>
{
    options.Filters.Add(new ProblemDetailsContentTypeFilter());
})
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var firstError = context.ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Nieprawidłowe dane wejściowe." : e.ErrorMessage)
                .FirstOrDefault() ?? "Nieprawidłowe dane wejściowe.";

            var validationProblem = new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = firstError,
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#status.400",
                Instance = context.HttpContext.Request.Path.Value
            };
            validationProblem.Extensions["message"] = firstError;

            var result = new BadRequestObjectResult(validationProblem);
            result.ContentTypes.Add(ApiProblemResults.MediaType);
            return result;
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        TimeSpan? retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryValue)
            ? retryValue
            : TimeSpan.FromSeconds(60);

        if (retryAfter.HasValue)
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalSeconds)).ToString();
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = ApiProblemResults.MediaType;

        var problem = ApiProblemResults.BuildProblemDetails(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "Zbyt wiele prób. Odczekaj chwilę i spróbuj ponownie.");

        await context.HttpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: ApiProblemResults.MediaType,
            cancellationToken: cancellationToken);
    };

    options.AddPolicy("auth-login", httpContext =>
    {
        var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        int permitLimit = Math.Max(1, config.GetValue("RateLimiting:AuthLoginPermitLimit", 15));
        int windowSeconds = Math.Max(1, config.GetValue("RateLimiting:AuthLoginWindowSeconds", 60));
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            SegmentsPerWindow = 4,
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    options.AddPolicy("auth-recovery", httpContext =>
    {
        var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        int permitLimit = Math.Max(1, config.GetValue("RateLimiting:AuthRecoveryPermitLimit", 5));
        int windowMinutes = Math.Max(1, config.GetValue("RateLimiting:AuthRecoveryWindowMinutes", 15));
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(windowMinutes),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });
});

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("Spotify", client => client.Timeout = TimeSpan.FromSeconds(10));

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
            .Any(m => m is IAuthorizeData);

        var allowAnonymous = context.Description.ActionDescriptor.EndpointMetadata
            .Any(m => m is IAllowAnonymous);

        if (hasAuthorize && !allowAnonymous)
        {
            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer")] = new List<string>()
            });
        }

        if (operation.Responses != null)
        {
            foreach (var (statusKey, response) in operation.Responses)
            {
                if (response is not OpenApiResponse concreteResponse)
                {
                    continue;
                }

                if (statusKey is "204" or "205" or "302" or "304")
                {
                    concreteResponse.Content?.Clear();
                    concreteResponse.Content = null;
                    continue;
                }

                if (int.TryParse(statusKey, out var statusCode) &&
                    statusCode >= 400 &&
                    concreteResponse.Content != null &&
                    concreteResponse.Content.TryGetValue("application/json", out var mediaType) &&
                    !concreteResponse.Content.ContainsKey(ApiProblemResults.MediaType))
                {
                    concreteResponse.Content[ApiProblemResults.MediaType] = mediaType;
                    concreteResponse.Content.Remove("application/json");
                }
            }
        }

        return Task.CompletedTask;
    });
});

// Database and Redis config
builder.Services.AddDbContext<schoolmusicContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString))
);

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var config = ConfigurationOptions.Parse(redisConnectionString);
    config.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(config);
});

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

builder.Services.AddSignalR();

builder.Services.AddScoped<IBreakService, BreakService>();
builder.Services.AddScoped<IVotingTargetService, VotingTargetService>();
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
var schoolTimeZone = SchoolTimeProvider.ResolveTimeZone(builder.Configuration["School:TimeZone"]);
builder.Services.AddSingleton<TimeProvider>(new SchoolTimeProvider(TimeProvider.System, schoolTimeZone));
builder.Services.AddScoped<ISystemStatusService, SystemStatusService>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddSingleton<IStatisticsRefreshCoordinator, StatisticsRefreshCoordinator>();
builder.Services.AddSingleton<ILdapService, LdapService>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<IAuthSecurityStampService, AuthSecurityStampService>();
builder.Services.AddScoped<ISessionTokenService, SessionTokenService>();
builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
builder.Services.AddScoped<IPasswordRecoveryService, PasswordRecoveryService>();

if (builder.Configuration.GetValue<bool>("Smtp:Enabled") ||
    (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing")))
{
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}
else
{
    builder.Services.AddSingleton<LocalDevelopmentEmailSink>();
    builder.Services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<LocalDevelopmentEmailSink>());
    builder.Services.AddSingleton<ILocalEmailSink>(sp => sp.GetRequiredService<LocalDevelopmentEmailSink>());
}

var app = builder.Build();

if (!builder.Configuration.GetValue("Scheduler:Enabled", true))
    app.Logger.LogWarning("Break scheduler is disabled by configuration; this instance will not control automatic playback.");

var ldapService = app.Services.GetRequiredService<ILdapService>();
if (ldapService is LdapService ldapServiceImpl)
{
    ldapServiceImpl.ValidateConfiguration();
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = ApiProblemResults.MediaType;
        var problem = ApiProblemResults.BuildProblemDetails(
            context,
            StatusCodes.Status500InternalServerError,
            "Wystąpił nieoczekiwany błąd serwera.");
        await context.Response.WriteAsJsonAsync(problem, options: null, contentType: ApiProblemResults.MediaType);
    });
});

if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Features:SwaggerEnabled"))
{
    // Dokumentacja API jest jawnie publiczna; domyślna polityka uwierzytelniania
    // nie może blokować Scalar ani schematu OpenAPI.
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference().AllowAnonymous();
}

app.UseCors("CorsPolicy");

if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

// Hub udostępnia wyłącznie odczyt publicznego stanu kolejki i kiosku.
app.MapHub<schoolmusic_backend.Hubs.QueueHub>("/hubs/queue").AllowAnonymous();

app.Run();

public partial class Program { }
