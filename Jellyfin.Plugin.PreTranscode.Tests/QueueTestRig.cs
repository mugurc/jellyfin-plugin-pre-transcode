using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// Every queue component reads its settings from the static <see cref="Plugin.Instance"/>, and building a
/// <see cref="Plugin"/> replaces it. Tests that do so run in this collection, one at a time and after the
/// parallel ones, so none of them can swap the configuration out from under another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginInstanceCollection
{
    public const string Name = "Plugin.Instance";
}

/// <summary>
/// The real <see cref="JobQueue"/>, <see cref="TranscodeExecutor"/> and <see cref="QueueProcessor"/>, wired
/// together the way the plugin wires them, with seams where a test needs to force an interleaving.
/// </summary>
internal sealed class QueueRig : IDisposable
{
    private readonly Mock<IApplicationPaths> _paths = new();
    private string _encoderPath;

    public QueueRig(string? ffmpeg = null, string? ffprobe = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "pt-rig-" + Path.GetRandomFileName());
        MediaDir = Directory.CreateDirectory(Path.Combine(Root, "media")).FullName;
        OutDir = Directory.CreateDirectory(Path.Combine(Root, "out")).FullName;
        DataDir = Directory.CreateDirectory(Path.Combine(Root, "data")).FullName;
        _paths.SetupGet(p => p.DataPath).Returns(DataDir);
        _paths.SetupGet(p => p.PluginsPath).Returns(Path.Combine(Root, "plugins"));
        _paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(Root, "plugin-config"));

        Xml.Setup(x => x.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>())).Returns(() => new PluginConfiguration());
        Plugin = new Plugin(_paths.Object, Xml.Object);
        Config.Enabled = true;
        Config.MaxConcurrentJobs = 1;
        Config.QueuePaused = false;
        Config.Profiles = new List<EncodingProfile> { Profile("p1", OutputHandlingMode.SeparateDirectory, "mkv") };
        Config.Profiles[0].OutputDirectory = OutDir;
        Config.DefaultProfileId = "p1";

        Ffmpeg = ffmpeg;
        _encoderPath = ffmpeg ?? "ffmpeg";
        Encoder.SetupGet(e => e.EncoderPath).Returns(() => _encoderPath);
        Encoder.SetupGet(e => e.ProbePath).Returns(ffprobe ?? string.Empty);

        Queue = new JobQueue(_paths.Object, NullLogger<JobQueue>.Instance);
        Hooked = new HookedQueue(Queue);
        Prober = new ScriptedProber(new MediaProber(Encoder.Object, NullLogger<MediaProber>.Instance));
        Capabilities = new FfmpegCapabilitiesService(Encoder.Object, NullLogger<FfmpegCapabilitiesService>.Instance);
        Executor = new TranscodeExecutor(
            Hooked,
            Prober,
            Encoder.Object,
            Capabilities,
            CreateWithMocks<AlternateVersionMerger>(),
            CreateWithMocks<ReplacedItemUpdater>(),
            _paths.Object,
            NullLogger<TranscodeExecutor>.Instance);
    }

    public string Root { get; }

    public string MediaDir { get; }

    public string OutDir { get; }

    public string DataDir { get; }

    public string TempDir => Path.Combine(DataDir, "pretranscode", "tmp");

    public string? Ffmpeg { get; }

    public Mock<IXmlSerializer> Xml { get; } = new();

    public Mock<IMediaEncoder> Encoder { get; } = new();

    public Plugin Plugin { get; }

    public PluginConfiguration Config => Plugin.Configuration;

    public JobQueue Queue { get; }

    public HookedQueue Hooked { get; }

    public ScriptedProber Prober { get; }

    public FfmpegCapabilitiesService Capabilities { get; }

    public TranscodeExecutor Executor { get; }

    public string EncoderPath
    {
        get => Volatile.Read(ref _encoderPath);
        set => Volatile.Write(ref _encoderPath, value);
    }

    public static EncodingProfile Profile(string id, OutputHandlingMode mode, string container, string preset = "ultrafast")
    {
        return new EncodingProfile
        {
            Id = id,
            Name = id,
            VideoCodec = "h264",
            VideoEncoder = "libx264",
            VideoQualityMode = QualityMode.Crf,
            Crf = 30,
            Preset = preset,
            AudioCodec = "aac",
            AudioEncoder = "aac",
            AudioBitrateKbps = 96,
            Container = container,
            OutputMode = mode,
            SkipIfAlreadyCompliant = false,
            ResolutionMode = ResolutionMode.Unchanged,
            ChannelPolicy = AudioChannelPolicy.Unchanged
        };
    }

    public QueueProcessor CreateProcessor(Microsoft.Extensions.Logging.ILogger<QueueProcessor>? logger = null)
    {
        return new QueueProcessor(Hooked, Executor, logger ?? NullLogger<QueueProcessor>.Instance);
    }

    public TranscodeJob Enqueue(string source, string profileId = "p1")
    {
        var job = new TranscodeJob { SourcePath = source, ProfileId = profileId, DisplayName = Path.GetFileName(source), CreatedUtc = DateTime.UtcNow };
        Assert.True(Queue.Enqueue(job));
        return job;
    }

    /// <summary>Enqueues a job and claims it, as the queue loop would before handing it to the executor.</summary>
    public TranscodeJob Claim(string source, string profileId = "p1")
    {
        var job = Enqueue(source, profileId);
        Assert.Same(job, Queue.ClaimNextPending());
        return job;
    }

    public async Task MakeClipAsync(string path, double seconds, string pattern = "testsrc2", string size = "640x360")
    {
        var duration = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ProcessRunner.RunAsync(
            Ffmpeg!,
            new[]
            {
                "-v", "error", "-y",
                "-f", "lavfi", "-i", pattern + "=size=" + size + ":rate=25:duration=" + duration,
                "-f", "lavfi", "-i", "sine=frequency=440:duration=" + duration,
                "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", path
            },
            120000,
            CancellationToken.None);
        Assert.True(File.Exists(path), "could not generate " + path);
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>Process ids of the ffmpeg running this job: its temp output path carries the job id.</summary>
    public static int[] FfmpegPidsFor(string jobId)
    {
        using var pgrep = Process.Start(new ProcessStartInfo("pgrep", "-f " + jobId) { RedirectStandardOutput = true, UseShellExecute = false })!;
        var text = pgrep.StandardOutput.ReadToEnd();
        pgrep.WaitForExit();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
    }

    public static void Signal(int pid, int signal)
    {
        _ = SysKill(pid, signal);
    }

    public static void KillLeftovers(string jobId)
    {
        foreach (var pid in FfmpegPidsFor(jobId))
        {
            Signal(pid, 9);
        }
    }

    public void Dispose()
    {
        Queue.Dispose();
        Capabilities.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a straggling ffmpeg may still hold a file for a moment.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);

    // AlternateVersionMerger and ReplacedItemUpdater only touch the library after a job completes, which
    // these tests never rely on, so whatever they depend on is a loose mock.
    private static T CreateWithMocks<T>()
    {
        return ActivatorUtilities.CreateInstance<T>(new MockingServiceProvider());
    }

    private sealed class MockingServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (!serviceType.IsInterface)
            {
                return null;
            }

            // Moq cannot proxy ILogger<T> for an internal T.
            if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(Microsoft.Extensions.Logging.ILogger<>))
            {
                return typeof(NullLogger<>).MakeGenericType(serviceType.GetGenericArguments()).GetField("Instance")!.GetValue(null);
            }

            var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(serviceType))!;
            return mock.Object;
        }
    }
}

/// <summary>A pass-through <see cref="IJobQueue"/> a test can hook to act at a precise moment.</summary>
internal sealed class HookedQueue : IJobQueue
{
    private readonly IJobQueue _inner;
    private int _plainTerminalUpdates;

    public HookedQueue(IJobQueue inner)
    {
        _inner = inner;
    }

    /// <summary>Gets or sets an action run after every update, on the updating thread.</summary>
    public Action<TranscodeJob>? AfterUpdate { get; set; }

    /// <summary>Gets how often a job arrived in a final state through the plain, unlocked update.</summary>
    public int PlainTerminalUpdates => Volatile.Read(ref _plainTerminalUpdates);

    public bool IsPaused
    {
        get => _inner.IsPaused;
        set => _inner.IsPaused = value;
    }

    public IReadOnlyList<TranscodeJob> GetJobs() => _inner.GetJobs();

    public TranscodeJob? Get(string id) => _inner.Get(id);

    public bool Enqueue(TranscodeJob job, Func<IReadOnlyList<TranscodeJob>, bool>? isRedundant = null) => _inner.Enqueue(job, isRedundant);

    public void Update(TranscodeJob job)
    {
        if (JobQuery.IsFinished(job.Status))
        {
            Interlocked.Increment(ref _plainTerminalUpdates);
        }

        _inner.Update(job);
        AfterUpdate?.Invoke(job);
    }

    public void Update(TranscodeJob job, Action<TranscodeJob> change)
    {
        _inner.Update(job, change);
        AfterUpdate?.Invoke(job);
    }

    public TranscodeJob? ClaimNextPending() => _inner.ClaimNextPending();

    public bool Cancel(string id) => _inner.Cancel(id);

    public int CancelAllPending() => _inner.CancelAllPending();

    public bool Requeue(string id) => _inner.Requeue(id);

    public bool Remove(string id) => _inner.Remove(id);

    public void ClearFinished() => _inner.ClearFinished();
}

/// <summary>An <see cref="IMediaProber"/> whose answers a test can script per call.</summary>
internal sealed class ScriptedProber : IMediaProber
{
    private readonly IMediaProber _inner;
    private int _calls;

    public ScriptedProber(IMediaProber inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Gets or sets the script: given the zero-based call index, the path and the token, a task to answer
    /// with, or <c>null</c> to let the real prober answer.
    /// </summary>
    public Func<int, string, CancellationToken, Task<MediaProbeInfo?>?>? Script { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public Task<MediaProbeInfo?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var index = Interlocked.Increment(ref _calls) - 1;
        return Script?.Invoke(index, path, cancellationToken) ?? _inner.ProbeAsync(path, cancellationToken);
    }
}

/// <summary>Keeps every formatted log message, so a test can check what was said and how often.</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_messages)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
