using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Models;

namespace schoolmusic_backend.Services;

public sealed record IssuedAccessToken(string Token, DateTime ExpiresAtUtc, string Jti);

/// <summary>
/// Tworzy podpisane tokeny JWT dla użytkowników aplikacji.
/// </summary>
public interface IJwtTokenService
{
    string CreateToken(User user, bool isLdap);
    IssuedAccessToken IssueAccessToken(User user, bool isLdap);
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider? timeProvider = null) : IJwtTokenService
{
    public const string TokenVersionClaimType = "tv";
    public const string AuthMethodClaimType = "auth_method";

    private readonly JwtOptions _options = options.Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string CreateToken(User user, bool isLdap) =>
        IssueAccessToken(user, isLdap).Token;

    public IssuedAccessToken IssueAccessToken(User user, bool isLdap)
    {
        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAtUtc = utcNow.Add(_options.GetAccessTokenLifetime());
        var jti = Guid.NewGuid().ToString();
        var key = Encoding.UTF8.GetBytes(_options.Key);
        var credentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserLogin),
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim(ClaimTypes.Role, user.Rank?.Rank1 ?? "user"),
            new Claim(AuthMethodClaimType, isLdap ? "ldap" : "password"),
            new Claim(TokenVersionClaimType, user.TokenVersion.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: utcNow,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        return new IssuedAccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc, jti);
    }
}
