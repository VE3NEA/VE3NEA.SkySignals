using System;
using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;
using VDsp = VE3NEA.Dsp;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>
  /// Coherent MLSE (Viterbi) CPM detector with per-survivor processing (PSP). For h = 1/2 binary CPM (GMSK/MSK) the
  /// Laurent decomposition writes the signal as PAM with one dominant pulse C₀ carrying ~99% of the
  /// energy: <c>s(t) ≈ Σ b_n·C₀(t−nT)</c> with pseudo-symbols <c>b_n = b_{n−1}·j^{a_n}</c> (π/2-shifted
  /// BPSK). The front end here is a single filter matched to C₀, sampled at the symbol boundaries; its
  /// output contains <i>deterministic</i> ISI (C₀ spans ~L symbols), which the Viterbi treats as
  /// evidence rather than noise: state = (cumulative phase ∈ 4 values, last 2M bits), branch metric
  /// <c>−|r·e^{−jφ} − ŝ|²</c> against the expected ISI-bearing sample. This is what the symbol-by-symbol
  /// DF-DD slicer fundamentally cannot do, and it lands within ~0.5 dB of antipodal signaling.
  ///
  /// <para><b>PSP.</b> A LEO burst guarantees neither carrier phase nor CFO (residual Doppler, drift),
  /// and decision-directed tracking fails at low SNR because wrong decisions poison the estimator. Here
  /// every survivor path carries its <i>own</i> second-order phase/frequency tracker updated with that
  /// path's hypothesized symbols: the correct path stays tuned by construction, wrong paths mistune and
  /// die. A coarse feed-forward CFO estimate (<c>arg(−Σ(r_k·r̄_{k−1})²)/2</c> — the ±j rotation of
  /// h=1/2 squares to a constant −1) pre-derotates the samples so the trackers only handle the residue.</para>
  ///
  /// <para><b>Soft output.</b> After the PSP pass fixes the winning path's phase trajectory, a second
  /// (max-log forward–backward) pass over the phase-corrected samples produces genuine per-bit LLRs,
  /// which feed the erasure-assisted RS and Chase stages (items 1/5) directly.</para>
  ///
  /// <para>Timing comes from the shared front end's Gardner strobes (the discriminator path); the
  /// matched filter is zero-phase, so the C₀ grid sits half a symbol past the strobe centres — the same
  /// boundary instant DF-DD samples. Falls back to DF-DD for h ≠ 1/2 (the trellis needs rational h and
  /// the payoff shrinks once the tones decorrelate).</para>
  /// </summary>
  public sealed class MlsePspDetector : IDetector
  {
    private readonly ModProfile profile;
    private readonly GmskDemodOptions opt;

    /// <summary>Keep ISI taps at least this fraction of the centre tap (then capped at 2 per side).</summary>
    private const double TapThreshold = 0.04;

    /// <summary>Per-survivor phase tracker gains (second-order DPLL, rad/symbol domain): loop BW ~0.02
    /// cycles/symbol, critically damped — fast enough to track Doppler-rate drift over a 0.3 s burst,
    /// slow enough not to chase noise at the SNRs where MLSE matters.</summary>
    private const double PllK1 = 0.18, PllK2 = 0.016;

    public MlsePspDetector(ModProfile profile, GmskDemodOptions opt)
    {
      this.profile = profile;
      this.opt = opt;
    }

    /// <summary>Diagnostics from the last <see cref="Detect"/> call (test/off-air triage only):
    /// the coarse feed-forward CFO estimate (rad/symbol) and the PSP-Viterbi pass-1 hard decisions
    /// before the soft pass — lets a failing decode be pinned to acquisition vs trellis vs LLR stage.</summary>
    internal double LastDOmega;
    internal int[] LastViterbiBits = Array.Empty<int>();

    /// <summary>Symbol-count cap: above this, fall back to DF-DD. MLSE is a per-burst detector (the BCJR
    /// pass keeps an alpha array of K×S doubles and the survivors carry one phase tracker per state) —
    /// a continuous multi-minute stream (batch <c>TraceStream</c>) would cost hundreds of MB and a single
    /// whole-stream CFO/amplitude estimate would be meaningless across noise gaps anyway. 100k symbols
    /// ≈ 10 s at 9k6 — far above any real burst window, well below the continuous case.</summary>
    private const int MaxSymbols = 100_000;

    public float[] Detect(DetectorContext ctx)
    {
      double h = ctx.Params.Deviation is double dev ? 2.0 * dev / ctx.Params.Baud : profile.ModIndex;
      // trellis below is hard-wired to the 4 phase states of h = 1/2; huge K = the continuous stream path
      if (Math.Abs(h - 0.5) > 0.1 || ctx.Strobes.Length > MaxSymbols)
        return new DifferentialDetector(opt, profile).Detect(ctx);

      int K = ctx.Strobes.Length;
      if (K < 8) return new float[K];

      // --- Laurent C₀ matched filter on the channel-filtered baseband -------------------------------
      double sps = ctx.Sps;
      double bt = profile.Pulse == PulseShape.Gaussian ? (profile.Bt ?? opt.FilterBt) : 0;
      int L = profile.Pulse == PulseShape.Gaussian ? 3 : 1;   // pulse span (symbols); MSK is exact at 1
      float[] c0 = LaurentC0(bt, sps, L);

      int n = ctx.Baseband.Length;
      // matched filter (C₀) over the complex baseband in one SIMD firfilt pass, then split to I/Q for the
      // strobe interpolation below. (Was two naive O(n·m) real convolutions — the GMSK/GFSK demod hot spot;
      // the sibling DF-DD detector already uses this SIMD path.)
      var bb = LiquidFir.ConvolveSame(ctx.Baseband, c0);
      var re = new float[n]; var im = new float[n];
      for (int i = 0; i < n; i++) { re[i] = bb[i].Real; im[i] = bb[i].Imaginary; }

      // complex MF samples at the symbol boundaries (strobe + T/2: the b_n grid — see class docs).
      var rr = new double[K]; var ri = new double[K];
      for (int k = 0; k < K; k++)
      {
        double pos = ctx.Strobes[k] + 0.5 * sps;
        rr[k] = VDsp.Interp(re, pos);
        ri[k] = VDsp.Interp(im, pos);
      }

      // --- trellis ISI taps (needed below by the CFO estimator's calibration too) -------------------
      double[] taps = IsiTaps(c0, sps);     // [g0=1, g1, g2…] normalized to the centre tap
      int M = taps.Length - 1;              // one-sided ISI span actually kept

      // --- coarse feed-forward CFO: E[(r_k·r̄_{k−1})²] = κ·e^{j2Δω} for h = 1/2 ---------------------
      // the ±j pseudo-symbol rotation squares to −1, but the ISI factor's expectation multiplies κ by
      // (1 − 8g₁² + 4g₁⁴ + …), which CHANGES SIGN with the pulse: MSK (g₁=1/π) leaves κ < 0, GMSK BT 0.5
      // (g₁≈0.45) flips it positive — assuming κ = −1 there aliases the estimate by exactly π/2/symbol,
      // which the 4-phase trellis cannot absorb. So compute arg κ from the taps themselves (enumerate the
      // 2M+2 bits spanning two adjacent ISI windows) and read the CFO as the deviation from it.
      double zr = 0, zi = 0;
      for (int k = 1; k < K; k++)
      {
        double dr = rr[k] * rr[k - 1] + ri[k] * ri[k - 1];   // r_k · conj(r_{k−1})
        double di = ri[k] * rr[k - 1] - rr[k] * ri[k - 1];
        zr += dr * dr - di * di;                             // (·)²
        zi += 2 * dr * di;
      }
      var (kapR, kapI) = EstimatorIntrinsic(taps);
      double dOmega = (Math.Atan2(zi, zr) - Math.Atan2(kapI, kapR)) / 2.0;   // rad/symbol
      while (dOmega > Math.PI / 2) dOmega -= Math.PI;        // 2Δω wraps mod 2π → Δω mod π
      while (dOmega < -Math.PI / 2) dOmega += Math.PI;
      // near the κ sign-flip BT the calibration loses traction — trust PSP instead of a wild estimate
      double kapMag = Math.Sqrt(kapR * kapR + kapI * kapI);
      double e2 = 0; foreach (var t in taps) e2 += t * t;    // σg² = E|e|² per symbol
      if (kapMag < 0.1 * e2 * e2) dOmega = 0;
      LastDOmega = dOmega;
      double cw = Math.Cos(dOmega), sw = Math.Sin(dOmega);
      double pr = 1, pi = 0;                                 // e^{−jΔω·k}, advanced by complex multiply
      for (int k = 0; k < K; k++)
      {
        double xr = rr[k] * pr + ri[k] * pi;                 // r_k · e^{−jΔω k}  (conj rotation)
        double xi = ri[k] * pr - rr[k] * pi;
        rr[k] = xr; ri[k] = xi;
        double npr = pr * cw + pi * sw; pi = pi * cw - pr * sw; pr = npr;
      }

      // amplitude normalization (the Euclidean metric needs a consistent scale; median is robust to
      // the burst's noise-only edges).
      var mags = new float[K];
      for (int k = 0; k < K; k++) mags[k] = (float)Math.Sqrt(rr[k] * rr[k] + ri[k] * ri[k]);
      Array.Sort(mags);
      double amp = Math.Max(mags[K / 2], 1e-9);
      for (int k = 0; k < K; k++) { rr[k] /= amp; ri[k] /= amp; }

      // --- trellis tables ---------------------------------------------------------------------------
      int histBits = 2 * M;
      int S = 4 << histBits;                // states: 4 phases × 2^(2M) bit histories
      int mask = (1 << histBits) - 1;

      // expected (noise-free) MF sample for each (state, input): ŝ = Σ_{d=−M..M} g_|d|·b_{n+1−M+d},
      // where b_{n+1} = j^{p'} and earlier pseudo-symbols unwind via b_{m−1} = b_m·j^{−a_m}.
      var expRe = new float[S, 2]; var expIm = new float[S, 2];
      for (int s = 0; s < S; s++)
        for (int a = 0; a < 2; a++)
        {
          int p = s >> histBits, hist = s & mask;
          int aNew = a == 1 ? 1 : -1;
          int pNew = (p + aNew) & 3;
          // walk b down from b_{n+1} (phase pNew), dividing by j^{a_m} as m descends
          double sr = 0, si = 0;
          int phase = pNew;
          for (int i = 0; i <= 2 * M; i++)              // i = 0 → b_{n+1}, i = t → b_{n+1−t}
          {
            int d = Math.Abs(M - i);
            if (d <= M) { sr += taps[d] * Re(phase); si += taps[d] * Im(phase); }
            // unwind one symbol: bit a_{n+1−i} (i=0 → the input; else hist bit i−1)
            int am = i == 0 ? aNew : ((hist >> (i - 1)) & 1) == 1 ? 1 : -1;
            phase = (phase - am) & 3;
          }
          expRe[s, a] = (float)sr; expIm[s, a] = (float)si;
        }

      // --- pass 1: PSP-Viterbi (per-survivor phase/frequency tracking) ------------------------------
      int T = K + M;                        // steps consume a_t; step t observes r at k = t − M
      var metric = new double[S]; var metricNext = new double[S];
      var phi = new double[S]; var phiNext = new double[S];
      var dom = new double[S]; var domNext = new double[S];
      var tb = new byte[T, S];              // winning predecessor: bit0 = dropped history bit, bit1 = input a
      for (int s = 0; s < S; s++) metric[s] = 0;   // free initial state (unknown history/phase)

      for (int t = 0; t < T; t++)
      {
        int k = t - M;
        bool hasObs = (uint)k < (uint)K;
        double or_ = hasObs ? rr[k] : 0, oi = hasObs ? ri[k] : 0;
        for (int s2 = 0; s2 < S; s2++) metricNext[s2] = double.NegativeInfinity;

        for (int s = 0; s < S; s++)
        {
          double m0 = metric[s];
          if (double.IsNegativeInfinity(m0)) continue;
          int p = s >> histBits, hist = s & mask;
          // phase-rotated observation for THIS survivor (shared by both branches)
          double c = Math.Cos(phi[s]), sn = Math.Sin(phi[s]);
          double yr = or_ * c + oi * sn;               // r·e^{−jφ}
          double yi = oi * c - or_ * sn;
          for (int a = 0; a < 2; a++)
          {
            int aNew = a == 1 ? 1 : -1;
            int pNew = (p + aNew) & 3;
            int histNew = ((hist << 1) | a) & mask;
            int s2 = (pNew << histBits) | histNew;
            double cand = m0;
            if (hasObs)
            {
              double er = yr - expRe[s, a], ei = yi - expIm[s, a];
              cand -= er * er + ei * ei;
            }
            if (cand > metricNext[s2])
            {
              metricNext[s2] = cand;
              tb[t, s2] = (byte)((a << 1) | ((hist >> (histBits - 1)) & 1));
              if (hasObs)
              {
                // per-survivor tracker update from this branch's innovation
                double xr = yr * expRe[s, a] + yi * expIm[s, a];
                double xi = yi * expRe[s, a] - yr * expIm[s, a];
                double err = Math.Atan2(xi, Math.Max(xr, 1e-12));
                phiNext[s2] = phi[s] + PllK1 * err + dom[s];
                domNext[s2] = dom[s] + PllK2 * err;
              }
              else { phiNext[s2] = phi[s]; domNext[s2] = dom[s]; }
            }
          }
        }
        (metric, metricNext) = (metricNext, metric);
        (phi, phiNext) = (phiNext, phi);
        (dom, domNext) = (domNext, dom);
      }

      // traceback the winner: bits a_t and the state sequence
      int best = 0;
      for (int s = 1; s < S; s++) if (metric[s] > metric[best]) best = s;
      var bits = new int[T];                 // a_t ∈ {0,1}
      var states = new int[T + 1];           // state AFTER step t (states[t+1]); states[0] = initial
      states[T] = best;
      for (int t = T - 1; t >= 0; t--)
      {
        int s2 = states[t + 1];
        byte w = tb[t, s2];
        int a = (w >> 1) & 1, dropped = w & 1;
        bits[t] = a;
        int p2 = s2 >> histBits, hist2 = s2 & mask;
        int p = (p2 - (a == 1 ? 1 : -1)) & 3;
        int hist = histBits > 0 ? ((hist2 >> 1) | (dropped << (histBits - 1))) & mask : 0;
        states[t] = (p << histBits) | hist;
      }

      {
        var hard = new int[K];
        for (int t = 0; t < T; t++) if ((uint)(t - M) < (uint)K) hard[t - M] = bits[t];
        LastViterbiBits = hard;
      }

      // replay the winner's phase trajectory (deterministic given its states/bits)
      var phiTraj = new double[K];
      {
        double f = 0, w = 0;
        for (int t = 0; t < T; t++)
        {
          int k = t - M;
          if ((uint)k < (uint)K)
          {
            phiTraj[k] = f;
            int s = states[t], a = bits[t];
            double c = Math.Cos(f), sn = Math.Sin(f);
            double yr = rr[k] * c + ri[k] * sn;
            double yi = ri[k] * c - rr[k] * sn;
            double xr = yr * expRe[s, a] + yi * expIm[s, a];
            double xi = yi * expRe[s, a] - yr * expIm[s, a];
            double err = Math.Atan2(xi, Math.Max(xr, 1e-12));
            double fNew = f + PllK1 * err + w;
            w += PllK2 * err;
            f = fNew;
          }
        }
      }

      // --- pass 2: max-log forward–backward over the phase-corrected samples → per-bit LLRs ---------
      // (PSP made the forward pass directional; with the winner's φ_k fixed, a clean two-sided pass is valid.)
      var yRe = new double[K]; var yIm = new double[K];
      for (int k = 0; k < K; k++)
      {
        double c = Math.Cos(phiTraj[k]), sn = Math.Sin(phiTraj[k]);
        yRe[k] = rr[k] * c + ri[k] * sn;
        yIm[k] = ri[k] * c - rr[k] * sn;
      }

      var alpha = new double[T + 1, S];
      for (int s = 0; s < S; s++) alpha[0, s] = 0;
      for (int t = 0; t < T; t++)
      {
        int k = t - M;
        bool hasObs = (uint)k < (uint)K;
        for (int s2 = 0; s2 < S; s2++) alpha[t + 1, s2] = double.NegativeInfinity;
        for (int s = 0; s < S; s++)
        {
          double a0 = alpha[t, s];
          if (double.IsNegativeInfinity(a0)) continue;
          int p = s >> histBits, hist = s & mask;
          for (int a = 0; a < 2; a++)
          {
            int s2 = (((p + (a == 1 ? 1 : -1)) & 3) << histBits) | (((hist << 1) | a) & mask);
            double cand = a0 + Gamma(hasObs, k, s, a);
            if (cand > alpha[t + 1, s2]) alpha[t + 1, s2] = cand;
          }
        }
      }

      var beta = new double[S]; var betaPrev = new double[S];
      var llr = new double[K];
      for (int s = 0; s < S; s++) beta[s] = 0;
      for (int t = T - 1; t >= 0; t--)
      {
        int k = t - M;
        bool hasObs = (uint)k < (uint)K;
        double best1 = double.NegativeInfinity, best0 = double.NegativeInfinity;
        for (int s = 0; s < S; s++) betaPrev[s] = double.NegativeInfinity;
        for (int s = 0; s < S; s++)
        {
          if (double.IsNegativeInfinity(alpha[t, s])) continue;
          int p = s >> histBits, hist = s & mask;
          for (int a = 0; a < 2; a++)
          {
            int s2 = (((p + (a == 1 ? 1 : -1)) & 3) << histBits) | (((hist << 1) | a) & mask);
            double g = Gamma(hasObs, k, s, a);
            double tot = alpha[t, s] + g + beta[s2];
            if (a == 1) { if (tot > best1) best1 = tot; }
            else { if (tot > best0) best0 = tot; }
            double bp = g + beta[s2];
            if (bp > betaPrev[s]) betaPrev[s] = bp;
          }
        }
        if ((uint)(t - M) < (uint)K) llr[t - M] = best1 - best0;   // LLR of a_t ↔ observation index k
        (beta, betaPrev) = (betaPrev, beta);
      }

      double Gamma(bool hasObs, int k, int s, int a)
      {
        if (!hasObs) return 0;
        double er = yRe[k] - expRe[s, a], ei = yIm[k] - expIm[s, a];
        return -(er * er + ei * ei);
      }

      // normalize LLRs into the soft-bit convention (sign = bit, |value| ≤ 1 = confidence)
      double meanAbs = 0;
      for (int k = 0; k < K; k++) meanAbs += Math.Abs(llr[k]);
      meanAbs = Math.Max(meanAbs / K, 1e-9);
      var soft = new float[K];
      for (int k = 0; k < K; k++)
        soft[k] = (float)Math.Clamp(llr[k] / (1.5 * meanAbs), -1.0, 1.0);
      return soft;
    }

    private static double Re(int phaseIdx) => phaseIdx switch { 0 => 1, 1 => 0, 2 => -1, _ => 0 };
    private static double Im(int phaseIdx) => phaseIdx switch { 0 => 0, 1 => 1, 2 => 0, _ => -1 };

    /// <summary>
    /// Intrinsic (zero-CFO) value κ = E[(e_k·ē_{k−1})²] of the squared-lag-product CFO estimator, where
    /// e_k = Σ_d g_|d|·b_{k+d} is the noise-free ISI-bearing MF sample. Exact expectation by enumerating
    /// the 2M+2 data bits spanning the two adjacent windows (global phase cancels in e_k·ē_{k−1}).
    /// </summary>
    internal static (double re, double im) EstimatorIntrinsic(double[] taps)
    {
      int M = taps.Length - 1;
      int nb = 2 * M + 2;                       // bits a_1..a_nb generate pseudo-symbols b_0..b_nb (b_0 = 1)
      double sr = 0, si = 0;
      for (int pat = 0; pat < 1 << nb; pat++)
      {
        var ph = new int[nb + 1];               // phase index of b_0..b_nb
        for (int i = 1; i <= nb; i++) ph[i] = (ph[i - 1] + (((pat >> (i - 1)) & 1) == 1 ? 1 : -1)) & 3;
        double eR0 = 0, eI0 = 0, eR1 = 0, eI1 = 0;
        for (int d = -M; d <= M; d++)
        {
          double g = taps[Math.Abs(d)];
          eR0 += g * Re(ph[M + d]); eI0 += g * Im(ph[M + d]);           // e_{k−1}: centre b_M
          eR1 += g * Re(ph[M + 1 + d]); eI1 += g * Im(ph[M + 1 + d]);   // e_k: centre b_{M+1}
        }
        double dr = eR1 * eR0 + eI1 * eI0;      // e_k · conj(e_{k−1})
        double di = eI1 * eR0 - eR1 * eI0;
        sr += dr * dr - di * di;                // (·)²
        si += 2 * dr * di;
      }
      return (sr / (1 << nb), si / (1 << nb));
    }

    /// <summary>
    /// Laurent principal pulse C₀ for h = 1/2 binary CPM, sampled at <paramref name="sps"/>: with the
    /// generalized phase pulse <c>u(t) = sin(π·q(t))</c> on [0, LT] mirrored to [LT, 2LT],
    /// <c>C₀(t) = Π_{i&lt;L} u(t + iT)</c> on [0, (L+1)T]. For a rectangular pulse (MSK, L = 1) this is
    /// exactly the half-sine. Returned as an odd-length zero-phase kernel (C₀ is symmetric), normalized
    /// to unit energy.
    /// </summary>
    internal static float[] LaurentC0(double bt, double sps, int L)
    {
      int m = (int)Math.Round((L + 1) * sps) + 1;
      if (m % 2 == 0) m++;                        // odd length so ConvolveSame centres it exactly
      var u = UPulse(bt, sps, L);                 // u(t) on [0, 2LT], step 1/sps
      var c = new float[m];
      for (int i = 0; i < m; i++)
      {
        double t = i / sps;                       // symbols, support [0, L+1]
        double v = 1;
        for (int j = 0; j < L; j++)
        {
          double x = (t + j) * sps;               // index into u
          int xi = (int)Math.Floor(x);
          if (xi < 0 || xi >= u.Length - 1) { v = 0; break; }
          double mu = x - xi;
          v *= u[xi] * (1 - mu) + u[xi + 1] * mu;
        }
        c[i] = (float)v;
      }
      double e = 0;
      foreach (var v in c) e += (double)v * v;
      if (e > 0) { float g = (float)(1.0 / Math.Sqrt(e)); for (int i = 0; i < m; i++) c[i] *= g; }
      return c;
    }

    /// <summary>u(t) = sin(2πh·q(t))/sin(πh) at h = 1/2 on [0, LT], mirrored about LT to [0, 2LT];
    /// q(t) = ∫g for the (Gaussian BT or rectangular) frequency pulse normalized to q(LT) = 1/2.</summary>
    private static double[] UPulse(double bt, double sps, int L)
    {
      int half = (int)Math.Round(L * sps);
      var q = new double[half + 1];
      if (bt > 0)
      {
        double kk = 2.0 * Math.PI * bt / Math.Sqrt(Math.Log(2.0));
        double acc = 0;
        var g = new double[half + 1];
        for (int i = 0; i <= half; i++)
        {
          double t = i / sps - L / 2.0;           // centre the Gaussian pulse on [0, L]
          g[i] = QFunc(kk * (t - 0.5)) - QFunc(kk * (t + 0.5));
          acc += g[i];
        }
        double run = 0;
        for (int i = 0; i <= half; i++) { run += g[i]; q[i] = 0.5 * run / acc; }
      }
      else
        for (int i = 0; i <= half; i++) q[i] = 0.5 * i / half;   // rectangular: linear phase ramp

      var u = new double[2 * half + 1];
      for (int i = 0; i <= half; i++)
      {
        double v = Math.Sin(Math.PI * q[i]);
        u[i] = v;
        u[2 * half - i] = v;                      // mirror about LT
      }
      return u;
    }

    /// <summary>One-sided ISI taps of the C₀ autocorrelation at symbol lags, normalized to the centre
    /// tap: <c>[1, g₁/g₀, …]</c>, truncated at <see cref="TapThreshold"/> and capped at 2 per side.</summary>
    internal static double[] IsiTaps(float[] c0, double sps)
    {
      double g0 = 0;
      for (int i = 0; i < c0.Length; i++) g0 += (double)c0[i] * c0[i];
      var taps = new System.Collections.Generic.List<double> { 1.0 };
      for (int m = 1; m <= 2; m++)
      {
        double g = 0;
        for (int i = 0; i < c0.Length; i++)
        {
          double x = i - m * sps;
          int xi = (int)Math.Floor(x);
          if (xi < 0 || xi >= c0.Length - 1) continue;
          double mu = x - xi;
          g += c0[i] * (c0[xi] * (1 - mu) + c0[xi + 1] * mu);
        }
        double t = g / g0;
        if (Math.Abs(t) < TapThreshold) break;
        taps.Add(t);
      }
      return taps.ToArray();
    }

    private static double QFunc(double x) => 0.5 * Erfc(x / Math.Sqrt(2.0));

    private static double Erfc(double x)
    {
      double z = Math.Abs(x), t = 1.0 / (1.0 + 0.5 * z);
      double ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 +
        t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 +
        t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
      return x >= 0 ? ans : 2.0 - ans;
    }
  }
}
