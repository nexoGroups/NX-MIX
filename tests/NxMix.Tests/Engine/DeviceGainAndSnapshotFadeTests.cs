using NxMix.Engine;

namespace NxMix.Tests.Engine;

public sealed class DeviceGainAndSnapshotFadeTests
{
    [Fact]
    public void Pooled_engine_grows_channels_and_buses_live_with_zero_process_allocations()
    {
        var s0 = MixerSettings.Create(48000, 2, 2);
        var engine = MixerEngine.CreatePooled(s0, maxInputs: 128, maxBusChannels: 64, maxOutputs: 128);

        var inSmall = Enumerable.Range(0, 2).Select(_ => Enumerable.Repeat(0.25f, 256).ToArray()).ToArray();
        var outSmall = Enumerable.Range(0, 2).Select(_ => new float[256]).ToArray();
        for (int i = 0; i < 10; i++) engine.Process(inSmall, outSmall, 256);

        var inBufs = Enumerable.Range(0, 8).Select(_ => Enumerable.Repeat(0.25f, 256).ToArray()).ToArray();
        var outBufs = Enumerable.Range(0, 8).Select(_ => new float[256]).ToArray();

        // Grow live from 2x2 to 8x8 + 2 stereo buses without recreating MixerEngine:
        var s1 = MixerSettings.Create(48000, 8, 8);
        var busA = new MixBus { Name = "Sub", Channels = 2, GainDb = -6.0 };
        var busB = new MixBus { Name = "Main", Channels = 2, GainDb = 0.0 };
        s1 = s1 with
        {
            Buses = new[] { busA, busB },
            BusRoutes = new[]
            {
                new BusRoute(new RoutePort(RouteKind.Input, s1.Inputs[6].Id), new RoutePort(RouteKind.Bus, busA.Id, 0)),
                new BusRoute(new RoutePort(RouteKind.Bus, busA.Id, 0), new RoutePort(RouteKind.Bus, busB.Id, 1)),
                new BusRoute(new RoutePort(RouteKind.Bus, busB.Id, 1), new RoutePort(RouteKind.Output, s1.Outputs[7].Id)),
            },
        };

        Assert.True(engine.TrySetSettings(s1));

        for (int i = 0; i < 20; i++) engine.Process(inBufs, outBufs, 256);

        double expected = 0.25 * Math.Pow(10.0, -6.0 / 20.0);
        Assert.Equal(expected, outBufs[7][255], 4);

        Span<float> busPeaks = stackalloc float[2];
        engine.ReadBusPeaks(busPeaks);
        Assert.Equal(expected, busPeaks[0], 3);
        Assert.Equal(expected, busPeaks[1], 3);

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) engine.Process(inBufs, outBufs, 256);
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(allocBefore, allocAfter);
    }

    [Fact]
    public void Device_master_scales_device_channels_without_altering_relative_channel_spacing()
    {
        var devId = Guid.NewGuid();
        var s = MixerSettings.Create(48000, 2, 2);
        s.MatrixDb[0][0] = 0.0;
        s.MatrixDb[1][1] = 0.0;
        s = s with
        {
            Outputs = new[]
            {
                s.Outputs[0] with { DeviceId = devId, DeviceChannelIndex = 0, GainDb = 0.0 },
                s.Outputs[1] with { DeviceId = devId, DeviceChannelIndex = 1, GainDb = -6.0 },
            },
            DeviceMasters = new[]
            {
                new DeviceMasterDsp { Id = devId, Name = "PA Amp", IsInput = false, GainDb = -10.0 },
            },
        };

        var engine = MixerEngine.CreatePooled(s);
        var inBufs = new[]
        {
            Enumerable.Repeat(0.5f, 256).ToArray(),
            Enumerable.Repeat(0.5f, 256).ToArray(),
        };
        var outBufs = new[] { new float[256], new float[256] };

        for (int i = 0; i < 20; i++) engine.Process(inBufs, outBufs, 256);

        double expectedCh0 = 0.5 * Math.Pow(10.0, -10.0 / 20.0);
        double expectedCh1 = 0.5 * Math.Pow(10.0, -16.0 / 20.0);
        Assert.Equal(expectedCh0, outBufs[0][255], 4);
        Assert.Equal(expectedCh1, outBufs[1][255], 4);

        Assert.Equal(0.0, engine.Settings.Outputs[0].GainDb);
        Assert.Equal(-6.0, engine.Settings.Outputs[1].GainDb);

        Span<float> devPeaks = stackalloc float[1];
        engine.ReadDeviceMasterPeaks(devPeaks);
        Assert.Equal(expectedCh0, devPeaks[0], 3);

        var sDim = s with
        {
            DeviceMasters = new[]
            {
                s.DeviceMasters[0] with { Dim = true, DimDb = -20.0 },
            },
        };
        Assert.True(engine.TrySetSettings(sDim));
        for (int i = 0; i < 20; i++) engine.Process(inBufs, outBufs, 256);
        Assert.Equal(0.5 * Math.Pow(10.0, -30.0 / 20.0), outBufs[0][255], 4);
        Assert.Equal(0.5 * Math.Pow(10.0, -36.0 / 20.0), outBufs[1][255], 4);
    }

    [Fact]
    public void Parallel_direct_and_bus_routing_sum_cleanly_and_report_diagnostic_pair()
    {
        var s = MixerSettings.Create(48000, 1, 1);
        s.MatrixDb[0][0] = -6.0;
        var bus = new MixBus { Channels = 1, GainDb = -6.0 };
        s = s with
        {
            Buses = new[] { bus },
            BusRoutes = new[]
            {
                new BusRoute(new RoutePort(RouteKind.Input, s.Inputs[0].Id), new RoutePort(RouteKind.Bus, bus.Id, 0)),
                new BusRoute(new RoutePort(RouteKind.Bus, bus.Id, 0), new RoutePort(RouteKind.Output, s.Outputs[0].Id)),
            },
        };

        var parallel = BusRouting.FindParallelDirectAndBusPaths(s);
        var w = Assert.Single(parallel);
        Assert.Equal(s.Inputs[0].Id, w.InputId);
        Assert.Equal(s.Outputs[0].Id, w.OutputId);
        Assert.Equal(bus.Id, w.BusId);

        var engine = MixerEngine.CreatePooled(s);
        var inBuf = new[] { Enumerable.Repeat(0.4f, 256).ToArray() };
        var outBuf = new[] { new float[256] };
        for (int i = 0; i < 20; i++) engine.Process(inBuf, outBuf, 256);

        double singlePath = 0.4 * Math.Pow(10.0, -6.0 / 20.0);
        Assert.Equal(singlePath * 2.0, outBuf[0][255], 4);
    }

    [Fact]
    public void Snapshot_transition_ms_performs_sample_accurate_multi_block_fade()
    {
        var s0 = MixerSettings.Create(48000, 1, 1);
        s0.MatrixDb[0][0] = 0.0;
        s0 = s0 with { Inputs = new[] { s0.Inputs[0] with { Mute = true } } };

        var engine = MixerEngine.CreatePooled(s0);
        var inBuf = new[] { Enumerable.Repeat(0.5f, 480).ToArray() };
        var outBuf = new[] { new float[480] };

        for (int i = 0; i < 5; i++) engine.Process(inBuf, outBuf, 480);
        Assert.Equal(0f, outBuf[0][479], 5);

        var s1 = s0 with
        {
            Inputs = new[] { s0.Inputs[0] with { Mute = false } },
            TransitionMs = 200.0,
        };
        Assert.True(engine.TrySetSettings(s1));

        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) engine.Process(inBuf, outBuf, 480);
        float midSample = outBuf[0][479];
        for (int i = 0; i < 10; i++) engine.Process(inBuf, outBuf, 480);
        float endRampSample = outBuf[0][479];
        engine.Process(inBuf, outBuf, 480);
        long allocAfter = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(allocBefore, allocAfter);
        Assert.InRange(midSample, 0.24f, 0.26f);
        Assert.InRange(endRampSample, 0.495f, 0.500f);
        Assert.Equal(0.5f, outBuf[0][479], 5);
    }
}
