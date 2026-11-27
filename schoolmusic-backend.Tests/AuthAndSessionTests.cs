using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using schoolmusic_backend.Controllers;
using schoolmusic_backend.Models;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Tests;

public sealed class AuthAndSessionTests
{
    private static async Task<ProblemDetails> ReadProblemAsync(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal((int)expectedStatus, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Detail));
        Assert.True(problem.Extensions.TryGetValue("message", out var msgObj));
        Assert.Equal(problem.Detail, msgObj?.ToString());
        return problem;
    }

    private static string ExtractTokenFromActionUrl(string? actionUrl)
    {
        Assert.False(string.IsNullOrWhiteSpace(actionUrl));
        var uri = new Uri(actionUrl!, UriKind.Absolute);
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.True(query.TryGetValue("token", out var values));
        var token = values.ToString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token;
    }

    private static void Authorize(HttpClient client, string jwt) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

    [Fact]
    public async Task Capabilities_ReturnsConfiguredAuthAndSchoolProviderMetadata()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/capabilities");
        response.EnsureSuccessStatusCode();
        var caps = await response.Content.ReadFromJsonAsync<UsersController.AuthCapabilitiesDto>();

        Assert.NotNull(caps);
        Assert.True(caps.AllowLocalRegistration);
        Assert.True(caps.EmailLoginEnabled);
        Assert.True(caps.LdapEnabled);
        Assert.True(caps.PasswordRecoveryEnabled);
        Assert.Equal("https://idp.school.test/reset-password", caps.SchoolIdentityProviderUrl);
    }

    [Fact]
    public async Task RegisterAndProfile_DisplayNameIsOptional_Validated_AndPersistsAcrossDevices()
    {
        using var factory = new TestWebApplicationFactory();
        await factory.SeedRanksAsync();
        using var deviceA = factory.CreateClient();
        using var deviceB = factory.CreateClient();

        // Bez displayName -> null
        var regNoName = await deviceA.PostAsJsonAsync("/api/auth/register", new
        {
            name = "uczen_bez_nazwy",
            password = "StrongPassword123!"
        });
        Assert.Equal(HttpStatusCode.Created, regNoName.StatusCode);
        var regNoNameDto = await regNoName.Content.ReadFromJsonAsync<UsersController.RegisterResponseDto>();
        Assert.NotNull(regNoNameDto);
        Assert.Null(regNoNameDto.DisplayName);

        // Za długi lub ze znakami sterującymi -> 400 ProblemDetails
        var badDisplay = await deviceA.PostAsJsonAsync("/api/auth/register", new
        {
            name = "uczen_zla_nazwa",
            password = "StrongPassword123!",
            displayName = new string('A', UsersController.MaxDisplayNameLength + 1)
        });
        await ReadProblemAsync(badDisplay, HttpStatusCode.BadRequest);

        var controlCharDisplay = await deviceA.PostAsJsonAsync("/api/auth/register", new
        {
            name = "uczen_ctrl",
            password = "StrongPassword123!",
            displayName = "Jan\nKowalski"
        });
        await ReadProblemAsync(controlCharDisplay, HttpStatusCode.BadRequest);

        // Rejestracja z prawidłowym displayName -> zapis i odczyt na innym urządzeniu
        var regOk = await deviceA.PostAsJsonAsync("/api/auth/register", new
        {
            name = "janek123",
            password = "StrongPassword123!",
            displayName = "  Janek z 3B  "
        });
        Assert.Equal(HttpStatusCode.Created, regOk.StatusCode);
        var regOkDto = await regOk.Content.ReadFromJsonAsync<UsersController.RegisterResponseDto>();
        Assert.NotNull(regOkDto);
        Assert.Equal("Janek z 3B", regOkDto.DisplayName);

        var loginResp = await deviceB.PostAsJsonAsync("/api/auth/login", new
        {
            name = "janek123",
            password = "StrongPassword123!"
        });
        loginResp.EnsureSuccessStatusCode();
        var loginDto = await loginResp.Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(loginDto);
        Assert.Equal("Janek z 3B", loginDto.displayName);

        Authorize(deviceA, loginDto.jwtSecurityToken);
        Authorize(deviceB, loginDto.jwtSecurityToken);

        var meBefore = await (await deviceA.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<UsersController.UserProfileDto>();
        Assert.NotNull(meBefore);
        Assert.Equal("Janek z 3B", meBefore.DisplayName);

        // Aktualizacja displayName na urządzeniu A i odczyt na urządzeniu B
        var patchOk = await deviceA.PatchAsJsonAsync("/api/auth/me", new { displayName = "DJ Janek" });
        patchOk.EnsureSuccessStatusCode();
        var updatedProfile = await patchOk.Content.ReadFromJsonAsync<UsersController.UserProfileDto>();
        Assert.Equal("DJ Janek", updatedProfile?.DisplayName);

        var meOtherDevice = await (await deviceB.GetAsync("/api/auth/me"))
            .Content.ReadFromJsonAsync<UsersController.UserProfileDto>();
        Assert.Equal("DJ Janek", meOtherDevice?.DisplayName);

        // Same białe znaki -> 400 ProblemDetails
        var whitespaceResp = await deviceA.PatchAsJsonAsync("/api/auth/me", new { displayName = "   " });
        await ReadProblemAsync(whitespaceResp, HttpStatusCode.BadRequest);

        // Wyczyszczenie pustym ciągiem ("") -> null
        var clearResp = await deviceA.PatchAsJsonAsync("/api/auth/me", new { displayName = "" });
        clearResp.EnsureSuccessStatusCode();
        var cleared = await clearResp.Content.ReadFromJsonAsync<UsersController.UserProfileDto>();
        Assert.Null(cleared?.DisplayName);
    }

    [Fact]
    public async Task EmailVerificationAndLogin_BlocksUnverifiedEmail_EnforcesUniqueness_AndAllowsLoginAfterVerify()
    {
        using var factory = new TestWebApplicationFactory();
        await factory.SeedRanksAsync();
        using var client = factory.CreateClient();

        // Rejestracja z e-mailem (niezweryfikowany)
        var registerResp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "ewa_local",
            password = "StrongPassword123!",
            displayName = "Ewa",
            email = "Ewa.Nowak@School.PL"
        });
        Assert.Equal(HttpStatusCode.Created, registerResp.StatusCode);
        var registerDto = await registerResp.Content.ReadFromJsonAsync<UsersController.RegisterResponseDto>();
        Assert.NotNull(registerDto);
        Assert.Equal("ewa.nowak@school.pl", registerDto.Email);
        Assert.True(registerDto.EmailVerificationRequired);

        // E-mail niezweryfikowany NIE może zalogować użytkownika
        var unverifiedEmailLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = "ewa.nowak@school.pl",
            password = "StrongPassword123!"
        });
        await ReadProblemAsync(unverifiedEmailLogin, HttpStatusCode.Unauthorized);

        // Pobranie linku weryfikacyjnego z lokalnego sinka
        var sentEmail = Assert.Single(factory.EmailSink.GetMessages());
        Assert.Equal("ewa.nowak@school.pl", sentEmail.To);
        var token = ExtractTokenFromActionUrl(sentEmail.ActionUrl);

        // Weryfikacja tokenem
        var verifyResp = await client.PostAsJsonAsync("/api/auth/email/verify", new { token });
        verifyResp.EnsureSuccessStatusCode();

        // Drugi raz ten sam token (single-use) -> 400 ProblemDetails
        var replayVerify = await client.PostAsJsonAsync("/api/auth/email/verify", new { token });
        await ReadProblemAsync(replayVerify, HttpStatusCode.BadRequest);

        // Teraz logowanie zarówno loginem, jak i adresem e-mail działa
        var loginByLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = "ewa_local",
            password = "StrongPassword123!"
        });
        loginByLogin.EnsureSuccessStatusCode();

        var loginByEmail = await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = "EWA.NOWAK@SCHOOL.PL",
            password = "StrongPassword123!"
        });
        loginByEmail.EnsureSuccessStatusCode();
        var loginDto = await loginByEmail.Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(loginDto);
        Assert.True(loginDto.emailVerified);
        Assert.Equal("ewa.nowak@school.pl", loginDto.email);

        // Próba rejestracji drugiego konta z już zweryfikowanym e-mailem -> 409 Conflict
        var duplicateReg = await client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "inne_konto",
            password = "StrongPassword123!",
            email = "ewa.nowak@school.pl"
        });
        await ReadProblemAsync(duplicateReg, HttpStatusCode.Conflict);

        // Przypisanie zajętego e-maila przez zalogowanego drugiego użytkownika -> 409 Conflict
        await factory.SeedUserAsync("drugi_user", "StrongPassword123!");
        var loginSecond = await (await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = "drugi_user",
            password = "StrongPassword123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Authorize(client, loginSecond!.jwtSecurityToken);

        var attachConflict = await client.PostAsJsonAsync("/api/auth/me/email", new { email = "ewa.nowak@school.pl" });
        await ReadProblemAsync(attachConflict, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PasswordRecovery_HandlesUnknownLdapUnverifiedAdminAssistedExpiredAndSingleUseReplay()
    {
        using var factory = new TestWebApplicationFactory();
        var verifiedUser = await factory.SeedUserAsync(
            "verified_user",
            "OldPassword123!",
            rankId: 1,
            email: "verified@school.pl",
            emailVerified: true);
        var unverifiedUser = await factory.SeedUserAsync(
            "unverified_user",
            "OldUnverified123!",
            rankId: 1,
            email: "pending@school.pl",
            emailVerified: false);
        var ldapUser = await factory.SeedUserAsync(
            "jan.kowalski",
            "",
            rankId: 1,
            email: "jan.kowalski@school.pl",
            emailVerified: true,
            authProvider: "ldap");
        var adminUser = await factory.SeedUserAsync("school_admin", "AdminPass123!", rankId: 3);
        var headAdminUser = await factory.SeedUserAsync("school_head", "HeadPass123!", rankId: 4);

        using var client = factory.CreateClient();

        // 1. Nieistniejące konto -> jednorodne 202 bez tokena i bez e-maila
        var unknownReq = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = "nikt_nieznany" });
        Assert.Equal(HttpStatusCode.Accepted, unknownReq.StatusCode);
        var unknownBody = await unknownReq.Content.ReadFromJsonAsync<PasswordRecoveryRequestResponseDto>();
        Assert.Equal("https://idp.school.test/reset-password", unknownBody?.SchoolIdentityProviderUrl);
        Assert.Empty(factory.EmailSink.GetMessages());

        // 2. Konto LDAP -> jednorodne 202 + przekierowanie na szkolnego dostawcę, 0 tokenów w DB
        var ldapReq = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = ldapUser.UserLogin });
        Assert.Equal(HttpStatusCode.Accepted, ldapReq.StatusCode);
        Assert.Empty(factory.EmailSink.GetMessages());

        // 3. Konto lokalne bez zweryfikowanego e-maila -> jednorodne 202, brak samodzielnego resetu
        var unverifiedReq = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = unverifiedUser.UserLogin });
        Assert.Equal(HttpStatusCode.Accepted, unverifiedReq.StatusCode);
        Assert.Empty(factory.EmailSink.GetMessages());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            Assert.Empty(db.PasswordResetTokens);
        }

        // 4. Konto lokalne ze zweryfikowanym e-mailem -> wysłany link, w DB wyłącznie hash tokenu
        var verifiedReq = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = "verified@school.pl" });
        Assert.Equal(HttpStatusCode.Accepted, verifiedReq.StatusCode);
        var emailMsg = Assert.Single(factory.EmailSink.GetMessages());
        var rawToken = ExtractTokenFromActionUrl(emailMsg.ActionUrl);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var stored = await db.PasswordResetTokens.SingleAsync();
            Assert.NotEqual(rawToken, stored.TokenHash);
            Assert.Equal(TokenHashing.HashToken(rawToken), stored.TokenHash);
        }

        // 5. Wygasły token -> 400 ProblemDetails
        factory.EmailSink.Clear();
        var reqForExpiry = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = verifiedUser.UserLogin });
        Assert.Equal(HttpStatusCode.Accepted, reqForExpiry.StatusCode);
        var expiringToken = ExtractTokenFromActionUrl(Assert.Single(factory.EmailSink.GetMessages()).ActionUrl);

        var initialNow = factory.TimeProviderInstance.GetUtcNow();
        factory.TimeProviderInstance.Advance(TimeSpan.FromMinutes(31));
        var expiredConfirm = await client.PostAsJsonAsync("/api/auth/password-recovery/confirm", new
        {
            token = expiringToken,
            newPassword = "NewPassword999!"
        });
        await ReadProblemAsync(expiredConfirm, HttpStatusCode.BadRequest);
        factory.TimeProviderInstance.SetUtcNow(initialNow);

        // 6. Nowy ważny token: jednorazowy reset hasła działa, stare hasło przestaje działać, replay odpada
        factory.EmailSink.Clear();
        await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = verifiedUser.UserLogin });
        var freshToken = ExtractTokenFromActionUrl(Assert.Single(factory.EmailSink.GetMessages()).ActionUrl);

        var confirmOk = await client.PostAsJsonAsync("/api/auth/password-recovery/confirm", new
        {
            token = freshToken,
            newPassword = "NewVerifiedPassword123!"
        });
        confirmOk.EnsureSuccessStatusCode();

        // Replay tego samego tokenu -> 400
        var replayConfirm = await client.PostAsJsonAsync("/api/auth/password-recovery/confirm", new
        {
            token = freshToken,
            newPassword = "AnotherPassword123!"
        });
        await ReadProblemAsync(replayConfirm, HttpStatusCode.BadRequest);

        // Stare hasło nie działa, nowe działa
        var oldLogin = await client.PostAsJsonAsync("/api/auth/login", new { name = verifiedUser.UserLogin, password = "OldPassword123!" });
        await ReadProblemAsync(oldLogin, HttpStatusCode.Unauthorized);

        var newLogin = await client.PostAsJsonAsync("/api/auth/login", new { name = verifiedUser.UserLogin, password = "NewVerifiedPassword123!" });
        newLogin.EnsureSuccessStatusCode();

        // 7. Odzyskiwanie wspomagane przez administratora dla konta lokalnego bez zweryfikowanego e-maila
        var adminLogin = await (await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = adminUser.UserLogin,
            password = "AdminPass123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Authorize(client, adminLogin!.jwtSecurityToken);

        // Admin nie może wystawić resetu dla konta head_admin (403) ani LDAP (400)
        var resetHeadResp = await client.PostAsync($"/api/users/{headAdminUser.Id}/reset-password", null);
        await ReadProblemAsync(resetHeadResp, HttpStatusCode.Forbidden);

        var resetLdapResp = await client.PostAsync($"/api/users/{ldapUser.Id}/reset-password", null);
        await ReadProblemAsync(resetLdapResp, HttpStatusCode.BadRequest);

        var adminIssueResp = await client.PostAsync($"/api/users/{unverifiedUser.Id}/reset-password", null);
        adminIssueResp.EnsureSuccessStatusCode();
        var issued = await adminIssueResp.Content.ReadFromJsonAsync<AdminIssuedRecoveryDto>();
        Assert.NotNull(issued);
        Assert.False(string.IsNullOrWhiteSpace(issued.ResetToken));

        client.DefaultRequestHeaders.Authorization = null;
        var finishAssisted = await client.PostAsJsonAsync("/api/auth/password-recovery/confirm", new
        {
            token = issued.ResetToken,
            newPassword = "RecoveredByAdmin123!"
        });
        finishAssisted.EnsureSuccessStatusCode();

        var assistedLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = unverifiedUser.UserLogin,
            password = "RecoveredByAdmin123!"
        });
        assistedLogin.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AuthRateLimiting_Returns429ProblemDetailsWithRetryAfterWhenExceeded()
    {
        using var factory = new TestWebApplicationFactory
        {
            AuthLoginPermitLimit = 2,
            AuthRecoveryPermitLimit = 2
        };
        await factory.SeedRanksAsync();
        using var client = factory.CreateClient();

        for (int i = 0; i < 2; i++)
        {
            var attempt = await client.PostAsJsonAsync("/api/auth/login", new { name = "nobody", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        var limitedLogin = await client.PostAsJsonAsync("/api/auth/login", new { name = "nobody", password = "wrong" });
        await ReadProblemAsync(limitedLogin, HttpStatusCode.TooManyRequests);
        Assert.True(limitedLogin.Headers.TryGetValues("Retry-After", out _));

        for (int i = 0; i < 2; i++)
        {
            var req = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = "nobody" });
            Assert.Equal(HttpStatusCode.Accepted, req.StatusCode);
        }

        var limitedRecovery = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new { identifier = "nobody" });
        await ReadProblemAsync(limitedRecovery, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Sessions_RefreshRotatesToken_ReuseRevokesFamily_AndLogoutRevokesAccessAcrossProtectedRoutes()
    {
        using var factory = new TestWebApplicationFactory();
        await factory.SeedUserAsync("student1", "StrongPassword123!", rankId: 1);
        using var client = factory.CreateClient();

        var loginDto = await (await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = "student1",
            password = "StrongPassword123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(loginDto);
        Assert.False(string.IsNullOrWhiteSpace(loginDto.refreshToken));

        // 1. Rotacja refresh tokenu
        var refreshResp = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginDto.refreshToken });
        refreshResp.EnsureSuccessStatusCode();
        var rotated = await refreshResp.Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(rotated);
        Assert.NotEqual(loginDto.refreshToken, rotated.refreshToken);

        // 2. Ponowne użycie starego refresh tokenu -> 401 i unieważnienie całej rodziny tokenów!
        var reuseOld = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginDto.refreshToken });
        await ReadProblemAsync(reuseOld, HttpStatusCode.Unauthorized);

        var newAfterReuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = rotated.refreshToken });
        await ReadProblemAsync(newAfterReuse, HttpStatusCode.Unauthorized);

        // 3. Nowe logowanie i pojedynczy logout natychmiast unieważnia access token (jti) i refresh token
        var session2 = await (await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = "student1",
            password = "StrongPassword123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Authorize(client, session2!.jwtSecurityToken);

        var meBeforeLogout = await client.GetAsync("/api/auth/me");
        meBeforeLogout.EnsureSuccessStatusCode();

        var logoutResp = await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = session2.refreshToken });
        logoutResp.EnsureSuccessStatusCode();

        var meAfterLogout = await client.GetAsync("/api/auth/me");
        await ReadProblemAsync(meAfterLogout, HttpStatusCode.Unauthorized);

        var favAfterLogout = await client.GetAsync("/api/tracks/favourites");
        await ReadProblemAsync(favAfterLogout, HttpStatusCode.Unauthorized);

        // 4. Logout-all unieważnia wszystkie urządzenia przez token_version
        client.DefaultRequestHeaders.Authorization = null;
        var dev1 = await (await client.PostAsJsonAsync("/api/auth/login", new { name = "student1", password = "StrongPassword123!" }))
            .Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        var dev2 = await (await client.PostAsJsonAsync("/api/auth/login", new { name = "student1", password = "StrongPassword123!" }))
            .Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();

        Authorize(client, dev1!.jwtSecurityToken);
        (await client.PostAsync("/api/auth/logout-all", null)).EnsureSuccessStatusCode();

        Authorize(client, dev2!.jwtSecurityToken);
        await ReadProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = null;
        await ReadProblemAsync(await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = dev2.refreshToken }), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RoleDemotion_InvalidatesDemotedTokensEverywhereAndForcesReloginWithUpdatedRole()
    {
        using var factory = new TestWebApplicationFactory();
        var headAdmin = await factory.SeedUserAsync("head_boss", "HeadPass123!", rankId: 4);
        var moderator = await factory.SeedUserAsync("target_mod", "ModPass123!", rankId: 2);
        using var headClient = factory.CreateClient();
        using var targetClient = factory.CreateClient();

        // Logowanie head_admin oraz target_mod (w roli mod)
        var headLogin = await (await headClient.PostAsJsonAsync("/api/auth/login", new
        {
            name = headAdmin.UserLogin,
            password = "HeadPass123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Authorize(headClient, headLogin!.jwtSecurityToken);

        var modLogin = await (await targetClient.PostAsJsonAsync("/api/auth/login", new
        {
            name = moderator.UserLogin,
            password = "ModPass123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.Equal("mod", modLogin!.role);
        Authorize(targetClient, modLogin.jwtSecurityToken);

        // Weryfikacja przed degradacją -> dostęp do /api/moderation/pending działa
        (await targetClient.GetAsync("/api/moderation/pending")).EnsureSuccessStatusCode();

        // Head admin degraduje target_mod z mod (2) -> user (1)
        var demoteResp = await headClient.PutAsJsonAsync($"/api/users/{moderator.Id}/role", new UpdateUserRoleDto(1));
        demoteResp.EnsureSuccessStatusCode();

        // Zdegradowany stary access token zwraca 401 na wszystkich chronionych trasach
        HttpRequestMessage[] protectedRequests =
        [
            new(HttpMethod.Get, "/api/auth/me"),
            new(HttpMethod.Get, "/api/tracks/favourites"),
            new(HttpMethod.Post, "/api/queue/propose") { Content = JsonContent.Create(new { songId = 1 }) },
            new(HttpMethod.Get, "/api/moderation/pending"),
            new(HttpMethod.Get, "/api/users"),
            new(HttpMethod.Get, "/api/admin/tracks"),
            new(HttpMethod.Get, "/api/admin/default-songs"),
            new(HttpMethod.Get, "/api/admin/special-events"),
            new(HttpMethod.Get, "/api/breaks/templates"),
            new(HttpMethod.Get, "/api/breaks/exceptions"),
            new(HttpMethod.Post, "/api/player/pause"),
            new(HttpMethod.Get, "/api/statistics/dashboard"),
            new(HttpMethod.Get, "/api/spotify-playback/status"),
            new(HttpMethod.Put, "/api/schedule/weekly/1") { Content = JsonContent.Create(new { musicEnabled = true }) }
        ];

        foreach (var req in protectedRequests)
        {
            var resp = await targetClient.SendAsync(req);
            await ReadProblemAsync(resp, HttpStatusCode.Unauthorized);
        }

        // Również refresh token został unieważniony -> 401, wymuszenie ponownego logowania
        using var anonClient = factory.CreateClient();
        await ReadProblemAsync(
            await anonClient.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = modLogin.refreshToken }),
            HttpStatusCode.Unauthorized);

        // Po ponownym logowaniu nowa rola to "user": /api/auth/me działa (200), a trasy mod/admin -> 403 Forbidden
        var relogin = await (await anonClient.PostAsJsonAsync("/api/auth/login", new
        {
            name = moderator.UserLogin,
            password = "ModPass123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.Equal("user", relogin!.role);

        Authorize(targetClient, relogin.jwtSecurityToken);
        var meAfterRelogin = await targetClient.GetAsync("/api/auth/me");
        meAfterRelogin.EnsureSuccessStatusCode();
        var profile = await meAfterRelogin.Content.ReadFromJsonAsync<UsersController.UserProfileDto>();
        Assert.Equal("user", profile?.Role);

        await ReadProblemAsync(await targetClient.GetAsync("/api/moderation/pending"), HttpStatusCode.Forbidden);
        await ReadProblemAsync(await targetClient.GetAsync("/api/users"), HttpStatusCode.Forbidden);
        await ReadProblemAsync(await targetClient.GetAsync("/api/statistics/dashboard"), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Sessions_FamilyIdIsGuidAndPersistsAcrossRotationAndReuseRevocation()
    {
        using var factory = new TestWebApplicationFactory();
        var user = await factory.SeedUserAsync("guid_student", "StrongPassword123!", rankId: 1);
        using var client = factory.CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = user.UserLogin,
            password = "StrongPassword123!"
        });
        loginResp.EnsureSuccessStatusCode();
        var loginDto = await loginResp.Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(loginDto?.refreshToken);

        Guid originalFamilyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var initialToken = await db.RefreshTokens.SingleAsync(rt => rt.UserId == user.Id);
            Assert.NotEqual(Guid.Empty, initialToken.FamilyId);
            originalFamilyId = initialToken.FamilyId;
        }

        var refreshResp = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginDto.refreshToken });
        refreshResp.EnsureSuccessStatusCode();
        var rotatedDto = await refreshResp.Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(rotatedDto?.refreshToken);
        Assert.NotEqual(loginDto.refreshToken, rotatedDto.refreshToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var familyTokens = await db.RefreshTokens
                .Where(rt => rt.UserId == user.Id)
                .OrderBy(rt => rt.Id)
                .ToListAsync();
            Assert.Equal(2, familyTokens.Count);
            Assert.All(familyTokens, rt => Assert.Equal(originalFamilyId, rt.FamilyId));
            Assert.NotNull(familyTokens[0].RevokedAt);
            Assert.Equal(familyTokens[1].Id, familyTokens[0].ReplacedById);
            Assert.Null(familyTokens[1].RevokedAt);
        }

        var reuseResp = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = loginDto.refreshToken });
        await ReadProblemAsync(reuseResp, HttpStatusCode.Unauthorized);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<schoolmusicContext>();
            var familyTokens = await db.RefreshTokens.Where(rt => rt.FamilyId == originalFamilyId).ToListAsync();
            Assert.All(familyTokens, rt => Assert.NotNull(rt.RevokedAt));
        }
    }

    [Fact]
    public async Task AuthPages_VerifyEmailGetDoesNotConsumeToken_AndPostVerifiesEmail()
    {
        using var factory = new TestWebApplicationFactory
        {
            ExtraConfiguration = new Dictionary<string, string?>
            {
                ["Auth:MobileEmailVerificationUrlTemplate"] = "schoolmusic://verify-email?token={token}"
            }
        };
        await factory.SeedRanksAsync();
        using var client = factory.CreateClient();

        var registerResp = await client.PostAsJsonAsync("/api/auth/register", new
        {
            name = "html_verify_user",
            password = "StrongPassword123!",
            email = "html.verify@school.pl"
        });
        Assert.Equal(HttpStatusCode.Created, registerResp.StatusCode);

        var sentEmail = Assert.Single(factory.EmailSink.GetMessages());
        var token = ExtractTokenFromActionUrl(sentEmail.ActionUrl);

        // GET /verify-email nie może zwracać 401 ani zużywać tokenu (ochrona przed skanerami e-maili)
        var getPageResp = await client.GetAsync($"/verify-email?token={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.OK, getPageResp.StatusCode);
        Assert.Equal("text/html", getPageResp.Content.Headers.ContentType?.MediaType);
        Assert.True(getPageResp.Headers.CacheControl?.NoStore);
        var getHtml = await getPageResp.Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(token)}\"", getHtml);
        Assert.Contains($"schoolmusic://verify-email?token={Uri.EscapeDataString(token)}", getHtml);

        // POST /verify-email potwierdza weryfikację, a ponowne użycie kończy się 400 HTML
        var postVerifyResp = await client.PostAsync("/verify-email", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, postVerifyResp.StatusCode);
        Assert.Equal("text/html", postVerifyResp.Content.Headers.ContentType?.MediaType);
        var verifiedHtml = await postVerifyResp.Content.ReadAsStringAsync();
        Assert.Contains("html.verify@school.pl", verifiedHtml);

        var replayResp = await client.PostAsync("/verify-email", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token
        }));
        Assert.Equal(HttpStatusCode.BadRequest, replayResp.StatusCode);
        Assert.Equal("text/html", replayResp.Content.Headers.ContentType?.MediaType);

        // Po weryfikacji przez stronę HTML logowanie adresem e-mail działa
        var emailLoginResp = await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = "html.verify@school.pl",
            password = "StrongPassword123!"
        });
        emailLoginResp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AuthPages_ResetPasswordGetAndPost_ValidatesInputResetsPasswordAndBlocksUnsafeSchemes()
    {
        using var factory = new TestWebApplicationFactory
        {
            ExtraConfiguration = new Dictionary<string, string?>
            {
                ["Auth:MobilePasswordResetUrlTemplate"] = "javascript:alert(1)"
            }
        };
        var user = await factory.SeedUserAsync(
            "html_reset_user",
            "OldPassword123!",
            rankId: 1,
            email: "html.reset@school.pl",
            emailVerified: true);
        using var client = factory.CreateClient();

        // Zaloguj się przed resetem, aby zweryfikować późniejsze unieważnienie sesji
        var initialLogin = await (await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = user.UserLogin,
            password = "OldPassword123!"
        })).Content.ReadFromJsonAsync<UsersController.LoginResponseDto>();
        Assert.NotNull(initialLogin?.refreshToken);

        var reqResp = await client.PostAsJsonAsync("/api/auth/password-recovery/request", new
        {
            identifier = "html.reset@school.pl"
        });
        Assert.Equal(HttpStatusCode.Accepted, reqResp.StatusCode);

        var sentEmail = Assert.Single(factory.EmailSink.GetMessages());
        var token = ExtractTokenFromActionUrl(sentEmail.ActionUrl);

        // GET /reset-password jest publiczny (200 HTML), pokazuje token i blokuje niebezpieczny schemat javascript:
        var getResp = await client.GetAsync($"/reset-password?token={Uri.EscapeDataString(token)}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        Assert.Equal("text/html", getResp.Content.Headers.ContentType?.MediaType);
        var getHtml = await getResp.Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(token)}\"", getHtml);
        Assert.DoesNotContain("javascript:", getHtml, StringComparison.OrdinalIgnoreCase);

        // Niezgodne hasła -> 400 HTML z zachowanym tokenem, bez zużycia tokenu
        var mismatchResp = await client.PostAsync("/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["newPassword"] = "NewPassword123!",
            ["confirmPassword"] = "DifferentPassword123!"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, mismatchResp.StatusCode);
        var mismatchHtml = await mismatchResp.Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(token)}\"", mismatchHtml);
        Assert.DoesNotContain("NewPassword123!", mismatchHtml);

        // Poprawne wysłanie formularza zmienia hasło i unieważnia poprzedni refresh token
        var resetOkResp = await client.PostAsync("/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = token,
            ["newPassword"] = "BrandNewPass123!",
            ["confirmPassword"] = "BrandNewPass123!"
        }));
        Assert.Equal(HttpStatusCode.OK, resetOkResp.StatusCode);
        Assert.Equal("text/html", resetOkResp.Content.Headers.ContentType?.MediaType);

        await ReadProblemAsync(
            await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = initialLogin.refreshToken }),
            HttpStatusCode.Unauthorized);

        var newLoginResp = await client.PostAsJsonAsync("/api/auth/login", new
        {
            name = user.UserLogin,
            password = "BrandNewPass123!"
        });
        newLoginResp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task AuthPages_WithoutToken_Return200FormOnGetAnd400OnEmptyPost()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var verifyGet = await client.GetAsync("/verify-email");
        Assert.Equal(HttpStatusCode.OK, verifyGet.StatusCode);
        Assert.Equal("text/html", verifyGet.Content.Headers.ContentType?.MediaType);

        var resetGet = await client.GetAsync("/reset-password");
        Assert.Equal(HttpStatusCode.OK, resetGet.StatusCode);
        Assert.Equal("text/html", resetGet.Content.Headers.ContentType?.MediaType);

        var verifyPost = await client.PostAsync("/verify-email", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, verifyPost.StatusCode);

        var resetPost = await client.PostAsync("/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, resetPost.StatusCode);
    }
}
