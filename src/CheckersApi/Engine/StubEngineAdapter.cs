using Checkers.Core;

namespace CheckersApi.Engine;

/// <summary>In-process worker around the built-in alpha-beta searcher and 3-piece tablebase (Chinook stand-in).</summary>
public sealed class StubEngineAdapter : IEngineAdapter
{
    private readonly EndgameTablebase _tablebase;
    private readonly Searcher _searcher = new();
    private Position? _position;

    public StubEngineAdapter(EndgameTablebase tablebase) => _tablebase = tablebase;

    public string Name => "stub-alphabeta";

    public Task StartAsync(CancellationToken ct) =>
        OnDedicatedThread(() => _searcher.Search(Position.Start, 4, 50, ct), ct); // JIT warm-up

    public Task SetPositionAsync(string pdn, CancellationToken ct)
    {
        _position = Pdn.Parse(pdn);
        return Task.CompletedTask;
    }

    public Task<EngineSearchResult> SearchAsync(EngineLimits limits, CancellationToken ct)
    {
        var pos = _position ?? throw new InvalidOperationException("No position set.");
        return OnDedicatedThread(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (limits.UseTablebase && _tablebase.Probe(pos) is { } hit)
            {
                string mv = Pdn.FormatMove(hit.Best);
                return new EngineSearchResult(mv, new[] { mv }, ScoreOrWdl.FromWdl(hit.Result.ToString().ToUpperInvariant()), 0, 1, true);
            }
            var o = _searcher.Search(pos, limits.MaxDepth, limits.SoftTimeMs, ct)
                    ?? throw new InvalidOperationException("No legal moves.");
            var pv = o.Pv.Select(Pdn.FormatMove).ToList();
            return new EngineSearchResult(Pdn.FormatMove(o.Best), pv, ScoreOrWdl.FromCp(o.ScoreCp), o.Depth, o.Nodes, false);
        }, ct);
    }

    /// <summary>
    /// A search is CPU-bound for up to hundreds of milliseconds. Running it on a pool thread starves the timers
    /// that enforce hardTimeMs and the threads that serve other requests, so it gets its own thread.
    /// </summary>
    private static Task<T> OnDedicatedThread<T>(Func<T> work, CancellationToken ct) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
