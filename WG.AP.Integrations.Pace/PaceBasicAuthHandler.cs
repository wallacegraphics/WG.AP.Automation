using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;

namespace WG.AP.Integrations.Pace;

internal sealed class PaceBasicAuthHandler(IOptions<PaceOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var paceOptions = options.Value;
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{paceOptions.UserName}:{paceOptions.Password}"));

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);

        return base.SendAsync(request, cancellationToken);
    }
}
