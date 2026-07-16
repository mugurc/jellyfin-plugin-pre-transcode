namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// A single condition inspecting one media property. The <see cref="Value"/> is interpreted
/// according to <see cref="Type"/> and <see cref="Operator"/> (a plain value for scalar comparisons,
/// or a comma-separated list for <see cref="ComparisonOperator.In"/>/<see cref="ComparisonOperator.NotIn"/>).
/// </summary>
public class RuleCondition
{
    /// <summary>
    /// Gets or sets the media property to inspect.
    /// </summary>
    public ConditionType Type { get; set; }

    /// <summary>
    /// Gets or sets the comparison to apply.
    /// </summary>
    public ComparisonOperator Operator { get; set; }

    /// <summary>
    /// Gets or sets the comparison value (plain value, or comma-separated list). Ignored for
    /// <see cref="ComparisonOperator.Exists"/>/<see cref="ComparisonOperator.NotExists"/>.
    /// </summary>
    public string Value { get; set; } = string.Empty;
}
