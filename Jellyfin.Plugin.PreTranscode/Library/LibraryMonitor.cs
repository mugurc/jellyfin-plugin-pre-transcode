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
internal sealed class LibraryMonitor : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ItemEvaluator _evaluator;
    private readonly ILogger<LibraryMonitor> _logger;
    private readonly ConcurrentQueue<Guid> _pending = new();
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

        _pending.Enqueue(item.Id);
        _signal.Release();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                while (_pending.TryDequeue(out var id))
                {
                    var item = _libraryManager.GetItemById(id);
                    if (item is not null)
                    {
                        await _evaluator.EvaluateAndEnqueueAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                }
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
}
