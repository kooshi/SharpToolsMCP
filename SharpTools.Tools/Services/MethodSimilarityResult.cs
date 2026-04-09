namespace SharpTools.Tools.Services;

public class MethodSimilarityResult(List<MethodSemanticFeatures> similarMethods, double averageSimilarityScore)
{
    public List<MethodSemanticFeatures> SimilarMethods { get; } = similarMethods;
    public double AverageSimilarityScore { get; } = averageSimilarityScore;
}
