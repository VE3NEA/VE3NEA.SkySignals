using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Audio;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.IO;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Regression
{
  /// <summary>
  /// The whole voice chain over off-air IQ: samples → <see cref="StreamingPipeline"/> → frames →
  /// <see cref="IAudioAssembler"/> → WAV. Every other audio test starts from bytes already known to be
  /// good; this one starts from radio, so it is what would catch a demod or deframe change silently
  /// costing sub-frames — and unlike imaging there is no CRC anywhere in this path, so a lost sub-frame
  /// is silence in the audio rather than an error anyone would notice.
  /// <para>
  /// The clip is <c>fsk_hades_codec2_HADES-SA.wav</c>, cut by <see cref="Codec2CorpusBuilder"/> from a
  /// 2026-04-17 pass: two consecutive bursts holding one 15-second stored message, 35 of its 37
  /// sub-frames. It is a separate clip from the decode-regression one for the same reason the imaging
  /// clip is — that clip holds a pass's median-SNR bursts, which on HADES-SA carry telemetry.
  /// </para>
  /// </summary>
  [Trait("Category", "Regression")]
  public class CorpusVoiceTests
  {
    [Fact]
    public void HadesSa_OffAirIq_ProducesAudio()
    {
      string path = Path.Combine(TestPaths.WavDir, "fsk_hades_codec2_HADES-SA.wav");
      File.Exists(path).Should().BeTrue("the voice corpus clip must be generated (run Codec2CorpusBuilder)");

      var (samples, fs) = WavIqReader.Read(path);
      var p = SignalParamsSidecar.Load(path + ".json") with { SampleRate = fs };

      var assembler = AudioAssemblerFactory.Create(p, noradId: 68446);
      assembler.Should().NotBeNull("HADES framing means HADES-SA, which sends codec2 voice");

      var updates = new List<VoiceProduct>();
      var completed = new List<VoiceProduct>();
      assembler!.VoiceUpdated += updates.Add;
      assembler.VoiceCompleted += completed.Add;

      using (var sp = new StreamingPipeline(p, new StreamingOptions()))
      {
        sp.BurstDecoded += r => { foreach (var f in r.Frames) assembler.Push(f); };

        int block = Math.Max(1, (int)(0.1 * fs));
        for (int i = 0; i < samples.Length; i += block)
          sp.Push(samples.AsSpan(i, Math.Min(block, samples.Length - i)));
        sp.Flush();
      }
      assembler.Flush();

      updates.Should().HaveCountGreaterThanOrEqualTo(MinSubFrames,
        $"the clip was cut around the two bursts carrying one message (accepted {updates.Count})");
      completed.Should().ContainSingle("the clip holds one message, announced when the stream ends");

      var voice = completed[0];
      voice.FirstNumber.Should().Be(0, "the clip starts at the beginning of the transmission");
      voice.LastNumber.Should().Be(36, "35 sub-frames of a 15-second message, numbered 0..36");
      voice.SubFramesReceived.Should().Be(updates.Count);
      voice.Complete.Should().BeFalse("sub-frames 18 and 19 fell in the gap between the two bursts");

      // duration is the message's own timeline, gaps included: 37 sub-frames of 400 ms each. This is
      // the assertion that a lost sub-frame must not shorten the recording — it becomes silence in
      // place, so what did arrive stays where it belongs.
      voice.DurationSeconds.Should().BeApproximately(
        (voice.LastNumber - voice.FirstNumber + 1) * SubFrameSeconds, 0.01);
      voice.SampleRate.Should().Be(8000);

      voice.Wav.Take(4).Should().Equal("RIFF"u8.ToArray(), "a playable WAV comes out of the far end");
      voice.Wav.Length.Should().Be(44 + (int)(voice.DurationSeconds * voice.SampleRate) * 2);

      // and what came off the air is speech rather than noise — the same 700C energy-index continuity
      // measure the unit tests calibrated (>0.4 for real sub-frames, <0.25 with the XOR mask removed).
      // Without it this test would pass on 37 sub-frames of rubbish that merely had the right shape,
      // which with no CRC anywhere in this path is exactly the failure that could go unnoticed.
      var payloads = voice.Fragments.ToDictionary(f => f.Number, f => f.Payload);
      Unit.Codec2VoiceTests.StableEnergyFraction(payloads, Audio.Codec2.Codec2Variant.HadesSa700C)
        .Should().BeGreaterThan(0.4, "off-air sub-frames decode to speech, not to random bits");
    }

    /// <summary>What the clip yielded when it was cut. A drop here means sub-frames are being lost
    /// somewhere upstream — detection, demod, or deframing.</summary>
    private const int MinSubFrames = 35;

    /// <summary>Codec2 700C: 10 frames of 320 samples at 8 kHz per sub-frame.</summary>
    private const double SubFrameSeconds = 0.4;
  }
}
