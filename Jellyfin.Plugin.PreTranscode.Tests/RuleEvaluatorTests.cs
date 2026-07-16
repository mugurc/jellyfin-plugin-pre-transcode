using System.Collections.Generic;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Media;
using Jellyfin.Plugin.PreTranscode.Rules;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class RuleEvaluatorTests
{
    private static MediaProbeInfo Info(
        string videoCodec = "h264", int width = 1920, int height = 1080,
        string audioCodec = "aac", int channels = 2, string container = "mp4",
        bool hdr = false, int videoBitrateKbps = 5000)
    {
        return new MediaProbeInfo
        {
            VideoCodec = videoCodec, Width = width, Height = height,
            AudioCodec = audioCodec, AudioChannels = channels, Container = container,
            IsHdr = hdr, VideoBitrateKbps = videoBitrateKbps
        };
    }

    private static RuleCondition Cond(ConditionType t, ComparisonOperator op, string value = "")
    {
        return new RuleCondition { Type = t, Operator = op, Value = value };
    }

    [Theory]
    [InlineData("hevc", ComparisonOperator.Equals, "hevc", true)]
    [InlineData("hevc", ComparisonOperator.Equals, "h264", false)]
    [InlineData("hevc", ComparisonOperator.NotEquals, "h264", true)]
    [InlineData("hevc", ComparisonOperator.In, "h264,hevc,av1", true)]
    [InlineData("hevc", ComparisonOperator.NotIn, "h264,av1", true)]
    [InlineData("HEVC", ComparisonOperator.Equals, "hevc", true)] // case-insensitive
    public void StringCondition_Works(string actual, ComparisonOperator op, string value, bool expected)
    {
        var info = Info(videoCodec: actual);
        Assert.Equal(expected, RuleEvaluator.EvaluateCondition(Cond(ConditionType.VideoCodec, op, value), info));
    }

    [Theory]
    [InlineData(2160, ComparisonOperator.GreaterThan, "1080", true)]
    [InlineData(720, ComparisonOperator.GreaterThan, "1080", false)]
    [InlineData(1080, ComparisonOperator.GreaterThanOrEqual, "1080", true)]
    [InlineData(720, ComparisonOperator.LessThan, "1080", true)]
    public void NumericCondition_Works(int height, ComparisonOperator op, string value, bool expected)
    {
        var info = Info(height: height);
        Assert.Equal(expected, RuleEvaluator.EvaluateCondition(Cond(ConditionType.VideoHeight, op, value), info));
    }

    [Fact]
    public void BooleanCondition_HdrExists()
    {
        Assert.True(RuleEvaluator.EvaluateCondition(Cond(ConditionType.IsHdr, ComparisonOperator.Exists), Info(hdr: true)));
        Assert.False(RuleEvaluator.EvaluateCondition(Cond(ConditionType.IsHdr, ComparisonOperator.Exists), Info(hdr: false)));
        Assert.True(RuleEvaluator.EvaluateCondition(Cond(ConditionType.IsHdr, ComparisonOperator.NotExists), Info(hdr: false)));
    }

    [Fact]
    public void RuleCombine_AllRequiresEveryCondition()
    {
        var rule = new TriggerRule
        {
            Enabled = true, Combine = ConditionCombine.All,
            Conditions = new List<RuleCondition>
            {
                Cond(ConditionType.VideoCodec, ComparisonOperator.Equals, "hevc"),
                Cond(ConditionType.VideoHeight, ComparisonOperator.GreaterThan, "1080")
            }
        };
        Assert.True(RuleEvaluator.EvaluateRule(rule, Info(videoCodec: "hevc", height: 2160)));
        Assert.False(RuleEvaluator.EvaluateRule(rule, Info(videoCodec: "hevc", height: 720)));
    }

    [Fact]
    public void RuleCombine_AnyRequiresOneCondition()
    {
        var rule = new TriggerRule
        {
            Enabled = true, Combine = ConditionCombine.Any,
            Conditions = new List<RuleCondition>
            {
                Cond(ConditionType.VideoCodec, ComparisonOperator.Equals, "hevc"),
                Cond(ConditionType.AudioChannels, ComparisonOperator.GreaterThan, "2")
            }
        };
        Assert.True(RuleEvaluator.EvaluateRule(rule, Info(videoCodec: "h264", channels: 6)));
        Assert.False(RuleEvaluator.EvaluateRule(rule, Info(videoCodec: "h264", channels: 2)));
    }

    [Fact]
    public void ShouldProcess_IgnoresDisabledRules_OrsAcrossRules()
    {
        var disabled = new TriggerRule { Enabled = false, Combine = ConditionCombine.All, Conditions = new List<RuleCondition> { Cond(ConditionType.VideoCodec, ComparisonOperator.Equals, "h264") } };
        var enabled = new TriggerRule { Enabled = true, Combine = ConditionCombine.All, Conditions = new List<RuleCondition> { Cond(ConditionType.VideoHeight, ComparisonOperator.GreaterThan, "1080") } };
        var rules = new[] { disabled, enabled };

        Assert.True(RuleEvaluator.ShouldProcess(rules, Info(videoCodec: "h264", height: 2160)));
        Assert.False(RuleEvaluator.ShouldProcess(rules, Info(videoCodec: "h264", height: 720)));
    }
}
