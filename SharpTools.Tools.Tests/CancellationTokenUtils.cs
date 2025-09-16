using System.Collections.Immutable;

namespace SharpTools.Tools.Tests;
public static class CancellationTokenUtils
{
    public static TimeoutWatcher ApplyTimeout(int seconds, ref CancellationToken cancellationToken)
    {
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));

        var ct = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        cancellationToken = ct.Token;

        return new TimeoutWatcher(cts.Token, [cts, ct]);
    }

    public record TimeoutWatcher(
        CancellationToken CancellationToken,
        ImmutableArray<IDisposable> Disposables
    ) : IDisposable
    {
        public bool TimedOut => CancellationToken.IsCancellationRequested;
        
        public void Dispose() {
            foreach(var disposable in Disposables)
                disposable.Dispose();
        }
    }
}
