using QuaiMail.Core.Agent;
using QuaiMail.Core.Storage;
using QuaiMail.Core.Tests.Support;
using QuaiMail.Core.ViewModels;

namespace QuaiMail.Core.Tests.Agent;

public sealed class AgentSettingsTests
{
    private sealed class FakeAgent : IBackgroundAgent
    {
        public bool IsAvailable => true;

        public bool IsAutoStartEnabled { get; set; }

        public int Stops { get; private set; }

        public void SetAutoStart(bool enabled) => IsAutoStartEnabled = enabled;

        public void Stop() => Stops++;
    }

    [Fact]
    public void The_agent_is_on_by_default_and_its_start_with_windows_is_read_from_the_agent()
    {
        using var temp = new TempDirectory();
        var agent = new FakeAgent { IsAutoStartEnabled = true };
        var viewModel = new SettingsViewModel(new JsonSettingsService(temp.File("settings.json")), new AppPaths(temp.Path), agent);

        Assert.True(viewModel.AgentAvailable);
        Assert.True(viewModel.BackgroundAgent);
        Assert.True(viewModel.AgentAutoStart);
    }

    [Fact]
    public void Turning_the_agent_off_stops_it_and_its_start_with_windows()
    {
        using var temp = new TempDirectory();
        var settings = new JsonSettingsService(temp.File("settings.json"));
        var agent = new FakeAgent { IsAutoStartEnabled = true };
        var viewModel = new SettingsViewModel(settings, new AppPaths(temp.Path), agent);

        viewModel.BackgroundAgent = false;

        Assert.False(settings.Current.BackgroundAgent);
        Assert.Equal(1, agent.Stops);
        Assert.False(agent.IsAutoStartEnabled);
        Assert.False(viewModel.AgentAutoStart);
    }

    [Fact]
    public void The_start_with_windows_choice_is_applied_and_remembered_as_the_users()
    {
        using var temp = new TempDirectory();
        var settings = new JsonSettingsService(temp.File("settings.json"));
        var agent = new FakeAgent();
        var viewModel = new SettingsViewModel(settings, new AppPaths(temp.Path), agent);

        viewModel.AgentAutoStart = true;
        Assert.True(agent.IsAutoStartEnabled);

        viewModel.AgentAutoStart = false;
        Assert.False(agent.IsAutoStartEnabled);
        Assert.Equal(0, agent.Stops);
    }

    [Fact]
    public void Without_agent_its_settings_are_hidden_and_harmless()
    {
        using var temp = new TempDirectory();
        var viewModel = new SettingsViewModel(new JsonSettingsService(temp.File("settings.json")), new AppPaths(temp.Path));

        Assert.False(viewModel.AgentAvailable);
        viewModel.AgentAutoStart = true;
        viewModel.BackgroundAgent = false;
    }
}
