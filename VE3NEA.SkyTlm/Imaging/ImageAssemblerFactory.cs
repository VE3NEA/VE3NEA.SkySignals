using VE3NEA.SkyTlm.Core;
using VE3NEA.SkyTlm.Imaging.RawJpeg;
using VE3NEA.SkyTlm.Imaging.Ssdv;

namespace VE3NEA.SkyTlm.Imaging
{
  /// <summary>
  /// Maps a resolved <see cref="SignalParams"/> to its <see cref="IImageAssembler"/>, or <c>null</c> when
  /// the satellite sends no images we can reconstruct. The imaging counterpart of
  /// <see cref="Deframing.DeframerFactory"/>, and the single place both the application and the tests ask,
  /// so they cannot drift apart.
  /// <para>
  /// <paramref name="noradId"/> is passed separately because <see cref="SignalParams"/> does not carry
  /// one — the same arrangement <c>TelemetryRegistry.ForNorad</c> already uses. It is unused today:
  /// <see cref="Framing.HADES"/> is flown only by HADES-SA, so the framing alone identifies the source.
  /// The parameter is here because the families still to come need it — the Geoscan fleet shares one
  /// framing across six satellites, and the Sputnix birds share USP with satellites that send no images
  /// at all.
  /// </para>
  /// </summary>
  public static class ImageAssemblerFactory
  {
    public static IImageAssembler? Create(SignalParams p, int? noradId = null) => p.Framing switch
    {
      // HADES-SA packet type 10: standard SSDV with five constant leading bytes stripped.
      Framing.HADES => new SsdvImageAssembler(SsdvSource.HadesSa),

      // JY1SAT: the 200-byte SSDV variant riding in the tail of the AO-40 frame. AO40FEC is flown by
      // the FUNcube family, of which only this bird sends images — the others' frames simply never
      // pass the source's gate, so no norad check is needed to keep them out.
      Framing.AO40FEC => new SsdvImageAssembler(SsdvSource.Jy1Sat),

      // The Geoscan fleet plus Lobachevsky: raw JPEG byte ranges, a different assembler entirely.
      // One framing, several satellites — but the payload names the sender, so no norad check is needed.
      Framing.GEOSCAN => new RawJpegAssembler(RawJpegSource.Geoscan),

      // The Sputnix birds — Luca, 239Alferov, HyperView-1G — send JPEGs down the ordinary telemetry
      // downlink as USP file transfers. Not all USP satellites image, but the ones that do not simply
      // never send a FILETRANSFER_* message, so the source's gate is the whole filter.
      Framing.USP => new RawJpegAssembler(RawJpegSource.Usp),

      _ => null
    };
  }
}
