using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PreTranscode.Library;

/// <summary>
/// Text matching for the manual single-item search. Every word must match, each of them against either
/// the item's display label or its file path — which is what lets "someshow s03e06 1080p" find one
/// episode file when no single field contains all three words.
/// </summary>
internal static class ItemSearch
{
    private static readonly char[] Separators = new[] { ' ', '\t', '\n', '\r' };

    internal static string[] Tokenize(string? query)
        => string.IsNullOrWhiteSpace(query)
            ? Array.Empty<string>()
            : query.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    internal static bool Matches(string? label, string? path, IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if ((label is null || !label.Contains(token, StringComparison.OrdinalIgnoreCase))
                && (path is null || !path.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }
}
