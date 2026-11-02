using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using Xunit;

namespace schoolmusic_backend.Tests;

public class LdapAuthTests
{
    private const string JanKowalskiDn = "uid=jan.kowalski,ou=Users,dc=szkola,dc=edu,dc=pl";

    private static schoolmusicContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<schoolmusicContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var context = new schoolmusicContext(options);
        context.Database.EnsureCreated();

        if (!context.Ranks.Any(r => r.Id == 1))
        {
            context.Ranks.Add(new Rank { Id = 1, Rank1 = "user" });
            context.SaveChanges();
        }

        return context;
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> inMemorySettings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();
    }

    private static UsersController CreateController(
        schoolmusicContext context,
        IConfiguration configuration,
        ILdapService ldapService)
    {
        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        var controller = new UsersController(
            context,
            configuration,
            ldapService,
            new JwtTokenService(Options.Create(jwtOptions)))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private static LdapUser CreateLdapUser(string username, string? distinguishedName = null)
    {
        return new LdapUser(
            username,
            username,
            $"{username}@szkola.edu.pl",
            Array.Empty<string>(),
            distinguishedName ?? $"uid={username},ou=Users,dc=szkola,dc=edu,dc=pl");
    }

    #region LdapService Tests

    [Fact]
    public async Task LdapService_WhenDisabled_ReturnsFail()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "false"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);
        var result = await service.AuthenticateAsync("jan.kowalski", "haslo123");

        Assert.False(result.Success);
        Assert.Contains("wyłączone", result.ErrorMessage);
    }

    [Theory]
    [InlineData("", "haslo123")]
    [InlineData("jan.kowalski", "")]
    [InlineData("   ", "   ")]
    public async Task LdapService_WhenCredentialsEmpty_ReturnsFail(string login, string password)
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);
        var result = await service.AuthenticateAsync(login, password);

        Assert.False(result.Success);
        Assert.Contains("wymagane", result.ErrorMessage);
    }

    [Fact]
    public async Task LdapService_SimulatedMode_ConfiguredUser_ValidPassword_ReturnsSuccess()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!",
            ["Ldap:SimulatedUsers:0:DisplayName"] = "Jan Kowalski",
            ["Ldap:SimulatedUsers:0:Email"] = "jan.kowalski@szkola.edu.pl"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);
        var result = await service.AuthenticateAsync("jan.kowalski", "Szkola2026!");

        Assert.True(result.Success);
        Assert.NotNull(result.User);
        Assert.Equal("jan.kowalski", result.User.Username);
        Assert.Equal("Jan Kowalski", result.User.DisplayName);
        Assert.Equal("jan.kowalski@szkola.edu.pl", result.User.Email);
        Assert.False(string.IsNullOrWhiteSpace(result.User.DistinguishedName));
    }

    [Fact]
    public async Task LdapService_SimulatedMode_UsesConfiguredDistinguishedName()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!",
            ["Ldap:SimulatedUsers:0:DistinguishedName"] = JanKowalskiDn
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);
        var result = await service.AuthenticateAsync("jan.kowalski", "Szkola2026!");

        Assert.True(result.Success);
        Assert.Equal(JanKowalskiDn, result.User!.DistinguishedName);
    }

    [Fact]
    public async Task LdapService_SimulatedMode_ConfiguredUser_InvalidPassword_ReturnsFail()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);
        var result = await service.AuthenticateAsync("jan.kowalski", "ZleHaslo123");

        Assert.False(result.Success);
        Assert.Contains("Niepoprawne hasło", result.ErrorMessage);
    }

    [Fact]
    public async Task LdapService_SimulatedMode_UnknownUser_ReturnsFail()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);
        var result = await service.AuthenticateAsync("nieznany.uczen", "haslo");

        Assert.False(result.Success);
        Assert.Contains("Nie znaleziono", result.ErrorMessage);
    }

    [Fact]
    public async Task LdapService_NormalizesDomainAndEmailFormat()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);
        
        var resultEmail = await service.AuthenticateAsync("jan.kowalski@szkola.edu.pl", "Szkola2026!");
        Assert.True(resultEmail.Success);
        Assert.Equal("jan.kowalski", resultEmail.User!.Username);

        var resultDomain = await service.AuthenticateAsync(@"SZKOLA\jan.kowalski", "Szkola2026!");
        Assert.True(resultDomain.Success);
        Assert.Equal("jan.kowalski", resultDomain.User!.Username);
    }

    #endregion

    #region UsersController School/LDAP Login Tests

    [Fact]
    public async Task SchoolLogin_WhenLdapDisabled_ReturnsServiceUnavailable503()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokens1234567890!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        });

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(false);

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "jan.kowalski",
            password = "SecretPassword123!"
        });

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("login", "")]
    public async Task SchoolLogin_WhenInputInvalid_ReturnsBadRequest400(string login, string password)
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());
        var mockLdap = new Mock<ILdapService>();

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = login,
            password = password
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SchoolLogin_WhenLdapAuthFails_ReturnsUnauthorized401()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("jan.kowalski", "wrong", default))
            .ReturnsAsync(LdapAuthResult.Fail("Błędny login lub hasło."));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "jan.kowalski",
            password = "wrong"
        });

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.NotNull(unauthorized.Value);
    }

    [Fact]
    public async Task SchoolLogin_WhenValid_AndUserDoesNotExistLocally_AutoProvisionsUserAndReturnsJwt()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokens1234567890!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        });

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("nowy.uczen", "haslo123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("nowy.uczen")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "nowy.uczen",
            password = "haslo123!"
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var val = Assert.IsType<UsersController.LoginResponseDto>(okResult.Value);
        Assert.NotNull(val.jwtSecurityToken);
        Assert.Equal("user", val.role);
        Assert.Equal("nowy.uczen", val.username);
        Assert.True(val.isLdap);

        // Weryfikacja bazy danych: uczeń został zarejestrowany razem z powiązaniem do katalogu
        using var verificationDb = CreateInMemoryContext(dbName);
        var createdUser = await verificationDb.Users.FirstOrDefaultAsync(u => u.UserLogin == "nowy.uczen");
        Assert.NotNull(createdUser);
        Assert.Equal(1, createdUser.RankId);
        Assert.Equal("uid=nowy.uczen,ou=Users,dc=szkola,dc=edu,dc=pl", createdUser.LdapDn);
    }

    [Fact]
    public async Task SchoolLogin_WhenValid_AndUserAlreadyLinkedToSameDirectoryAccount_LogsInWithoutDuplicatingUser()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            setupDb.Users.Add(new User
            {
                Id = 15,
                UserLogin = "istniejacy.uczen",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("DummySecret"),
                RankId = 1,
                CreatedAt = DateTime.UtcNow,
                LdapDn = "uid=istniejacy.uczen,ou=Users,dc=szkola,dc=edu,dc=pl"
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokens1234567890!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        });

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("istniejacy.uczen", "haslo123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("istniejacy.uczen")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "istniejacy.uczen",
            password = "haslo123!"
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var val = Assert.IsType<UsersController.LoginResponseDto>(okResult.Value);
        Assert.Equal(15, val.userId);
        Assert.Equal("istniejacy.uczen", val.username);

        // Weryfikacja braku duplikatów
        using var verificationDb = CreateInMemoryContext(dbName);
        var count = await verificationDb.Users.CountAsync(u => u.UserLogin == "istniejacy.uczen");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task SchoolLogin_WhenLocalAccountIsNotLinkedToDirectory_ReturnsConflictInsteadOfTakingOverAccount()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            setupDb.Users.Add(new User
            {
                Id = 42,
                UserLogin = "moderator",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("LokalneHaslo123!"),
                RankId = 2,
                CreatedAt = DateTime.UtcNow,
                LdapDn = null
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("moderator", "HasloZKatalogu123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("moderator")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "moderator",
            password = "HasloZKatalogu123!"
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        // Konto lokalne nie mogło zostać przejęte
        using var verificationDb = CreateInMemoryContext(dbName);
        var localUser = await verificationDb.Users.FirstOrDefaultAsync(u => u.UserLogin == "moderator");
        Assert.NotNull(localUser);
        Assert.Equal(2, localUser.RankId);
        Assert.Null(localUser.LdapDn);
    }

    [Fact]
    public async Task SchoolLogin_WhenLocalAccountIsLinkedToDifferentDirectoryAccount_ReturnsConflict()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            setupDb.Users.Add(new User
            {
                Id = 43,
                UserLogin = "jan.kowalski",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("DummySecret"),
                RankId = 1,
                CreatedAt = DateTime.UtcNow,
                LdapDn = "uid=stary.wpis,ou=Users,dc=szkola,dc=edu,dc=pl"
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("jan.kowalski", "Haslo123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("jan.kowalski")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "jan.kowalski",
            password = "Haslo123!"
        });

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task SchoolLogin_WhenDirectoryReturnsNoDistinguishedName_ReturnsConflict()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("anonim", "Haslo123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(new LdapUser("anonim", null, null, Array.Empty<string>(), null)));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "anonim",
            password = "Haslo123!"
        });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

        using var verificationDb = CreateInMemoryContext(dbName);
        Assert.False(await verificationDb.Users.AnyAsync(u => u.UserLogin == "anonim"));
    }

    [Fact]
    public async Task SchoolLogin_WhenUserIsBanned_ReturnsForbidden403()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            var user = new User
            {
                Id = 20,
                UserLogin = "zbanowany.uczen",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("Secret"),
                RankId = 1,
                CreatedAt = DateTime.UtcNow,
                LdapDn = "uid=zbanowany.uczen,ou=Users,dc=szkola,dc=edu,dc=pl"
            };
            setupDb.Users.Add(user);
            setupDb.BannedUsers.Add(new BannedUser
            {
                UserId = 20,
                ModeratorId = 1,
                Reason = "Wulgaryzmy w propozycji",
                BannedAt = DateTime.UtcNow
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("zbanowany.uczen", "valid_ldap_password", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("zbanowany.uczen")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.SchoolLogin(new UsersController.SchoolLoginDto
        {
            login = "zbanowany.uczen",
            password = "valid_ldap_password"
        });

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }

    [Fact]
    public async Task StandardLogin_WhenUserNotInLocalDb_FallsBackToLdapAndAutoProvisions()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokens1234567890!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        });

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("jan.kowalski", "Szkola2026!", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("jan.kowalski")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.Login(new UsersController.UserLoginDto
        {
            name = "jan.kowalski",
            password = "Szkola2026!"
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var val = Assert.IsType<UsersController.LoginResponseDto>(okResult.Value);
        Assert.NotNull(val.jwtSecurityToken);
        Assert.Equal("jan.kowalski", val.username);
        Assert.True(val.isLdap);

        // Uczeń został utworzony w bazie razem z powiązaniem do katalogu
        using var verificationDb = CreateInMemoryContext(dbName);
        var userInDb = await verificationDb.Users.FirstOrDefaultAsync(u => u.UserLogin == "jan.kowalski");
        Assert.NotNull(userInDb);
        Assert.Equal("uid=jan.kowalski,ou=Users,dc=szkola,dc=edu,dc=pl", userInDb.LdapDn);
    }

    [Fact]
    public async Task StandardLogin_WhenLocalPasswordMatches_LogsInDirectlyWithoutLdap()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            setupDb.Users.Add(new User
            {
                Id = 30,
                UserLogin = "lokalny_user",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("LocalPassword123!"),
                RankId = 1,
                CreatedAt = DateTime.UtcNow
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokens1234567890!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        });

        var mockLdap = new Mock<ILdapService>();

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.Login(new UsersController.UserLoginDto
        {
            name = "lokalny_user",
            password = "LocalPassword123!"
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var val = Assert.IsType<UsersController.LoginResponseDto>(okResult.Value);
        Assert.False(val.isLdap);
        Assert.Equal("lokalny_user", val.username);

        // Serwis LDAP nie musiał być nawet wywoływany
        mockLdap.Verify(l => l.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task StandardLogin_WhenLocalPasswordIsWrong_DoesNotFallBackToDirectoryForUnlinkedAccount()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            setupDb.Users.Add(new User
            {
                Id = 60,
                UserLogin = "head_admin",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("BardzoTajneHaslo123!"),
                RankId = 4,
                CreatedAt = DateTime.UtcNow,
                LdapDn = null
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.Login(new UsersController.UserLoginDto
        {
            name = "head_admin",
            password = "HasloZKatalogu123!"
        });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, badRequest.StatusCode);

        // Kluczowe: katalog nie może być nawet odpytany o konto lokalne
        mockLdap.Verify(l => l.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StandardLogin_WhenLinkedAccountPasswordIsWrong_RejectsDirectoryIdentityFromDifferentAccount()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            setupDb.Users.Add(new User
            {
                Id = 70,
                UserLogin = "uczen",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("DummySecret"),
                RankId = 1,
                CreatedAt = DateTime.UtcNow,
                LdapDn = "uid=uczen,ou=Users,dc=szkola,dc=edu,dc=pl"
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>());

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        // Ten sam login, ale inne konto w katalogu
        mockLdap.Setup(l => l.AuthenticateAsync("uczen", "Haslo123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("uczen", "uid=obcy,ou=Users,dc=szkola,dc=edu,dc=pl")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.Login(new UsersController.UserLoginDto
        {
            name = "uczen",
            password = "Haslo123!"
        });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task StandardLogin_WhenLinkedAccountPasswordIsWrong_AllowsDirectoryLoginForSameIdentity()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateInMemoryContext(dbName))
        {
            setupDb.Users.Add(new User
            {
                Id = 71,
                UserLogin = "uczen",
                UserPassword = BCrypt.Net.BCrypt.HashPassword("DummySecret"),
                RankId = 1,
                CreatedAt = DateTime.UtcNow,
                LdapDn = "uid=uczen,ou=Users,dc=szkola,dc=edu,dc=pl"
            });
            await setupDb.SaveChangesAsync();
        }

        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokens1234567890!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        });

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("uczen", "Haslo123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("uczen")));

        var controller = CreateController(context, config, mockLdap.Object);
        var result = await controller.Login(new UsersController.UserLoginDto
        {
            name = "uczen",
            password = "Haslo123!"
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var val = Assert.IsType<UsersController.LoginResponseDto>(okResult.Value);
        Assert.Equal(71, val.userId);
        Assert.True(val.isLdap);
    }

    #endregion

    #region LdapService Security Tests

    [Theory]
    [InlineData("user*", "user\\2a")]
    [InlineData("user(name", "user\\28name")]
    [InlineData("user)name", "user\\29name")]
    [InlineData("user\\name", "user\\5cname")]
    [InlineData("normal.user", "normal.user")]
    [InlineData("jan@szkola.edu.pl", "jan@szkola.edu.pl")]
    public void LdapService_EscapeLdapFilterValue_EscapesSpecialCharactersCorrectly(string input, string expected)
    {
        var result = LdapService.EscapeLdapFilterValue(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task LdapService_SimulatedMode_InjectionAttemptInLogin_ReturnsFail()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);

        // Próba LDAP injection - login z nawiasami i gwiazdką
        var result = await service.AuthenticateAsync("*)(uid=*))(|(uid=*", "Szkola2026!");

        Assert.False(result.Success);
    }

    [Fact]
    public void LdapService_ValidateConfiguration_SimulatedModeInProduction_ThrowsException()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Production");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);

        // Tryb symulacyjny na produkcji powinien rzucić wyjątek
        Assert.Throws<InvalidOperationException>(() => service.ValidateConfiguration());
    }

    [Fact]
    public void LdapService_ValidateConfiguration_SimulatedModeInDevelopment_DoesNotThrow()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);

        // W Development tryb symulacyjny jest dozwolony
        var exception = Record.Exception(() => service.ValidateConfiguration());
        Assert.Null(exception);
    }

    [Fact]
    public void LdapService_ValidateConfiguration_WhenDisabled_DoesNotThrow()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "false",
            ["Ldap:SimulatedMode"] = "false"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Production");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);

        var exception = Record.Exception(() => service.ValidateConfiguration());
        Assert.Null(exception);
    }

    #endregion

    [Fact]
    public async Task LdapService_RequiredGroup_WhenUserNotInRequiredGroup_ReturnsFail()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:RequiredGroup"] = "Radiowęzeł",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!",
            ["Ldap:SimulatedUsers:0:Groups:0"] = "Uczniowie"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);

        var result = await service.AuthenticateAsync("jan.kowalski", "Szkola2026!");

        Assert.False(result.Success);
        Assert.Contains("uprawnień", result.ErrorMessage);
    }

    [Fact]
    public async Task LdapService_RequiredGroup_WhenUserInRequiredGroup_ReturnsSuccess()
    {
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Ldap:Enabled"] = "true",
            ["Ldap:SimulatedMode"] = "true",
            ["Ldap:RequiredGroup"] = "Radiowęzeł",
            ["Ldap:SimulatedUsers:0:Login"] = "jan.kowalski",
            ["Ldap:SimulatedUsers:0:Password"] = "Szkola2026!",
            ["Ldap:SimulatedUsers:0:Groups:0"] = "Uczniowie",
            ["Ldap:SimulatedUsers:0:Groups:1"] = "Radiowęzeł"
        });

        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.SetupGet(e => e.EnvironmentName).Returns("Development");
        var service = new LdapService(config, NullLogger<LdapService>.Instance, mockEnv.Object);

        var result = await service.AuthenticateAsync("jan.kowalski", "Szkola2026!");

        Assert.True(result.Success);
        Assert.NotNull(result.User);
    }

    [Fact]
    public async Task SchoolLogin_WhenConcurrentFirstLogins_DoesNotProduceDuplicateUsers()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var config = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "SuperSecretKeyForTestingJwtTokens1234567890!",
            ["Jwt:Issuer"] = "TestIssuer",
            ["Jwt:Audience"] = "TestAudience"
        });

        var mockLdap = new Mock<ILdapService>();
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("concurrent.user", "pass123", default))
            .ReturnsAsync(LdapAuthResult.Ok(CreateLdapUser("concurrent.user")));

        var controller = CreateController(context, config, mockLdap.Object);

        var task1 = controller.SchoolLogin(new UsersController.SchoolLoginDto { login = "concurrent.user", password = "pass123" });
        var task2 = controller.SchoolLogin(new UsersController.SchoolLoginDto { login = "concurrent.user", password = "pass123" });

        var results = await Task.WhenAll(task1, task2);

        Assert.All(results, r => Assert.IsType<OkObjectResult>(r));

        using var verificationDb = CreateInMemoryContext(dbName);
        var count = await verificationDb.Users.CountAsync(u => u.UserLogin == "concurrent.user");
        Assert.Equal(1, count);
    }
}
