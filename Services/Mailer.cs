using System.Net;
using System.Net.Mail;
using System.Text;

namespace Starfall.Grove.Moba.Api.Services;

/// <summary>
/// Sends the game's few emails (password reset links, player reports) over SMTP. Settings under "Smtp" in
/// appsettings: Host, Port (587), User, Password, From, FromName, and EnableSsl (true). Without a Host nothing is sent:
/// the message is written to the log instead (and the HTML to App_Data/outbox), which is enough for local development.
/// </summary>
public sealed class Mailer(IConfiguration config, ILogger<Mailer> log)
{
    private readonly IConfigurationSection _smtp = config.GetSection("Smtp");
    public bool Enabled => !string.IsNullOrWhiteSpace(_smtp["Host"]);
    /// <summary>Where reports about players go (Smtp:ReportsTo, else the sender address).</summary>
    public string? ReportsTo => _smtp["ReportsTo"] ?? _smtp["From"];

    public async Task<bool> SendAsync(string to, string subject, string text, string html)
    {
        if (!Enabled)
        {
            log.LogWarning("No SMTP settings; email to {To} not sent. Subject: {Subject}\n{Text}", to, subject, text);
            // Keep a copy to look at in a browser.
            try { Directory.CreateDirectory("App_Data/outbox"); await File.WriteAllTextAsync($"App_Data/outbox/{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.html", html); } catch { /* only a convenience */ }
            return false;
        }
        try
        {
            using var client = new SmtpClient(_smtp["Host"], _smtp.GetValue("Port", 587))
            {
                EnableSsl = _smtp.GetValue("EnableSsl", true),
                Credentials = new NetworkCredential(_smtp["User"], _smtp["Password"]),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15000,
            };
            using var msg = new MailMessage
            {
                From = new MailAddress(_smtp["From"] ?? _smtp["User"]!, _smtp["FromName"] ?? "Mini Rift"), Subject = subject,
                SubjectEncoding = Encoding.UTF8, HeadersEncoding = Encoding.UTF8,
            };
            msg.To.Add(to);
            // Both versions as alternatives, plain text first and HTML last (mail programs show the last one they can).
            // Not Body + IsBodyHtml: with alternate views added, .NET sends the Body as text/plain, so the HTML showed
            // up as code.
            msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(text, Encoding.UTF8, "text/plain"));
            msg.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html"));
            await client.SendMailAsync(msg);
            return true;
        }
        catch (Exception e) { log.LogError(e, "Could not send email to {To}", to); return false; }
    }
}
