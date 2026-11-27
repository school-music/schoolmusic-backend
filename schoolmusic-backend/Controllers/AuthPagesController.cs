using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using schoolmusic_backend.Configuration;
using schoolmusic_backend.Services;

namespace schoolmusic_backend.Controllers;

/// <summary>
/// Publiczne strony HTML obsługujące linki wysyłane w wiadomościach e-mail
/// (weryfikacja adresu e-mail i reset hasła) z formularzem w przeglądarce,
/// podglądem tokenu oraz opcjonalnym przyciskiem głębokiego linku do aplikacji mobilnej.
/// </summary>
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class AuthPagesController(
    IOptions<AuthOptions> authOptions,
    IEmailVerificationService emailVerificationService,
    IPasswordRecoveryService passwordRecoveryService) : ControllerBase
{
    private readonly AuthOptions _authOptions = authOptions.Value;

    /// <summary>
    /// Wyświetla stronę potwierdzenia weryfikacji adresu e-mail bez zużywania jednorazowego tokenu w żądaniu GET.
    /// </summary>
    [HttpGet("/verify-email")]
    public IActionResult VerifyEmailPage([FromQuery] string? token = null)
    {
        var trimmedToken = token?.Trim() ?? string.Empty;
        var notice = string.IsNullOrEmpty(trimmedToken)
            ? "Brak tokenu weryfikacyjnego w adresie URL. Wklej token poniżej lub wyślij link ponownie w aplikacji."
            : null;

        return HtmlResult(
            StatusCodes.Status200OK,
            RenderVerifyEmailForm(trimmedToken, notice, isError: false));
    }

    /// <summary>
    /// Realizuje jednorazową weryfikację adresu e-mail z formularza WWW.
    /// </summary>
    [HttpPost("/verify-email")]
    [EnableRateLimiting("auth-recovery")]
    public async Task<IActionResult> VerifyEmailSubmit(
        [FromForm] string? token = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedToken = token?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedToken))
        {
            return HtmlResult(
                StatusCodes.Status400BadRequest,
                RenderVerifyEmailForm(string.Empty, "Token weryfikacyjny jest wymagany.", isError: true));
        }

        var result = await emailVerificationService.VerifyTokenAsync(trimmedToken, cancellationToken);
        if (!result.Success || result.Data == null)
        {
            return HtmlResult(
                result.StatusCode,
                RenderVerifyEmailForm(
                    trimmedToken,
                    result.Message ?? "Nie udało się zweryfikować adresu e-mail.",
                    isError: true));
        }

        var verifiedEmail = WebUtility.HtmlEncode(result.Data.Email);
        var message = WebUtility.HtmlEncode(result.Data.Message);
        var body = new StringBuilder()
            .Append("<div class=\"alert alert-success\">")
            .Append(message)
            .Append("</div>")
            .Append("<p class=\"muted\">Zweryfikowany adres: <strong>")
            .Append(verifiedEmail)
            .Append("</strong></p>")
            .Append("<p class=\"muted\">Możesz teraz wrócić do aplikacji SchoolMusic i zalogować się tym adresem e-mail.</p>")
            .ToString();

        return HtmlResult(
            StatusCodes.Status200OK,
            RenderDocument("Weryfikacja adresu e-mail", "Adres e-mail zweryfikowany", body));
    }

    /// <summary>
    /// Wyświetla formularz ustawienia nowego hasła dla jednorazowego tokenu z wiadomości e-mail.
    /// </summary>
    [HttpGet("/reset-password")]
    public IActionResult ResetPasswordPage([FromQuery] string? token = null)
    {
        var trimmedToken = token?.Trim() ?? string.Empty;
        var notice = string.IsNullOrEmpty(trimmedToken)
            ? "Brak tokenu resetu hasła w adresie URL. Wklej token poniżej lub wygeneruj nowy link w aplikacji."
            : null;

        return HtmlResult(
            StatusCodes.Status200OK,
            RenderResetPasswordForm(trimmedToken, notice, isError: false));
    }

    /// <summary>
    /// Zapisuje nowe hasło użytkownika na podstawie formularza WWW i unieważnia poprzednie sesje.
    /// </summary>
    [HttpPost("/reset-password")]
    [EnableRateLimiting("auth-recovery")]
    public async Task<IActionResult> ResetPasswordSubmit(
        [FromForm] string? token = null,
        [FromForm] string? newPassword = null,
        [FromForm] string? confirmPassword = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedToken = token?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmedToken))
        {
            return HtmlResult(
                StatusCodes.Status400BadRequest,
                RenderResetPasswordForm(string.Empty, "Jednorazowy token resetu hasła jest wymagany.", isError: true));
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return HtmlResult(
                StatusCodes.Status400BadRequest,
                RenderResetPasswordForm(trimmedToken, "Podane hasła nie są identyczne.", isError: true));
        }

        var result = await passwordRecoveryService.ConfirmRecoveryAsync(
            trimmedToken,
            newPassword ?? string.Empty,
            cancellationToken);

        if (!result.Success)
        {
            return HtmlResult(
                result.StatusCode,
                RenderResetPasswordForm(
                    trimmedToken,
                    result.Message ?? "Nie udało się zresetować hasła.",
                    isError: true));
        }

        var message = WebUtility.HtmlEncode(result.Data ?? "Hasło zostało pomyślnie zmienione. Zaloguj się nowym hasłem.");
        var body = new StringBuilder()
            .Append("<div class=\"alert alert-success\">")
            .Append(message)
            .Append("</div>")
            .Append("<p class=\"muted\">Wszystkie wcześniejsze sesje zostały wylogowane ze względów bezpieczeństwa. Wróć do aplikacji SchoolMusic i zaloguj się nowym hasłem.</p>")
            .ToString();

        return HtmlResult(
            StatusCodes.Status200OK,
            RenderDocument("Reset hasła", "Hasło zostało zmienione", body));
    }

    private string RenderVerifyEmailForm(string token, string? statusMessage, bool isError)
    {
        var body = new StringBuilder();
        AppendAlert(body, statusMessage, isError);

        body.Append("<p class=\"muted\">Kliknij przycisk poniżej, aby potwierdzić przypisanie adresu e-mail do konta SchoolMusic.</p>");
        body.Append("<form method=\"post\" action=\"/verify-email\">");
        AppendTokenInputOrHidden(body, token, "Token weryfikacyjny");
        body.Append("<button type=\"submit\" class=\"btn-primary\">Potwierdź adres e-mail</button>");
        body.Append("</form>");

        AppendMobileSection(
            body,
            token,
            _authOptions.BuildMobileEmailVerificationUrl(token),
            "Token weryfikacyjny");

        return RenderDocument("Weryfikacja adresu e-mail", "Potwierdź adres e-mail", body.ToString());
    }

    private string RenderResetPasswordForm(string token, string? statusMessage, bool isError)
    {
        var body = new StringBuilder();
        AppendAlert(body, statusMessage, isError);

        body.Append("<p class=\"muted\">Ustaw nowe hasło do swojego lokalnego konta SchoolMusic (minimum 8 znaków).</p>");
        body.Append("<form method=\"post\" action=\"/reset-password\">");
        AppendTokenInputOrHidden(body, token, "Token resetu hasła");
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"newPassword\">Nowe hasło</label>");
        body.Append("<input id=\"newPassword\" name=\"newPassword\" type=\"password\" minlength=\"8\" required autocomplete=\"new-password\" />");
        body.Append("</div>");
        body.Append("<div class=\"field\">");
        body.Append("<label for=\"confirmPassword\">Powtórz nowe hasło</label>");
        body.Append("<input id=\"confirmPassword\" name=\"confirmPassword\" type=\"password\" minlength=\"8\" required autocomplete=\"new-password\" />");
        body.Append("</div>");
        body.Append("<button type=\"submit\" class=\"btn-primary\">Ustaw nowe hasło</button>");
        body.Append("</form>");

        AppendMobileSection(
            body,
            token,
            _authOptions.BuildMobilePasswordResetUrl(token),
            "Token resetu hasła");

        return RenderDocument("Reset hasła", "Ustaw nowe hasło", body.ToString());
    }

    private static void AppendAlert(StringBuilder body, string? message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var cssClass = isError ? "alert alert-error" : "alert alert-info";
        body.Append("<div class=\"")
            .Append(cssClass)
            .Append("\">")
            .Append(WebUtility.HtmlEncode(message))
            .Append("</div>");
    }

    private static void AppendTokenInputOrHidden(StringBuilder body, string token, string label)
    {
        var encoded = WebUtility.HtmlEncode(token);
        if (string.IsNullOrEmpty(token))
        {
            body.Append("<div class=\"field\">");
            body.Append("<label for=\"token\">").Append(WebUtility.HtmlEncode(label)).Append("</label>");
            body.Append("<input id=\"token\" name=\"token\" type=\"text\" required autocomplete=\"off\" placeholder=\"Wklej token z wiadomości e-mail\" />");
            body.Append("</div>");
        }
        else
        {
            body.Append("<input type=\"hidden\" name=\"token\" value=\"").Append(encoded).Append("\" />");
        }
    }

    private static void AppendMobileSection(StringBuilder body, string token, string? mobileUrl, string tokenLabel)
    {
        if (string.IsNullOrEmpty(token))
            return;

        body.Append("<div class=\"divider\"></div>");
        body.Append("<div class=\"mobile-box\">");
        body.Append("<h2>Aplikacja mobilna</h2>");
        body.Append("<p class=\"muted\">Możesz także skopiować token i wkleić go bezpośrednio w aplikacji mobilnej SchoolMusic.</p>");

        if (!string.IsNullOrWhiteSpace(mobileUrl))
        {
            body.Append("<p><a class=\"btn-secondary\" href=\"")
                .Append(WebUtility.HtmlEncode(mobileUrl))
                .Append("\">Otwórz w aplikacji SchoolMusic</a></p>");
        }

        body.Append("<div class=\"field\">");
        body.Append("<label for=\"mobileToken\">").Append(WebUtility.HtmlEncode(tokenLabel)).Append("</label>");
        body.Append("<input id=\"mobileToken\" type=\"text\" readonly value=\"")
            .Append(WebUtility.HtmlEncode(token))
            .Append("\" aria-label=\"")
            .Append(WebUtility.HtmlEncode(tokenLabel))
            .Append("\" />");
        body.Append("</div>");
        body.Append("</div>");
    }

    private static string RenderDocument(string title, string heading, string bodyHtml)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeHeading = WebUtility.HtmlEncode(heading);

        return $$"""
<!DOCTYPE html>
<html lang="pl">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <meta name="referrer" content="no-referrer" />
  <title>{{safeTitle}} — SchoolMusic</title>
  <style>
    :root {
      color-scheme: light dark;
      --bg: #f4f6f9;
      --card: #ffffff;
      --text: #111827;
      --muted: #4b5563;
      --border: #d1d5db;
      --primary: #2563eb;
      --primary-hover: #1d4ed8;
      --success-bg: #ecfdf5;
      --success-border: #10b981;
      --success-text: #065f46;
      --error-bg: #fef2f2;
      --error-border: #ef4444;
      --error-text: #991b1b;
      --info-bg: #eff6ff;
      --info-border: #3b82f6;
      --info-text: #1e40af;
    }
    @media (prefers-color-scheme: dark) {
      :root {
        --bg: #0f172a;
        --card: #1e293b;
        --text: #f8fafc;
        --muted: #cbd5e1;
        --border: #334155;
        --primary: #3b82f6;
        --primary-hover: #2563eb;
        --success-bg: #064e3b;
        --success-border: #10b981;
        --success-text: #d1fae5;
        --error-bg: #7f1d1d;
        --error-border: #f87171;
        --error-text: #fee2e2;
        --info-bg: #1e3a8a;
        --info-border: #60a5fa;
        --info-text: #dbeafe;
      }
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      min-height: 100vh;
      display: flex;
      align-items: center;
      justify-content: center;
      padding: 1.5rem;
      font-family: system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
      background: var(--bg);
      color: var(--text);
      line-height: 1.5;
    }
    .card {
      width: 100%;
      max-width: 440px;
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 14px;
      padding: 1.75rem;
      box-shadow: 0 10px 25px rgba(0, 0, 0, 0.08);
    }
    .brand {
      font-size: 0.85rem;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--primary);
      margin-bottom: 0.4rem;
    }
    h1 {
      margin: 0 0 0.75rem;
      font-size: 1.4rem;
      line-height: 1.25;
    }
    h2 {
      margin: 0 0 0.4rem;
      font-size: 1rem;
    }
    p { margin: 0 0 1rem; }
    .muted { color: var(--muted); font-size: 0.95rem; }
    .field { margin-bottom: 1rem; }
    label {
      display: block;
      font-size: 0.88rem;
      font-weight: 600;
      margin-bottom: 0.35rem;
    }
    input {
      width: 100%;
      padding: 0.65rem 0.75rem;
      border: 1px solid var(--border);
      border-radius: 8px;
      background: transparent;
      color: var(--text);
      font-size: 0.95rem;
    }
    input[readonly] {
      font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
      font-size: 0.85rem;
      user-select: all;
    }
    .btn-primary, .btn-secondary {
      display: inline-block;
      width: 100%;
      padding: 0.75rem 1rem;
      border-radius: 8px;
      font-size: 0.95rem;
      font-weight: 600;
      text-align: center;
      text-decoration: none;
      cursor: pointer;
    }
    .btn-primary {
      border: none;
      background: var(--primary);
      color: #ffffff;
    }
    .btn-primary:hover { background: var(--primary-hover); }
    .btn-secondary {
      border: 1px solid var(--border);
      color: var(--text);
      background: transparent;
    }
    .alert {
      padding: 0.8rem 0.9rem;
      border-radius: 8px;
      border-left: 4px solid;
      margin-bottom: 1rem;
      font-size: 0.92rem;
    }
    .alert-success { background: var(--success-bg); border-color: var(--success-border); color: var(--success-text); }
    .alert-error { background: var(--error-bg); border-color: var(--error-border); color: var(--error-text); }
    .alert-info { background: var(--info-bg); border-color: var(--info-border); color: var(--info-text); }
    .divider {
      height: 1px;
      background: var(--border);
      margin: 1.35rem 0 1.1rem;
    }
  </style>
</head>
<body>
  <main class="card">
    <div class="brand">SchoolMusic</div>
    <h1>{{safeHeading}}</h1>
    {{bodyHtml}}
  </main>
</body>
</html>
""";
    }

    private ContentResult HtmlResult(int statusCode, string html)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "text/html; charset=utf-8",
            Content = html
        };
    }
}
