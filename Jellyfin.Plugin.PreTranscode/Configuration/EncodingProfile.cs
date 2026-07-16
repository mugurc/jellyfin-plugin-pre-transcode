namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// A named, reusable set of target encoding parameters (the "compatibility baseline"). Every value
/// is admin-configurable; the codec/encoder/container/preset strings correspond to whatever the
/// system's ffmpeg binary supports (discovered dynamically), not a fixed enum.
/// </summary>
public class EncodingProfile
{
    /// <summary>
    /// Gets or sets the stable identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    // ---- Video ----

    /// <summary>
    /// Gets or sets the target video codec (e.g. <c>h264</c>), or <c>copy</c> to passthrough.
    /// </summary>
    public string VideoCodec { get; set; } = "h264";

    /// <summary>
    /// Gets or sets the encoder implementation for the codec (e.g. <c>libx264</c>, <c>h264_nvenc</c>).
    /// </summary>
    public string VideoEncoder { get; set; } = "libx264";

    /// <summary>
    /// Gets or sets how video quality is controlled.
    /// </summary>
    public QualityMode VideoQualityMode { get; set; } = QualityMode.Crf;

    /// <summary>
    /// Gets or sets the CRF/QP value (used when <see cref="VideoQualityMode"/> is <see cref="QualityMode.Crf"/>).
    /// </summary>
    public int Crf { get; set; } = 21;

    /// <summary>
    /// Gets or sets the target video bitrate in kbps (used when <see cref="VideoQualityMode"/> is <see cref="QualityMode.Bitrate"/>).
    /// </summary>
    public int VideoBitrateKbps { get; set; } = 6000;

    /// <summary>
    /// Gets or sets the maximum video bitrate in kbps (0 = unset).
    /// </summary>
    public int VideoMaxBitrateKbps { get; set; }

    /// <summary>
    /// Gets or sets the encoder preset/speed (e.g. <c>medium</c>). Free-form; valid values depend on the encoder.
    /// </summary>
    public string Preset { get; set; } = "medium";

    /// <summary>
    /// Gets or sets additional raw ffmpeg output arguments for video (advanced escape hatch).
    /// </summary>
    public string ExtraVideoArgs { get; set; } = string.Empty;

    // ---- Resolution ----

    /// <summary>
    /// Gets or sets the resolution policy.
    /// </summary>
    public ResolutionMode ResolutionMode { get; set; } = ResolutionMode.Unchanged;

    /// <summary>
    /// Gets or sets the maximum width (used by <see cref="ResolutionMode.CapWidth"/>/<see cref="ResolutionMode.CapLongestEdge"/>).
    /// </summary>
    public int MaxWidth { get; set; }

    /// <summary>
    /// Gets or sets the maximum height (used by <see cref="ResolutionMode.CapHeight"/>/<see cref="ResolutionMode.CapLongestEdge"/>).
    /// </summary>
    public int MaxHeight { get; set; }

    /// <summary>
    /// Gets or sets the resolution preset id (used by <see cref="ResolutionMode.UsePreset"/>).
    /// </summary>
    public string ResolutionPresetId { get; set; } = string.Empty;

    // ---- Audio ----

    /// <summary>
    /// Gets or sets the target audio codec (e.g. <c>aac</c>), or <c>copy</c> to passthrough.
    /// </summary>
    public string AudioCodec { get; set; } = "aac";

    /// <summary>
    /// Gets or sets the encoder implementation for the audio codec (e.g. <c>aac</c>, <c>libopus</c>).
    /// </summary>
    public string AudioEncoder { get; set; } = "aac";

    /// <summary>
    /// Gets or sets the target audio bitrate in kbps (per output stream).
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 256;

    /// <summary>
    /// Gets or sets the audio channel/downmix policy.
    /// </summary>
    public AudioChannelPolicy ChannelPolicy { get; set; } = AudioChannelPolicy.Unchanged;

    /// <summary>
    /// Gets or sets the custom maximum channel count (used by <see cref="AudioChannelPolicy.CapCustom"/>).
    /// </summary>
    public int MaxAudioChannels { get; set; } = 2;

    // ---- HDR / tone-mapping ----

    /// <summary>
    /// Gets or sets a value indicating whether HDR content should be tone-mapped to SDR.
    /// </summary>
    public bool TonemapHdr { get; set; }

    /// <summary>
    /// Gets or sets the tone-mapping algorithm (one of the values exposed by ffmpeg's tonemap filter).
    /// </summary>
    public string TonemapAlgorithm { get; set; } = "hable";

    // ---- Container / output ----

    /// <summary>
    /// Gets or sets the output container/muxer (e.g. <c>mp4</c>, <c>matroska</c>).
    /// </summary>
    public string Container { get; set; } = "mp4";

    /// <summary>
    /// Gets or sets what happens to the produced output file.
    /// </summary>
    public OutputHandlingMode OutputMode { get; set; } = OutputHandlingMode.SeparateDirectory;

    /// <summary>
    /// Gets or sets the output root directory (used by <see cref="OutputHandlingMode.SeparateDirectory"/>).
    /// </summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets additional raw ffmpeg output arguments applied to the whole command (advanced escape hatch).
    /// </summary>
    public string ExtraOutputArgs { get; set; } = string.Empty;
}
