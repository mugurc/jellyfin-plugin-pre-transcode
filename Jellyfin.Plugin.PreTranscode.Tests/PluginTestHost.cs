using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Tests that construct a real <see cref="Plugin"/>. Its constructor sets the static
/// <see cref="Plugin.Instance"/> that the queue, evaluator and executor all read, so these run on their
/// own rather than alongside tests that expect it unset.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginInstanceCollection
{
    public const string Name = "Plugin.Instance";
}

/// <summary>
/// A plugin configuration directory on disk, and a way to "start the server" on it: each
/// <see cref="Start"/> constructs a fresh <see cref="Plugin"/> through Jellyfin's real
/// <c>BasePlugin&lt;T&gt;</c>, which loads the file exactly as a restart would.
/// </summary>
internal sealed class PluginTestHost : IDisposable
{
    private readonly Mock<IApplicationPaths> _paths = new();

    public PluginTestHost()
    {
        Directory = Path.Combine(Path.GetTempPath(), "pt-plugin-" + Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(Directory);
        _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Directory);
        _paths.SetupGet(p => p.PluginsPath).Returns(Directory);
    }

    public string Directory { get; }

    public string ConfigFile => Path.Combine(Directory, "Jellyfin.Plugin.PreTranscode.xml");

    public ListLogger<Plugin> Logger { get; } = new();

    public JellyfinXmlSerializer Serializer { get; } = new();

    public Plugin Start() => new(_paths.Object, Serializer, Logger);

    public string[] Backups() => System.IO.Directory.GetFiles(Directory, "*.bak");

    public void Dispose()
    {
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, null);
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// A copy of Jellyfin's own <c>MyXmlSerializer</c> (Emby.Server.Implementations, which no package the tests
/// can reference ships). The asymmetry is the point: <see cref="XmlTextWriter"/> writes any character, an
/// illegal one as a character reference, while <see cref="XmlReader.Create(Stream)"/>'s default settings
/// refuse it on the way back in.
/// </summary>
internal sealed class JellyfinXmlSerializer : IXmlSerializer
{
    public object? DeserializeFromStream(Type type, Stream stream)
    {
        using var reader = XmlReader.Create(stream);
        return new XmlSerializer(type).Deserialize(reader);
    }

    public void SerializeToStream(object obj, Stream stream)
    {
        using var writer = new StreamWriter(stream, null, -1, true);
        using var textWriter = new XmlTextWriter(writer) { Formatting = Formatting.Indented };
        new XmlSerializer(obj.GetType()).Serialize(textWriter, obj);
    }

    public void SerializeToFile(object obj, string file)
    {
        using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
        SerializeToStream(obj, stream);
    }

    public object? DeserializeFromFile(Type type, string file)
    {
        using var stream = File.OpenRead(file);
        return DeserializeFromStream(type, stream);
    }

    public object? DeserializeFromBytes(Type type, byte[] buffer)
    {
        using var stream = new MemoryStream(buffer, false);
        return DeserializeFromStream(type, stream);
    }
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
