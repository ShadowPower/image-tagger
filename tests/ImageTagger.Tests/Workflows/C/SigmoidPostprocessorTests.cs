using ImageTagger.Core;
using ImageTagger.Infrastructure.Runtime;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", "Unit")]
public sealed class SigmoidPostprocessorTests
{
    [Fact]
    public void Extreme_values_do_not_overflow_or_produce_nan()
    {
        float[] logits = [1000f, -1000f, 88f, -88f, 0f, 10f, -10f];
        float[] probs = new float[logits.Length];

        SigmoidPostprocessor.Activate(logits, probs);

        Assert.All(probs, p =>
        {
            Assert.False(float.IsNaN(p), "sigmoid must never be NaN");
            Assert.False(float.IsInfinity(p), "sigmoid must never be infinite");
            Assert.InRange(p, 0f, 1f);
        });
        Assert.Equal(1f, probs[0], precision: 5);
        Assert.Equal(0f, probs[1], precision: 5);
        Assert.Equal(0.5f, probs[4], precision: 5);
        Assert.True(probs[5] > 0.9999f);
        Assert.True(probs[6] < 0.0001f);
    }

    [Fact]
    public void Negative_branch_uses_stable_formulation()
    {
        // -100 would overflow exp(100) in the naive 1/(1+exp(-x)) form.
        float[] logits = [-100f];
        float[] probs = new float[1];
        SigmoidPostprocessor.Activate(logits, probs);
        Assert.InRange(probs[0], 0f, 1e-30f);
        Assert.False(float.IsNaN(probs[0]));
    }

    [Fact]
    public void Length_mismatch_is_rejected()
    {
        var logitsMismatch = Assert.Throws<TaggerException>(
            () => SigmoidPostprocessor.Activate(new float[3], new float[4]));
        Assert.Equal(TaggerErrorCode.ModelIncompatible, logitsMismatch.Code);

        var lengthMismatch = Assert.Throws<TaggerException>(
            () => SigmoidPostprocessor.ValidateLength(5, 6));
        Assert.Equal(TaggerErrorCode.ModelIncompatible, lengthMismatch.Code);

        // Matching lengths pass without throwing.
        SigmoidPostprocessor.ValidateLength(6, 6);
    }

    [Theory]
    [InlineData(0f, 0.5f)]
    [InlineData(2f, 0.880797f)]
    [InlineData(-2f, 0.119203f)]
    public void Known_values_match_reference(float logit, float expected)
    {
        float[] probs = new float[1];
        SigmoidPostprocessor.Activate([logit], probs);
        Assert.Equal(expected, probs[0], precision: 4);
    }
}
