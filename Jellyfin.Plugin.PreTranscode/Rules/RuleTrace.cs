using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;

namespace Jellyfin.Plugin.PreTranscode.Rules;

/// <summary>
/// Renders a human-readable explanation of a rule-set evaluation: what the probe found, and for every
/// condition of every rule, the operator, the configured value, the value it was actually compared
/// against and whether it passed.
/// <para>
/// This exists because a rule that does not fire is otherwise undiagnosable: <see cref="RuleEvaluator"/>
/// collapses a whole rule set to a single boolean, and every skip path in the evaluator returns quietly.
/// Pure string-building over already-computed facts, so producing a trace cannot change a decision.
/// </para>
/// </summary>
internal static class RuleTrace
{
    /// <summary>
    /// One line summarising the facts every condition type reads, so a trace shows the probe result even
    /// when no rule references a given field.
    /// </summary>
    /// <param name="info">The probed media facts.</param>
    /// <returns>The summary line.</returns>
    public static string DescribeMedia(MediaProbeInfo info)
    {
        var sb = new StringBuilder();
        sb.Append("video=").Append(RuleEvaluator.ActualValue(ConditionType.VideoCodec, info))
          .Append(' ').Append(RuleEvaluator.ActualValue(ConditionType.VideoWidth, info))
          .Append('x').Append(RuleEvaluator.ActualValue(ConditionType.VideoHeight, info))
          .Append(" @ ").Append(RuleEvaluator.ActualValue(ConditionType.VideoBitrateKbps, info)).Append(" kbps")
          .Append(", fps=").Append(RuleEvaluator.ActualValue(ConditionType.VideoFramerate, info))
          .Append(", audio=").Append(RuleEvaluator.ActualValue(ConditionType.AudioCodec, info))
          .Append('/').Append(RuleEvaluator.ActualValue(ConditionType.AudioChannels, info)).Append("ch")
          .Append(", container=").Append(RuleEvaluator.ActualValue(ConditionType.Container, info))
          .Append(", duration=").Append(RuleEvaluator.ActualValue(ConditionType.VideoDurationMinutes, info)).Append(" min")
          .Append(", size=").Append(RuleEvaluator.ActualValue(ConditionType.FileSizeMb, info)).Append(" MB")
          .Append(", hdr=").Append(RuleEvaluator.ActualValue(ConditionType.IsHdr, info))
          .Append(", dolbyvision=").Append(RuleEvaluator.ActualValue(ConditionType.IsDolbyVision, info));
        return sb.ToString();
    }

    /// <summary>
    /// A multi-line breakdown of every rule and condition, ending with the overall verdict.
    /// </summary>
    /// <param name="rules">The rules that were evaluated.</param>
    /// <param name="info">The probed media facts.</param>
    /// <returns>The trace text (no trailing newline).</returns>
    public static string DescribeRules(IEnumerable<TriggerRule> rules, MediaProbeInfo info)
    {
        var sb = new StringBuilder();
        var any = false;
        var matched = false;

        foreach (var rule in rules)
        {
            any = true;
            var name = string.IsNullOrEmpty(rule.Name) ? "(unnamed)" : rule.Name;

            if (!rule.Enabled)
            {
                sb.Append("  rule '").Append(name).Append("': DISABLED").Append('\n');
                continue;
            }

            if (rule.Conditions.Count == 0)
            {
                sb.Append("  rule '").Append(name).Append("': NO CONDITIONS -> never matches").Append('\n');
                continue;
            }

            var ruleResult = RuleEvaluator.EvaluateRule(rule, info);
            matched |= ruleResult;

            sb.Append("  rule '").Append(name).Append("' (match ")
              .Append(rule.Combine == ConditionCombine.All ? "ALL" : "ANY")
              .Append(") => ").Append(ruleResult ? "MATCH" : "no match").Append('\n');

            foreach (var condition in rule.Conditions)
            {
                sb.Append("      ").Append(DescribeCondition(condition, info)).Append('\n');
            }
        }

        if (!any)
        {
            return "  no rules are configured -> nothing is ever queued";
        }

        sb.Append("  verdict: ").Append(matched ? "at least one rule matched" : "no rule matched");
        return sb.ToString();
    }

    /// <summary>
    /// One condition rendered as <c>VideoCodec In 'HEVC' | actual: hevc => PASS</c>.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="info">The probed media facts.</param>
    /// <returns>The condition line.</returns>
    public static string DescribeCondition(RuleCondition condition, MediaProbeInfo info)
    {
        var passed = RuleEvaluator.EvaluateCondition(condition, info);
        var sb = new StringBuilder();
        sb.Append(condition.Type.ToString())
          .Append(' ').Append(condition.Operator.ToString());

        // Exists/NotExists take no operand; printing an empty '' for them reads like a misconfiguration.
        if (condition.Operator is not (ComparisonOperator.Exists or ComparisonOperator.NotExists))
        {
            sb.Append(" '").Append(condition.Value ?? string.Empty).Append('\'');
            if (string.IsNullOrWhiteSpace(condition.Value))
            {
                sb.Append(" [no value set — this condition never passes]");
            }
        }

        sb.Append(" | actual: ").Append(RuleEvaluator.ActualValue(condition.Type, info))
          .Append(" => ").Append(passed ? "PASS" : "FAIL");
        return sb.ToString();
    }

    /// <summary>
    /// Explains the compliance verdict for a profile, naming the dimension that would change (or stating
    /// that nothing would).
    /// </summary>
    /// <param name="profile">The target profile.</param>
    /// <param name="info">The probed media facts.</param>
    /// <param name="presets">The configured resolution presets.</param>
    /// <returns>The compliance line.</returns>
    public static string DescribeCompliance(EncodingProfile profile, MediaProbeInfo info, IReadOnlyList<ResolutionPreset> presets)
    {
        var needsWork = ProfileComplianceChecker.NeedsWork(profile, info, presets, out var reason);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"profile '{profile.Name}' (video={profile.VideoCodec}, audio={profile.AudioCodec}, container={profile.Container}): {(needsWork ? "would change '" + reason + "'" : "source already matches every dimension this profile compares")}");
    }
}
