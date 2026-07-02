using System;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VDsp = VE3NEA.Dsp;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Per-burst inputs a decision stage works from. The <see cref="CpmFskDemodulator"/> front end
  /// (channel filter → discriminator → smoothing → Gardner timing recovery) fills this, then hands it to an
  /// <see cref="IDetector"/>. A detector may reuse the discriminator's <see cref="GardnerSoft"/> directly, or
  /// resample <see cref="Baseband"/> at the recovered <see cref="Strobes"/> with its own decision rule.
  /// </summary>
  public sealed class DetectorContext
  {
    /// <summary>Channel-filtered complex baseband (the CFO-corrected burst, band-limited to ~Carson BW).</summary>
    public required Complex32[] Baseband { get; init; }

    /// <summary>Soft symbols the Gardner loop produced on the FM-discriminator path (one per recovered period).</summary>
    public required float[] GardnerSoft { get; init; }

    /// <summary>Recovered symbol-strobe sample positions into <see cref="Baseband"/>, index-aligned with <see cref="GardnerSoft"/>.</summary>
    public required double[] Strobes { get; init; }

    /// <summary>Samples per symbol the front end ran at (post-upsample rate).</summary>
    public required double Sps { get; init; }

    /// <summary>Resolved signal parameters for this burst.</summary>
    public required SignalParams Params { get; init; }
  }

  /// <summary>
  /// The pluggable decision stage of <see cref="CpmFskDemodulator"/> (the seam new FSK flavors specialize).
  /// Timing recovery, CFO and the channel filter are shared by the
  /// engine; only this final symbol-decision rule differs between flavors (discriminator slicer, DF-DD,
  /// orthogonal matched filter, coherent linear). Returns soft symbols (<c>sign</c> = bit, <c>|value|</c> =
  /// confidence), index-aligned with <see cref="DetectorContext.Strobes"/>.
  /// </summary>
  public interface IDetector
  {
    float[] Detect(DetectorContext ctx);
  }

  /// <summary>
  /// Non-coherent FM-discriminator slicer: the soft symbols already produced by the Gardner loop on the
  /// discriminator path. This is the baseline detector the FSK tools use; it passes the front end's output
  /// through unchanged.
  /// </summary>
  public sealed class DiscriminatorDetector : IDetector
  {
    public float[] Detect(DetectorContext ctx) => ctx.GardnerSoft;
  }

  /// <summary>
  /// Decision-feedback differential detection (DF-DD) of order <see cref="GmskDemodOptions.DifferentialOrder"/>
  /// on the complex baseband — the GMSK-specific detector, worth ~2.5–3 dB (N=2) over the slicer while staying
  /// non-coherent. Samples <see cref="DetectorContext.Baseband"/> at each recovered strobe plus a half-symbol
  /// offset (the differential detector's optimum is the symbol <b>boundary</b>, not the centre), optionally
  /// pre-smoothed, then for each symbol builds a phase reference from the previous <i>N</i> samples rotated by
  /// the decided phase (±π<i>h</i> per symbol — ±π/2 for GMSK/MSK, the signal's real <i>h</i> for wider GFSK)
  /// and decides <c>â_k = sign(Im{r_k·conj(ref)})</c>. Outputs the data directly — no differential decode needed
  /// on non-precoded links. The DF-DD math is the former <c>GmskDemodulator.DifferentialDetect</c>; at GMSK's
  /// h=0.5 the ±π·h step is bit-for-bit the original ±π/2, so the GMSK path stays byte-identical.
  /// </summary>
  public sealed class DifferentialDetector : IDetector
  {
    private readonly GmskDemodOptions opt;
    private readonly ModProfile profile;
    public DifferentialDetector(GmskDemodOptions opt, ModProfile profile)
    {
      this.opt = opt;
      this.profile = profile;
    }

    public float[] Detect(DetectorContext ctx)
    {
      Complex32[] chan = ctx.Baseband;
      double[] strobes = ctx.Strobes;
      double sps = ctx.Sps;
      int n = chan.Length, K = strobes.Length;
      if (K == 0) return Array.Empty<float>();

      // split to real/imag, optional complex pre-detection low-pass (zero-phase, unit DC gain)
      var re = new float[n]; var im = new float[n];
      for (int i = 0; i < n; i++) { re[i] = chan[i].Real; im[i] = chan[i].Imaginary; }
      float[] h = KernelCache.GaussianLowpass(opt.DifferentialPredetSymbols * sps);
      if (h.Length > 1) { re = LiquidFir.ConvolveSame(re, h); im = LiquidFir.ConvolveSame(im, h); }

      // sample the complex signal at the symbol boundary (strobe + half a symbol)
      double off = opt.DifferentialSampleOffset * sps;
      var rRe = new double[K]; var rIm = new double[K];
      for (int k = 0; k < K; k++)
      {
        double pos = strobes[k] + off;
        rRe[k] = VDsp.Interp(re, pos);
        rIm[k] = VDsp.Interp(im, pos);
      }

      int N = opt.DifferentialOrder;
      // per-symbol CPM phase step ±π·h. GMSK is h=0.5 (step ±π/2, byte-identical: Math.PI*0.5 ≡ Math.PI/2
      // bit-for-bit). GFSK honors the signal's real h (2·dev/Rs) so DF-DD's phase reference advances by the
      // true tone separation when h≠0.5; absent a resolved deviation it falls back to the profile default.
      double modIndex = ctx.Params.Deviation is double dev ? 2.0 * dev / ctx.Params.Baud : profile.ModIndex;
      double step = Math.PI * modIndex;
      var soft = new float[K];
      var phi = new double[K];          // cumulative decided phase (±π·h per symbol)
      // symbol 0 has no predecessor to difference against, so it is undetectable: keep phi[0] as the
      // arbitrary phase origin for the reference, but emit soft[0]=0 (no confidence) rather than a fake
      // +1 that would feed a spurious confident bit into the eye stats and the deframer.
      phi[0] = step; soft[0] = 0f;
      for (int k = 1; k < K; k++)
      {
        int nt = Math.Min(N, k);
        double refRe = 0, refIm = 0;
        for (int i = 1; i <= nt; i++)
        {
          double ang = phi[k - 1] - phi[k - i];   // rotate r[k-i] forward to the phase just before symbol k
          double c = Math.Cos(ang), s = Math.Sin(ang);
          refRe += rRe[k - i] * c - rIm[k - i] * s;
          refIm += rRe[k - i] * s + rIm[k - i] * c;
        }
        // im{ r_k · conj(ref) } ∝ sin(phase step) → +1 bit if the step is +π·h
        double imag = rIm[k] * refRe - rRe[k] * refIm;
        double mag = Math.Sqrt(rRe[k] * rRe[k] + rIm[k] * rIm[k]) * Math.Sqrt(refRe * refRe + refIm * refIm) + 1e-12;
        double sft = imag / mag;        // ∈ [−1, 1]
        int ak = sft >= 0 ? 1 : -1;
        soft[k] = (float)sft;
        phi[k] = phi[k - 1] + step * ak;
      }
      return soft;
    }
  }

  /// <summary>
  /// Non-coherent <b>orthogonal matched-filter</b> detector for wide-<i>h</i> binary FSK. When the tone separation
  /// is an integer (or large) number of symbol rates — HADES-SA is <c>h=2.0</c> (800 bps / 1600 Hz) and
  /// <c>h≈5.6</c> (200 bps / 1125 Hz) — the two FSK tones are orthogonal over a symbol, and the optimal
  /// non-coherent receiver correlates each symbol against both tones and compares <i>energies</i>, beating the
  /// FM-discriminator slicer (whose click noise dominates at low SNR). For each recovered symbol it integrates
  /// the complex baseband over one symbol period against the mark tone (lower, −dev) and the space tone (upper,
  /// +dev), then emits the normalized magnitude difference <c>(|mark|−|space|)/(|mark|+|space|)</c> ∈ [−1,1].
  /// Timing/CFO/channel-filtering are the shared <see cref="CpmFskDemodulator"/> front end; this stage only
  /// re-reads <see cref="DetectorContext.Baseband"/> at the <see cref="DetectorContext.Strobes"/>. The
  /// mark→+1 / space→−1 mapping (lower tone = bit 1 per the HADES spec) need not be exact: the deframer's
  /// <c>SyncToPacket</c> tries both polarities, absorbing any global sign/spectral inversion.
  /// </summary>
  public sealed class OrthogonalFskDetector : IDetector
  {
    private readonly ModProfile profile;
    public OrthogonalFskDetector(ModProfile profile) => this.profile = profile;

    public float[] Detect(DetectorContext ctx)
    {
      Complex32[] x = ctx.Baseband;
      double[] strobes = ctx.Strobes;
      int n = x.Length, K = strobes.Length;
      if (K == 0) return Array.Empty<float>();

      // peak deviation in Hz → the two tone offsets are ±dev about the (CFO-corrected) carrier. Prefer the
      // resolved deviation; fall back to h·Rs/2 from the profile when the recording carries no deviation.
      double dev = ctx.Params.Deviation
        ?? ctx.Params.Baud * profile.ModIndex / 2.0;

      double fs = ctx.Params.SampleRate;
      double dθ = 2.0 * Math.PI * dev / fs;          // per-sample phase of a tone at +dev
      double cosd = Math.Cos(dθ), sind = Math.Sin(dθ);
      int W = Math.Max(1, (int)Math.Round(ctx.Sps)); // integrate over one symbol period

      var soft = new float[K];
      for (int k = 0; k < K; k++)
      {
        int start = (int)Math.Round(strobes[k]) - W / 2;
        // phasor e^{+jθ} for the window, advanced by a complex multiply each sample (no per-sample trig).
        double θ0 = dθ * start;
        double pr = Math.Cos(θ0), pi = Math.Sin(θ0);
        double markRe = 0, markIm = 0, spaceRe = 0, spaceIm = 0;
        for (int i = 0; i < W; i++, start++)
        {
          if ((uint)start < (uint)n)
          {
            double xr = x[start].Real, xi = x[start].Imaginary;
            // mark = lower tone (−dev): mix up by +dev → multiply by e^{+jθ}
            markRe += xr * pr - xi * pi; markIm += xr * pi + xi * pr;
            // space = upper tone (+dev): mix down by −dev → multiply by e^{−jθ} = conj
            spaceRe += xr * pr + xi * pi; spaceIm += xi * pr - xr * pi;
          }
          double npr = pr * cosd - pi * sind; pi = pr * sind + pi * cosd; pr = npr; // p ·= e^{+jdθ}
        }
        double mark = Math.Sqrt(markRe * markRe + markIm * markIm);
        double space = Math.Sqrt(spaceRe * spaceRe + spaceIm * spaceIm);
        soft[k] = (float)((mark - space) / (mark + space + 1e-12));
      }
      return soft;
    }
  }
}
