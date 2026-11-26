using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using schoolmusic_backend.Configuration;

namespace schoolmusic_backend.Services;

public sealed record SentEmailMessage(
    string To,
    string Subject,
    string TextBody,
    string HtmlBody,
    string? ActionUrl,
    DateTimeOffset SentAtUtc
);

/// <summary>
/// Wysyła wiadomości e-mail z linkami weryfikacji adresu oraz resetu hasła.
/// </summary>
public interface IEmailSender
{
    bool IsConfigured { get; }
    Task SendAsync(
        string toEmail,
        string subject,
        string textBody,
        string htmlBody,
        string? actionUrl = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Lokalny magazyn wiadomości dla środowiska Development oraz testów automatycznych.
/// </summary>
public interface ILocalEmailSink
{
    IReadOnlyList<SentEmailMessage> GetMessages();
    void Clear();
}

public sealed class LocalDevelopmentEmailSink(
    ILogger<LocalDevelopmentEmailSink> logger,
    TimeProvider? timeProvider = null) : IEmailSender, ILocalEmailSink
{
    private const int MaxStoredMessages = 100;
    private readonly ConcurrentQueue<SentEmailMessage> _messages = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public bool IsConfigured => true;

    public Task SendAsync(
        string toEmail,
        string subject,
        string textBody,
        string htmlBody,
        string? actionUrl = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = new SentEmailMessage(
            toEmail,
            subject,
            textBody,
            htmlBody,
            actionUrl,
            _timeProvider.GetUtcNow());

        _messages.Enqueue(entry);
        while (_messages.Count > MaxStoredMessages)
        {
            _messages.TryDequeue(out _);
        }

        logger.LogInformation(
            "LocalDevelopmentEmailSink przechwycił wiadomość '{Subject}' dla odbiorcy (długość URL={UrlLength}).",
            subject,
            actionUrl?.Length ?? 0);

        return Task.CompletedTask;
    }

    public IReadOnlyList<SentEmailMessage> GetMessages() => _messages.ToArray();

    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
    }
}

public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public bool IsConfigured =>
        _options.Enabled &&
        !string.IsNullOrWhiteSpace(_options.Host) &&
        !string.IsNullOrWhiteSpace(_options.FromAddress);

    public async Task SendAsync(
        string toEmail,
        string subject,
        string textBody,
        string htmlBody,
        string? actionUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            logger.LogWarning("Pominięto wysyłkę e-maila '{Subject}', ponieważ SMTP nie jest skonfigurowane.", subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(new MailAddress(toEmail));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network
        };

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password ?? string.Empty);
        }

        await client.SendMailAsync(message, cancellationToken);
        logger.LogInformation("Wysłano wiadomość e-mail '{Subject}' przez SMTP.", subject);
    }
}
