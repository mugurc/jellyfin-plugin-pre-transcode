using System.Collections.Generic;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Rules;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class RuleTraceTests
{
    private static MediaProbeInfo Info()
    {
        return new MediaProbeInfo
        {
            Path = "/media/Movie.mkv",
            VideoCodec = "hevc",
            Container = "matroska,webm",
            DurationSeconds = 120 * 60,
            FileSizeBytes = 4000L * 1024 * 1024,
            Width = 1920,
            Height = 1080,
            AudioCodec = "aac",
            AudioChannels = 2
        };
    }

    [Fact]
    public void DescribeCondition_ShowsOperatorConfiguredValueActualValueAndOutcome()
    {
        var line = RuleTrace.DescribeCondition(
            new RuleCondition { Type = ConditionType.VideoCodec, Operator = ComparisonOperator.In, Value = "HEVC" },
            Info());

        Assert.Equal("VideoCodec In 'HEVC' | actual: hevc => PASS", line);
    }

    [Fact]
    public void DescribeCondition_ReportsFailure()
    {
        var line = RuleTrace.DescribeCondition(
            new RuleCondition { Type = ConditionType.FileSizeMb, Operator = ComparisonOperator.LessThan, Value = "500" },
            Info());

        Assert.Equal("FileSizeMb LessThan '500' | actual: 4000 => FAIL", line);
    }

    // The reason issue #4 was undiagnosable: an unknown numeric field fails for EVERY operator because of
    // the "actual <= 0" guard, which looks exactly like a threshold miss. The trace must say so.
    [Fact]
    public void DescribeCondition_DistinguishesUnknownValueFromThresholdMiss()
    {
        var info = Info();
        info.DurationSeconds = 0;

        var line = RuleTrace.DescribeCondition(
            new RuleCondition { Type = ConditionType.VideoDurationMinutes, Operator = ComparisonOperator.GreaterThanOrEqual, Value = "100" },
            info);

        Assert.Contains("unknown (probe reported none)", line, System.StringComparison.Ordinal);
        Assert.EndsWith("=> FAIL", line, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCondition_FlagsAnEmptyValueAsUnfinished()
    {
        var line = RuleTrace.DescribeCondition(
            new RuleCondition { Type = ConditionType.VideoCodec, Operator = ComparisonOperator.Equals, Value = string.Empty },
            Info());

        Assert.Contains("[no value set — this condition never passes]", line, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCondition_OmitsTheOperandForPresenceOperators()
    {
        var line = RuleTrace.DescribeCondition(
            new RuleCondition { Type = ConditionType.IsHdr, Operator = ComparisonOperator.NotExists, Value = string.Empty },
            Info());

        Assert.Equal("IsHdr NotExists | actual: false => PASS", line);
    }

    [Fact]
    public void DescribeRules_ReportsPerRuleOutcomeAndOverallVerdict()
    {
        var rules = new[]
        {
            new TriggerRule
            {
                Name = "h265 too big", Enabled = true, Combine = ConditionCombine.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Type = ConditionType.VideoCodec, Operator = ComparisonOperator.In, Value = "HEVC" },
                    new() { Type = ConditionType.FileSizeMb, Operator = ComparisonOperator.GreaterThanOrEqual, Value = "950" }
                }
            },
            new TriggerRule
            {
                Name = "off", Enabled = false, Combine = ConditionCombine.All,
                Conditions = new List<RuleCondition>
                {
                    new() { Type = ConditionType.VideoCodec, Operator = ComparisonOperator.Equals, Value = "h264" }
                }
            }
        };

        var text = RuleTrace.DescribeRules(rules, Info());

        Assert.Contains("rule 'h265 too big' (match ALL) => MATCH", text, System.StringComparison.Ordinal);
        Assert.Contains("rule 'off': DISABLED", text, System.StringComparison.Ordinal);
        Assert.Contains("verdict: at least one rule matched", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRules_SaysSoWhenNoRulesAreConfigured()
    {
        Assert.Contains(
            "no rules are configured",
            RuleTrace.DescribeRules(System.Array.Empty<TriggerRule>(), Info()),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeRules_CallsOutARuleWithNoConditions()
    {
        var rules = new[] { new TriggerRule { Name = "empty", Enabled = true, Conditions = new List<RuleCondition>() } };
        Assert.Contains("NO CONDITIONS", RuleTrace.DescribeRules(rules, Info()), System.StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCompliance_NamesTheDimensionThatWouldChange()
    {
        var profile = new EncodingProfile { Name = "H.265", VideoCodec = "hevc", AudioCodec = "copy", Container = "mp4" };
        Assert.Contains(
            "would change 'container differs'",
            RuleTrace.DescribeCompliance(profile, Info(), System.Array.Empty<ResolutionPreset>()),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeCompliance_SaysWhenNothingWouldChange()
    {
        var profile = new EncodingProfile { Name = "H.265", VideoCodec = "hevc", AudioCodec = "copy", Container = "matroska" };
        Assert.Contains(
            "already matches every dimension",
            RuleTrace.DescribeCompliance(profile, Info(), System.Array.Empty<ResolutionPreset>()),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeMedia_ReportsEveryFieldTheConditionsRead()
    {
        var text = RuleTrace.DescribeMedia(Info());

        Assert.Contains("video=hevc 1920x1080", text, System.StringComparison.Ordinal);
        Assert.Contains("container=matroska,webm", text, System.StringComparison.Ordinal);
        Assert.Contains("duration=120 min", text, System.StringComparison.Ordinal);
        Assert.Contains("size=4000 MB", text, System.StringComparison.Ordinal);
        // Matroska carries no per-stream video bitrate; the trace must show that as unknown, not 0.
        Assert.Contains("unknown (probe reported none) kbps", text, System.StringComparison.Ordinal);
    }
}
