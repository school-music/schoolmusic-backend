using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using schoolmusic_backend.Controllers;

namespace schoolmusic_backend.Tests;

/// <summary>
/// Pilnuje, aby każda końcówka kontrolera miała jawną deklarację dostępu.
/// Domyślna polityka uwierzytelniania (fallback policy) chroni wszystko, co nie jest
/// oznaczone jako publiczne, więc brak atrybutu nie może po cichu odebrać dostępu
/// końcówkom przeznaczonym dla niezalogowanych użytkowników.
/// </summary>
public sealed class AuthorizationMetadataTests
{
    private static readonly HashSet<string> ExpectedPublicEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET api/breaks",
        "GET api/breaks/current-status",
        "GET api/player/status",
        "GET api/queue",
        "GET api/queue/break/{breakId:int}",
        "GET api/queue/break/{breakId:int}/plan",
        "GET api/queue/now-playing",
        "GET api/rankings/artists",
        "GET api/rankings/history",
        "GET api/rankings/songs",
        "GET api/schedule/calendar",
        "GET api/schedule/weekly",
        "GET api/schedule/weekly/{weekday:int}/breaks",
        "GET api/spotify-playback/callback",
        "GET api/system/status",
        "GET api/tracks",
        "GET api/tracks/genres",
        "GET api/tracks/search",
        "GET api/tracks/{id:int}",
        "POST api/auth/ldap/login",
        "POST api/auth/login",
        "POST api/auth/register",
        "POST api/auth/school/login"
    };

    [Fact]
    public void PublicEndpoints_AreExplicitlyMarkedAsAnonymous()
    {
        var actual = GetEndpoints()
            .Where(endpoint => IsAnonymous(endpoint.Metadata))
            .Select(endpoint => endpoint.Signature)
            .OrderBy(signature => signature, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expected = ExpectedPublicEndpoints
            .OrderBy(signature => signature, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryEndpoint_HasExplicitAuthorizationMetadata()
    {
        var missing = GetEndpoints()
            .Where(endpoint => !IsAnonymous(endpoint.Metadata) && !HasAuthorize(endpoint.Metadata))
            .Select(endpoint => endpoint.Signature)
            .OrderBy(signature => signature, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(missing);
    }

    private static IEnumerable<EndpointMetadata> GetEndpoints()
    {
        var controllers = typeof(UsersController).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type));

        foreach (var controller in controllers)
        {
            var controllerRoute = controller.GetCustomAttribute<RouteAttribute>()?.Template;
            var controllerAttributes = controller.GetCustomAttributes(inherit: true);

            var actions = controller
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName);

            foreach (var action in actions)
            {
                var httpAttributes = action.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToArray();
                if (httpAttributes.Length == 0)
                {
                    continue;
                }

                var metadata = controllerAttributes
                    .Concat(action.GetCustomAttributes(inherit: true))
                    .ToArray();

                foreach (var httpAttribute in httpAttributes)
                {
                    var route = CombineRoute(controller, controllerRoute, httpAttribute.Template);
                    foreach (var method in httpAttribute.HttpMethods)
                    {
                        yield return new EndpointMetadata($"{method.ToUpperInvariant()} {route}", metadata);
                    }
                }
            }
        }
    }

    private static string CombineRoute(Type controller, string? controllerRoute, string? actionRoute)
    {
        var resolvedControllerRoute = controllerRoute?.Replace(
            "[controller]",
            controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? controller.Name[..^"Controller".Length]
                : controller.Name,
            StringComparison.OrdinalIgnoreCase);

        return string.Join(
            "/",
            new[] { resolvedControllerRoute, actionRoute }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part!.Trim('/')));
    }

    private static bool IsAnonymous(IReadOnlyList<object> metadata) =>
        metadata.OfType<IAllowAnonymous>().Any();

    private static bool HasAuthorize(IReadOnlyList<object> metadata) =>
        metadata.OfType<IAuthorizeData>().Any();

    private sealed record EndpointMetadata(string Signature, IReadOnlyList<object> Metadata);
}
