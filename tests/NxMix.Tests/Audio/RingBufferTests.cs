using NxMix.Audio.Dsp;

namespace NxMix.Tests.Audio;

public class RingBufferTests
{
    private static float[] Frames(int channels, long first, int count)
    {
        var a = new float[count * channels];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < channels; c++)
                a[i * channels + c] = first + i + c * 0.25f;
        return a;
    }

    [Fact]
    public void CapacityIsPowerOfTwo_AtLeastRequested()
    {
        var ring = new SpscFloatRing(2, 100);
        Assert.Equal(128, ring.CapacityFrames);
        Assert.Equal(2, ring.Channels);
    }

    [Fact]
    public void WriteThenRead_ReturnsSameFramesInOrder()
    {
        var ring = new SpscFloatRing(2, 64);
        ring.Write(Frames(2, 0, 10), 10);
        Assert.Equal(10, ring.Lag);

        var dst = new float[10 * 2];
        Assert.Equal(10, ring.Read(dst, 10));
        Assert.Equal(Frames(2, 0, 10), dst);
        Assert.Equal(0, ring.Lag);
    }

    [Fact]
    public void Read_WithTooFewFrames_ReturnsZeroAndConsumesNothing()
    {
        var ring = new SpscFloatRing(1, 32);
        ring.Write(Frames(1, 0, 5), 5);
        var dst = new float[8];
        Assert.Equal(0, ring.Read(dst, 8));
        Assert.Equal(5, ring.Lag);
    }

    [Fact]
    public void Wraparound_PreservesOrder()
    {
        var ring = new SpscFloatRing(3, 16); // Kapazitaet 16
        long next = 0, expect = 0;
        var rnd = new Random(1);
        var dst = new float[16 * 3];
        for (int iter = 0; iter < 2000; iter++)
        {
            int w = rnd.Next(1, 12);
            if (ring.Lag + w > 16) w = (int)(16 - ring.Lag);
            if (w > 0)
            {
                ring.Write(Frames(3, next, w), w);
                next += w;
            }
            int r = rnd.Next(1, 12);
            if (r > ring.Lag) r = (int)ring.Lag;
            if (r > 0)
            {
                Assert.Equal(r, ring.Read(dst, r));
                for (int i = 0; i < r; i++)
                    for (int c = 0; c < 3; c++)
                        Assert.Equal(expect + i + c * 0.25f, dst[i * 3 + c]);
                expect += r;
            }
        }
        Assert.True(next > 3000, "Der Test muss die Kapazitaet oft umrundet haben.");
    }

    [Fact]
    public void Overrun_IsDetected_AndDropOldestKeepsNewestFrames()
    {
        var ring = new SpscFloatRing(1, 16);
        for (int i = 0; i < 4; i++) ring.Write(Frames(1, i * 10, 10), 10); // 40 Frames in 16 Platz
        Assert.Equal(40, ring.Lag);

        var dst = new float[8];
        Assert.False(ring.TryPeek(dst, 8), "Ueberrundeter Leser darf keine (zerrissenen) Daten bekommen.");

        long dropped = ring.DropOldest(8);
        Assert.Equal(32, dropped);
        Assert.Equal(8, ring.Lag);
        Assert.Equal(8, ring.Read(dst, 8));
        for (int i = 0; i < 8; i++) Assert.Equal(32 + i, dst[i]);
    }

    [Fact]
    public void WriteMoreThanCapacity_KeepsNewestFrames()
    {
        var ring = new SpscFloatRing(1, 16);
        ring.Write(Frames(1, 0, 50), 50);
        Assert.Equal(50, ring.Lag);
        ring.DropOldest(16);
        var dst = new float[16];
        Assert.Equal(16, ring.Read(dst, 16));
        for (int i = 0; i < 16; i++) Assert.Equal(34 + i, dst[i]);
    }

    [Fact]
    public void DropOldest_WhenFewerThanKeep_DoesNothing()
    {
        var ring = new SpscFloatRing(1, 32);
        ring.Write(Frames(1, 0, 5), 5);
        Assert.Equal(0, ring.DropOldest(10));
        Assert.Equal(5, ring.Lag);
    }

    [Fact]
    public void TwoThreads_NoLoss_StrictOrder()
    {
        const int channels = 2;
        const long total = 6_000_000;
        var ring = new SpscFloatRing(channels, 4096);
        var done = false;

        var producer = new Thread(() =>
        {
            var rnd = new Random(7);
            var buf = new float[300 * channels];
            long n = 0;
            while (n < total)
            {
                int cnt = (int)Math.Min(rnd.Next(1, 300), total - n);
                // nie ueberfuellen: Verlustfreiheit pruefen
                while (ring.Lag + cnt > ring.CapacityFrames / 2) Thread.SpinWait(20);
                for (int i = 0; i < cnt; i++)
                    for (int c = 0; c < channels; c++)
                        buf[i * channels + c] = n + i;
                ring.Write(buf, cnt);
                n += cnt;
            }
            Volatile.Write(ref done, true);
        }) { IsBackground = true };

        long errors = 0, received = 0;
        var consumer = new Thread(() =>
        {
            var rnd = new Random(9);
            var buf = new float[200 * channels];
            while (received < total)
            {
                int want = rnd.Next(1, 200);
                long lag = ring.Lag;
                if (lag < want)
                {
                    if (Volatile.Read(ref done) && lag == 0 && received >= total) break;
                    Thread.SpinWait(20);
                    continue;
                }
                if (!ring.TryPeek(buf, want)) { Interlocked.Increment(ref errors); break; }
                for (int i = 0; i < want; i++)
                    for (int c = 0; c < channels; c++)
                        if (buf[i * channels + c] != (float)(received + i)) Interlocked.Increment(ref errors);
                ring.Advance(want);
                received += want;
            }
        }) { IsBackground = true };

        producer.Start();
        consumer.Start();
        Assert.True(producer.Join(60000), "Produzent haengt");
        Assert.True(consumer.Join(60000), "Konsument haengt");
        Assert.Equal(0, Interlocked.Read(ref errors));
        Assert.Equal(total, received);
    }

    [Fact]
    public void TwoThreads_ContinuousOverrun_NeverDeliversTornData()
    {
        const int channels = 2;
        const long total = 6_000_000; // < 2^24: als float exakt
        var ring = new SpscFloatRing(channels, 64);
        var done = false;

        var producer = new Thread(() =>
        {
            var buf = new float[16 * channels];
            long n = 0;
            while (n < total)
            {
                int cnt = (int)Math.Min(16, total - n);
                for (int i = 0; i < cnt; i++)
                    for (int c = 0; c < channels; c++)
                        buf[i * channels + c] = n + i;
                ring.Write(buf, cnt); // unbegrenzt schnell: ueberschreibt staendig die aeltesten Daten
                n += cnt;
            }
            Volatile.Write(ref done, true);
        }) { IsBackground = true };

        long errors = 0, tornDetected = 0, framesChecked = 0;
        var consumer = new Thread(() =>
        {
            var buf = new float[8 * channels];
            while (true)
            {
                long lag = ring.Lag;
                if (lag > ring.CapacityFrames)
                {
                    ring.DropOldest(ring.CapacityFrames / 2);
                    continue;
                }
                if (lag < 8)
                {
                    if (Volatile.Read(ref done) && ring.Lag < 8) break;
                    continue;
                }
                long pos = ring.TotalRead;
                if (!ring.TryPeek(buf, 8))
                {
                    tornDetected++;
                    ring.DropOldest(ring.CapacityFrames / 2);
                    continue;
                }
                // Der Wert jedes Frames ist seine globale Position: jede Abweichung = zerrissene/verschobene Daten.
                for (int i = 0; i < 8; i++)
                    for (int c = 0; c < channels; c++)
                        if (buf[i * channels + c] != (float)(pos + i)) errors++;
                framesChecked += 8;
                ring.Advance(8);
            }
        }) { IsBackground = true };

        producer.Start();
        consumer.Start();
        Assert.True(producer.Join(60000), "Produzent haengt");
        Assert.True(consumer.Join(60000), "Konsument haengt");
        Assert.Equal(0, errors);
        Assert.True(framesChecked > 1000, "Konsument hat zu wenig geprueft: " + framesChecked);
    }
}
