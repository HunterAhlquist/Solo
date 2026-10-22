using Microsoft.Extensions.Configuration;
using Solo.Api.Realtime;
using Solo.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Solo.Api.Tests;

public sealed class LiveMessageLimitsTests
{
    [Fact]
    public void Live_budgets_are_per_connection_and_disposed_on_disconnect()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["RateLimiting:Live:Burst"] = "2", ["RateLimiting:Live:TokensPerSecond"] = "1" }).Build();
        using var limits = new LiveMessageLimits(configuration);
        limits.Open("a");
        limits.Open("b");
        Assert.True(limits.TryAcquire("a"));
        Assert.True(limits.TryAcquire("a"));
        Assert.False(limits.TryAcquire("a"));
        Assert.True(limits.TryAcquire("b"));
        limits.Close("a");
        Assert.False(limits.TryAcquire("a"));
    }

    [Fact]
    public void A_second_handler_in_one_extension_is_a_registration_failure()
    {
        var services = new ServiceCollection();
        services.AddLiveMessageHandler<First>();
        Assert.Throws<InvalidOperationException>(() => services.AddLiveMessageHandler<Second>());
    }

    private sealed class First : ISoloLiveMessageHandler
    {
        public ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct) => ValueTask.CompletedTask;
    }
    private sealed class Second : ISoloLiveMessageHandler
    {
        public ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
