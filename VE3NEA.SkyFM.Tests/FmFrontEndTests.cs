using System;
using System.Collections.Generic;
using FluentAssertions;
using MathNet.Numerics;
using Xunit;

namespace VE3NEA.SkyFM.Tests
{
  /// <summary>Closed-loop tests of the FM voice front-end: an FM-modulated tone must come back at the
  /// right frequency and deviation, the out-of-band terms (Doppler DC, 67 Hz CTCSS) must be rejected,
  /// and the streaming chain must be block-size invariant.</summary>
  public class FmFrontEndTests
  {
    private const double Fs = 48000.0;

    [Fact]
    public void ToneRoundTrip_RecoversDeviation()
    {
      var audio = FmTestSignal.Tone(1000.0, 3000.0, 2.0, Fs);
      var iq = FmTestSignal.Modulate(audio, Fs);

      var res = FmDecoder.Decode(iq);
      res.SampleRate.Should().Be(16000);
      res.Voice.Length.Should().BeCloseTo(iq.Length / 3, 10);

      // measure away from the filter edges
      int skip = (int)(0.2 * res.SampleRate);
      var mid = res.Voice.AsSpan(skip, res.Voice.Length - 2 * skip);
      double amp = FmTestSignal.ToneAmplitude(mid, 1000.0, res.SampleRate);
      amp.Should().BeInRange(2700.0, 3300.0, "the 1 kHz tone must come back at the modulated 3 kHz deviation");
    }

    [Fact]
    public void DopplerDcAndCtcss_AreRejected()
    {
      // residual Doppler = +500 Hz DC on the discriminator; CTCSS = 67 Hz at 300 Hz deviation
      var ctcss = FmTestSignal.Tone(67.0, 300.0, 2.0, Fs);
      for (int i = 0; i < ctcss.Length; i++) ctcss[i] += 500f;
      var iq = FmTestSignal.Modulate(ctcss, Fs);

      var res = FmDecoder.Decode(iq);
      int skip = (int)(0.2 * res.SampleRate);
      var mid = res.Voice.AsSpan(skip, res.Voice.Length - 2 * skip);

      FmTestSignal.ToneAmplitude(mid, 67.0, res.SampleRate)
        .Should().BeLessThan(15.0, "the voice bandpass high-pass skirt must remove the CTCSS (−26 dB or better)");
      FmTestSignal.Rms(mid)
        .Should().BeLessThan(30.0, "a CTCSS+Doppler-only signal must demodulate to near-silence in the voice band");
    }

    [Fact]
    public void Streaming_IsBlockSizeInvariant()
    {
      var iq = BurstSignal();

      var whole = RunBlocks(iq, iq.Length);
      var blocks = RunBlocks(iq, 1000);

      blocks.Voice.Should().Equal(whole.Voice, "the streaming chain must not depend on block boundaries");
      blocks.Transmissions.Should().Equal(whole.Transmissions);
      blocks.Levels.Should().Equal(whole.Levels);
    }

    [Fact]
    public void SquelchSegmentation_FindsTheCarrierSpan()
    {
      var res = FmDecoder.Decode(BurstSignal());

      res.Transmissions.Should().HaveCount(1, "one keyed transmission is on the air");
      var t = res.Transmissions[0];
      t.StartSeconds.Should().BeInRange(0.75, 1.25, "the carrier keys at 1.0 s (pad + detector lag allowed)");
      t.EndSeconds.Should().BeInRange(2.35, 2.85, "the carrier drops at 2.5 s (pad + detector lag allowed)");

      // the squelch level track must show the carrier quieting between the noise spans
      int frames = res.SquelchLevelDb.Count;
      frames.Should().BeGreaterThan(150);
      float noiseDb = res.SquelchLevelDb[(int)(0.5 / res.SquelchFrameS)];
      float carrierDb = res.SquelchLevelDb[(int)(1.75 / res.SquelchFrameS)];
      (noiseDb - carrierDb).Should().BeGreaterThan(10f, "carrier capture must quiet the broadband discriminator");
    }

    /// <summary>1 s noise — 1.5 s carrier (1 kHz tone at 3 kHz deviation, light noise) — 1 s noise.</summary>
    private static Complex32[] BurstSignal()
    {
      int nNoise = (int)Math.Round(1.0 * Fs);
      var audio = FmTestSignal.Tone(1000.0, 3000.0, 1.5, Fs);
      var carrier = FmTestSignal.Modulate(audio, Fs);
      FmTestSignal.AddNoise(carrier, 0.02f, seed: 2);

      var iq = new Complex32[nNoise + carrier.Length + nNoise];
      Array.Copy(carrier, 0, iq, nNoise, carrier.Length);
      var noise = new Complex32[iq.Length];
      FmTestSignal.AddNoise(noise, 0.5f, seed: 3);
      for (int i = 0; i < nNoise; i++) iq[i] = noise[i];
      for (int i = nNoise + carrier.Length; i < iq.Length; i++) iq[i] = noise[i];
      return iq;
    }

    private static (float[] Voice, FmTransmission[] Transmissions, float[] Levels) RunBlocks(Complex32[] iq, int blockSize)
    {
      using var fe = new FmFrontEnd(new FmDecodeOptions());
      var voice = new List<float>();
      for (int at = 0; at < iq.Length; at += blockSize)
      {
        int n = Math.Min(blockSize, iq.Length - at);
        voice.AddRange(fe.Process(new ReadOnlySpan<Complex32>(iq, at, n)));
      }
      voice.AddRange(fe.Flush());
      return (voice.ToArray(), [.. fe.Transmissions], [.. fe.SquelchLevelDb]);
    }
  }
}
