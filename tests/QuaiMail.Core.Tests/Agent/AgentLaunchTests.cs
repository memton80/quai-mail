using QuaiMail.Core.Agent;

namespace QuaiMail.Core.Tests.Agent;

public sealed class AgentLaunchTests
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_inbox_of_an_account_is_found_in_split_or_whole_arguments()
    {
        Assert.True(AgentLaunch.TryGetInbox([@"C:\Quai Mail\QuaiMail.exe", "--inbox", Id], out var split));
        Assert.Equal(Id, split);

        Assert.True(AgentLaunch.TryGetInbox([AgentLaunch.InboxArguments(Id)], out var whole));
        Assert.Equal(Id, whole);
        Assert.True(AgentLaunch.TryGetInbox([$"\"C:\\Quai Mail\\QuaiMail.exe\" --INBOX {Id}"], out _));
    }

    [Theory]
    [InlineData("--inbox")]
    [InlineData("--inbox alice@example.org")]
    [InlineData("--inbox ../../windows")]
    [InlineData("--inbox 0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("mailto:alice@example.org")]
    [InlineData("")]
    public void Anything_but_an_account_id_is_refused(string arguments)
    {
        Assert.False(AgentLaunch.TryGetInbox([arguments], out var id));
        Assert.Empty(id);
    }

    [Fact]
    public void Building_arguments_for_something_else_than_an_id_fails()
    {
        Assert.Throws<ArgumentException>(() => AgentLaunch.InboxArguments("alice@example.org"));
    }
}
