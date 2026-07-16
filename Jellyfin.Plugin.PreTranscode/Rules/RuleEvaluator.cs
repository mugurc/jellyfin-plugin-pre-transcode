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
                return EvaluateString(info.AudioCodec, condition);
            case ConditionType.Container:
                return EvaluateString(info.Container, condition);
            case ConditionType.VideoHeight:
                return EvaluateNumber(info.Height, condition);
            case ConditionType.VideoWidth:
                return EvaluateNumber(info.Width, condition);
            case ConditionType.VideoBitrateKbps:
                return EvaluateNumber(info.VideoBitrateKbps, condition);
            case ConditionType.AudioChannels:
                return EvaluateNumber(info.AudioChannels, condition);
            case ConditionType.VideoFramerate:
                return EvaluateNumber(info.VideoFramerate, condition);
            case ConditionType.FileSizeMb:
                return EvaluateNumber(info.FileSizeMb, condition);
            case ConditionType.IsHdr:
                return EvaluateBool(info.IsHdr, condition);
            case ConditionType.IsDolbyVision:
                return EvaluateBool(info.IsDolbyVision, condition);
            default:
                return false;
        }
    }

    private static bool EvaluateString(string actual, RuleCondition condition)
    {
        actual ??= string.Empty;
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
        switch (condition.Operator)
        {
            case ComparisonOperator.Exists:
                return actual > 0;
            case ComparisonOperator.NotExists:
                return actual <= 0;
            case ComparisonOperator.In:
                return SplitNumbers(condition.Value).Any(v => NearlyEqual(v, actual));
            case ComparisonOperator.NotIn:
                return !SplitNumbers(condition.Value).Any(v => NearlyEqual(v, actual));
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
