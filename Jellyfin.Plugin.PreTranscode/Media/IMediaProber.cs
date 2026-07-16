using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PreTranscode.Media;

/// <summary>
/// Probes a media file (via ffprobe) into a <see cref="MediaProbeInfo"/>.
/// </summary>
public interface IMediaProber
{
    /// <summary>
    /// Probes the given file.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The probe result, or <c>null</c> if probing failed.</returns>
    Task<MediaProbeInfo?> ProbeAsync(string path, CancellationToken cancellationToken);
}
