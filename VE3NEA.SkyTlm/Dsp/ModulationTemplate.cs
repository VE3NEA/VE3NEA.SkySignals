using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Expected normalized PSD shape of each modulation, used as the matched-filter template
  /// for shape scoring (CfoEstimator) and the streaming detector's matched statistic
  /// (<see cref="Core.StreamingPipeline"/>).
  /// </summary>
  internal static class ModulationTemplate
  {
    /// <summary>Root-raised-cosine roll-off assumed for the linear-PSK PSD model; mirrors
    /// <see cref="BpskDemodOptions.RrcRolloff"/> (the matched filter the demod actually uses).</summary>
    public const double PskRolloff = 0.35;

    /// <summary>Normalized expected PSD value at offset <paramref name="f"/> Hz from the carrier.</summary>
    public static double ShapeValue(double f, SignalParams p)
    {
      double baud = p.Baud;
      return p.Modulation switch
      {
        // unfiltered FSK with known deviation: two narrow tones at ±dev.
        Modulation.FSK when p.Deviation is double d && d > 0
          => Gauss(f - d, 0.30 * baud) + Gauss(f + d, 0.30 * baud),
        // blind FSK (deviation unknown): fall through to the broad bell (_) so the detector
        // captures both bell-shaped MSK/GFSK (h ≤ 1) and partially-visible wider FSK tones.
        // The M-shaped ring template is not used for
        // detection: it weights DC nearly zero, making it deaf to h ≤ 1 bell-shaped spectra
        // like SNIPE B (MSK 4k8). Deviation estimation runs in DecodeBurst on the wide avgQ.
        // GMSK/GFSK: a single bell.
        Modulation.GMSK or Modulation.GFSK => Gauss(f, 0.30 * baud),
        // linear PSK (BPSK/QPSK): the RRC-shaped baseband is a raised-cosine PSD — a near-flat lobe out to
        // ±(1−β)·Rs/2 with a cosine skirt to ±(1+β)·Rs/2. NOT a CPM/FSK bell (PSK is linear, not frequency
        // modulated), and much wider than the Gaussian bell.
        Modulation.BPSK or Modulation.QPSK => RaisedCosine(f, baud, PskRolloff),
        // AFSK-over-FM: a carrier-dominated FM-subcarrier spectrum (~93% of the power in the carrier
        // line + subcarrier sidebands), nothing like the broad bell — the mismatch dilutes the matched z
        // by several dB and the per-frame Pearson scores the near-line spectrum as an analog interloper.
        // Sample the synthesized two-stage PSD (the same model as CpmTemplate.SynthesizeAfskBank) instead.
        Modulation.AFSK => CpmTemplate.AfskDetectionShapeValue(f, p),
        // unknown width: a broad bell.
        _ => Gauss(f, 0.40 * baud),
      };
    }

    private static double Gauss(double x, double sigma) => Math.Exp(-(x * x) / (2 * sigma * sigma));

    /// <summary>Raised-cosine power spectrum (= |RRC|²) at offset <paramref name="f"/> Hz, roll-off
    /// <paramref name="beta"/>, symbol rate <paramref name="baud"/>: flat to ±(1−β)·Rs/2, cosine skirt to
    /// ±(1+β)·Rs/2, zero beyond. Peak 1 at DC.</summary>
    public static double RaisedCosine(double f, double baud, double beta)
    {
      double a = Math.Abs(f);
      double rs = baud;
      double flat = (1 - beta) * rs / 2.0;
      double edge = (1 + beta) * rs / 2.0;
      if (a <= flat) return 1.0;
      if (a >= edge) return 0.0;
      return 0.5 * (1 + Math.Cos(Math.PI / (beta * rs) * (a - flat)));
    }
  }
}
