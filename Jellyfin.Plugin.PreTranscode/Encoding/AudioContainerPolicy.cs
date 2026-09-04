using System;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Encoding;

/// <summary>
/// Which audio codecs a target container can actually store, and what to encode to when it cannot.
/// <para>
/// The subtitle path already negotiates this (see <c>FfmpegCommandBuilder.SubtitlesToCarry</c>): a track
/// the output container cannot hold is converted rather than copied, because a muxer that is handed a
/// codec it has no tag for rejects the output header and kills the whole encode seconds in. Audio had no
/// such negotiation — <c>-c:a copy</c> was emitted for any container, and a track whose codec merely
/// matched the profile was copied without asking whether the container could hold it. So an mkv source's
/// TrueHD track copied into mp4, or an aac track copied into webm, failed every attempt for that file.
/// </para>
/// <para>
/// Both the command builder and <c>ProfileComplianceChecker</c> decide through here, so they cannot
/// disagree: if the builder substitutes a codec the container can hold, compliance must compare the
/// source against that same substituted codec, or every sweep would find the file non-compliant,
/// re-encode it, and find it non-compliant again.
/// </para>
/// </summary>
internal static class AudioContainerPolicy
{
    /// <summary>
    /// Whether <paramref name="container"/> can store <paramref name="codec"/> as it is.
    /// </summary>
    /// <param name="container">The target container/muxer name.</param>
    /// <param name="codec">The audio codec, as ffprobe names it.</param>
    /// <returns><c>true</c> when the track can be copied verbatim.</returns>
    /// <remarks>
    /// Only the containers whose audio constraints are actually modelled answer <c>false</c>. Anything
    /// else — Matroska, which stores every audio codec in practice, and any container an admin picked
    /// from the muxer list that is not modelled here — answers <c>true</c>, which is exactly the
    /// behaviour that shipped before this policy existed. Saying "no" for an unmodelled container would
    /// substitute aac into, say, an ogg output and break a case that is not known to be broken.
    /// </remarks>
    public static bool CanStore(string container, string codec)
    {
        var normalized = (codec ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            // The probe could not name the codec. Assume it fits rather than force a lossy re-encode on
            // a guess; a genuinely unstorable track then fails the way it did before, visibly.
            return true;
        }

        return Normalize(container) switch
        {
            // webm is a strict Matroska subset: Opus and Vorbis, nothing else.
            "webm" => normalized is "opus" or "vorbis",

            // The codecs ffmpeg's mov/mp4 muxer has a sample-entry tag for. Deliberately excludes
            // truehd/mlp and aac_latm, which it does not, and which are the ones that were failing.
            "mp4" => normalized is "aac" or "mp3" or "ac3" or "eac3" or "alac" or "flac" or "opus"
                or "dts" or "pcm_s16le" or "pcm_s16be" or "pcm_s24le" or "pcm_s24be",

            _ => true
        };
    }

    /// <summary>
    /// The audio codec this profile will really produce: its own target when the container can hold it,
    /// otherwise the container's safe default. <c>copy</c> passes through — the copy path decides per
    /// track, since only some of a file's tracks may be unstorable.
    /// </summary>
    /// <param name="profile">The target profile.</param>
    /// <returns>The codec name.</returns>
    public static string EffectiveCodec(EncodingProfile profile)
    {
        return KeepsProfileTarget(profile) ? profile.AudioCodec : FallbackCodec(profile.Container);
    }

    /// <summary>
    /// The encoder that goes with <see cref="EffectiveCodec"/>.
    /// </summary>
    /// <param name="profile">The target profile.</param>
    /// <returns>The encoder name.</returns>
    public static string EffectiveEncoder(EncodingProfile profile)
    {
        return KeepsProfileTarget(profile) ? profile.AudioEncoder : FallbackEncoder(profile.Container);
    }

    /// <summary>
    /// Whether the profile's own audio target survives into the output, i.e. no substitution happens.
    /// </summary>
    /// <param name="profile">The target profile.</param>
    /// <returns><c>true</c> when the profile's configured codec is used as-is.</returns>
    public static bool KeepsProfileTarget(EncodingProfile profile)
    {
        return IsCopy(profile.AudioCodec) || CanStore(profile.Container, profile.AudioCodec);
    }

    /// <summary>
    /// The codec to fall back to for a container that cannot store what was asked for.
    /// </summary>
    /// <param name="container">The target container/muxer name.</param>
    /// <returns>The fallback codec name.</returns>
    public static string FallbackCodec(string container)
    {
        return Normalize(container) == "webm" ? "opus" : "aac";
    }

    /// <summary>
    /// The encoder to fall back to for a container that cannot store what was asked for.
    /// </summary>
    /// <param name="container">The target container/muxer name.</param>
    /// <returns>The fallback encoder name.</returns>
    public static string FallbackEncoder(string container)
    {
        // libopus rather than ffmpeg's native "opus" encoder: the native one is still marked
        // experimental and refuses to run without -strict -2, while libopus is built into
        // jellyfin-ffmpeg and every mainstream distribution build.
        return Normalize(container) == "webm" ? "libopus" : "aac";
    }

    // Collapses the container name to the one whose rules apply. Mirrors FfmpegCommandBuilder.MuxerFor,
    // including its treatment of an empty container as mp4, so the policy and the -f flag never
    // disagree about which container the output actually is.
    private static string Normalize(string container)
    {
        var normalized = (container ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => "mp4",
            "mkv" => "matroska",
            "mov" or "m4v" or "ipod" => "mp4",
            _ => normalized
        };
    }

    private static bool IsCopy(string codec)
    {
        return string.Equals(codec, "copy", StringComparison.OrdinalIgnoreCase);
    }
}
