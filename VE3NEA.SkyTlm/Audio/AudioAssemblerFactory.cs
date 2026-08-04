using VE3NEA.SkyTlm.Audio.Codec2;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Audio
{
  /// <summary>
  /// Maps a resolved <see cref="SignalParams"/> to its <see cref="IAudioAssembler"/>, or <c>null</c>
  /// when the satellite sends no voice we can reconstruct. The audio counterpart of
  /// <see cref="Imaging.ImageAssemblerFactory"/>, and the single place both the application and the
  /// tests ask, so they cannot drift apart.
  /// <para>
  /// <paramref name="noradId"/> is passed separately because <see cref="SignalParams"/> does not carry
  /// one, matching the imaging factory and <c>TelemetryRegistry.ForNorad</c>. It is unused today:
  /// <see cref="Framing.HADES"/> is flown only by HADES-SA. The rest of the GENESIS family
  /// (HADES-D/-R/-ICM, MARIA-G, UNNE-1) will need it — their packet-type tables differ, and on HADES-R
  /// type 11 is 9 bytes and is not voice at all.
  /// </para>
  /// </summary>
  public static class AudioAssemblerFactory
  {
    public static IAudioAssembler? Create(SignalParams p, int? noradId = null) => p.Framing switch
    {
      // HADES-SA packet type 11: Codec2 700C, ten 28-bit frames per sub-frame behind a fixed XOR mask.
      Framing.HADES => new Codec2VoiceAssembler(Codec2Source.HadesSa),

      _ => null
    };
  }
}
