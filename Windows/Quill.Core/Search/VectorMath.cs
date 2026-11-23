namespace Quill.Core.Search;

/// <summary>Small vector helpers for brute-force cosine search.</summary>
public static class VectorMath
{
    /// <summary>Cosine similarity; 0 when either vector is zero or the lengths differ.</summary>
    public static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.Length == 0)
        {
            return 0;
        }

        double dot = 0, leftNorm = 0, rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        return leftNorm <= 0 || rightNorm <= 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    /// <summary>Copy scaled to unit length (the zero vector stays zero).</summary>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        var result = vector.ToArray();
        double norm = 0;
        foreach (var value in result)
        {
            norm += value * value;
        }

        if (norm > 0)
        {
            var scale = (float)(1 / Math.Sqrt(norm));
            for (var index = 0; index < result.Length; index++)
            {
                result[index] *= scale;
            }
        }

        return result;
    }
}
