using FluentAssertions;
using VE3NEA.SkyTlm.Audio;
using VE3NEA.SkyTlm.Audio.Codec2;
using VE3NEA.SkyTlm.Core;
using Xunit;

namespace VE3NEA.SkyTlm.Tests.Unit
{
  public class AudioAssemblerFactoryTests
  {
    [Fact]
    public void Hades_GetsTheCodec2VoiceAssembler()
    {
      using var asm = AudioAssemblerFactory.Create(new SignalParams(800, Modulation.FSK, Framing.HADES, 38400), 68446)
        as Codec2VoiceAssembler;
      asm.Should().NotBeNull();
    }

    [Theory]
    [InlineData(Framing.AX25G3RUH)]
    [InlineData(Framing.USP)]
    [InlineData(Framing.GEOSCAN)]
    [InlineData(Framing.AO40FEC)]
    public void EverythingElse_SendsNoVoiceWeCanDecode(Framing framing)
    {
      AudioAssemblerFactory.Create(new SignalParams(9600, Modulation.FSK, framing, 38400)).Should().BeNull();
    }
  }


  public class WavWriterTests
  {
    [Fact]
    public void Write_ProducesACanonical44ByteHeaderAndTheSamplesAfterIt()
    {
      short[] pcm = [0, 1, -1, short.MaxValue, short.MinValue];

      byte[] wav = WavWriter.Write(pcm, 8000);

      wav.Should().HaveCount(44 + pcm.Length * 2);
      System.Text.Encoding.ASCII.GetString(wav, 0, 4).Should().Be("RIFF");
      System.Text.Encoding.ASCII.GetString(wav, 8, 4).Should().Be("WAVE");
      System.Text.Encoding.ASCII.GetString(wav, 36, 4).Should().Be("data");
      System.BitConverter.ToInt32(wav, 4).Should().Be(36 + pcm.Length * 2);
      System.BitConverter.ToInt16(wav, 20).Should().Be(1);       // PCM
      System.BitConverter.ToInt16(wav, 22).Should().Be(1);       // mono
      System.BitConverter.ToInt32(wav, 24).Should().Be(8000);
      System.BitConverter.ToInt32(wav, 28).Should().Be(16000);   // byte rate
      System.BitConverter.ToInt16(wav, 32).Should().Be(2);       // block align
      System.BitConverter.ToInt16(wav, 34).Should().Be(16);
      System.BitConverter.ToInt32(wav, 40).Should().Be(pcm.Length * 2);
      System.BitConverter.ToInt16(wav, 44 + 6).Should().Be(short.MaxValue);
    }
  }
}
