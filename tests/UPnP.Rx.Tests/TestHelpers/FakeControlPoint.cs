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
    public Subject<MSearchResponse> Responses { get; } = new();

    public Subject<Notify> Notifies { get; } = new();

    public List<(MSearchRequest Request, IPAddress Address)> SentSearches { get; } = [];

    public void HotStart(IObservable<HttpRequestResponse> httpListenerObservable)
    {
        // The seam under test is the subjects below; a hot-started stream is unused.
    }

    public IObservable<Notify> NotifyObservable() => Notifies;

    public IObservable<MSearchResponse> MSearchResponseObservable() => Responses;

    public Task SendMSearchAsync(MSearchRequest mSearch, IPAddress ipAddress, CancellationToken ct = default)
    {
        SentSearches.Add((mSearch, ipAddress));
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Responses.Dispose();
        Notifies.Dispose();
    }
}
