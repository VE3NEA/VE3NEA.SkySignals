using MathNet.Numerics;
using VE3NEA.SkyTlm.Core;

namespace VE3NEA.SkyTlm.Dsp
{
  /// <summary>Tunables for the batch acquisition's detect/select stage (<see cref="Acquisition.Detect"/>):
  /// the CFO search span and the shape-selection gate.</summary>
  public sealed record MatchedDetectorOptions(
    double CfoMaxHz = 2000,
    double MinShapeScore = 0.25); // burst-selection gate: log-power correlation of avg spectrum vs template (CpmTemplate.Match)

  /// <summary>
  /// Acquisition: detect bursts, estimate the per-burst residual CFO feed-forward, and
  /// provide NCO derotation. Per-burst and loop-free, suited to short bursts separated
  /// by long silences.
  /// </summary>
  public static class Acquisition
  {
    public static List<Burst> Detect(Complex32[] iq, double fs, SignalParams p,
      MatchedDetectorOptions? options = null)
    {
      var o = options ?? new MatchedDetectorOptions();

      // two-stage acquisition, uniform across all FSK flavors (GMSK/GFSK/FSK/AFSK):
      //  1. DETECT candidate spans by ENERGY only (good coverage, modulation-agnostic).
      //  2. SELECT real bursts by correlating each burst's AVERAGED spectrum with the expected modulation
      //     template — the synthesized CPM PSD (two tones + filling for wide h, a bell for h≈0.5).
      //     the correlation runs over a window 2× the signal width with the template's zero skirt, so flat
      //     noise rejects (see CpmTemplate.Match).
      var spans = BurstDetector.Detect(iq, fs, p, new BurstDetectorOptions { CfoMaxHz = o.CfoMaxHz });
      var template = CpmTemplate.Synthesize(p);

      var bursts = new List<Burst>(spans.Count);
      if (spans.Count > 0)
      {
        using var cfo = new CfoEstimator(fs, o.CfoMaxHz, p);
        foreach (var (start, end, snr) in spans)
        {
          var info = cfo.Analyze(iq, start, end);
          var meas = cfo.EstimateShape(iq, start, end, info.CfoHz);
          double match = CpmTemplate.Match(meas, template);
          if (match < o.MinShapeScore) continue;     // reject noise / wrong-shape (SSTV/CW/interferers)
          bursts.Add(new Burst(start, end, fs, info.CfoHz, snr)
          {
            ShapeScore = match,
            BandwidthHz = info.BandwidthHz
          });
        }
      }
      return bursts;
    }

    /// <summary>Returns a copy of the burst samples derotated by −CFO via a complex NCO.</summary>
    public static Complex32[] Derotate(Complex32[] iq, Burst b)
    {
      int len = b.EndSample - b.StartSample;
      var seg = new Complex32[len];
      Array.Copy(iq, b.StartSample, seg, 0, len);
      // dsp.Mix multiplies by exp(+j2π·f·n); use −CFO (normalized) to remove the offset.
      global::VE3NEA.Dsp.Mix(seg, -b.CfoHz / b.SampleRate);
      return seg;
    }

    /// <summary>
    /// Derotate <c>iq[start..end]</c> by a <b>time-varying</b> −CFO(t) from <paramref name="traj"/> (continuous
    /// demod): a complex NCO whose instantaneous frequency tracks the interpolated/extrapolated burst CFO,
    /// keeping a drifting (Doppler) carrier centred in the demodulator's channel filter across the whole stream.
    /// The phasor is advanced incrementally and the per-sample rotation step is refreshed only every
    /// <c>StepSamples</c> (CFO changes slowly), with periodic renormalisation to bound numeric drift.
    /// </summary>
    public static Complex32[] DerotateVarying(Complex32[] iq, int start, int end, double fs, CfoTrajectory traj)
    {
      const int StepSamples = 256;     // refresh the NCO step this often (CFO is ~constant over this span)
      int len = Math.Max(0, end - start);
      var seg = new Complex32[len];

      double phase = 0.0;              // accumulated NCO phase (rad); derotation is exp(j·phase), phase = −2π∫f dt
      double cr = 1.0, ci = 0.0;       // current rotation phasor exp(j·phase)
      double sr = 1.0, si = 0.0;       // per-sample step exp(−j2π f/fs), refreshed per block
      double dphi = 0.0;               // current block's per-sample phase step

      for (int i = 0; i < len; i++)
      {
        if (i % StepSamples == 0)
        {
          double f = traj.Eval((start + i) / fs);   // CFO ≈ constant over the next StepSamples
          dphi = -2.0 * Math.PI * f / fs;
          sr = Math.Cos(dphi); si = Math.Sin(dphi);
          cr = Math.Cos(phase); ci = Math.Sin(phase);   // resync phasor to exact phase (kills incremental drift)
        }

        var x = iq[start + i];
        seg[i] = new Complex32(
          (float)(x.Real * cr - x.Imaginary * ci),
          (float)(x.Real * ci + x.Imaginary * cr));

        double nr = cr * sr - ci * si, nci = cr * si + ci * sr;   // advance phasor by one sample
        cr = nr; ci = nci;
        phase += dphi;
      }
      return seg;
    }

    /// <summary>CFO remaining after derotation — should be ≈0; used to validate the NCO stage.</summary>
    public static double ResidualCfo(Complex32[] iq, double fs, SignalParams p, Burst b)
    {
      var seg = Derotate(iq, b);
      using var cfo = new CfoEstimator(fs, 5000, p);
      return cfo.Estimate(seg, 0, seg.Length);
    }
  }
}
