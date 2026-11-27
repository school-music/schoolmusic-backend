using System.Net;
using System.Text.Json;

namespace schoolmusic_backend.Tests;

public sealed class OpenApiContractTests
{
    private static async Task<JsonDocument> FetchOpenApiDocumentAsync(TestWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    private static JsonElement ResolveSchema(JsonElement root, JsonElement schemaOrRef)
    {
        if (schemaOrRef.TryGetProperty("$ref", out var refProp))
        {
            var refPath = refProp.GetString()!;
            const string prefix = "#/components/schemas/";
            Assert.StartsWith(prefix, refPath);
            var schemaName = refPath[prefix.Length..];
            return root.GetProperty("components").GetProperty("schemas").GetProperty(schemaName);
        }

        return schemaOrRef;
    }

    private static JsonElement GetOperationResponse(JsonElement root, string path, string method, string statusCode)
    {
        return root
            .GetProperty("paths")
            .GetProperty(path)
            .GetProperty(method)
            .GetProperty("responses")
            .GetProperty(statusCode);
    }

    [Fact]
    public async Task OpenApiDocument_IncludesTypedSchemasForQueueProposeMultipartAndBodylessEndpoints()
    {
        using var factory = new TestWebApplicationFactory();
        using var doc = await FetchOpenApiDocumentAsync(factory);
        var root = doc.RootElement;

        // 1. POST /api/queue/propose & POST /api/queue -> rozróżnienie 201 Created i 202 Accepted z typowanymi schematami
        foreach (var proposePath in new[] { "/api/Queue", "/api/Queue/propose" })
        {
            // Obsługa wielkości liter ścieżek OpenAPI wygenerowanych z [controller]
            var matchingPath = root.GetProperty("paths").EnumerateObject()
                .FirstOrDefault(p => string.Equals(p.Name, proposePath, StringComparison.OrdinalIgnoreCase)).Name;
            Assert.NotNull(matchingPath);

            var resp201 = GetOperationResponse(root, matchingPath, "post", "201");
            var schema201 = ResolveSchema(root, resp201.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
            Assert.True(schema201.TryGetProperty("properties", out var props201));
            Assert.True(props201.TryGetProperty("queueItemId", out _));

            var resp202 = GetOperationResponse(root, matchingPath, "post", "202");
            var schema202 = ResolveSchema(root, resp202.GetProperty("content").GetProperty("application/json").GetProperty("schema"));
            Assert.True(schema202.TryGetProperty("properties", out var props202));
            Assert.True(props202.TryGetProperty("proposalId", out _));
        }

        // 2. POST /api/player/upload-and-play -> multipart/form-data binary file
        var uploadOp = root.GetProperty("paths").GetProperty("/api/player/upload-and-play").GetProperty("post");
        var content = uploadOp.GetProperty("requestBody").GetProperty("content");
        Assert.True(content.TryGetProperty("multipart/form-data", out var multipartContent), uploadOp.GetRawText());
        var multipartSchema = ResolveSchema(root, multipartContent.GetProperty("schema"));
        Assert.True(multipartSchema.TryGetProperty("properties", out var multipartProps), multipartSchema.GetRawText());
        var fileProp = multipartProps.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, "file", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrEmpty(fileProp.Name), multipartSchema.GetRawText());
        var fileSchema = ResolveSchema(root, fileProp.Value);
        Assert.True(fileSchema.TryGetProperty("type", out var fileTypeProp), fileProp.Value.GetRawText());
        Assert.Equal("string", fileTypeProp.GetString());
        Assert.True(fileSchema.TryGetProperty("format", out var fileFormatProp), fileProp.Value.GetRawText());
        Assert.Equal("binary", fileFormatProp.GetString());

        // 3. Bodyless endpoints (204 No Content & 302 Found bez payloadu JSON)
        Assert.False(GetOperationResponse(root, "/api/auth/logout", "post", "204").TryGetProperty("content", out _));
        Assert.False(GetOperationResponse(root, "/api/auth/logout-all", "post", "204").TryGetProperty("content", out _));
        Assert.False(GetOperationResponse(root, "/api/auth/delete-acc", "delete", "204").TryGetProperty("content", out _));
        Assert.False(GetOperationResponse(root, "/api/breaks/templates/{id}", "delete", "204").TryGetProperty("content", out _));
        // 4. Publiczne strony HTML dla linków z e-maili nie zaśmiecają kontraktu REST OpenAPI
        Assert.DoesNotContain(
            root.GetProperty("paths").EnumerateObject(),
            p => string.Equals(p.Name, "/verify-email", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.Name, "/reset-password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpenApiDocument_ExposesMetadataVotingTargetAuthFieldsAndProblemDetailsErrors()
    {
        using var factory = new TestWebApplicationFactory();
        using var doc = await FetchOpenApiDocumentAsync(factory);
        var root = doc.RootElement;

        // Voting target fields
        var vtPath = root.GetProperty("paths").EnumerateObject()
            .First(p => p.Name.EndsWith("/voting-target", StringComparison.OrdinalIgnoreCase)).Name;
        var vtSchema = ResolveSchema(root, GetOperationResponse(root, vtPath, "get", "200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var vtProps = vtSchema.GetProperty("properties");
        Assert.True(vtProps.TryGetProperty("eligibleBreakId", out _));
        Assert.True(vtProps.TryGetProperty("cutoffAt", out _));
        Assert.True(vtProps.TryGetProperty("timeZone", out _));
        Assert.True(vtProps.TryGetProperty("reasonCode", out _));

        // Queue GET fields
        var queuePath = root.GetProperty("paths").EnumerateObject()
            .First(p => string.Equals(p.Name, "/api/Queue", StringComparison.OrdinalIgnoreCase)).Name;
        var queueSchema = ResolveSchema(root, GetOperationResponse(root, queuePath, "get", "200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var qProps = queueSchema.GetProperty("properties");
        Assert.True(qProps.TryGetProperty("votingStatus", out _));
        Assert.True(qProps.TryGetProperty("userVotedQueueItemIds", out _));

        // Login response DTO fields (displayName, email, emailVerified, refreshToken)
        var loginSchema = ResolveSchema(root, GetOperationResponse(root, "/api/auth/login", "post", "200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var loginProps = loginSchema.GetProperty("properties");
        Assert.True(loginProps.TryGetProperty("displayName", out _));
        Assert.True(loginProps.TryGetProperty("email", out _));
        Assert.True(loginProps.TryGetProperty("emailVerified", out _));
        Assert.True(loginProps.TryGetProperty("refreshToken", out _));

        // Błędy 4xx/5xx powinny wskazywać na ProblemDetails z media type application/problem+json
        var login401 = GetOperationResponse(root, "/api/auth/login", "post", "401");
        var login401Content = login401.GetProperty("content");
        Assert.True(login401Content.TryGetProperty("application/problem+json", out var problemMedia));
        var problemSchema = ResolveSchema(root, problemMedia.GetProperty("schema"));
        var problemProps = problemSchema.GetProperty("properties");
        Assert.True(problemProps.TryGetProperty("detail", out _));
        Assert.True(problemProps.TryGetProperty("status", out _));
    }
}
