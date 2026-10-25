using System.Text.RegularExpressions;
using Novell.Directory.Ldap;

namespace schoolmusic_backend.Services;

/// <summary>
/// Informacje o użytkowniku pobrane ze szkolnego katalogu LDAP / Active Directory.
/// </summary>
public record LdapUser(
    string Username,
    string? DisplayName,
    string? Email,
    IReadOnlyList<string> Groups
);

/// <summary>
/// Wynik operacji uwierzytelniania w katalogu LDAP.
/// </summary>
public record LdapAuthResult(
    bool Success,
    string? ErrorMessage,
    LdapUser? User
)
{
    public static LdapAuthResult Ok(LdapUser user) => new(true, null, user);
    public static LdapAuthResult Fail(string message) => new(false, message, null);
}

/// <summary>
/// Interfejs usługi autoryzacji przez szkolne konto LDAP / Active Directory.
/// </summary>
public interface ILdapService
{
    /// <summary>
    /// Czy obsługa LDAP jest włączona w konfiguracji.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Czy usługa działa w trybie symulacyjnym (demo/testy).
    /// </summary>
    bool IsSimulatedMode { get; }

    /// <summary>
    /// Uwierzytelnia użytkownika na podstawie loginu szkolnego (np. imie.nazwisko) i hasła.
    /// </summary>
    Task<LdapAuthResult> AuthenticateAsync(string login, string password, CancellationToken cancellationToken = default);
}

/// <summary>
/// Domyślna implementacja usługi LDAP dla szkolnych kont z obsługą Novell LDAP oraz trybu demonstracyjnego.
/// </summary>
public class LdapService : ILdapService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LdapService> _logger;

    public LdapService(IConfiguration configuration, ILogger<LdapService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsEnabled => _configuration.GetValue<bool>("Ldap:Enabled", true);

    public bool IsSimulatedMode => _configuration.GetValue<bool>("Ldap:SimulatedMode", false);

    public async Task<LdapAuthResult> AuthenticateAsync(string login, string password, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return LdapAuthResult.Fail("Logowanie przez szkolny katalog LDAP jest obecnie wyłączone.");
        }

        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            return LdapAuthResult.Fail("Login i hasło są wymagane.");
        }

        var normalizedLogin = NormalizeLogin(login);

        if (IsSimulatedMode)
        {
            return AuthenticateSimulated(normalizedLogin, password);
        }

        return await AuthenticateRealLdapAsync(normalizedLogin, password, cancellationToken);
    }

    private LdapAuthResult AuthenticateSimulated(string normalizedLogin, string password)
    {
        _logger.LogInformation("Uwierzytelnianie konta '{Login}' w trybie symulacyjnym LDAP (Demo).", normalizedLogin);

        var simulatedUsersSection = _configuration.GetSection("Ldap:SimulatedUsers");
        var users = simulatedUsersSection.GetChildren();

        foreach (var userSec in users)
        {
            var confLogin = NormalizeLogin(userSec["Login"] ?? "");
            if (string.Equals(confLogin, normalizedLogin, StringComparison.OrdinalIgnoreCase))
            {
                var confPass = userSec["Password"] ?? "";
                if (confPass == password)
                {
                    var user = new LdapUser(
                        Username: normalizedLogin,
                        DisplayName: userSec["DisplayName"] ?? normalizedLogin,
                        Email: userSec["Email"] ?? $"{normalizedLogin}@szkola.edu.pl",
                        Groups: new[] { "Uczniowie" }
                    );
                    return LdapAuthResult.Ok(user);
                }

                return LdapAuthResult.Fail("Niepoprawne hasło konta szkolnego.");
            }
        }

        // Jeżeli w trybie demo podano login o formacie imie.nazwisko i hasło demo
        if (Regex.IsMatch(normalizedLogin, @"^[a-zA-Z0-9_\.\-]+$") && password.Length >= 6 && password.EndsWith("!"))
        {
            var user = new LdapUser(
                Username: normalizedLogin,
                DisplayName: normalizedLogin,
                Email: $"{normalizedLogin}@szkola.edu.pl",
                Groups: new[] { "Uczniowie" }
            );
            return LdapAuthResult.Ok(user);
        }

        return LdapAuthResult.Fail("Nie znaleziono użytkownika w szkolnym katalogu kont.");
    }

    private async Task<LdapAuthResult> AuthenticateRealLdapAsync(string normalizedLogin, string password, CancellationToken cancellationToken)
    {
        var server = _configuration["Ldap:Server"] ?? "localhost";
        var port = _configuration.GetValue<int>("Ldap:Port", 389);
        var useSsl = _configuration.GetValue<bool>("Ldap:UseSsl", false);
        var domain = _configuration["Ldap:Domain"];
        var searchBase = _configuration["Ldap:SearchBase"];
        var userSearchFilter = _configuration["Ldap:UserSearchFilter"] ?? "(|(sAMAccountName={0})(userPrincipalName={0})(mail={0}))";

        try
        {
            var options = new LdapConnectionOptions();
            if (useSsl)
            {
                options.UseSsl();
            }

            using var connection = new LdapConnection(options);
            connection.ConnectionTimeout = 5000;

            _logger.LogInformation("Łączenie z serwerem LDAP {Server}:{Port} (SSL: {Ssl})...", server, port, useSsl);
            await connection.ConnectAsync(server, port, cancellationToken);

            // Przygotowanie User Principal Name lub bind DN
            string bindUser;
            if (!string.IsNullOrWhiteSpace(domain))
            {
                bindUser = normalizedLogin.Contains('@') ? normalizedLogin : $"{normalizedLogin}@{domain}";
            }
            else
            {
                bindUser = normalizedLogin;
            }

            _logger.LogInformation("Wykonywanie LDAP Bind dla '{BindUser}'...", bindUser);
            await connection.BindAsync(bindUser, password, cancellationToken);

            if (!connection.Connected)
            {
                return LdapAuthResult.Fail("Błędny login lub hasło konta szkolnego.");
            }

            string? displayName = normalizedLogin;
            string? email = normalizedLogin.Contains('@') ? normalizedLogin : $"{normalizedLogin}@{domain ?? "szkola.edu.pl"}";
            var groups = new List<string>();

            // Opcjonalne wyszukanie dodatkowych atrybutów jeśli podano SearchBase
            if (!string.IsNullOrWhiteSpace(searchBase))
            {
                try
                {
                    var filter = string.Format(userSearchFilter, normalizedLogin);
                    var searchResults = await connection.SearchAsync(
                        searchBase,
                        LdapConnection.ScopeSub,
                        filter,
                        new[] { "displayName", "mail", "memberOf" },
                        false,
                        cancellationToken
                    );

                    if (await searchResults.HasMoreAsync(cancellationToken))
                    {
                        var entry = await searchResults.NextAsync(cancellationToken);
                        displayName = entry.GetStringValueOrDefault("displayName", normalizedLogin);
                        email = entry.GetStringValueOrDefault("mail", $"{normalizedLogin}@{domain ?? "szkola.edu.pl"}");

                        var memberOfAttr = entry.GetOrDefault("memberOf", null!);
                        if (memberOfAttr != null)
                        {
                            foreach (var g in memberOfAttr.StringValues)
                            {
                                groups.Add(g);
                            }
                        }
                    }
                }
                catch (Exception searchEx)
                {
                    _logger.LogWarning(searchEx, "Nie udało się pobrać szczegółowych atrybutów użytkownika z LDAP: {Message}", searchEx.Message);
                }
            }

            connection.Disconnect();

            var ldapUser = new LdapUser(normalizedLogin, displayName, email, groups);
            return LdapAuthResult.Ok(ldapUser);
        }
        catch (LdapException ldapEx)
        {
            _logger.LogWarning("Błąd uwierzytelniania LDAP dla '{Login}': kod={ResultCode}, {Message}",
                normalizedLogin, ldapEx.ResultCode, ldapEx.Message);

            if (ldapEx.ResultCode == LdapException.InvalidCredentials)
            {
                return LdapAuthResult.Fail("Błędny login lub hasło konta szkolnego.");
            }

            return LdapAuthResult.Fail($"Błąd serwera katalogowego LDAP: {ldapEx.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nieoczekiwany wyjątek podczas komunikacji z serwerem LDAP: {Message}", ex.Message);
            return LdapAuthResult.Fail("Błąd połączenia ze szkolnym serwerem kont.");
        }
    }

    private static string NormalizeLogin(string login)
    {
        var trimmed = login.Trim();

        // Jeśli login to DOMENA\login
        if (trimmed.Contains('\\'))
        {
            var parts = trimmed.Split('\\', 2);
            return parts[1].Trim();
        }

        // Jeśli login to email z domeną (np. jan.kowalski@szkola.edu.pl)
        if (trimmed.Contains('@'))
        {
            var parts = trimmed.Split('@', 2);
            return parts[0].Trim();
        }

        return trimmed;
    }
}
