using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// An independently-toggleable rule: a set of conditions combined by <see cref="Combine"/>. An item
/// is queued for pre-transcoding when <em>any</em> enabled rule matches (logical OR across rules),
/// while conditions <em>within</em> a rule are combined by AND or OR.
/// </summary>
public class TriggerRule
{
    /// <summary>
    /// Gets or sets the stable identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this rule is active.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets how the conditions are combined.
    /// </summary>
    public ConditionCombine Combine { get; set; } = ConditionCombine.All;

    /// <summary>
    /// Gets or sets the conditions.
    /// </summary>
    public List<RuleCondition> Conditions { get; set; } = new List<RuleCondition>();
}
