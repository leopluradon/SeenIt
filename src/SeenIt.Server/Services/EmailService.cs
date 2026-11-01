using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace SeenIt.Server.Services;

public class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Sender address, e.g. "SeenIt &lt;seenit@example.com&gt;". Defaults to User.</summary>
    public string? From { get; set; }
    /// <summary>Auto (465 = SSL, otherwise STARTTLS when offered), StartTls, SslOnConnect or None.</summary>
    public string Security { get; set; } = "Auto";

    public bool Enabled => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From ?? User);
}

public record EmailMessage(string To, string Subject, string Text, string Html);

/// <summary>Sends mail over SMTP. <see cref="Queue"/> hands it to a background worker so requests don't wait on the mail server.</summary>
public class EmailService(IOptionsMonitor<SmtpOptions> options, ILogger<EmailService> log)
{
    private readonly Channel<EmailMessage> _queue = Channel.CreateBounded<EmailMessage>(
        new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });

    public bool Enabled => options.CurrentValue.Enabled;
    internal ChannelReader<EmailMessage> Pending => _queue.Reader;

    public void Queue(EmailMessage message)
    {
        if (!Enabled) { log.LogWarning("Email to {To} not sent: SMTP isn't configured", message.To); return; }
        _queue.Writer.TryWrite(message);
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var o = options.CurrentValue;
        if (!o.Enabled) throw new InvalidOperationException("SMTP isn't configured.");

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(o.From ?? o.User!));
        mail.To.Add(MailboxAddress.Parse(message.To));
        mail.Subject = message.Subject;
        mail.Body = new BodyBuilder { TextBody = message.Text, HtmlBody = message.Html }.ToMessageBody();

        var security = o.Security.ToLowerInvariant() switch
        {
            "starttls" => SecureSocketOptions.StartTls,
            "sslonconnect" or "ssl" or "tls" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            _ => o.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
        };

        using var smtp = new SmtpClient { Timeout = 20_000 };
        await smtp.ConnectAsync(o.Host, o.Port, security, ct);
        if (!string.IsNullOrEmpty(o.User)) await smtp.AuthenticateAsync(o.User, o.Password ?? "", ct);
        await smtp.SendAsync(mail, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

public class EmailWorker(EmailService email, ILogger<EmailWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var m in email.Pending.ReadAllAsync(stoppingToken))
            {
                try { await email.SendAsync(m, stoppingToken); log.LogInformation("Sent \"{Subject}\" to {To}", m.Subject, m.To); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Couldn't send \"{Subject}\" to {To}", m.Subject, m.To); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
