using System.Threading;
using System.Threading.Tasks;

namespace StopFire.Api.Services;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
}