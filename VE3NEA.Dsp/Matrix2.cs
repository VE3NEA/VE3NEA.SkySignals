namespace VE3NEA
{
    // ----------------------------------------------------------------------------------------------------
    //                     lightweight value types – column vector and 2×2 matrix
    // ----------------------------------------------------------------------------------------------------
    internal readonly struct Vector2
    {
        public readonly double V1, V2;

        public Vector2(double v1, double v2) { V1 = v1; V2 = v2; }

        public Vector2 Add(Vector2 b) => new(V1 + b.V1, V2 + b.V2);
        public Vector2 Scale(double s) => new(V1 * s, V2 * s);

        /// <summary>outer product: returns a 2×2 matrix (this * bᵀ).</summary>
        public Matrix2 OuterProduct(Vector2 b) =>
            new(V1 * b.V1, V1 * b.V2,
                    V2 * b.V1, V2 * b.V2);
    }

    /// <summary>immutable row-major 2×2 matrix.</summary>
    internal readonly struct Matrix2
    {
        public readonly double A11, A12, A21, A22;

        public Matrix2(double a11, double a12, double a21, double a22)
        { A11 = a11; A12 = a12; A21 = a21; A22 = a22; }

        public Matrix2 Transpose() => new(A11, A21, A12, A22);

        public Matrix2 Scale(double s) =>
            new(A11 * s, A12 * s, A21 * s, A22 * s);

        public Matrix2 Add(Matrix2 b) =>
            new(A11 + b.A11, A12 + b.A12,
                    A21 + b.A21, A22 + b.A22);

        public Matrix2 Subtract(Matrix2 b) =>
            new(A11 - b.A11, A12 - b.A12,
                    A21 - b.A21, A22 - b.A22);

        /// <summary>matrix × matrix.</summary>
        public Matrix2 Multiply(Matrix2 b) =>
            new(A11 * b.A11 + A12 * b.A21, A11 * b.A12 + A12 * b.A22,
                    A21 * b.A11 + A22 * b.A21, A21 * b.A12 + A22 * b.A22);

        /// <summary>matrix × column vector.</summary>
        public Vector2 Multiply(Vector2 v) =>
            new(A11 * v.V1 + A12 * v.V2,
                    A21 * v.V1 + A22 * v.V2);
    }
}
