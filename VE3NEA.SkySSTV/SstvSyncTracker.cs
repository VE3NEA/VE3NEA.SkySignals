using System;

namespace VE3NEA.SkySSTV
{
  /// <summary>
  /// KF1 sync/slant tracker (plan §1.6, §7): a single 2nd-order Kalman filter over the FM-discriminated
  /// audio whose <b>argument is the line index</b>, <b>value is that line's 1200 Hz sync-onset sample</b>
  /// and <b>rate is samples-per-line</b> — i.e. the true line period measured against the receiver sample
  /// clock. Each line the filter predicts the next sync position, the 1200 Hz correlator
  /// (<see cref="SstvToneBank"/>) measures it near the prediction, and <see cref="KalmanFilter2nd.Correct"/>
  /// folds it in; because prediction couples value↔rate, correcting the onset also refines the period, so
  /// the rate converges to the transmitter's actual line period and the accumulated <b>slant</b> is removed.
  ///
  /// <para><b>Coast through fades (plan §1.10):</b> when no sync clears the coherence threshold near the
  /// prediction (a dropout, or a mode like Robot36 whose sync some receivers clip), the line is left to the
  /// filter's <b>prediction</b> — timing rides through the gap and re-locks when sync returns, so one fade
  /// does not shear or fragment the rest of the image.</para>
  /// </summary>
  internal static class SstvSyncTracker
  {
    /// <summary>A matched-filter score must clear this to be accepted as a real sync measurement (else coast).</summary>
    public const double SyncScoreThreshold = 0.20;

    /// <summary>Track the per-line sync onset (absolute samples) across the whole image, starting from the
    /// acquired line-0 onset <paramref name="startSample"/>. Returns one onset per transmitted line
    /// (<see cref="SstvModeSpec.LineCount"/>); missed lines carry the Kalman prediction (coasting).</summary>
    public static double[] Track(double[] disc, double fs, SstvModeSpec spec, double startSample)
      => Track(new SstvSyncFilter(disc, fs), fs, spec, startSample);

    /// <summary>As <see cref="Track(double[],double,SstvModeSpec,double)"/> but reusing a prebuilt
    /// <see cref="SstvSyncFilter"/> (the mode detector already has one).</summary>
    public static double[] Track(SstvSyncFilter filter, double fs, SstvModeSpec spec, double startSample)
    {
      double nominal = spec.LinePeriodMs / 1000.0 * fs;              // nominal samples per line
      int pulseLen = Math.Max(1, (int)Math.Round(spec.SyncMs / 1000.0 * fs));
      int searchRad = Math.Max(pulseLen, (int)Math.Round(0.1 * nominal)); // ± search around each prediction

      var kf = new KalmanFilter2nd
      {
        Argument0 = 0,
        Value0 = startSample,
        Rate0 = nominal,
        Value0Sigma = pulseLen,            // acquisition is good to ~one sync width
        Rate0Sigma = nominal * 0.005,      // ±5000 ppm of line-period room to converge into
        Jitter = 1e-6,                     // the sample clock is near-constant: tiny process noise
        Decay = 0
      };
      kf.Reset();

      var onset = new double[spec.LineCount];
      for (int line = 0; line < spec.LineCount; line++)
      {
        kf.Predict(line);
        if (filter.FindPeak(kf.Value, searchRad, pulseLen, SyncScoreThreshold, out double pos, out double score))
          kf.Correct(pos, MeasurementSigma(score));
        onset[line] = kf.Value;             // corrected estimate, or the pure prediction when coasting
      }
      return onset;
    }

    /// <summary>Measurement noise (samples) for a sync detection: a clean peak (score ≈ 0.5) is worth
    /// ~1 sample; a marginal one is trusted less. Bounded so a barely-passing detection cannot dominate the
    /// prediction.</summary>
    private static double MeasurementSigma(double score) => Math.Clamp(0.5 / Math.Max(score, 0.05), 1.0, 10.0);
  }
}
