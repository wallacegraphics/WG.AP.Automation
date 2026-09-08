using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace WG.AP.Tests.Email;

/// <summary>
/// Routes Graph requests by URL substring match, in registration order, so tests can fake a
/// mailbox backend without any network access or real credentials.
/// </summary>
internal sealed class FakeGraphHandler : HttpMessageHandler
{
    private readonly List<(Func<HttpRequestMessage, bool> Matches, Func<HttpRequestMessage, HttpContent> Respond)> _routes = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeGraphHandler On(Func<HttpRequestMessage, bool> matches, string jsonResponse)
    {
        _routes.Add((matches, _ => new StringContent(jsonResponse, Encoding.UTF8, "application/json")));
        return this;
    }

    public FakeGraphHandler On(Func<HttpRequestMessage, bool> matches, Func<HttpRequestMessage, string> respond)
    {
        _routes.Add((matches, request => new StringContent(respond(request), Encoding.UTF8, "application/json")));
        return this;
    }

    /// <summary>Fakes a raw/binary response — used for the attachment $value endpoint, which returns bytes, not JSON.</summary>
    public FakeGraphHandler OnBinary(Func<HttpRequestMessage, bool> matches, byte[] content, string contentType = "application/octet-stream")
    {
        _routes.Add((matches, _ => new ByteArrayContent(content) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } }));
        return this;
    }

    /// <summary>
    /// Simulates a transient failure: throws <paramref name="exception"/> for the first
    /// <paramref name="failures"/> matching requests, then serves <paramref name="jsonResponse"/>
    /// normally - for testing a caller's retry logic against something like a dropped connection.
    /// </summary>
    public FakeGraphHandler OnFlaky(Func<HttpRequestMessage, bool> matches, int failures, Exception exception, string jsonResponse)
    {
        var remaining = failures;

        _routes.Add((matches, _ =>
        {
            if (remaining > 0)
            {
                remaining--;
                throw exception;
            }

            return new StringContent(jsonResponse, Encoding.UTF8, "application/json");
        }));

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

        try
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = route.Respond(request)
            };

            return Task.FromResult(response);
        }
        catch (Exception exception)
        {
            // Faulted task, not a synchronous throw - matches how a real transport failure actually
            // surfaces to an awaiting caller (e.g. OnFlaky simulating a dropped connection).
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }
}
