using System;

namespace HomiliaMessenger.Business.Modules.Memories;

/// <summary>
/// Exact vector comparison, shared by recall and by stores that compare vectors themselves.
/// </summary>
public static class VectorSimilarity
{
    /// <summary>
    /// The cosine of the angle between two vectors of the same length: 1 when they point the same way, 0 when unrelated.
    /// Returns 0 when either has no length.
    /// </summary>
    public static float Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Vectors must have the same length to be compared.", nameof(right));
        }

        double dot = 0;
        double leftSquares = 0;
        double rightSquares = 0;

        for (int index = 0; index < left.Length; index++)
        {
            dot += left[index] * (double)right[index];
            leftSquares += left[index] * (double)left[index];
            rightSquares += right[index] * (double)right[index];
        }

        if (leftSquares == 0 || rightSquares == 0)
        {
            return 0;
        }

        return (float)(dot / (Math.Sqrt(leftSquares) * Math.Sqrt(rightSquares)));
    }

    /// <summary>
    /// Whether a vector can be stored and compared: it has a length, and every value is a finite number, not all zero.
    /// </summary>
    public static bool IsUsable(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
        {
            return false;
        }

        bool nonZero = false;

        foreach (float value in vector)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            nonZero |= value != 0;
        }

        return nonZero;
    }
}

/// <summary>
/// Checks what an embedding provider returned before it is stored or searched with.
/// </summary>
internal static class EmbeddingVectors
{
    /// <summary>
    /// Whether <paramref name="result"/> succeeded with a named model and exactly <paramref name="count"/> usable vectors,
    /// all of the same length.
    /// </summary>
    internal static bool AreUsable(EmbeddingResult result, int count)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status != EmbeddingStatus.Success || string.IsNullOrWhiteSpace(result.Model) || result.Vectors.Count != count || count == 0)
        {
            return false;
        }

        int dimensions = result.Vectors[0]?.Length ?? 0;

        foreach (float[]? vector in result.Vectors)
        {
            if (vector is null || vector.Length != dimensions || !VectorSimilarity.IsUsable(vector))
            {
                return false;
            }
        }

        return true;
    }
}
