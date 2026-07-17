using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Default <see cref="IFfmpegCapabilitiesService"/> implementation that probes the ffmpeg binary
/// configured for the Jellyfin server (via <see cref="IMediaEncoder.EncoderPath"/>).
/// </summary>
internal sealed class FfmpegCapabilitiesService : IFfmpegCapabilitiesService, IDisposable
{
    private const int ProbeTimeoutMs = 30000;

    // libx264/libx265 do not expose their presets as enumerable ffmpeg options ("cf. x264 --fullhelp"),
    // so ffmpeg cannot report them. These are the documented preset vocabularies for those encoder
    // families, offered as editable suggestions. Marked FromFfmpeg=false so the UI can say so.
    private static readonly IReadOnlyList<string> X26XPresets = new[]
    {
        "ultrafast", "superfast", "veryfast", "faster", "fast",
        "medium", "slow", "slower", "veryslow", "placebo"
    };

    private static readonly Dictionary<string, IReadOnlyList<string>> KnownPresets =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["libx264"] = X26XPresets,
            ["libx264rgb"] = X26XPresets,
            ["libx265"] = X26XPresets
        };

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<FfmpegCapabilitiesService> _logger;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private readonly Dictionary<string, EncoderPresetInfo> _presetCache = new(StringComparer.OrdinalIgnoreCase);

    private FfmpegCapabilities? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegCapabilitiesService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The Jellyfin media encoder (provides the ffmpeg path).</param>
    /// <param name="logger">The logger.</param>
    public FfmpegCapabilitiesService(IMediaEncoder mediaEncoder, ILogger<FfmpegCapabilitiesService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FfmpegCapabilities> GetCapabilitiesAsync(bool refresh, CancellationToken cancellationToken)
    {
        var cached = _cache;
        if (!refresh && cached is not null)
        {
            return cached;
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _cache is not null)
            {
                return _cache;
            }

            var capabilities = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            _cache = capabilities;
            return capabilities;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EncoderPresetInfo> GetEncoderPresetsAsync(string encoder, CancellationToken cancellationToken)
    {
        // Serve from cache without spawning ffmpeg. Without this each call runs `ffmpeg -h encoder=X`,
        // and the endpoint is otherwise ungated: a client could fire many requests for distinct encoder
        // names and spawn an unbounded number of concurrent ffmpeg processes. The shared _probeLock both
        // deduplicates concurrent probes for the same encoder and bounds total probe concurrency to one.
        if (_presetCache.TryGetValue(encoder, out var cached))
        {
            return cached;
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_presetCache.TryGetValue(encoder, out cached))
            {
                return cached;
            }

            var info = new EncoderPresetInfo { Encoder = encoder };
            var path = FfmpegPaths.ResolveFfmpeg(_mediaEncoder);

            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    var help = await ProcessRunner.RunAsync(
                        path,
                        new[] { "-hide_banner", "-h", "encoder=" + encoder },
                        ProbeTimeoutMs,
                        cancellationToken).ConfigureAwait(false);
                    info = FfmpegOutputParser.ParseEncoderPresets(help, encoder);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to probe presets for encoder {Encoder}", encoder);
                }
            }

            if (info.Kind == PresetKind.None && KnownPresets.TryGetValue(encoder, out var known))
            {
                info.Kind = PresetKind.NamedList;
                info.Values = known;
                info.FromFfmpeg = false;
            }

            _presetCache[encoder] = info;
            return info;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _probeLock.Dispose();
    }

    private async Task<FfmpegCapabilities> ProbeAsync(CancellationToken cancellationToken)
    {
        var path = FfmpegPaths.ResolveFfmpeg(_mediaEncoder);

        _logger.LogInformation("Probing ffmpeg capabilities using {FfmpegPath}", path);

        var codecsOutput = await ProcessRunner.RunAsync(path, "-hide_banner -codecs", ProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
        var muxersOutput = await ProcessRunner.RunAsync(path, "-hide_banner -muxers", ProbeTimeoutMs, cancellationToken).ConfigureAwait(false);

        var tonemapOutput = string.Empty;
        try
        {
            tonemapOutput = await ProcessRunner.RunAsync(path, "-hide_banner -h filter=tonemap", ProbeTimeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to probe tonemap filter");
        }

        var codecs = FfmpegOutputParser.ParseCodecs(codecsOutput);

        return new FfmpegCapabilities
        {
            FfmpegPath = path,
            FfmpegVersion = _mediaEncoder.EncoderVersion?.ToString() ?? string.Empty,
            VideoCodecs = codecs.Where(c => c.MediaType == CodecMediaType.Video).ToList(),
            AudioCodecs = codecs.Where(c => c.MediaType == CodecMediaType.Audio).ToList(),
            Containers = FfmpegOutputParser.ParseMuxers(muxersOutput),
            TonemapAlgorithms = FfmpegOutputParser.ParseTonemapModes(tonemapOutput)
        };
    }
}
