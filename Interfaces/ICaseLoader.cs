using SearchFrontend.Endpoints.GraphRAG.Models.Entities;

namespace SearchFrontend.Endpoints.GraphRAG.Interfaces
{
    /// <summary>
    /// Interface for loading case data from various sources
    /// </summary>
    public interface ICaseLoader
    {
        Task<List<ClosedCaseRecord>> GetClosedCasesAsync(
            string productId,
            string sapPath,
            string? caseFilter,
            int numCases,
            int? maxAgeDays = null,
            CancellationToken ct = default);

        Task<List<string>> GetEmailThreadAsync(string sr, CancellationToken ct = default);
        
        Task<List<string>> GetNotesThreadAsync(string sr, CancellationToken ct = default);
        
        Task<Dictionary<string, string>> GetEmailThreadsBatchAsync(
            IEnumerable<string> caseNumbers, 
            CancellationToken ct = default);
        
        Task<Dictionary<string, string>> GetNotesThreadsBatchAsync(
            IEnumerable<string> caseNumbers, 
            CancellationToken ct = default);
    }
}