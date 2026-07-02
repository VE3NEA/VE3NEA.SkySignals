namespace VE3NEA
{
    /// <summary>
    /// 2nd-order Kalman filter that tracks a Value and its derivative Rate
    /// as functions of a monotonically increasing Argument (e.g. time)
    /// </summary>
    public class KalmanFilter2nd
    {
        // initial conditions (set before calling Reset)
        public double Argument0 { get; set; }
        public double Value0 { get; set; }
        public double Rate0 { get; set; }
        public double Value0Sigma { get; set; }
        public double Rate0Sigma { get; set; }
        public double Decay { get; set; }

        // relative process-noise (fraction of current rate)
        public double Jitter { get; set; }

        // internal state
        private double LastArgument;
        private Vector2 X;   // state: [value, rate]
        private Matrix2 P;   // error covariance
        private Vector2 H;   // observation row: [1, 0]

        // public read-only accessors
        public double Value => X.V1;
        public double Rate => X.V2;
        public double Sigma => Math.Sqrt(P.A11);
        public double Argument => LastArgument;




        // ----------------------------------------------------------------------------------------------------
        //                                       public methods
        // ----------------------------------------------------------------------------------------------------
        public void Reset()
        {
            LastArgument = Argument0;
            X = new Vector2(Value0, Rate0);
            P = new Matrix2(Value0Sigma * Value0Sigma, 0, 0, Rate0Sigma * Rate0Sigma);
            H = new Vector2(1, 0);
        }

        public void Predict(double arg)
        {
            double tau = arg - LastArgument;
            LastArgument = arg;

            double jitterSigma = Jitter * X.V2;
            var g = new Vector2(tau * tau / 2.0, tau);
            var q = g.OuterProduct(g).Scale(jitterSigma * jitterSigma);
            double d = Decay > 0 ? Math.Exp(-Decay * tau) : 1.0;

            var f = new Matrix2(d, tau, 0, 1);
            X = f.Multiply(X);
            P = f.Multiply(P).Multiply(f.Transpose()).Add(q);
        }

        public void Correct(double value, double sigma)
        {
            double r = sigma * sigma;
            double y = value - X.V1;                // innovation
            double s = P.A11 + r;                   // H * P * H' + R  (H = [1,0])
            var k = P.Multiply(H).Scale(1.0 / s);   // Kalman gain

            X = X.Add(k.Scale(y));
            P = P.Subtract(k.OuterProduct(H).Multiply(P));
        }

        // extrapolates the value to a future argument without updating state
        public double GetValue(double arg)
        {
            double tau = arg - LastArgument;
            double d = Decay > 0 ? Math.Exp(-Decay * tau) : 1.0;
            return d * X.V1 + tau * X.V2;
        }


        public void ClampValue(double min, double max) =>
            X = new Vector2(Math.Clamp(X.V1, min, max), X.V2);

        public void ClampRate(double minRate, double maxRate) =>
            X = new Vector2(X.V1, Math.Clamp(X.V2, minRate, maxRate));

        public void ReplaceValue(double value) => X = new Vector2(value, X.V2);
        public void ReplaceRate(double rate) => X = new Vector2(X.V1, rate);

        public void CopyFrom(KalmanFilter2nd source)
        {
            LastArgument = source.LastArgument;
            X = source.X;
            P = source.P;
            H = source.H;
        }
    }
}
