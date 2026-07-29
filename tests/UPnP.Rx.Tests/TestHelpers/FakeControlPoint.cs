using System.Net;
using System.Reactive.Subjects;
using SimpleHttpListener.Rx.Model;
using SSDP.UPnP.PCL;
using SSDP.UPnP.PCL.Model;

namespace UPnP.Rx.Tests.TestHelpers;

/// <summary>
/// A test double for the SSDP seam: exposes subjects to inject parsed messages
/// and records every M-SEARCH the client sends.
/// </summary>
internal sealed class FakeControlPoint : IControlPoint
{
    public Subject<ReceivedMSearchResponse> Responses { get; } = new();

    public Subject<ReceivedNotify> Notifies { get; } = new();

    public Subject<SsdpParseFailure> Failures { get; } = new();

    public List<(MSearchRequest Request, IPAddress Address)> SentSearches { get; } = [];

    public void HotStart(IObservable<HttpRequestResponse> httpListenerObservable)
    {
        // The seam under test is the subjects below; a hot-started stream is unused.
    }

    public IObservable<ReceivedNotify> NotifyObservable() => Notifies;

    public IObservable<ReceivedMSearchResponse> MSearchResponseObservable() => Responses;

    public IObservable<SsdpParseFailure> ParseFailures() => Failures;

    public Task SendMSearchAsync(MSearchRequest mSearch, IPAddress ipAddress, CancellationToken ct = default)
    {
        SentSearches.Add((mSearch, ipAddress));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Responses.Dispose();
        Notifies.Dispose();
        Failures.Dispose();
    }

    /// <summary>No protocol goodbye to say; the fake's graceful path is its abrupt one.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
