using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using VE3NEA.SkyTlm.Audio;
using VE3NEA.SkyTlm.Audio.Codec2;
using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Tests.Regression;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  /// <summary>
  /// The HADES-SA Codec2 700C voice path: the source gate, the XOR/repack/decode chain, message
  /// segmentation and the assembler's product.
  /// <para>
  /// The real frames come from <c>Data/hades_kiss.log</c>, the reference UZ7HO <c>hadessa</c> modem's
  /// own KISS output — 48 type-11 frames carrying two receptions of one stored BBS message
  /// (sub-frames 7–31 plus 34, then 10–29 plus 31–32).
  /// </para>
  /// </summary>
  public class Codec2VoiceTests
  {
    private static readonly Codec2Variant V = Codec2Variant.HadesSa700C;
    private static readonly SignalParams P = new(800, Modulation.FSK, Framing.HADES, 38400);

    // ----------------------------------------------------------------------------------------------
    //                                        the source gate
    // ----------------------------------------------------------------------------------------------
    [Fact]
    public void Source_AcceptsAVoiceFrameAndSplitsItCorrectly()
    {
      var payload = Enumerable.Range(0, 35).Select(i => (byte)i).ToArray();
      var frame = VoiceFrame(17, payload);

      Codec2Source.HadesSa.TryExtract(frame, out int number, out var got).Should().BeTrue();
      number.Should().Be(17);
      got.Should().Equal(payload);
    }

    [Theory]
    [InlineData(37, (byte)0xA3, Framing.HADES)]     // type 10 (SSDV), not voice
    [InlineData(37, (byte)0xB5, Framing.HADES)]     // type 11 but source address 5, not 3
    [InlineData(36, (byte)0xB3, Framing.HADES)]     // right type, wrong length
    [InlineData(37, (byte)0xB3, Framing.AX25G3RUH)] // right bytes, wrong framing
    public void Source_RejectsWhatIsNotAVoiceSubFrame(int length, byte first, Framing framing)
    {
      var bytes = new byte[length];
      bytes[0] = first;
      var frame = new Frame { Bytes = bytes, CrcValid = null, Framing = framing };

      Codec2Source.HadesSa.TryExtract(frame, out _, out _).Should().BeFalse();
    }


    // ----------------------------------------------------------------------------------------------
    //                                      the decode chain
    // ----------------------------------------------------------------------------------------------
    [Fact]
    public void Decode_OfARealMessage_MatchesC2decSampleForSample()
    {
      // Pins the whole chain at once — XOR mask, 28-bit repack, codec2 700C — against c2dec's output
      // for the same 25 sub-frames. A wrong mask or a mis-shifted repack produces plausible noise
      // rather than an error, so nothing but a reference oracle will do. The comparison is a bound and
      // not equality only because codec2's RNG is process-global; see NativeCodec2Tests.
      var message = Messages().First();
      message.Should().HaveCount(25, "sub-frames 7..31 arrived contiguously");

      var payloads = message.ToDictionary(m => m.Number, m => m.Payload);
      using var decoder = new Codec2Decoder(V);
      short[] pcm = decoder.Decode(payloads, 7, 31);

      short[] expected = NativeCodec2Tests.ReadPcm(
        Path.Combine(TestPaths.DataDir, "Codec2", "hades_msg0_700c.raw"));
      pcm.Should().HaveCount(expected.Length);
      NativeCodec2Tests.RelativeRms(pcm, expected).Should().BeLessThan(NativeCodec2Tests.DecodeTolerance);
    }

    [Fact]
    public void Decode_OneSubFrameYields400MsOfAudio()
    {
      var one = Messages().First().Take(1).ToDictionary(m => m.Number, m => m.Payload);
      using var decoder = new Codec2Decoder(V);

      decoder.Decode(one, 7, 7).Should().HaveCount(V.SamplesPerPacket);
      V.SamplesPerPacket.Should().Be(3200);
      V.SecondsPerPacket.Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void Decode_AMissingSubFrame_IsSilenceInItsOwnPlaceAndShiftsNothingAfterIt()
    {
      var message = Messages().First().Take(3).ToList();
      var all = message.ToDictionary(m => m.Number, m => m.Payload);
      var holed = all.Where(kv => kv.Key != 8).ToDictionary(kv => kv.Key, kv => kv.Value);

      using var decoder = new Codec2Decoder(V);
      short[] whole = decoder.Decode(all, 7, 9);
      using var decoder2 = new Codec2Decoder(V);
      short[] gapped = decoder2.Decode(holed, 7, 9);

      gapped.Should().HaveCount(whole.Length, "a lost sub-frame costs its own 400 ms and no more");
      gapped.Take(V.SamplesPerPacket).Should().Contain(s => s != 0, "the first sub-frame still decodes");
      gapped.Skip(V.SamplesPerPacket).Take(V.SamplesPerPacket)
            .Should().AllSatisfy(s => s.Should().Be(0), "the hole is silence, exactly where it belongs");

      // The third sub-frame still starts at its own offset — that is the property that matters, since
      // a gap that shortened the file would slide everything after it earlier in time.
      gapped.Skip(2 * V.SamplesPerPacket).Should().Contain(s => s != 0);
    }

    [Fact]
    public void Decode_WithoutTheXorMask_IsNotSpeech()
    {
      // The mask is not decoration: without it the payload measures as random data. Asserted through
      // the 700C energy field, whose index moves by at most 1 between frames in real speech about
      // half the time, and at chance (~15 %) otherwise.
      var payloads = Messages().First().ToDictionary(m => m.Number, m => m.Payload);

      StableEnergyFraction(payloads, V).Should().BeGreaterThan(0.4);
      StableEnergyFraction(payloads, V with { XorMask = new byte[35] }).Should().BeLessThan(0.25);
    }


    // ----------------------------------------------------------------------------------------------
    //                                    message segmentation
    // ----------------------------------------------------------------------------------------------
    [Fact]
    public void Assembler_GroupsAContiguousRunIntoOneMessage()
    {
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      var products = new List<VoiceProduct>();
      asm.VoiceCompleted += products.Add;

      PushRun(asm, first: 3, count: 5, startTime: 100.0);
      asm.Flush();

      products.Should().ContainSingle();
      var p = products[0];
      p.FirstNumber.Should().Be(3);
      p.LastNumber.Should().Be(7);
      p.SubFramesReceived.Should().Be(5);
      p.SubFramesExpected.Should().Be(5);
      p.Complete.Should().BeTrue();
      p.DurationSeconds.Should().BeApproximately(2.0, 1e-6);
    }

    [Fact]
    public void Assembler_AMessageMayStartAtAnyNumber()
    {
      // Neither message in the reference capture began at 0 — the start of a transmission is routinely
      // missed, and there is no way to know how much went by before we tuned in.
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      VoiceProduct? done = null;
      asm.VoiceCompleted += p => done = p;

      PushRun(asm, first: 10, count: 3, startTime: 0.0);
      asm.Flush();

      done!.FirstNumber.Should().Be(10);
      done.DurationSeconds.Should().BeApproximately(1.2, 1e-6, "only what was heard is in the file");
    }

    [Fact]
    public void Assembler_ANumberThatDoesNotAdvance_StartsANewMessage()
    {
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      var products = new List<VoiceProduct>();
      asm.VoiceCompleted += products.Add;

      PushRun(asm, first: 5, count: 3, startTime: 0.0);
      PushRun(asm, first: 0, count: 2, startTime: 1.5);
      asm.Flush();

      products.Should().HaveCount(2);
      products[0].FirstNumber.Should().Be(5);
      products[1].FirstNumber.Should().Be(0);
    }

    [Fact]
    public void Assembler_ALongSilence_StartsANewMessage()
    {
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      var products = new List<VoiceProduct>();
      asm.VoiceCompleted += products.Add;

      PushRun(asm, first: 1, count: 2, startTime: 0.0);
      PushRun(asm, first: 3, count: 2, startTime: 60.0);   // numbers continue, four minutes later
      asm.Flush();

      products.Should().HaveCount(2, "the numbering alone cannot tell two transmissions apart");
      products[0].LastNumber.Should().Be(2);
      products[1].FirstNumber.Should().Be(3);
    }

    [Fact]
    public void Assembler_AHoleIsReportedAndFilledWithSilence()
    {
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      VoiceProduct? done = null;
      asm.VoiceCompleted += p => done = p;

      Push(asm, 0, 0.0);
      Push(asm, 2, 0.8);   // 1 never arrived
      asm.Flush();

      done!.SubFramesReceived.Should().Be(2);
      done.SubFramesExpected.Should().Be(3);
      done.Complete.Should().BeFalse();
      done.DurationSeconds.Should().BeApproximately(1.2, 1e-6, "the gap keeps its place on the timeline");
    }

    [Fact]
    public void Assembler_ADuplicateSubFrameChangesNothing()
    {
      // The overlap re-decode of a burst that straddles two windows delivers the same sub-frame twice.
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      int updates = 0;
      asm.VoiceUpdated += _ => updates++;

      Push(asm, 4, 0.0);
      Push(asm, 5, 0.4);
      Push(asm, 5, 0.5);
      asm.Flush();

      updates.Should().Be(2);
      asm.SubFramesAccepted.Should().Be(2);
    }

    [Fact]
    public void Assembler_ALateSubFrameDoesNotSplitTheMessage()
    {
      // A straggler arrives out of order, so "the number went backwards" must be judged against the
      // last number accepted, not against the highest one held.
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      var products = new List<VoiceProduct>();
      asm.VoiceCompleted += products.Add;

      Push(asm, 1, 0.0);
      Push(asm, 3, 0.8);
      Push(asm, 2, 1.0);    // late
      Push(asm, 4, 1.2);
      asm.Flush();

      products.Should().ContainSingle();
      products[0].SubFramesReceived.Should().Be(4);
      products[0].Complete.Should().BeTrue();
    }

    [Fact]
    public void Assembler_FlushFinalisesTheOpenMessageOnceOnly()
    {
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      var products = new List<VoiceProduct>();
      asm.VoiceCompleted += products.Add;

      PushRun(asm, first: 0, count: 2, startTime: 0.0);
      asm.Flush();
      asm.Flush();

      products.Should().ContainSingle();
    }

    [Fact]
    public void Assembler_IgnoresFramesThatAreNotVoice()
    {
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      int updates = 0;
      asm.VoiceUpdated += _ => updates++;

      // an SSDV frame and a telemetry frame, both of which ride the same downlink
      asm.Push(new Frame { Bytes = new byte[251], CrcValid = null, Framing = Framing.HADES });
      asm.Push(new Frame { Bytes = new byte[31], CrcValid = true, Framing = Framing.HADES });

      updates.Should().Be(0);
    }


    // ----------------------------------------------------------------------------------------------
    //                                     end to end, off air
    // ----------------------------------------------------------------------------------------------
    [Fact]
    public void Assembler_OverTheReferenceCapture_RecoversBothReceptions()
    {
      using var asm = new Codec2VoiceAssembler(Codec2Source.HadesSa);
      var products = new List<VoiceProduct>();
      asm.VoiceCompleted += products.Add;

      // The log's timestamps are whole seconds, so drive the assembler at the measured 0.4 s cadence
      // and separate the two receptions by the four minutes that actually elapsed.
      double t = 0;
      int previous = -1;
      foreach (var (number, payload) in RealSubFrames())
      {
        t += number > previous ? 0.4 : 240.0;
        previous = number;
        asm.Push(VoiceFrame(number, payload, t));
      }
      asm.Flush();

      products.Should().HaveCount(2);
      products[0].FirstNumber.Should().Be(7);
      products[0].LastNumber.Should().Be(34);
      products[0].SubFramesReceived.Should().Be(26);
      products[0].SubFramesExpected.Should().Be(28, "32 and 33 were lost");
      products[0].Complete.Should().BeFalse();

      products[1].FirstNumber.Should().Be(10);
      products[1].LastNumber.Should().Be(32);
      products[1].SubFramesReceived.Should().Be(22, "30 was lost");

      foreach (var p in products)
      {
        p.SampleRate.Should().Be(8000);
        p.Wav.Should().StartWith("RIFF"u8.ToArray());
        p.DurationSeconds.Should().BeApproximately(p.SubFramesExpected * 0.4, 1e-6);
        p.Fragments.Should().HaveCount(p.SubFramesReceived);
        p.FragmentFormat.Should().BeNull("a sub-frame carries no integrity check, so it is not archivable");
      }
    }

    [Fact]
    public void TheTwoReceptionsAreTheSameStoredMessage()
    {
      // The BBS replays one recording, so the two bursts agree on the sub-frames they share. This is
      // what makes cross-reception voting worth building, and it doubles as a check that the
      // sub-frame numbering means what this code assumes.
      var a = Messages()[0].ToDictionary(m => m.Number, m => m.Payload);
      var b = Messages()[1].ToDictionary(m => m.Number, m => m.Payload);
      var shared = a.Keys.Intersect(b.Keys).ToList();
      shared.Should().HaveCountGreaterThan(15);

      int same = 0, total = 0;
      foreach (int n in shared)
        for (int i = 0; i < 35; i++)
        {
          same += 8 - System.Numerics.BitOperations.PopCount((uint)(a[n][i] ^ b[n][i]));
          total += 8;
        }
      ((double)same / total).Should().BeGreaterThan(0.98, "the residual link BER is under 1 %");
    }


    // ----------------------------------------------------------------------------------------------
    //                                          helpers
    // ----------------------------------------------------------------------------------------------
    private static Frame VoiceFrame(int number, byte[] payload, double time = 0)
    {
      var bytes = new byte[37];
      bytes[0] = (11 << 4) | 3;
      bytes[1] = (byte)number;
      payload.CopyTo(bytes, 2);
      return new Frame { Bytes = bytes, CrcValid = null, Framing = Framing.HADES, TimeSeconds = time };
    }

    private static void Push(Codec2VoiceAssembler asm, int number, double time) =>
      asm.Push(VoiceFrame(number, new byte[35], time));

    private static void PushRun(Codec2VoiceAssembler asm, int first, int count, double startTime)
    {
      for (int i = 0; i < count; i++) Push(asm, first + i, startTime + i * 0.4);
    }

    /// <summary>Every type-11 sub-frame in the reference KISS capture, in the order it was heard.</summary>
    private static IEnumerable<(int Number, byte[] Payload)> RealSubFrames()
    {
      string path = Path.Combine(TestPaths.DataDir, "hades_kiss.log");
      foreach (string line in File.ReadLines(path))
      {
        var m = Regex.Match(line, "[0-9A-Fa-f]{16,}");
        if (!m.Success) continue;
        byte[] bytes = Convert.FromHexString(m.Value);
        if (bytes.Length != 37 || bytes[0] != ((11 << 4) | 3)) continue;
        yield return (bytes[1], bytes[2..]);
      }
    }

    /// <summary>The capture's sub-frames split into the two receptions, by ascending number.</summary>
    private static List<List<(int Number, byte[] Payload)>> Messages()
    {
      var result = new List<List<(int, byte[])>>();
      List<(int, byte[])>? current = null;
      int previous = int.MaxValue;
      foreach (var (number, payload) in RealSubFrames())
      {
        if (number != previous + 1) { current = []; result.Add(current); }
        current!.Add((number, payload));
        previous = number;
      }
      return result.Where(r => r.Count > 5).ToList();
    }

    /// <summary>
    /// Fraction of adjacent codec2 frames whose 700C energy index moves by at most 1. The 28-bit frame
    /// packs <c>vq(9) | vq(9) | energy(4) | Wo(6)</c> MSB-first, so the field can be read straight out
    /// of the repacked bits — and its smoothness is what tells speech from noise without listening.
    /// </summary>
    internal static double StableEnergyFraction(IReadOnlyDictionary<int, byte[]> payloads,
                                                Codec2Variant variant)
    {
      var energies = new List<int>();
      foreach (var payload in payloads.OrderBy(kv => kv.Key).Select(kv => kv.Value))
      {
        var unmasked = payload.Select((b, i) => (byte)(b ^ variant.XorMask[i])).ToArray();
        for (int f = 0; f < variant.FramesPerPacket; f++)
        {
          int start = f * variant.BitsPerFrame + 18;   // energy field
          int value = 0;
          for (int i = 0; i < 4; i++)
          {
            int src = start + i;
            value = (value << 1) | ((unmasked[src >> 3] >> (7 - (src & 7))) & 1);
          }
          energies.Add(value);
        }
      }
      int stable = energies.Skip(1).Where((e, i) => Math.Abs(e - energies[i]) <= 1).Count();
      return (double)stable / (energies.Count - 1);
    }
  }
}
