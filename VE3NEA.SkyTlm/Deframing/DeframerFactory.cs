using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Deframing
{
  /// <summary>
  /// Maps a resolved <see cref="SignalParams"/> to its <see cref="IDeframer"/> (the deframer registry), or
  /// <c>null</c> when no deframer exists for the framing yet. Takes the whole params (not just
  /// <see cref="Framing"/>) because <see cref="Framing.CCSDS"/> builds its <see cref="CcsdsOptions"/> from the
  /// carry-through fields and <see cref="Framing.GEOSCAN"/> reads the per-satellite frame size from them; the
  /// other framings ignore <paramref name="p"/>. The single source of truth shared by
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
      // CC1125 sync+PN9+CRC-16 (FEC-free). The frame length is per-satellite, so the satyaml-resolved value
      // is used when the DB has one; the 74 fallback is the only size still flying (see GeoscanOptions).
      Framing.GEOSCAN => new GeoscanDeframer(new GeoscanOptions { FrameSize = p.FrameSize ?? 74 }),
      Framing.AO40FEC => new Ao40FecDeframer(),   // AO-40 syncframe+deinterleave+Viterbi+RS via libfec
      _ => null
    };
  }
}
