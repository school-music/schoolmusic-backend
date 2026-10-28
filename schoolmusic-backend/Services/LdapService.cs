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
    private readonly IWebHostEnvironment _environment;

    public LdapService(IConfiguration configuration, ILogger<LdapService> logger, IWebHostEnvironment environment)
    {
        _configuration = configuration;
        _logger = logger;
        _environment = environment;
    }

    public bool IsEnabled => _configuration.GetValue<bool>("Ldap:Enabled", true);

    public bool IsSimulatedMode => _configuration.GetValue<bool>("Ldap:SimulatedMode", false);

    /// <summary>
    /// Waliduje konfigurację LDAP przy starcie aplikacji. Rzuca InvalidOperationException gdy konfiguracja jest niepoprawna.
    /// </summary>
    public void ValidateConfiguration()
    {
        // Tryb symulacyjny nie może działać poza środowiskiem deweloperskim
        if (IsSimulatedMode && !_environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "BŁĄD KRYTYCZNY: Ldap:SimulatedMode=true jest dozwolony wyłącznie w środowisku Development. " +
                "Wyłącz tryb symulacyjny w konfiguracji produkcyjnej.");
        }

        if (!IsEnabled)
        {
            _logger.LogInformation("Usługa LDAP jest wyłączona (Ldap:Enabled=false).");
            return;
        }

        if (IsSimulatedMode)
        {
            _logger.LogWarning(
                "UWAGA: Usługa LDAP działa w trybie SYMULACYJNYM (Ldap:SimulatedMode=true). " +
                "Nie używaj tego trybu poza środowiskiem deweloperskim.");
            return;
        }

        // W trybie produkcyjnym sprawdź niezbędne ustawienia
        var server = _configuration["Ldap:Server"];
        if (string.IsNullOrWhiteSpace(server))
        {
            _logger.LogWarning(
                "Ldap:Server nie jest skonfigurowany. Ustawienie wartości domyślnej 'localhost'. " +
                "Skonfiguruj poprawny adres serwera LDAP.");
        }

        var useSsl = _configuration.GetValue<bool>("Ldap:UseSsl", false);
        if (!useSsl)
        {
            _logger.LogWarning(
                "Ldap:UseSsl=false. Hasła użytkowników będą przesyłane niezaszyfrowane. " +
                "Zalecane jest włączenie LDAPS lub StartTLS przed wdrożeniem produkcyjnym.");
        }
    }

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
        _logger.LogInformation("Rozpoczęto uwierzytelnianie w trybie symulacyjnym LDAP (Demo).");

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
                    var configuredGroups = userSec.GetSection("Groups").Get<string[]>() ?? new[] { "Uczniowie" };
                    var user = new LdapUser(
                        Username: normalizedLogin,
                        DisplayName: userSec["DisplayName"] ?? normalizedLogin,
                        Email: userSec["Email"] ?? $"{normalizedLogin}@szkola.edu.pl",
                        Groups: configuredGroups
                    );

                    // Sprawdzenie wymaganej grupy jeśli skonfigurowana
                    var requiredGroup = _configuration["Ldap:RequiredGroup"];
                    if (!string.IsNullOrWhiteSpace(requiredGroup))
                    {
                        bool inGroup = user.Groups.Any(g => string.Equals(g, requiredGroup, StringComparison.OrdinalIgnoreCase));
                        if (!inGroup)
                        {
                            _logger.LogWarning("Symulowane konto LDAP nie należy do wymaganej grupy '{RequiredGroup}'.", requiredGroup);
                            return LdapAuthResult.Fail("Konto szkolne nie posiada uprawnień do korzystania z systemu radiowęzła.");
                        }
                    }

                    return LdapAuthResult.Ok(user);
                }

                return LdapAuthResult.Fail("Niepoprawne hasło konta szkolnego.");
            }
        }

        // W trybie demo akceptujemy wyłącznie użytkowników z listy SimulatedUsers.
        // Nie stosujemy fallbacku na podstawie wzorca hasła – zapobiega to przypadkowemu
        // ominięciu listy testowej i poprawia bezpieczeństwo.
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

            _logger.LogInformation("Wykonywanie operacji LDAP Bind.");
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
                    // Escapowanie wartości loginu w filtrze LDAP - ochrona przed LDAP injection
                    var escapedLogin = EscapeLdapFilterValue(normalizedLogin);
                    var filter = string.Format(userSearchFilter, escapedLogin);

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
                    _logger.LogWarning(searchEx, "Nie udało się pobrać szczegółowych atrybutów konta z LDAP.");
                }
            }

            connection.Disconnect();

            var ldapUser = new LdapUser(normalizedLogin, displayName, email, groups);

            // Sprawdzenie przynależności do wymaganej grupy
            var requiredGroup = _configuration["Ldap:RequiredGroup"];
            if (!string.IsNullOrWhiteSpace(requiredGroup))
            {
                bool inGroup = ldapUser.Groups.Any(g =>
                    string.Equals(g, requiredGroup, StringComparison.OrdinalIgnoreCase) ||
                    g.Contains($"CN={requiredGroup}", StringComparison.OrdinalIgnoreCase));

                if (!inGroup)
                {
                    _logger.LogWarning("Konto LDAP nie należy do wymaganej grupy '{RequiredGroup}'.", requiredGroup);
                    return LdapAuthResult.Fail("Konto szkolne nie posiada uprawnień do korzystania z systemu radiowęzła.");
                }
            }

            return LdapAuthResult.Ok(ldapUser);
        }
        catch (LdapException ldapEx)
        {
            _logger.LogWarning("Błąd uwierzytelniania LDAP: kod={ResultCode}.", ldapEx.ResultCode);

            if (ldapEx.ResultCode == LdapException.InvalidCredentials)
            {
                return LdapAuthResult.Fail("Błędny login lub hasło konta szkolnego.");
            }

            // Nie zwracamy szczegółów błędu LDAP do klienta
            return LdapAuthResult.Fail("Błąd komunikacji z serwerem kont szkolnych.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nieoczekiwany wyjątek podczas komunikacji z serwerem LDAP.");
            return LdapAuthResult.Fail("Błąd połączenia ze szkolnym serwerem kont.");
        }
    }

    /// <summary>
    /// Escapuje znaki specjalne w wartości filtra LDAP zgodnie z RFC 4515 (ochrona przed LDAP injection).
    /// Znaki: \ * ( ) \0 / wymagają escapowania.
    /// </summary>
    public static string EscapeLdapFilterValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value
            .Replace("\\", "\\5c")
            .Replace("*",  "\\2a")
            .Replace("(",  "\\28")
            .Replace(")",  "\\29")
            .Replace("\0", "\\00")
            .Replace("/",  "\\2f");
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
