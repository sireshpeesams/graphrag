using System.Threading;
using System.Threading.Tasks;
using SearchFrontend.Domain.Models;

namespace SearchFrontend.Domain.Interfaces
{
    public interface IGraphRagService
    {
        Task<GraphRagAnalysisResult> ProcessAnalysisAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default);

        Task<string> QueryAsync(
            string rootDir,
            string query,
            string method = "global",
            CancellationToken cancellationToken = default);
    }

    public interface IAnalysisEngine
    {
        Task<SelfHelpGapAnalysis> PerformSelfHelpGapAnalysisAsync(
            string rootDir,
            CancellationToken cancellationToken = default);

        Task<FMEAAnalysisResult> PerformFMEAAnalysisAsync(
            string rootDir,
            CancellationToken cancellationToken = default);

        Task PerformEntityRelationshipAnalysisAsync(
            GraphRagAnalysisResult result,
            string outputDir,
            CancellationToken cancellationToken = default);
    }

    public interface IProcessingDecisionEngine
    {
        Task<ProcessingDecision> MakeDecisionAsync(
            IngestionRequest request,
            CancellationToken cancellationToken = default);
    }

    public interface IReportGenerator
    {
        Task GenerateReportsAsync(
            GraphRagAnalysisResult result,
            IngestionRequest input,
            CancellationToken cancellationToken = default);
    }

    public interface IContentUploader
    {
        Task UploadAsync(string path, CancellationToken cancellationToken = default);
    }
}