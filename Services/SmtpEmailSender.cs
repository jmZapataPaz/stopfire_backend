using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace StopFire.Api.Services;

public class SmtpEmailSender : IEmailSender
{
    private readonly IConfiguration _config;
    public SmtpEmailSender(IConfiguration config) => _config = config;

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var email = _config.GetSection("Email");
        var host = email["SmtpHost"]!;
        var port = int.Parse(email["SmtpPort"]!);
        var user = email["User"]!;
        var pass = email["Password"]!;
        var from = email["From"] ?? user;
        var enableSsl = bool.TryParse(email["UseSsl"], out var ssl) ? ssl : true;

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl,
            Credentials = new NetworkCredential(user, pass)
        };

        using var message = new MailMessage(from, to)
        {
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        await client.SendMailAsync(message, ct);
    }
}