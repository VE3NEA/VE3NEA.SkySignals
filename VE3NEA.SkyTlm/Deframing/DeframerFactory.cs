using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Maps a resolved <see cref="SignalParams"/> to its <see cref="IDeframer"/> (the deframer registry), or
  /// <c>null</c> when no deframer exists for the framing yet. Takes the whole params (not just
  /// <see cref="Framing"/>) because <see cref="Framing.CCSDS"/> builds its <see cref="CcsdsOptions"/> from the
  /// carry-through fields; the other framings ignore <paramref name="p"/>. The single source of truth shared by
  /// the <see cref="Core.StreamingPipeline"/> and the tests so they never drift apart.
  /// </summary>
  public static class DeframerFactory
  {
    public static IDeframer? Create(SignalParams p) => p.Framing switch
    {
      Framing.AX25G3RUH => new Ax25G3ruhDeframer(),
      Framing.USP => new UspDeframer(),     // CCSDS concatenated FEC via libfec
      Framing.HADES => new HadesDeframer(), // GENESIS sync+crop+CRC+descramble (FEC-free)
      Framing.AX100ASM => new Ax100Deframer(),  // GOMspace ASM+Golay+RS via libfec
      Framing.AX100RS => new Ax100Deframer(new Ax100Options { Mode = Ax100Mode.Rs }),
      Framing.CCSDS => new CcsdsDeframer(CcsdsOptions.From(p)), // uncoded/RS/concatenated
      _ => null
    };
  }
}
