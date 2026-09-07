using LR.Core.Services;

namespace LR.Core.Tests.Services;

public class ServerConcurrencyLimiterTests
{
    private readonly ServerConcurrencyLimiter _limiter = new();

    [Fact]
    public void TryAcquire_AllowsUpToCapacity_ThenRefuses()
    {
        var server = Guid.NewGuid();

        var a = _limiter.TryAcquire(server, capacity: 2);
        var b = _limiter.TryAcquire(server, capacity: 2);
        var c = _limiter.TryAcquire(server, capacity: 2);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(c);
        Assert.Equal(2, _limiter.InFlight(server));
    }

    [Fact]
    public void DisposingLease_FreesTheSlot()
    {
        var server = Guid.NewGuid();

        var a = _limiter.TryAcquire(server, capacity: 1);
        Assert.NotNull(a);
        Assert.Null(_limiter.TryAcquire(server, capacity: 1));

        a!.Dispose();

        Assert.Equal(0, _limiter.InFlight(server));
        Assert.NotNull(_limiter.TryAcquire(server, capacity: 1));
    }

    [Fact]
    public void DisposingLeaseTwice_OnlyReleasesOnce()
    {
        var server = Guid.NewGuid();

        var a = _limiter.TryAcquire(server, capacity: 2);
        var b = _limiter.TryAcquire(server, capacity: 2);

        a!.Dispose();
        a.Dispose();

        // Only one slot should have been returned — b is still in flight.
        Assert.Equal(1, _limiter.InFlight(server));
        Assert.NotNull(b);
    }

    [Fact]
    public void Counts_AreIsolatedPerServer()
    {
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        Assert.NotNull(_limiter.TryAcquire(s1, capacity: 1));
        Assert.Null(_limiter.TryAcquire(s1, capacity: 1));

        // s2 is untouched.
        Assert.NotNull(_limiter.TryAcquire(s2, capacity: 1));
        Assert.Equal(1, _limiter.InFlight(s1));
        Assert.Equal(1, _limiter.InFlight(s2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCapacity_IsTreatedAsOne(int capacity)
    {
        var server = Guid.NewGuid();

        Assert.NotNull(_limiter.TryAcquire(server, capacity));
        Assert.Null(_limiter.TryAcquire(server, capacity));
    }

    [Fact]
    public void NoopLease_DoesNotThrow_AndTracksNothing()
    {
        ServerConcurrencyLimiter.NoopLease.Dispose();
        ServerConcurrencyLimiter.NoopLease.Dispose();
    }
}
