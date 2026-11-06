using NxMix.Engine;

namespace NxMix.Tests.Engine;

public sealed class BusRoutingTests
{
    static RoutePort Input(MixerSettings s, int i) => new(RouteKind.Input, s.Inputs[i].Id);
    static RoutePort Output(MixerSettings s, int i) => new(RouteKind.Output, s.Outputs[i].Id);
    static RoutePort Bus(MixBus b, int i) => new(RouteKind.Bus, b.Id, i);

    [Fact]
    public void Multichannel_summing_cascade_and_multiple_destinations_preserve_channel_mapping()
    {
        var s = MixerSettings.Create(48000, 3, 3);
        var a = new MixBus { Channels = 2, GainDb = -6 };
        var b = new MixBus { Channels = 2, GainDb = -12 };
        s = s with { Buses = new[] { a, b }, BusRoutes = new[]
        {
            new BusRoute(Input(s, 0), Bus(a, 0)), new BusRoute(Input(s, 1), Bus(a, 1)),
            new BusRoute(Input(s, 2), Bus(a, 0)), new BusRoute(Bus(a, 0), Bus(b, 1)),
            new BusRoute(Bus(a, 1), Output(s, 0)), new BusRoute(Bus(b, 1), Output(s, 1)),
            new BusRoute(Bus(b, 1), Output(s, 2)),
        } };
        var matrix = BusRouting.Compile(s);
        Assert.Equal(Math.Pow(10, -6.0 / 20), matrix[1, 0], 10);
        Assert.Equal(Math.Pow(10, -18.0 / 20), matrix[0, 1], 10);
        Assert.Equal(matrix[0, 1], matrix[2, 1]);
        Assert.Equal(matrix[0, 1], matrix[0, 2]);
        Assert.Equal(0, matrix[0, 0]);
        var muted = BusRouting.Compile(s with { Buses = new[] { a with { Mute = true }, b } });
        Assert.All(muted.Cast<double>(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Cycles_missing_channels_and_duplicate_routes_are_rejected()
    {
        var s = MixerSettings.Create(48000, 1, 1);
        var a = new MixBus(); var b = new MixBus();
        s = s with { Buses = new[] { a, b } };
        Assert.Throws<ArgumentException>(() => BusRouting.Compile(s with { BusRoutes = new[] { new BusRoute(Bus(a, 0), Bus(b, 0)), new BusRoute(Bus(b, 1), Bus(a, 1)) } }));
        Assert.Throws<ArgumentException>(() => BusRouting.Compile(s with { BusRoutes = new[] { new BusRoute(Input(s, 0), Bus(a, 2)) } }));
        var route = new BusRoute(Input(s, 0), Bus(a, 0));
        Assert.Throws<ArgumentException>(() => BusRouting.Compile(s with { BusRoutes = new[] { route, route } }));
    }

    [Fact]
    public void Bus_gain_reaches_real_output_and_changes_without_audio_allocations()
    {
        var s = MixerSettings.Create(48000, 1, 1);
        var bus = new MixBus { GainDb = -6 };
        s = s with { Buses = new[] { bus }, BusRoutes = new[] { new BusRoute(Input(s, 0), Bus(bus, 0)), new BusRoute(Bus(bus, 0), Output(s, 0)) } };
        var engine = new MixerEngine(s);
        var input = new[] { Enumerable.Repeat(0.1f, 480).ToArray() };
        var output = new[] { new float[480] };
        for (int i = 0; i < 100; i++) engine.Process(input, output, 480);
        Assert.Equal(0.1 * Math.Pow(10, -6.0 / 20), output[0][479], 5);
        Assert.True(engine.TrySetSettings(s with { Buses = new[] { bus with { GainDb = -12 } } }));
        for (int i = 0; i < 100; i++) engine.Process(input, output, 480);
        Assert.Equal(0.1 * Math.Pow(10, -12.0 / 20), output[0][479], 5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) engine.Process(input, output, 480);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
