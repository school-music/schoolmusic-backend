using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;
using Xunit;

namespace schoolmusic_backend.Tests;

public class LdapAuthTests
{
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
        var controller = new UsersController(context, configuration, ldapService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
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
            .ReturnsAsync(LdapAuthResult.Ok(new LdapUser("nowy.uczen", "Nowy Uczeń", "nowy.uczen@szkola.edu.pl", Array.Empty<string>())));

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

        // Weryfikacja bazy danych: uczeń został zarejestrowany
        using var verificationDb = CreateInMemoryContext(dbName);
        var createdUser = await verificationDb.Users.FirstOrDefaultAsync(u => u.UserLogin == "nowy.uczen");
        Assert.NotNull(createdUser);
        Assert.Equal(1, createdUser.RankId);
    }

    [Fact]
    public async Task SchoolLogin_WhenValid_AndUserAlreadyExistsLocally_LogsInWithoutDuplicatingUser()
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
        mockLdap.Setup(l => l.IsEnabled).Returns(true);
        mockLdap.Setup(l => l.AuthenticateAsync("istniejacy.uczen", "haslo123!", default))
            .ReturnsAsync(LdapAuthResult.Ok(new LdapUser("istniejacy.uczen", "Istniejący Uczeń", "istniejacy@szkola.edu.pl", Array.Empty<string>())));

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
                CreatedAt = DateTime.UtcNow
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
            .ReturnsAsync(LdapAuthResult.Ok(new LdapUser("zbanowany.uczen", "Zbanowany", "zbanowany@szkola.edu.pl", Array.Empty<string>())));

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
            .ReturnsAsync(LdapAuthResult.Ok(new LdapUser("jan.kowalski", "Jan Kowalski", "jan.kowalski@szkola.edu.pl", Array.Empty<string>())));

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

        // Uczeń został utworzony w bazie
        using var verificationDb = CreateInMemoryContext(dbName);
        var userInDb = await verificationDb.Users.FirstOrDefaultAsync(u => u.UserLogin == "jan.kowalski");
        Assert.NotNull(userInDb);
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
            .ReturnsAsync(LdapAuthResult.Ok(new LdapUser("concurrent.user", "Concurrent", "c@szkola.pl", new[] { "Uczniowie" })));

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