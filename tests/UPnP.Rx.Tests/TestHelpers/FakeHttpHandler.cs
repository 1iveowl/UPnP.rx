using System.Net;
using System.Text;

namespace UPnP.Rx.Tests.TestHelpers;

/// <summary>
/// A test double for the HTTP seam: routes requests by absolute URL, records
/// every request (with body and headers), counts fetches per URL.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, (HttpStatusCode Status, string Body)>> _routes = [];

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public Dictionary<string, int> FetchCounts { get; } = [];

    public void Map(string url, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        _routes[url] = _ => (status, body);

    public void Map(string url, Func<HttpRequestMessage, (HttpStatusCode, string)> responder) =>
        _routes[url] = responder;

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        Requests.Add((request, body));
        FetchCounts[url] = FetchCounts.GetValueOrDefault(url) + 1;

        if (!_routes.TryGetValue(url, out var responder))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var (status, responseBody) = responder(request);

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/xml")
        };
    }
}
