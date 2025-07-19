
namespace SharpTools.Tools.Services {
    public class MethodSimilarityResult {
        public List<MethodSemanticFeatures> SimilarMethods { get; }
        public double AverageSimilarityScore { get; } // Or some other metric

        public MethodSimilarityResult(List<MethodSemanticFeatures> similarMethods, double averageSimilarityScore) {
            SimilarMethods = similarMethods;
            AverageSimilarityScore = averageSimilarityScore;
        }
    }
}
