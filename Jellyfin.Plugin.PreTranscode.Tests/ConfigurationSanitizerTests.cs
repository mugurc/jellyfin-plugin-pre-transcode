using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.PreTranscode.Configuration;

namespace Jellyfin.Plugin.PreTranscode.Tests;

public class ConfigurationSanitizerTests
{
    // A config holding one object at every place the graph has room for one — every list gets an entry,
    // found by reflection, so a configuration type or list added later is in here without being listed.
    private static (PluginConfiguration Config, List<object> Nodes) EveryKindOfNode()
    {
        var config = new PluginConfiguration();
        var nodes = new List<object>();
        Populate(config, nodes);
        return (config, nodes);
    }

    private static void Populate(object node, List<object> nodes)
    {
        nodes.Add(node);
        foreach (var property in node.GetType().GetProperties().Where(p => p.CanWrite))
        {
            var type = property.PropertyType;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && IsConfigurationType(type.GetGenericArguments()[0]))
            {
                var item = Activator.CreateInstance(type.GetGenericArguments()[0])!;
                ((IList)property.GetValue(node)!).Add(item);
                Populate(item, nodes);
            }
            else if (IsConfigurationType(type))
            {
                var child = Activator.CreateInstance(type)!;
                property.SetValue(node, child);
                Populate(child, nodes);
            }
        }
    }

    private static bool IsConfigurationType(Type type) => type.IsClass && type.Namespace == typeof(PluginConfiguration).Namespace;

    private static IEnumerable<(object Node, PropertyInfo Property)> StringProperties(IEnumerable<object> nodes)
    {
        return nodes.SelectMany(n => n.GetType().GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite)
            .Select(p => (n, p)));
    }

    [Fact]
    public void EveryStringInTheGraph_LosesItsXmlInvalidCharacters_AndTheResultRoundTrips()
    {
        // Set by reflection, so a string property added to any configuration type later is covered by this
        // test without anyone listing it.
        var (config, nodes) = EveryKindOfNode();
        Assert.True(nodes.Count >= 8, "expected the root, a profile, a preset, two rules, two conditions and an override");
        var properties = StringProperties(nodes).ToList();
        foreach (var (node, property) in properties)
        {
            property.SetValue(node, "a\u001bb\u0000c￾d\uD800e");
        }

        var stripped = ConfigurationSanitizer.Sanitize(config);

        Assert.Equal(properties.Count, stripped.Count);
        Assert.All(properties, x => Assert.Equal("abcde", x.Property.GetValue(x.Node)));
        Assert.Contains("LibraryOverrides[0].Rules[0].Conditions[0].Value", stripped);

        var serializer = new JellyfinXmlSerializer();
        using var buffer = new MemoryStream();
        serializer.SerializeToStream(config, buffer);
        var reloaded = (PluginConfiguration)serializer.DeserializeFromBytes(typeof(PluginConfiguration), buffer.ToArray())!;
        Assert.Equal("abcde", reloaded.LibraryOverrides[0].Rules[0].Conditions[0].Value);
    }

    [Fact]
    public void WithoutTheStrip_JellyfinsSerializerWritesAFileItCannotRead()
    {
        // The failure being guarded against, pinned down so the copy of Jellyfin's serializer used by the
        // other tests is known to reproduce it.
        var config = new PluginConfiguration { Profiles = { new EncodingProfile { Name = "x\u001b" } } };
        var serializer = new JellyfinXmlSerializer();
        using var buffer = new MemoryStream();
        serializer.SerializeToStream(config, buffer);

        Assert.Throws<InvalidOperationException>(() => serializer.DeserializeFromBytes(typeof(PluginConfiguration), buffer.ToArray()));
    }

    [Theory]
    [InlineData("-tune film -x264-params \"aq-mode=3\"")]
    [InlineData("tab\there\r\nnew line")]
    [InlineData("Filme \U0001F3AC Ünïcödé")]
    [InlineData("")]
    public void ValidText_IsLeftAsTheSameInstance(string text)
    {
        Assert.Same(text, ConfigurationSanitizer.StripInvalidXmlChars(text));
    }

    [Fact]
    public void ACleanConfig_IsReportedUnchanged()
    {
        var config = new PluginConfiguration();
        ConfigurationInitializer.Normalize(config);

        Assert.Empty(ConfigurationSanitizer.Sanitize(config));
    }

    [Fact]
    public void AnUndefinedEnum_ThrowsBeforeAnyStringIsTouched()
    {
        var (config, _) = EveryKindOfNode();
        config.Profiles[0].Name = "x\u001b";
        config.Profiles[0].OutputMode = (OutputHandlingMode)3;

        var ex = Assert.Throws<ArgumentException>(() => ConfigurationSanitizer.Sanitize(config));

        Assert.Contains("Profiles[0].OutputMode = 3 (not a OutputHandlingMode)", ex.Message, StringComparison.Ordinal);
        Assert.Equal("x\u001b", config.Profiles[0].Name);
    }
}
