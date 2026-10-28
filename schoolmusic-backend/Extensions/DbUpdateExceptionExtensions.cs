using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace schoolmusic_backend.Extensions;

/// <summary>
/// Pomocnicze rozpoznawanie błędów integralności zwracanych przez MariaDB.
/// </summary>
public static class DbUpdateExceptionExtensions
{
    public static bool IsUniqueConstraintViolation(this DbUpdateException exception) =>
        FindMySqlException(exception) is { Number: 1062 };

    private static MySqlException? FindMySqlException(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is MySqlException mySqlException)
                return mySqlException;
        }

        return null;
    }
}
