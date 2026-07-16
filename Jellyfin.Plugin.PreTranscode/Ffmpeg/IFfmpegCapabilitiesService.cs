using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Ffmpeg;

/// <summary>
/// Discovers what the system's ffmpeg binary actually supports (codecs, encoders, containers,
/// tone-mapping, per-encoder presets) so the configuration UI can be populated dynamically.
/// </summary>
public interface IFfmpegCapabilitiesService
{
    /// <summary>
    /// Gets the discovered ffmpeg capabilities (cached after the first probe).
    /// </summary>
    /// <param name="refresh">When <c>true</c>, re-probes the binary instead of using the cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered capabilities.</returns>
    Task<FfmpegCapabilities> GetCapabilitiesAsync(bool refresh, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the valid preset/speed values for a specific encoder.
    /// </summary>
    /// <param name="encoder">The encoder name (e.g. <c>libx264</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered preset information.</returns>
    Task<EncoderPresetInfo> GetEncoderPresetsAsync(string encoder, CancellationToken cancellationToken);
}
