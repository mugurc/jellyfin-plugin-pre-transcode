using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// Subscribes to Jellyfin's item-added/updated events for more granular, near-immediate evaluation of
/// individual items (in addition to the post-scan hook). Evaluations run one-at-a-time on a background
/// loop so a large scan cannot flood the server with concurrent ffprobe calls.
/// </summary>
/// <remarks>
/// A just-added file is often still inside its stability window (a fresh copy/download), which the
/// evaluator skips. Rather than evaluate once and drop it — leaving it for the daily sweep, up to a day
/// later — the monitor defers such items and re-checks them until they settle (or a bounded number of
/// attempts elapse), so "process new items automatically" actually fires shortly after the file lands.
/// </remarks>
internal sealed class LibraryMonitor : IHostedService, IDisposable
{
    // Bounded retries for an item that never settles (e.g. a stalled download). The scheduled sweep
    // remains the ultimate backstop, so giving up here only forgoes the near-immediate path.
    private const int MaxAttempts = 60;

    // How often the loop wakes to service deferred re-checks while items are waiting to settle.
    private static readonly TimeSpan DeferredPollInterval = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly ItemEvaluator _evaluator;
    private readonly ILogger<LibraryMonitor> _logger;
    private readonly ConcurrentDictionary<Guid, PendingItem> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public LibraryMonitor(ILibraryManager libraryManager, ItemEvaluator evaluator, ILogger<LibraryMonitor> logger)
    {
        _libraryManager = libraryManager;
        _evaluator = evaluator;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        _logger.LogInformation("Pre-Transcode library monitor started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _signal.Dispose();
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.ProcessNewItemsAutomatically)
        {
            return;
        }

        var item = e.Item;
        if (item is null || item.MediaType != MediaType.Video || string.IsNullOrEmpty(item.Path))
        {
            return;
        }

        // First check once the file should have settled; keying by id collapses the burst of
        // added/updated events a single import produces (and restarts the timer if it changes again).
        var dueUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, config.FileStabilitySeconds));
        _pending[item.Id] = new PendingItem(item.Id, dueUtc, 0);
        _signal.Release();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Block until an item arrives; once items are waiting, wake periodically to re-check
                // whether they have settled and become due.
                if (_pending.IsEmpty)
                {
                    await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _signal.WaitAsync(DeferredPollInterval, cancellationToken).ConfigureAwait(false);
                }

                await ProcessDueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Library monitor evaluation error");
            }
        }
    }

    private async Task ProcessDueAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.Enabled || !config.ProcessNewItemsAutomatically)
        {
            _pending.Clear();
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var entry in _pending.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.DueUtc > now || !_pending.TryRemove(entry.Id, out _))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(entry.Id);
            if (item is null || string.IsNullOrEmpty(item.Path))
            {
                continue;
            }

            if (!ItemEvaluator.IsStable(item.Path, config.FileStabilitySeconds))
            {
                // Still settling (e.g. an in-progress download): re-check later, up to a bound.
                if (entry.Attempts + 1 < MaxAttempts)
                {
                    var retryInSeconds = Math.Max(15, config.FileStabilitySeconds);
                    _pending[entry.Id] = new PendingItem(entry.Id, now.AddSeconds(retryInSeconds), entry.Attempts + 1);
                }
                else
                {
                    _logger.LogInformation(
                        "Stopped waiting for {Path} to settle after {Attempts} attempts; the scheduled sweep will still pick it up",
                        item.Path,
                        entry.Attempts + 1);
                }

                continue;
            }

            await _evaluator.EvaluateAndEnqueueAsync(item, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record PendingItem(Guid Id, DateTime DueUtc, int Attempts);
}
