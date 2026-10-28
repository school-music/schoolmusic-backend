using System.Data.Common;
using StackExchange.Redis;

namespace schoolmusic_backend.Configuration;

/// <summary>
/// Waliduje ustawienia infrastruktury wymagane w środowisku produkcyjnym.
/// </summary>
public static class ProductionConfigurationValidator
{
    public static void Validate(IConfiguration configuration, IWebHostEnvironment environment)
    {
        if (!environment.IsProduction())
            return;

        var errors = new List<string>();
        ValidateDatabase(configuration, errors);
        ValidateRedis(configuration, errors);

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Nieprawidłowa konfiguracja środowiska produkcyjnego:" +
                Environment.NewLine + "- " + string.Join(Environment.NewLine + "- ", errors));
        }
    }

    private static void ValidateDatabase(IConfiguration configuration, List<string> errors)
    {
        var connectionString = configuration.GetConnectionString("database")
            ?? configuration.GetSection("ConnectionString")["database"];

        if (string.IsNullOrWhiteSpace(connectionString) ||
            connectionString == "DATABASE_CONNECTION_STRING_HERE")
        {
            errors.Add("Połączenie do bazy danych nie zostało skonfigurowane.");
            return;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var user = GetValue(builder, "user", "user id", "uid", "username");
            var password = GetValue(builder, "password", "pwd");

            if (string.Equals(user, "root", StringComparison.OrdinalIgnoreCase))
                errors.Add("Aplikacja produkcyjna nie może łączyć się z bazą danych jako użytkownik root.");
            if (string.IsNullOrWhiteSpace(password) ||
                string.Equals(password, "secret", StringComparison.OrdinalIgnoreCase))
                errors.Add("Hasło bazy danych jest brakujące lub używa znanej wartości domyślnej.");
        }
        catch (ArgumentException)
        {
            errors.Add("Połączenie do bazy danych ma nieprawidłowy format.");
        }
    }

    private static void ValidateRedis(IConfiguration configuration, List<string> errors)
    {
        var connectionString = configuration.GetConnectionString("redis")
            ?? configuration.GetSection("ConnectionString")["redis"];

        if (string.IsNullOrWhiteSpace(connectionString) ||
            connectionString == "REDIS_CONNECTION_STRING_HERE")
        {
            errors.Add("Połączenie do Redis nie zostało skonfigurowane.");
            return;
        }

        try
        {
            var options = ConfigurationOptions.Parse(connectionString);
            if (string.IsNullOrWhiteSpace(options.Password))
                errors.Add("Redis w środowisku produkcyjnym musi wymagać hasła.");
        }
        catch (ArgumentException)
        {
            errors.Add("Połączenie do Redis ma nieprawidłowy format.");
        }
    }

    private static string? GetValue(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value))
                return value?.ToString();
        }

        return null;
    }
}
