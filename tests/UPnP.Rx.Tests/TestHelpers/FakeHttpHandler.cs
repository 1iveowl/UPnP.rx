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
    private readonly Dictionary<string, Dictionary<string, string>> _responseHeaders = [];

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public Dictionary<string, int> FetchCounts { get; } = [];

    public void Map(
        string url,
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string? server = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        _routes[url] = _ => (status, body);

        var all = new Dictionary<string, string>(headers ?? new Dictionary<string, string>());

        if (server is not null)
        {
            all["SERVER"] = server;
        }

        if (all.Count > 0)
        {
            _responseHeaders[url] = all;
        }
    }

    /// <summary>
    /// Maps a GENA event endpoint so a SUBSCRIBE succeeds: 200 with the SID and
    /// TIMEOUT headers the transport requires. Without this an event URL answers
    /// 404, which the engine rightly treats as a permanent refusal and dies on -
    /// taking anything that only exists while the subscription lives with it.
    /// </summary>
    public void MapGenaSubscribe(string url, string sid = "uuid:test-sid-1", int timeoutSeconds = 1800) =>
        Map(url, string.Empty, headers: new Dictionary<string, string>
        {
            ["SID"] = sid,
            ["TIMEOUT"] = $"Second-{timeoutSeconds}"
        });

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

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "text/xml")
        };

        if (_responseHeaders.TryGetValue(url, out var extra))
        {
            foreach (var (name, value) in extra)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }
}
