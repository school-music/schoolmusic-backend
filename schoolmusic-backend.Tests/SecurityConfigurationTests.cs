using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using schoolmusic_backend.Models;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace schoolmusic_backend.Tests;

public sealed class SecurityConfigurationTests
{
    private static IWebHostEnvironment CreateEnvironment(string name)
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(name);
        return environment.Object;
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-key")]
    [InlineData("YOUR_SECRET_KEY_HERE")]
    [InlineData("default_jwt_secret_key_development_only")]
    [InlineData("schoolmusic_super_secret_jwt_key_2026_dev!")]
    [InlineData("UZUPELNIJ_LOSOWYM_KLUCZEM_MINIMUM_32_BAJTY")]
    public void JwtOptionsValidator_ProductionRejectsMissingWeakOrKnownKey(string key)
    {
        var validator = new JwtOptionsValidator(CreateEnvironment("Production"));
        var options = new JwtOptions
        {
            Issuer = "SchoolMusicAPI",
            Audience = "SchoolMusicApp",
            Key = key
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void JwtOptionsValidator_ProductionAcceptsStrongDeploymentKey()
    {
        var validator = new JwtOptionsValidator(CreateEnvironment("Production"));
        var options = new JwtOptions
        {
            Issuer = "SchoolMusicAPI",
            Audience = "SchoolMusicApp",
            Key = "LosowyKluczWdrożeniowyMającyPonad32Bajty!2026"
        };

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ProductionConfigurationValidator_RejectsKnownDatabaseAndUnprotectedRedis()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionString:database"] = "server=db;database=schoolmusic;user=root;password=secret;",
                ["ConnectionString:redis"] = "redis:6379"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, CreateEnvironment("Production")));

        Assert.Contains("root", exception.Message);
        Assert.Contains("Redis", exception.Message);
    }

    [Theory]
    [InlineData("UZUPELNIJ_LOSOWYM_HASLEM_BAZY", "redis:6379,password=StrongRedisPassword!2026")]
    [InlineData("StrongDatabasePassword!2026", "redis:6379,password=UZUPELNIJ_LOSOWYM_HASLEM_REDIS")]
    public void ProductionConfigurationValidator_RejectsEnvExamplePlaceholders(string databasePassword, string redisConnection)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionString:database"] = $"server=db;database=schoolmusic;user=schoolmusic;password={databasePassword};",
                ["ConnectionString:redis"] = redisConnection
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfigurationValidator.Validate(configuration, CreateEnvironment("Production")));

        Assert.Contains("domyśl", exception.Message);
    }

    [Fact]
    public void ProductionConfigurationValidator_AcceptsDedicatedDatabaseUserAndProtectedRedis()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionString:database"] = "server=db;database=schoolmusic;user=schoolmusic;password=StrongDatabasePassword!2026;",
                ["ConnectionString:redis"] = "redis:6379,password=StrongRedisPassword!2026"
            })
            .Build();

        var exception = Record.Exception(() =>
            ProductionConfigurationValidator.Validate(configuration, CreateEnvironment("Production")));

        Assert.Null(exception);
    }

    [Fact]
    public void ProductionConfigurationValidator_DevelopmentAllowsLocalServices()
    {
        var configuration = new ConfigurationBuilder().Build();

        var exception = Record.Exception(() =>
            ProductionConfigurationValidator.Validate(configuration, CreateEnvironment("Development")));

        Assert.Null(exception);
    }

    [Fact]
    public void JwtTokenService_CreatesTokenWithExpectedIssuerAudienceAndSignature()
    {
        var options = new JwtOptions
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            Key = "StrongTestKeyWithAtLeast32BytesLong!2026",
            ExpirationHours = 1
        };
        var service = new JwtTokenService(Options.Create(options));
        var user = new User
        {
            Id = 7,
            UserLogin = "test.user",
            Rank = new Rank { Id = 2, Rank1 = "mod" }
        };

        var token = service.CreateToken(user, isLdap: true);
        var principal = new JwtSecurityTokenHandler().ValidateToken(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key)),
            ClockSkew = TimeSpan.Zero
        }, out _);

        Assert.Equal("7", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("mod", principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Equal("ldap", principal.FindFirst("auth_method")?.Value);
    }
}
