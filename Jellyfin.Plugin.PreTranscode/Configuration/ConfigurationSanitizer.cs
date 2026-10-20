using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;

namespace Jellyfin.Plugin.PreTranscode.Configuration;

/// <summary>
/// Makes an incoming <see cref="PluginConfiguration"/> safe to persist, before it replaces the live one.
/// <para>
/// Jellyfin writes the config with an <see cref="XmlWriter"/> that does not check characters but reads it
/// back with one that does, so a value it cannot round-trip is not caught when it is saved. Two such values
/// reach the config, and each one loses all of it:
/// </para>
/// <list type="bullet">
/// <item>A C0 control character in any text field (a pasted <c>ESC</c> from a terminal, say). It saves as
/// <c>&amp;#x1B;</c>, which no XML reader accepts, so at the next start the file fails to load and
/// <c>BasePlugin</c> silently replaces it with a fresh default. These are stripped: none of them means
/// anything in a name, a path or an ffmpeg argument.</item>
/// <item>An enum carrying a number it has no name for (<c>"VideoQualityMode": 7</c> over the API — the JSON
/// layer accepts integers). The serializer throws mid-write, after truncating the file, and what is left is
/// well-formed: it loads at the next start with every preset, rule and override after that value gone, and
/// <c>DefaultsSeeded</c> already set so nothing is re-seeded. These are rejected rather than coerced: there
/// is no right guess for what a resolution policy of 9 was meant to be, and Jellyfin answers the
/// <see cref="ArgumentException"/> with a 400 while the live config stays as it was.</item>
/// </list>
/// <para>
/// The graph is walked by reflection rather than field by field so that a property added later is covered
/// without anyone remembering to list it here — forgetting one is exactly how the config would be lost.
/// </para>
/// </summary>
internal static class ConfigurationSanitizer
{
    /// <summary>
    /// Validates <paramref name="config"/> and, if it is valid, cleans it in place: strips every character
    /// XML cannot carry from every string in the graph, and replaces null lists and null list entries (both
    /// only reachable over the API, and both a crash for code that iterates the config) with what a reload
    /// of the saved file would give.
    /// </summary>
    /// <param name="config">The incoming configuration.</param>
    /// <returns>The paths of the strings that were changed, e.g. <c>Profiles[0].ExtraVideoArgs</c>.</returns>
    /// <exception cref="ArgumentException">An enum holds a value its type does not define. Nothing is changed.</exception>
    public static IReadOnlyList<string> Sanitize(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var undefined = new List<string>();
        Walk(config, string.Empty, false, (path, type, value, _) =>
        {
            if (type.IsEnum && value is not null && !Enum.IsDefined(type, value))
            {
                undefined.Add(string.Format(CultureInfo.InvariantCulture, "{0} = {1} (not a {2})", path, Convert.ToInt64(value, CultureInfo.InvariantCulture), type.Name));
            }
        });

        if (undefined.Count > 0)
        {
            throw new ArgumentException("Configuration not saved; these settings hold a value their type does not define: " + string.Join("; ", undefined), nameof(config));
        }

        var stripped = new List<string>();
        Walk(config, string.Empty, true, (path, _, value, set) =>
        {
            if (value is string text)
            {
                var clean = StripInvalidXmlChars(text);
                if (!ReferenceEquals(clean, text))
                {
                    set(clean);
                    stripped.Add(path);
                }
            }
        });

        return stripped;
    }

    /// <summary>
    /// Returns <paramref name="value"/> without the characters an XML document cannot contain (C0 controls
    /// other than tab/CR/LF, unpaired surrogates, U+FFFE/U+FFFF), or the same instance when it has none.
    /// </summary>
    /// <param name="value">The text to clean.</param>
    /// <returns>The cleaned text.</returns>
    internal static string StripInvalidXmlChars(string value)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                sb?.Append(c);
            }
            else if (i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                sb?.Append(c).Append(value[i + 1]);
                i++;
            }
            else
            {
                sb ??= new StringBuilder(value.Length).Append(value, 0, i);
            }
        }

        return sb?.ToString() ?? value;
    }

    // Visits every scalar (string/enum/number/bool) property of the configuration types, descending into
    // their lists and nested configuration objects; the visitor gets the property's path, declared type,
    // current value and a setter. Only with repair are null lists and null entries fixed up — otherwise
    // they are just skipped.
    private static void Walk(object node, string path, bool repair, Action<string, Type, object?, Action<object?>> visit)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var type = property.PropertyType;
            var name = path + property.Name;
            var value = property.GetValue(node);

            if (IsConfigurationList(type))
            {
                if (value is null && repair)
                {
                    value = Activator.CreateInstance(type);
                    property.SetValue(node, value);
                }

                if (value is not IList list)
                {
                    continue;
                }

                for (var i = list.Count - 1; repair && i >= 0; i--)
                {
                    if (list[i] is null)
                    {
                        list.RemoveAt(i);
                    }
                }

                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is { } item)
                    {
                        Walk(item, string.Format(CultureInfo.InvariantCulture, "{0}[{1}].", name, i), repair, visit);
                    }
                }
            }
            else if (IsConfigurationClass(type))
            {
                if (value is not null)
                {
                    Walk(value, name + ".", repair, visit);
                }
            }
            else
            {
                visit(name, type, value, v => property.SetValue(node, v));
            }
        }
    }

    // A List<T> of one of this namespace's configuration classes (Profiles, Rules, Conditions, ...).
    private static bool IsConfigurationList(Type type)
    {
        return type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(List<>)
            && IsConfigurationClass(type.GetGenericArguments()[0]);
    }

    private static bool IsConfigurationClass(Type type)
    {
        return type.IsClass && string.Equals(type.Namespace, typeof(ConfigurationSanitizer).Namespace, StringComparison.Ordinal);
    }
}
