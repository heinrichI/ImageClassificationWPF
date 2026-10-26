using System.Diagnostics;

namespace ImageClassification.Core.Helpers;

/// <summary>
/// Shared vector math for embedding-based services.
/// CLIP and feature embeddings are L2-normalized by their encoders, so the
/// dot product equals cosine similarity.
/// </summary>
public static class VectorMath
{
    public static float CosineSimilarity(float[] a, float[] b)
    {
        // Embeddings from the same model always share a dimension; a mismatch is a
        // bug (e.g. a stale cache row written by an older model). Math.Min below
        // would silently hide it.
        Debug.Assert(a.Length == b.Length, "embedding dimensions must match");

        float dot = 0f;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
            dot += a[i] * b[i];
        return dot; // Already L2-normalized, so dot product = cosine similarity
    }
}