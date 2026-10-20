using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.PreTranscode.Api;
using Jellyfin.Plugin.PreTranscode.Configuration;
using Jellyfin.Plugin.PreTranscode.Ffmpeg;
using Jellyfin.Plugin.PreTranscode.Jobs;
using Jellyfin.Plugin.PreTranscode.Library;
using Jellyfin.Plugin.PreTranscode.Media;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.PreTranscode.Tests;

/// <summary>
/// The profile a manually queued job (<c>POST PreTranscode/Jobs</c>, <c>POST PreTranscode/Jobs/Item</c>) is
/// stored with, through the real controller and queue.
/// </summary>
[Collection(PluginInstanceCollection.Name)]
public sealed class EnqueueProfileResolutionTests : IDisposable
{
    private readonly PluginTestHost _host = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly JobQueue _queue;
    private readonly PreTranscodeController _controller;
    private readonly PluginConfiguration _config;
    private readonly string _source;

    public EnqueueProfileResolutionTests()
    {
        _config = _host.Start().Configuration;
        _config.Profiles.Add(new EncodingProfile { Id = "p2", Name = "Phones" });

        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_host.Directory);
        _queue = new JobQueue(paths.Object, NullLogger<JobQueue>.Instance);
        _controller = new PreTranscodeController(
            Mock.Of<IFfmpegCapabilitiesService>(),
            _queue,
            Mock.Of<IQueueController>(),
            new ItemEvaluator(_queue, Mock.Of<IMediaProber>(), _library.Object, NullLogger<ItemEvaluator>.Instance),
            _library.Object,
            NullLogger<PreTranscodeController>.Instance);

        _source = Path.Combine(_host.Directory, "Bravo (2021).mkv");
        File.WriteAllText(_source, "not really a movie");
    }

    public void Dispose()
    {
        _queue.Dispose();
        _host.Dispose();
    }

    private static TranscodeJob Created(ActionResult<TranscodeJob> result)
    {
        return Assert.IsType<TranscodeJob>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private ActionResult<TranscodeJob> QueueItem(string profileId)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Path = _source, Name = "Bravo", ProductionYear = 2021 };
        _library.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        return _controller.EnqueueItem(new EnqueueItemRequest { ItemId = movie.Id.ToString("N"), ProfileId = profileId });
    }

    [Fact]
    public void EnqueueJob_WithoutAProfile_IsStoredUnderTheDefaultProfilesId()
    {
        var job = Created(_controller.EnqueueJob(new EnqueueJobRequest { SourcePath = _source, ProfileId = string.Empty }));

        Assert.Equal(_config.DefaultProfileId, job.ProfileId);
        Assert.False(string.IsNullOrEmpty(job.ProfileId));

        // Why it matters: once it has finished, the sweep must recognise it. It matches by exact profile id,
        // so a job stored as "" never counted as done and the file could be encoded again on every pass.
        var finished = new TranscodeJob { SourcePath = job.SourcePath, ProfileId = job.ProfileId, Status = JobStatus.Completed, OutputPath = _source + ".mp4" };
        Assert.True(ItemEvaluator.AlreadyHandled(new[] { finished }, _source, _config.DefaultProfileId, _ => 1, 3));
    }

    [Fact]
    public void EnqueueItem_WithoutAProfile_IsStoredUnderTheDefaultProfilesId()
    {
        Assert.Equal(_config.DefaultProfileId, Created(QueueItem(string.Empty)).ProfileId);
    }

    [Fact]
    public void ANamedProfile_IsKept()
    {
        Assert.Equal("p2", Created(_controller.EnqueueJob(new EnqueueJobRequest { SourcePath = _source, ProfileId = "p2" })).ProfileId);
    }

    [Fact]
    public void AnUnknownProfile_IsRejected_AndNothingIsQueued()
    {
        var byPath = _controller.EnqueueJob(new EnqueueJobRequest { SourcePath = _source, ProfileId = "deleted-long-ago" });
        var byItem = QueueItem("deleted-long-ago");

        Assert.Contains("deleted-long-ago", Assert.IsType<string>(Assert.IsType<BadRequestObjectResult>(byPath.Result).Value), StringComparison.Ordinal);
        Assert.IsType<BadRequestObjectResult>(byItem.Result);
        Assert.Empty(_queue.GetJobs());
    }

    [Fact]
    public void ADanglingDefault_ResolvesToTheFirstProfile_AsTheEvaluatorDoes()
    {
        var config = new PluginConfiguration { DefaultProfileId = "gone", Profiles = { new EncodingProfile { Id = "a" }, new EncodingProfile { Id = "b" } } };

        Assert.Equal(("a", (string?)null), PreTranscodeController.ResolveProfileId(config, null));
    }

    [Fact]
    public void RequeueJob_RefusedBecauseTheFileIsAlreadyQueued_IsAConflictNotNotFound()
    {
        // Two failed records of one file, as the jobs page lists them, each with its own Requeue button.
        var first = Created(_controller.EnqueueJob(new EnqueueJobRequest { SourcePath = _source }));
        first.Status = JobStatus.Failed;
        _queue.Update(first);
        var second = Created(_controller.EnqueueJob(new EnqueueJobRequest { SourcePath = _source }));
        second.Status = JobStatus.Failed;
        _queue.Update(second);

        Assert.IsType<NoContentResult>(_controller.RequeueJob(first.Id));
        Assert.IsType<ConflictObjectResult>(_controller.RequeueJob(second.Id));
        Assert.IsType<NotFoundResult>(_controller.RequeueJob("no-such-job"));
    }

    [Fact]
    public void WithNoProfileAtAll_NothingCanBeQueued()
    {
        var (_, error) = PreTranscodeController.ResolveProfileId(new PluginConfiguration(), string.Empty);

        Assert.NotNull(error);
        Assert.NotNull(PreTranscodeController.ResolveProfileId(null, "p1").Error);
    }
}
