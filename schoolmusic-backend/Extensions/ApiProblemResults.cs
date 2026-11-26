using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace schoolmusic_backend.Extensions;

/// <summary>
/// Wymusza nagłówek <c>application/problem+json</c> dla odpowiedzi zawierających <see cref="ProblemDetails"/>
/// także na kontrolerach oznaczonych atrybutem <see cref="ProducesAttribute"/>.
/// </summary>
public sealed class ProblemDetailsContentTypeFilter : IAlwaysRunResultFilter, IOrderedFilter
{
    public int Order => int.MaxValue;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult objectResult && objectResult.Value is ProblemDetails)
        {
            objectResult.ContentTypes.Clear();
            objectResult.ContentTypes.Add(ApiProblemResults.MediaType);
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}

/// <summary>
/// Fabryka odpowiedzi błędów zgodnych ze standardem RFC 9457 (Problem Details, application/problem+json)
/// z przejściowym polem rozszerzenia <c>message</c> równym <c>detail</c>.
/// </summary>
public static class ApiProblemResults
{
    public const string MediaType = "application/problem+json";

    public static ProblemDetails BuildProblemDetails(
        HttpContext? httpContext,
        int statusCode,
        string detail,
        string? title = null,
        IDictionary<string, object?>? extensions = null)
    {
        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? ReasonPhrases.GetReasonPhrase(statusCode)
            : title;

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = string.IsNullOrWhiteSpace(resolvedTitle) ? "Error" : resolvedTitle,
            Detail = detail,
            Type = $"https://datatracker.ietf.org/doc/html/rfc9110#status.{statusCode}",
            Instance = httpContext?.Request?.Path.Value
        };

        problem.Extensions["message"] = detail;

        if (extensions != null)
        {
            foreach (var pair in extensions)
            {
                problem.Extensions[pair.Key] = pair.Value;
            }
        }

        return problem;
    }

    public static ObjectResult Create(
        HttpContext? httpContext,
        int statusCode,
        string detail,
        string? title = null,
        IDictionary<string, object?>? extensions = null)
    {
        var problem = BuildProblemDetails(httpContext, statusCode, detail, title, extensions);
        ObjectResult result = statusCode switch
        {
            StatusCodes.Status400BadRequest => new BadRequestObjectResult(problem),
            StatusCodes.Status401Unauthorized => new UnauthorizedObjectResult(problem),
            StatusCodes.Status404NotFound => new NotFoundObjectResult(problem),
            StatusCodes.Status409Conflict => new ConflictObjectResult(problem),
            _ => new ObjectResult(problem) { StatusCode = statusCode }
        };
        result.ContentTypes.Add(MediaType);
        return result;
    }

    public static ObjectResult Create(
        this ControllerBase controller,
        int statusCode,
        string detail,
        string? title = null,
        IDictionary<string, object?>? extensions = null) =>
        Create(controller.HttpContext, statusCode, detail, title, extensions);
}
