using System.Net;
using System.Text;

namespace WG.AP.Tests.Email;

/// <summary>
/// Routes Graph requests by URL substring match, in registration order, so tests can fake a
/// mailbox backend without any network access or real credentials.
/// </summary>
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Matches, Func<HttpRequestMessage, string> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeGraphHandler On(Func<HttpRequestMessage, bool> matches, string jsonResponse)
    {
        _routes.Add((matches, _ => jsonResponse));
        return this;
    }

    public FakeGraphHandler On(Func<HttpRequestMessage, bool> matches, Func<HttpRequestMessage, string> respond)
    {
        _routes.Add((matches, respond));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        var route = _routes.FirstOrDefault(r => r.Matches(request));
        if (route.Respond is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No fake route registered for {request.Method} {request.RequestUri}")
            });
        }

        var body = route.Respond(request);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}
