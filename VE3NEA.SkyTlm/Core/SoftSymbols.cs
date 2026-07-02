namespace VE3NEA.SkyTlm.Core
{
  /// <summary>
  /// The demodulator's output for one burst: a stream of
  /// <b>soft</b> symbol decisions (signed floats — sign is the bit, magnitude is the
  /// confidence) kept <b>decoupled from framing</b>, plus the timing/quality metadata a
  /// deframer or the UI needs. For GMSK these are 1-D (real-axis) soft values from the
  /// frequency discriminator; the magnitude carries the soft information FEC wants.
  /// </summary>
  public sealed class SoftSymbols
  {
    /// <summary>Soft decisions in time order; <c>sign</c> = bit, <c>|value|</c> = confidence (≈1 = full eye).</summary>
    public required float[] Soft { get; init; }

    /// <summary>
    /// Optional per-bit log-likelihood ratios (LLRs), <c>null</c> when the demodulator only produces the
    /// scaled soft decisions in <see cref="Soft"/>. This is the extensibility hook for the deferred
    /// modulation families: binary FSK fills one LLR per symbol
    /// (1:1 with <see cref="Soft"/>), while a future M-ary FSK or PSK demod fills <c>bitsPerSymbol</c>
    /// entries per symbol. Deframers/FEC should prefer <see cref="Llr"/> when present and fall back to
    /// <see cref="Soft"/> otherwise, so the GMSK path and the existing deframers are unaffected.
    /// </summary>
    public float[]? Llr { get; init; }

    /// <summary>Symbol rate the burst was demodulated at (Bd) — same as the resolved baud.</summary>
    public required double SymbolRate { get; init; }

    /// <summary>Average samples/symbol the timing loop settled on (≈ nominal sps; differs by clock error).</summary>
    public double SamplesPerSymbol { get; init; }

    /// <summary>
    /// Eye opening as an SNR-like figure in dB: cluster separation over within-cluster spread,
    /// <c>20·log10((μ₊−μ₋)/(2σ))</c>. The headless harness uses this to validate the demod
    /// before any frame decode exists. Higher = cleaner eye.
    /// </summary>
    public double EyeSnrDb { get; init; }

    /// <summary>Fraction of symbols falling near the slicer threshold (|soft| &lt; 0.25·eye) — ambiguous bits.</summary>
    public double AmbiguousFraction { get; init; }

    public int Count => Soft.Length;
  }

  /// <summary>
  /// Demodulator output plus the intermediate signals the eye/constellation view needs:
  /// the matched-filter waveform, the recovered symbol-strobe sample positions into it, the nominal
  /// samples/symbol, and the soft symbols. Diagnostics only — the deframer just needs <see cref="Symbols"/>.
  /// </summary>
  public sealed record GmskTrace(float[] Filtered, double[] StrobePositions, double NominalSps, SoftSymbols Symbols);

  /// <summary>
  /// A modulation demodulator. One per modulation family; GMSK first.
  /// Consumes a detected, CFO-corrected burst and emits a soft-symbol stream, leaving
  /// framing/FEC to an <c>IDeframer</c>. New FSK flavors plug in here unchanged.
  /// </summary>
  public interface IDemodulator
  {
    /// <summary>Demodulate one burst of the recording's IQ to soft symbols using the resolved params.</summary>
    SoftSymbols Demodulate(MathNet.Numerics.Complex32[] iq, Burst burst, SignalParams p);
  }
}
