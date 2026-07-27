using System.Net;
using Microsoft.Extensions.Logging;

namespace UPnP.Rx;

/// <summary>
/// Every log message the library emits, source-generated. Centralised so the
/// wording and levels are visible in one place, and so no call site pays for a
/// message that is switched off: the generator checks <c>IsEnabled</c> and skips
/// argument boxing itself, which is why no hand-written guards remain.
/// </summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Debug, "Skipping {Location}: description unavailable.")]
    internal static partial void DescriptionUnavailable(this ILogger logger, Exception e, Uri? location);

    [LoggerMessage(2, LogLevel.Warning, "M-SEARCH failed on interface {Address}.")]
    internal static partial void SearchFailedOnInterface(this ILogger logger, Exception e, IPAddress address);

    [LoggerMessage(3, LogLevel.Debug, "Dropped an announcement without a usable LOCATION (USN: {Usn}).")]
    internal static partial void AnnouncementWithoutLocation(this ILogger logger, string? usn);

    [LoggerMessage(4, LogLevel.Debug, "Port mapping enumeration ended at index {Index} with UPnP error {Code}.")]
    internal static partial void PortMappingEnumerationEnded(this ILogger logger, int index, int code);

    [LoggerMessage(5, LogLevel.Debug,
        "Deleting port mapping {Port}/{Protocol} on dispose failed; the lease will expire on its own.")]
    internal static partial void PortMappingDeleteOnDisposeFailed(
        this ILogger logger, Exception e, int port, PortMapping.Protocol protocol);

    [LoggerMessage(6, LogLevel.Debug, "Handling an announcement for the roster failed.")]
    internal static partial void RosterAnnouncementFailed(this ILogger logger, Exception e);

    [LoggerMessage(7, LogLevel.Error, "The roster's announcement stream terminated.")]
    internal static partial void RosterAnnouncementStreamTerminated(this ILogger logger, Exception e);

    [LoggerMessage(8, LogLevel.Error, "The roster's byebye stream terminated.")]
    internal static partial void RosterByeByeStreamTerminated(this ILogger logger, Exception e);

    [LoggerMessage(9, LogLevel.Warning, "The roster's opening M-SEARCH failed.")]
    internal static partial void RosterOpeningSearchFailed(this ILogger logger, Exception e);

    [LoggerMessage(10, LogLevel.Error, "The roster engine failed.")]
    internal static partial void RosterEngineFailed(this ILogger logger, Exception e);

    [LoggerMessage(19, LogLevel.Error, "The roster's ssdp:update stream terminated.")]
    internal static partial void RosterUpdateStreamTerminated(this ILogger logger, Exception e);

    [LoggerMessage(11, LogLevel.Debug, "Roster re-describe of {Location} failed.")]
    internal static partial void RosterRedescribeFailed(this ILogger logger, Exception e, Uri? location);

    [LoggerMessage(20, LogLevel.Debug,
        "The presence stream backing an event subscription for {Url} ended; cancellations will go unnoticed.")]
    internal static partial void PresenceWatchEnded(this ILogger logger, Exception e, Uri url);

    [LoggerMessage(12, LogLevel.Error, "The event subscription engine for {Url} failed.")]
    internal static partial void EventEngineFailed(this ILogger logger, Exception e, Uri url);

    [LoggerMessage(13, LogLevel.Debug, "UNSUBSCRIBE for {Sid} failed; the device will time the subscription out.")]
    internal static partial void UnsubscribeFailed(this ILogger logger, Exception e, string sid);

    [LoggerMessage(14, LogLevel.Debug, "Dropped an unparsable NOTIFY: {Error}")]
    internal static partial void UnparsableNotify(this ILogger logger, string? error);

    [LoggerMessage(15, LogLevel.Debug, "An event subscription's goodbye failed during disposal.")]
    internal static partial void EventGoodbyeFailed(this ILogger logger, Exception e);

    [LoggerMessage(16, LogLevel.Debug, "Handling a NOTIFY failed.")]
    internal static partial void NotifyHandlingFailed(this ILogger logger, Exception e);

    [LoggerMessage(17, LogLevel.Error, "The event callback stream terminated.")]
    internal static partial void EventCallbackStreamTerminated(this ILogger logger, Exception e);

    [LoggerMessage(18, LogLevel.Debug, "A NOTIFY handler failed; answering 500.")]
    internal static partial void NotifyHandlerFailed(this ILogger logger, Exception e);
}
