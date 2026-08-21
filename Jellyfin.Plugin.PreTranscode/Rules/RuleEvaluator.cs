using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Rules;

/// <summary>
/// Pure evaluation of the trigger-rule engine against a <see cref="MediaProbeInfo"/>. An item should
/// be queued when <em>any</em> enabled rule matches; conditions within a rule are combined by AND/OR.
/// </summary>
internal static class RuleEvaluator
{
    public static bool ShouldProcess(IEnumerable<TriggerRule> rules, MediaProbeInfo info)
    {
        foreach (var rule in rules)
        {
            if (rule.Enabled && EvaluateRule(rule, info))
            {
                return true;
            }
        }

        return false;
    }

    public static bool EvaluateRule(TriggerRule rule, MediaProbeInfo info)
    {
        if (rule.Conditions.Count == 0)
        {
            return false;
        }

        return rule.Combine == ConditionCombine.All
            ? rule.Conditions.All(c => EvaluateCondition(c, info))
            : rule.Conditions.Any(c => EvaluateCondition(c, info));
    }

    public static bool EvaluateCondition(RuleCondition condition, MediaProbeInfo info)
    {
        switch (condition.Type)
        {
            case ConditionType.VideoCodec:
                return EvaluateString(info.VideoCodec, condition);
            case ConditionType.AudioCodec:
                return AcrossTracks(condition.Operator, AudioCodecs(info), c => EvaluateString(c, condition));
            case ConditionType.Container:
                return EvaluateContainer(info.Container, condition);
            case ConditionType.VideoHeight:
                return EvaluateNumber(info.Height, condition);
            case ConditionType.VideoWidth:
                return EvaluateNumber(info.Width, condition);
            case ConditionType.VideoBitrateKbps:
                return EvaluateNumber(info.VideoBitrateKbps, condition);
            case ConditionType.AudioChannels:
                return AcrossTracks(condition.Operator, AudioChannels(info), c => EvaluateNumber(c, condition));
            case ConditionType.VideoFramerate:
                return EvaluateNumber(info.VideoFramerate, condition);
            case ConditionType.FileSizeMb:
                return EvaluateNumber(info.FileSizeMb, condition);
            case ConditionType.VideoDurationMinutes:
                return EvaluateNumber(info.DurationSeconds / 60.0, condition);
            case ConditionType.IsHdr:
                return EvaluateBool(info.IsHdr, condition);
            case ConditionType.IsDolbyVision:
                return EvaluateBool(info.IsDolbyVision, condition);
            default:
                return false;
        }
    }

    /// <summary>
    /// The value the engine actually compared a condition of this type against, rendered for the
    /// diagnostic log. Numeric fields the probe could not determine are reported as <c>unknown</c>
    /// rather than <c>0</c>, because that is the state the <c>actual &lt;= 0</c> guard reacts to: such a
    /// condition fails for <em>every</em> operator, which is otherwise indistinguishable from a genuine
    /// threshold miss.
    /// </summary>
    /// <param name="type">The condition type.</param>
    /// <param name="info">The probed media facts.</param>
    /// <returns>A human-readable rendering of the probed value.</returns>
    internal static string ActualValue(ConditionType type, MediaProbeInfo info)
    {
        return type switch
        {
            ConditionType.VideoCodec => Text(info.VideoCodec),
            ConditionType.AudioCodec => List(AudioCodecs(info).Select(Text)),
            ConditionType.Container => Text(info.Container),
            ConditionType.VideoHeight => Number(info.Height),
            ConditionType.VideoWidth => Number(info.Width),
            ConditionType.VideoBitrateKbps => Number(info.VideoBitrateKbps),
            ConditionType.AudioChannels => List(AudioChannels(info).Select(Number)),
            ConditionType.VideoFramerate => Number(info.VideoFramerate),
            ConditionType.FileSizeMb => Number(info.FileSizeMb),
            ConditionType.VideoDurationMinutes => Number(info.DurationSeconds / 60.0),
            ConditionType.IsHdr => info.IsHdr ? "true" : "false",
            ConditionType.IsDolbyVision => info.IsDolbyVision ? "true" : "false",
            _ => "(unsupported condition type)"
        };
    }

    private static string Text(string value)
    {
        return string.IsNullOrEmpty(value) ? "(none)" : value;
    }

    // Audio conditions are answered over every track, so the log has to show every track too — otherwise
    // "AudioChannels GreaterThan 2 | actual: 2 => FAIL" on a file that also carries a 5.1 track is a lie.
    private static string List(IEnumerable<string> values)
    {
        var joined = string.Join(", ", values);
        return string.IsNullOrEmpty(joined) ? "(none)" : joined;
    }

    // Every audio track, falling back to the scalar first-track fields when the probe reported no
    // per-stream detail — which is also what keeps a file with no audio behaving exactly as before.
    private static List<string> AudioCodecs(MediaProbeInfo info)
    {
        return info.AudioStreams.Count > 0
            ? info.AudioStreams.Select(a => a.Codec).ToList()
            : new List<string> { info.AudioCodec };
    }

    private static List<double> AudioChannels(MediaProbeInfo info)
    {
        return info.AudioStreams.Count > 0
            ? info.AudioStreams.Select(a => (double)a.Channels).ToList()
            : new List<double> { info.AudioChannels };
    }

    /// <summary>
    /// Answers an audio condition about the FILE rather than about one track.
    /// <para>
    /// The scalar AudioCodec/AudioChannels fields are copied from the first audio stream, so every audio
    /// condition only ever saw track one. On a remux whose commentary is muxed ahead of the main track —
    /// routine — "AudioChannels GreaterThan 2" and "AudioCodec Equals truehd" both answered about the
    /// 2-channel commentary and the file was never queued, while the compliance check next door was
    /// already reading every track and disagreeing.
    /// </para>
    /// <para>
    /// A positive operator means "some track is like this"; a negating one means "no track is", which is
    /// <c>All</c> over a predicate that is itself negating. The list is never empty — see
    /// <see cref="AudioCodecs"/> — because <c>All</c> over an empty sequence is vacuously true and would
    /// make "AudioCodec NotEquals aac" match a silent file, exactly what the guard inside
    /// <c>EvaluateString</c> exists to prevent.
    /// </para>
    /// </summary>
    private static bool AcrossTracks<T>(ComparisonOperator op, IReadOnlyList<T> tracks, Func<T, bool> matches)
    {
        return op is ComparisonOperator.NotEquals or ComparisonOperator.NotIn or ComparisonOperator.NotExists
            ? tracks.All(matches)
            : tracks.Any(matches);
    }

    /// <summary>
    /// Compares a container the way an admin means it.
    /// <para>
    /// ffprobe names a container after its demuxer — "matroska,webm" for mkv, "mov,mp4,m4a,3gp,3g2,mj2"
    /// for mp4 — while the UI tells the admin to type "mkv". Comparing those two literally meant
    /// "Container Equals mkv" matched no mkv at all, and the negating operators turned that into the
    /// opposite failure: "Container NotEquals mp4" was true for <em>every</em> mp4, so a rule written to
    /// catch what is not yet mp4 queued the entire library. The compliance checker already knew how to
    /// compare these; this uses the same rules.
    /// </para>
    /// </summary>
    private static bool EvaluateContainer(string sourceContainer, RuleCondition condition)
    {
        sourceContainer ??= string.Empty;

        // Same two guards as EvaluateString: an unfinished condition is not a filter, and an absent value
        // can only be reasoned about with Exists/NotExists.
        if (string.IsNullOrWhiteSpace(condition.Value)
            && condition.Operator is ComparisonOperator.Equals or ComparisonOperator.NotEquals
                or ComparisonOperator.In or ComparisonOperator.NotIn)
        {
            return false;
        }

        if (sourceContainer.Length == 0
            && condition.Operator is not (ComparisonOperator.Exists or ComparisonOperator.NotExists))
        {
            return false;
        }

        switch (condition.Operator)
        {
            case ComparisonOperator.Equals:
                return ProfileComplianceChecker.ContainerIs(sourceContainer, condition.Value);
            case ComparisonOperator.NotEquals:
                return !ProfileComplianceChecker.ContainerIs(sourceContainer, condition.Value);
            case ComparisonOperator.In:
                return SplitList(condition.Value).Any(v => ProfileComplianceChecker.ContainerIs(sourceContainer, v));
            case ComparisonOperator.NotIn:
                return !SplitList(condition.Value).Any(v => ProfileComplianceChecker.ContainerIs(sourceContainer, v));
            case ComparisonOperator.Exists:
                return sourceContainer.Length > 0;
            case ComparisonOperator.NotExists:
                return sourceContainer.Length == 0;
            default:
                return false;
        }
    }

    private static string Number(double value)
    {
        return value > 0
            ? value.ToString("0.###", CultureInfo.InvariantCulture)
            : "unknown (probe reported none)";
    }

    private static bool EvaluateString(string actual, RuleCondition condition)
    {
        actual ??= string.Empty;

        // A value-comparison operator with no configured value is an unfinished condition, not a filter.
        // Without this guard NotEquals/NotIn against an empty value match every file (e.g. a rule the
        // admin added but has not yet typed a codec into would queue the entire library).
        if (string.IsNullOrWhiteSpace(condition.Value)
            && condition.Operator is ComparisonOperator.Equals or ComparisonOperator.NotEquals
                or ComparisonOperator.In or ComparisonOperator.NotIn)
        {
            return false;
        }

        // An unknown/absent actual value cannot be meaningfully compared with a value operator; only
        // Exists/NotExists can test its presence. Mirrors the numeric guard (actual <= 0) so that, for
        // example, "AudioCodec NotEquals aac" does not match a file that simply has no audio track.
        if (string.IsNullOrEmpty(actual)
            && condition.Operator is not (ComparisonOperator.Exists or ComparisonOperator.NotExists))
        {
            return false;
        }

        switch (condition.Operator)
        {
            case ComparisonOperator.Equals:
                return string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
            case ComparisonOperator.NotEquals:
                return !string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
            case ComparisonOperator.In:
                return SplitList(condition.Value).Contains(actual, StringComparer.OrdinalIgnoreCase);
            case ComparisonOperator.NotIn:
                return !SplitList(condition.Value).Contains(actual, StringComparer.OrdinalIgnoreCase);
            case ComparisonOperator.Exists:
                return !string.IsNullOrEmpty(actual);
            case ComparisonOperator.NotExists:
                return string.IsNullOrEmpty(actual);
            default:
                return false;
        }
    }

    private static bool EvaluateNumber(double actual, RuleCondition condition)
    {
        // Presence tests come first and are the only way to reason about an absent value.
        switch (condition.Operator)
        {
            case ComparisonOperator.Exists:
                return actual > 0;
            case ComparisonOperator.NotExists:
                return actual <= 0;
        }

        // An unknown/absent value (<= 0) cannot be meaningfully compared against a threshold — Matroska,
        // for instance, reports no per-stream video bitrate, so VideoBitrateKbps is 0 there. Without this
        // a rule like "VideoBitrateKbps LessThan 3000" (or NotIn/NotEquals) would match every such file.
        if (actual <= 0)
        {
            return false;
        }

        if (condition.Operator is ComparisonOperator.In or ComparisonOperator.NotIn)
        {
            // An In/NotIn with no valid numbers is an unfinished/invalid condition, not a filter. Without
            // this guard a numeric NotIn against an empty or unparseable value returns true for every file
            // that has the field (SplitNumbers yields nothing, so !Any() is always true), matching the
            // whole library. The string path guards the same case at the top of EvaluateString.
            var candidates = SplitNumbers(condition.Value).ToList();
            if (candidates.Count == 0)
            {
                return false;
            }

            var inList = candidates.Any(v => NearlyEqual(v, actual));
            return condition.Operator == ComparisonOperator.In ? inList : !inList;
        }

        if (!TryParse(condition.Value, out var target))
        {
            return false;
        }

        return condition.Operator switch
        {
            ComparisonOperator.Equals => NearlyEqual(actual, target),
            ComparisonOperator.NotEquals => !NearlyEqual(actual, target),
            ComparisonOperator.GreaterThan => actual > target,
            ComparisonOperator.LessThan => actual < target,
            ComparisonOperator.GreaterThanOrEqual => actual >= target,
            ComparisonOperator.LessThanOrEqual => actual <= target,
            _ => false
        };
    }

    private static bool EvaluateBool(bool actual, RuleCondition condition)
    {
        // A half-configured condition (operator chosen, value not yet typed) must not act as a filter:
        // ParseBool("") is false, so without this "IsHdr Equals <blank>" would silently match every SDR
        // file. Only the presence operators are meaningful without a value.
        if (string.IsNullOrWhiteSpace(condition.Value)
            && condition.Operator is ComparisonOperator.Equals or ComparisonOperator.NotEquals)
        {
            return false;
        }

        switch (condition.Operator)
        {
            case ComparisonOperator.Exists:
                return actual;
            case ComparisonOperator.NotExists:
                return !actual;
            case ComparisonOperator.Equals:
                return actual == ParseBool(condition.Value);
            case ComparisonOperator.NotEquals:
                return actual != ParseBool(condition.Value);
            default:
                return false;
        }
    }

    private static string[] SplitList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<double> SplitNumbers(string value)
    {
        foreach (var token in SplitList(value))
        {
            if (TryParse(token, out var number))
            {
                yield return number;
            }
        }
    }

    private static bool TryParse(string value, out double result)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool ParseBool(string value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NearlyEqual(double a, double b)
    {
        return Math.Abs(a - b) < 0.0001;
    }
}
