using Bbs.Core;
using Microsoft.Extensions.Logging;

namespace Bbs.Host;

/// <summary>
/// The webmail "undo send" release worker: deferred sends are held (hidden + unforwarded) until their
/// short undo window lapses, when this loop clears the marker and routes them. Ticks once a second off
/// the injected <see cref="TimeProvider"/> and releases anything due; the first tick after a restart
/// naturally drains a marker stamped before it (so a deferred send is never stranded held). Cancelling
/// within the window kills it instead (<see cref="BbsStore.CancelDeferredSend"/>), so it never routes.
/// </summary>
public sealed class PendingSendReleaser
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly BbsStore _store;
    private readonly RoutingService _routing;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates the releaser.</summary>
    public PendingSendReleaser(BbsStore store, RoutingService routing, TimeProvider time, ILogger<PendingSendReleaser> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _routing = routing;
        _time = time;
        _logger = logger;
    }

    /// <summary>The once-a-second release loop.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ReleaseDue();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(_logger, ex);
            }

            try
            {
                await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Releases + routes every deferred send whose window has lapsed. Synchronous and testable (the
    /// loop just calls it on a timer): clear the marker + unhold, then route the now-live message —
    /// the same routing path webmail compose would have taken immediately.
    /// </summary>
    public void ReleaseDue()
    {
        foreach (Message message in _store.ListDueDeferredSends())
        {
            _store.ReleaseDeferredSend(message.Number);
            if (_store.GetMessage(message.Number) is { } released)
            {
                _routing.RouteMessage(released);
                LogReleased(_logger, released.Number, null);
            }
        }
    }

    private static readonly Action<ILogger, long, Exception?> LogReleased =
        LoggerMessage.Define<long>(LogLevel.Information, new EventId(1, "SendReleased"),
            "Deferred send {Number} released and routed");

    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2, "SendReleaseFailed"),
            "Pending-send release pass failed");
}
