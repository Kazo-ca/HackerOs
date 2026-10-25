using HackerOs.Simulation.Abstractions.Events;

namespace HackerOs.Platform.Core.Tests.Events;

/// <summary>
/// Validation tests for <c>MSG-002</c> — <see cref="TopicNameBuilder"/> segment validation and
/// <see cref="TopicNames"/> root construction, per
/// doc/wasm/dev/old/Global-FileView-And-MessagingSystem/MessagingSystem.md.
/// </summary>
public sealed class TopicNameTests
{
    [Fact]
    public void ForApp_builds_the_expected_prefixed_path()
    {
        TopicName topic = TopicNames.ForApp("org.hackeros.samples.service-app").Segment("ticked").Build();

        Assert.Equal("app/org.hackeros.samples.service-app/ticked", topic.Value);
    }

    [Fact]
    public void Shared_builds_the_expected_prefixed_path()
    {
        TopicName topic = TopicNames.Shared("filesystem").Segment("changed").Segment("home-alice").Build();

        Assert.Equal("shared/filesystem/changed/home-alice", topic.Value);
    }

    [Fact]
    public void Multiple_segments_are_joined_in_order()
    {
        TopicName topic = TopicNames.ForApp("org.hackeros.app").Segment("a").Segment("b").Segment("c").Build();

        Assert.Equal("app/org.hackeros.app/a/b/c", topic.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("trailing-")]
    [InlineData("-leading")]
    [InlineData("double--hyphen")]
    [InlineData("wild*card")]
    [InlineData("dot.not.allowed")]
    public void Segment_rejects_invalid_shapes(string segment)
    {
        Assert.Throws<ArgumentException>(() => TopicNames.ForApp("org.hackeros.app").Segment(segment));
    }

    [Fact]
    public void Segment_accepts_lowercase_kebab_case()
    {
        TopicNameBuilder builder = TopicNames.ForApp("org.hackeros.app").Segment("a-valid-segment-123");

        TopicName topic = builder.Build();

        Assert.Equal("app/org.hackeros.app/a-valid-segment-123", topic.Value);
    }

    [Fact]
    public void Build_without_a_segment_throws()
    {
        Assert.Throws<InvalidOperationException>(() => TopicNames.ForApp("org.hackeros.app").Build());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has/slash")]
    [InlineData("has space")]
    public void ForApp_rejects_invalid_root(string? appId)
    {
        // ThrowsAny: a null root throws ArgumentNullException, a derived type of ArgumentException.
        Assert.ThrowsAny<ArgumentException>(() => TopicNames.ForApp(appId!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has/slash")]
    public void Shared_rejects_invalid_root(string? rootName)
    {
        Assert.ThrowsAny<ArgumentException>(() => TopicNames.Shared(rootName!));
    }
}
