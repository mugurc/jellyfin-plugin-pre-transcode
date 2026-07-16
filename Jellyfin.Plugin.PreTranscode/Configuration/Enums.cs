namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// How output video quality is controlled.
/// </summary>
public enum QualityMode
{
    /// <summary>Constant quality (CRF/QP): a quality number, variable bitrate.</summary>
    Crf,

    /// <summary>Target/maximum bitrate.</summary>
    Bitrate
}

/// <summary>
/// How output resolution is constrained.
/// </summary>
public enum ResolutionMode
{
    /// <summary>Leave the source resolution unchanged.</summary>
    Unchanged,

    /// <summary>Downscale only if the width exceeds a maximum.</summary>
    CapWidth,

    /// <summary>Downscale only if the height exceeds a maximum.</summary>
    CapHeight,

    /// <summary>Downscale only if the longest edge exceeds a maximum (orientation-independent).</summary>
    CapLongestEdge,

    /// <summary>Downscale to fit within a named resolution preset.</summary>
    UsePreset
}

/// <summary>
/// How output audio channel count is constrained.
/// </summary>
public enum AudioChannelPolicy
{
    /// <summary>Leave the source channel layout unchanged.</summary>
    Unchanged,

    /// <summary>Downmix to at most 2.0 (stereo).</summary>
    CapStereo,

    /// <summary>Downmix to at most 5.1.</summary>
    Cap51,

    /// <summary>Downmix to at most a custom channel count.</summary>
    CapCustom
}

/// <summary>
/// What the plugin does with a successfully-transcoded output file.
/// </summary>
public enum OutputHandlingMode
{
    /// <summary>Write to a separate output directory mirroring the source structure; originals untouched (safest).</summary>
    SeparateDirectory,

    /// <summary>Keep both and add the new file as an alternate version of the same Jellyfin item (best-effort).</summary>
    AddAsAlternateVersion,

    /// <summary>Replace the original file in place after the output is verified.</summary>
    ReplaceInPlace
}

/// <summary>
/// How the conditions within a single rule are combined.
/// </summary>
public enum ConditionCombine
{
    /// <summary>All conditions must match (logical AND).</summary>
    All,

    /// <summary>Any condition may match (logical OR).</summary>
    Any
}

/// <summary>
/// The media property a rule condition inspects.
/// </summary>
public enum ConditionType
{
    /// <summary>The video stream codec (e.g. hevc).</summary>
    VideoCodec,

    /// <summary>The video height in pixels.</summary>
    VideoHeight,

    /// <summary>The video width in pixels.</summary>
    VideoWidth,

    /// <summary>The overall/video bitrate in kbps.</summary>
    VideoBitrateKbps,

    /// <summary>Whether the video is HDR (HDR10/HLG/etc.).</summary>
    IsHdr,

    /// <summary>Whether the video carries Dolby Vision.</summary>
    IsDolbyVision,

    /// <summary>The audio stream codec (e.g. truehd).</summary>
    AudioCodec,

    /// <summary>The audio channel count.</summary>
    AudioChannels,

    /// <summary>The source container/format (e.g. mkv).</summary>
    Container,

    /// <summary>The video frame rate.</summary>
    VideoFramerate,

    /// <summary>The source file size in megabytes.</summary>
    FileSizeMb
}

/// <summary>
/// How a rule condition compares the media property to its configured value.
/// </summary>
public enum ComparisonOperator
{
    /// <summary>Equal to the value.</summary>
    Equals,

    /// <summary>Not equal to the value.</summary>
    NotEquals,

    /// <summary>Contained in the comma-separated value list.</summary>
    In,

    /// <summary>Not contained in the comma-separated value list.</summary>
    NotIn,

    /// <summary>Greater than the numeric value.</summary>
    GreaterThan,

    /// <summary>Less than the numeric value.</summary>
    LessThan,

    /// <summary>Greater than or equal to the numeric value.</summary>
    GreaterThanOrEqual,

    /// <summary>Less than or equal to the numeric value.</summary>
    LessThanOrEqual,

    /// <summary>The boolean property is true / the property is present.</summary>
    Exists,

    /// <summary>The boolean property is false / the property is absent.</summary>
    NotExists
}
