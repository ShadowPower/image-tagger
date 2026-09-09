using ImageTagger.Core;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// Stable sigmoid post-processing for multi-label logits (design 14.3, C-04).
/// Uses the branch-stable formulation to avoid exp overflow for extreme values.
/// </summary>
public static class SigmoidPostprocessor
{
    /// <summary>Validates that the model output length matches the catalog label count.</summary>
    /// <exception cref="TaggerException">Thrown when lengths disagree.</exception>
    public static void ValidateLength(int logitsLength, int labelCount)
    {
        if (logitsLength != labelCount)
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"模型输出长度 {logitsLength} 与标签数 {labelCount} 不一致，拒绝推理结果。");
        if (labelCount <= 0)
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"标签数 {labelCount} 非法，拒绝推理结果。");
    }

    /// <summary>
    /// Applies the numerically stable sigmoid element-wise.
    /// Negative inputs use exp(x)/(1+exp(x)); non-negative use 1/(1+exp(-x)).
    /// </summary>
    public static void Activate(ReadOnlySpan<float> logits, Span<float> probabilities)
    {
        if (logits.Length != probabilities.Length)
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"logits 长度 {logits.Length} 与概率缓冲长度 {probabilities.Length} 不一致。");

        for (int i = 0; i < logits.Length; i++)
        {
            float x = logits[i];
            float y;
            if (float.IsNaN(x))
            {
                // NaN logits indicate a broken model output; surface as NaN-free 0.5
                // would hide the fault, so propagate a safe 0 instead of NaN.
                y = 0f;
            }
            else if (x < 0f)
            {
                float e = MathF.Exp(x);
                y = e / (1f + e);
            }
            else
            {
                float e = MathF.Exp(-x);
                y = 1f / (1f + e);
            }

            // Defensive: exp overflow would yield Infinity; normalize to exact bounds.
            if (float.IsNaN(y))
                y = x >= 0f ? 1f : 0f;
            else if (float.IsPositiveInfinity(y))
                y = 1f;
            else if (float.IsNegativeInfinity(y))
                y = 0f;

            probabilities[i] = y;
        }
    }
}
