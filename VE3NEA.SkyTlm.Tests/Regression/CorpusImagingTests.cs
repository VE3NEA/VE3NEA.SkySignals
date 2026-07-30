using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Imaging;
using VE3NEA.SkyTlm.IO;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Regression
{
  /// <summary>
  /// The whole imaging chain over off-air IQ: samples → <see cref="StreamingPipeline"/> → frames →
  /// <see cref="IImageAssembler"/> → JPEG. Every other imaging test starts from bytes already known to
  /// be good; this one starts from radio, so it is what would catch a demod or deframe change silently
  /// costing packets.
  /// <para>
  /// The clip is <c>fsk_hades_ssdv_HADES-SA.wav</c>, cut by <see cref="SsdvCorpusBuilder"/> from a
  /// 2026-07-30 pass. It is a separate clip from the decode-regression one because that clip holds a
  /// pass's median-SNR bursts, which on HADES-SA carry telemetry — SSDV had to be selected for by
  /// packet type, not by SNR.
  /// </para>
  /// </summary>
  [Trait("Category", "Regression")]
  public class CorpusImagingTests
  {
    [Fact]
    public void HadesSa_OffAirIq_ProducesAnImage()
    {
      string path = Path.Combine(TestPaths.WavDir, "fsk_hades_ssdv_HADES-SA.wav");
      File.Exists(path).Should().BeTrue("the SSDV corpus clip must be generated (run SsdvCorpusBuilder)");

      var (samples, fs) = WavIqReader.Read(path);
      var p = SignalParamsSidecar.Load(path + ".json") with { SampleRate = fs };

      var assembler = ImageAssemblerFactory.Create(p, noradId: 68446);
      assembler.Should().NotBeNull("HADES framing means HADES-SA, which sends SSDV");

      var updates = new List<ImageProduct>();
      var completed = new List<ImageProduct>();
      assembler!.ImageUpdated += updates.Add;
      assembler.ImageCompleted += completed.Add;

      using (var sp = new StreamingPipeline(p, new StreamingOptions()))
      {
        sp.BurstDecoded += r => { foreach (var f in r.Frames) assembler.Push(f); };

        int block = Math.Max(1, (int)(0.1 * fs));
        for (int i = 0; i < samples.Length; i += block)
          sp.Push(samples.AsSpan(i, Math.Min(block, samples.Length - i)));
        sp.Flush();
      }
      assembler.Flush();

      updates.Should().HaveCountGreaterThanOrEqualTo(MinPackets,
        $"the clip was cut around bursts known to carry SSDV (accepted {updates.Count})");
      completed.Should().NotBeEmpty("Flush announces whatever was still open when the clip ended");

      var image = updates[^1];
      image.ImageId.Should().Be(235, "the clip is the run of bursts carrying image 235");
      image.Width.Should().Be(320, "every HADES-SA image seen off air is 320x240");
      image.Height.Should().Be(240);
      image.FragmentsReceived.Should().Be(updates.Count);
      image.Jpeg.Take(2).Should().Equal([0xFF, 0xD8], "a decodable JPEG comes out of the far end");
      image.Jpeg.TakeLast(2).Should().Equal([0xFF, 0xD9]);
      image.FirstGapOffset.Should().Be(-1, "SSDV has no byte-offset truth boundary");
    }

    /// <summary>What the clip yielded when it was cut. A drop here means packets are being lost
    /// somewhere upstream — detection, demod, deframing, or the CRC/RS gate.</summary>
    private const int MinPackets = 6;
  }
}
