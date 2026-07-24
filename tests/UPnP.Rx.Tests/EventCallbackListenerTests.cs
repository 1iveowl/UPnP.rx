using System.Reactive.Subjects;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleHttpListener.Rx.Model;
using UPnP.Rx.Eventing;
using Xunit;

namespace UPnP.Rx.Tests;

public class EventCallbackListenerTests
{
    private readonly Subject<HttpRequestResponse> _requests = new();
    private readonly List<(HttpRequestResponse Request, HttpResponse Response)> _sent = [];
    private readonly EventCallbackListener _listener;

    public EventCallbackListenerTests() =>
        _listener = new EventCallbackListener(
            _requests,
            (request, response, _) =>
            {
                _sent.Add((request, response));
                return Task.CompletedTask;
            },
            NullLogger.Instance);

    private static HttpRequestResponse Notify(string path, string sid = "uuid:sub-1", uint seq = 0,
        string body = "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"/>",
        Action<Dictionary<string, string>>? mutateHeaders = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NT"] = "upnp:event",
            ["NTS"] = "upnp:propchange",
            ["SID"] = sid,
            ["SEQ"] = seq.ToString()
        };

        mutateHeaders?.Invoke(headers);

        return new HttpRequestResponse
        {
            MessageType = MessageType.Request,
            Method = "NOTIFY",
            Path = path,
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(body)
        };
    }

    [Fact]
    public void RoutedNotify_ReachesTheHandler_AndGets200()
    {
        var received = new List<NotifyRequest>();
        using var route = _listener.Register("tok1", (notify, _) =>
        {
            received.Add(notify);
            return Task.CompletedTask;
        });

        _requests.OnNext(Notify("/upnp/events/tok1", sid: "uuid:s", seq: 7, body: "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><A>1</A></e:property></e:propertyset>"));

        var notify = Assert.Single(received);
        Assert.Equal("uuid:s", notify.Sid);
        Assert.Equal(7u, notify.Seq);
        Assert.Contains("<A>1</A>", notify.Body);
        Assert.Equal(200, Assert.Single(_sent).Response.StatusCode);
    }

    [Fact]
    public void UnknownToken_Gets412()
    {
        _requests.OnNext(Notify("/upnp/events/nope"));

        Assert.Equal(412, Assert.Single(_sent).Response.StatusCode);
    }

    [Fact]
    public void NonNotifyRequests_Get405WithAllow()
    {
        _requests.OnNext(new HttpRequestResponse
        {
            MessageType = MessageType.Request,
            Method = "GET",
            Path = "/upnp/events/tok1",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        });

        var response = Assert.Single(_sent).Response;
        Assert.Equal(405, response.StatusCode);
        Assert.Equal("NOTIFY", response.Headers["Allow"]);
    }

    [Fact]
    public void HandlerFailure_Answers500_AndDoesNotKillTheStream()
    {
        using var route = _listener.Register("boom", (_, _) => throw new InvalidOperationException("bang"));

        _requests.OnNext(Notify("/upnp/events/boom"));

        Assert.Equal(500, Assert.Single(_sent).Response.StatusCode);

        var survived = new List<NotifyRequest>();
        using var route2 = _listener.Register("ok", (notify, _) =>
        {
            survived.Add(notify);
            return Task.CompletedTask;
        });

        _requests.OnNext(Notify("/upnp/events/ok"));

        Assert.Single(survived);
        Assert.Equal(200, _sent[^1].Response.StatusCode);
    }

    [Fact]
    public void MissingNtOrNts_Gets400()
    {
        using var route = _listener.Register("tok1", (_, _) => Task.CompletedTask);

        _requests.OnNext(Notify("/upnp/events/tok1", mutateHeaders: h => h.Remove("NTS")));

        Assert.Equal(400, Assert.Single(_sent).Response.StatusCode);
    }

    [Fact]
    public void WrongNtsOrMissingSid_Gets412()
    {
        using var route = _listener.Register("tok1", (_, _) => Task.CompletedTask);

        _requests.OnNext(Notify("/upnp/events/tok1", mutateHeaders: h => h["NTS"] = "upnp:somethingelse"));
        _requests.OnNext(Notify("/upnp/events/tok1", mutateHeaders: h => h.Remove("SID")));

        Assert.Equal(2, _sent.Count);
        Assert.All(_sent, s => Assert.Equal(412, s.Response.StatusCode));
    }

    [Fact]
    public void DisposedRegistration_Gets412Afterwards()
    {
        var route = _listener.Register("gone", (_, _) => Task.CompletedTask);
        route.Dispose();

        _requests.OnNext(Notify("/upnp/events/gone"));

        Assert.Equal(412, Assert.Single(_sent).Response.StatusCode);
    }
}
