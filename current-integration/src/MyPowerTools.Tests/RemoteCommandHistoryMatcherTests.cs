using RemoteCommands.Surface.Views;

namespace MyPowerTools.Tests;

public sealed class RemoteCommandHistoryMatcherTests
{
    [Fact]
    public void Matches_the_original_command_with_case_insensitive_type()
    {
        Assert.True(RemoteCommandHistoryMatcher.Matches(
            "Build project",
            "./build.sh",
            "SSH",
            "Build project",
            "./build.sh",
            "ssh"));
    }

    [Fact]
    public void Rejects_a_history_item_when_the_command_changed()
    {
        Assert.False(RemoteCommandHistoryMatcher.Matches(
            "Build project",
            "./build-v2.sh",
            "ssh",
            "Build project",
            "./build.sh",
            "ssh"));
    }
}
