using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// The shape a phase slip deposits on a complex baseband pair, tabulated at fractional arrival times —
  /// the declick plan's §3.2(2) "the template is computable, not empirical" (Phase 2a).
  ///
  /// <para>A click is a ±2π step in the received phase, so at the discriminator output it is an impulse of
  /// area ±1 cycle — and every filter downstream of the discriminator is far wider in time than the event:
  /// the ±600 Hz brightness low-pass spreads ±6 samples of click over its own ±40. What lands on the
  /// baseband pair therefore IS that low-pass's impulse response, which the branch already builds. No
  /// empirical pulse table is needed, and no ±3-sample rectangle (the FmDenoiser approximation this
  /// replaces).</para>
  ///
  /// <para>Arrival is generally between samples, so the response is tabulated at <see cref="SubSteps"/>+1
  /// fractional delays spanning one whole sample INCLUSIVE: the sinc is shifted by the delay while the
  /// window stays put (the standard windowed-sinc fractional-delay design), and each row is renormalized
  /// to unit area. Giving delay 1.0 its own row instead of wrapping it onto row 0 keeps
  /// <see cref="NearestStep"/> free of integer-carry logic, at the cost of one redundant row. Holding the
  /// window still stretches the realized delay by a measured factor of 1.00033 — 3e-4 sample at the far
  /// end of the row, four orders below the ~13-sample Robot36 pixel, so it is left uncorrected.</para>
  ///
  /// <para><b>Scaling convention.</b> Rows carry unit area, so a slip of <c>turns</c> cycles arriving at
  /// baseband time <c>t0</c> deposits <c>turns · fs · e^(−jω·t0) · row</c>, where ω is the mixer's phase
  /// step per sample. The amplitude is a real scalar and the mixer contributes a pure rotation: the slip's
  /// disc-domain shape is symmetric, so the width it does have costs only the channel filter's (real)
  /// gain at the subcarrier center. Measured against a synthetic slip through the production mixer and
  /// low-pass, the fitted scalar is 0.9988·fs per turn (that gain, 0.012 dB down) with under 3e-4 rad of
  /// phase error, and the template accounts for 99.998 % of the deposit's energy at any arrival phase —
  /// see <c>SstvClickTemplateTests</c>. Phase 2c estimates the amplitude from the data anyway, so the
  /// 0.12 % is a bias on the estimate's starting point and not on its result.</para>
  /// </summary>
  internal sealed class SstvClickTemplates
  {
    /// <summary>Fractional-delay resolution. Eighth-sample steps put the worst-case timing error at 1/16
    /// sample, two orders below the ~13-sample Robot36 pixel, so the residue of a subtraction is set by
    /// the amplitude estimate rather than by the table.</summary>
    public const int DefaultSubSteps = 8;

    private readonly float[][] rows;

    /// <summary>Fractional delays per whole sample; the table holds <c>SubSteps + 1</c> rows.</summary>
    public int SubSteps { get; }

    /// <summary>Taps per row.</summary>
    public int Length { get; }

    /// <summary>The tap that carries the arrival sample: row <c>k</c> peaks at
    /// <c>Center + k / SubSteps</c>.</summary>
    public int Center { get; }

    /// <param name="fc">Low-pass cutoff, normalized (Hz / sample rate) — the branch's own cutoff.</param>
    /// <param name="taps">Kernel length, the branch's own tap count (both brightness branches share the
    /// narrow branch's count so their group delays match, so this is not derivable from
    /// <paramref name="fc"/>).</param>
    public SstvClickTemplates(double fc, int taps, int subSteps = DefaultSubSteps)
    {
      if (taps < 1) throw new ArgumentOutOfRangeException(nameof(taps));
      if (subSteps < 1) throw new ArgumentOutOfRangeException(nameof(subSteps));

      SubSteps = subSteps;
      Length = taps;
      Center = (taps - 1) / 2;

      float[] window = global::VE3NEA.Dsp.BlackmanWindow(taps);
      rows = new float[subSteps + 1][];
      for (int k = 0; k <= subSteps; k++) rows[k] = BuildRow(window, fc, (double)k / subSteps);
    }

    /// <summary>The template for an arrival <c>step / SubSteps</c> sample past <see cref="Center"/>.</summary>
    public ReadOnlySpan<float> Row(int step) => rows[step];

    /// <summary>The tabulated step nearest a fractional delay in [0, 1].</summary>
    public int NearestStep(double frac) => Math.Clamp((int)Math.Round(frac * SubSteps), 0, SubSteps);

    /// <summary>One fractional-delay row: the windowed sinc with the sinc — and only the sinc — shifted,
    /// renormalized to unit area so the caller's amplitude carries the whole scale.</summary>
    private float[] BuildRow(float[] window, double fc, double delay)
    {
      var h = new double[window.Length];
      double sum = 0;

      for (int i = 0; i < h.Length; i++)
      {
        double x = 2 * fc * (i - Center - delay);
        double sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
        h[i] = window[i] * sinc;
        sum += h[i];
      }

      var row = new float[h.Length];
      for (int i = 0; i < h.Length; i++) row[i] = (float)(h[i] / sum);
      return row;
    }
  }
}
