using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SearchFrontend.Domain.Models;

namespace SearchFrontend.Domain.Interfaces
{
    public interface ICaseRepository
    {
        Task<IReadOnlyList<ClosedCaseRecord>> GetClosedCasesAsync(
            string productId,
            string sapPath,
            string? caseFilter = null,
            int numCases = 100,
            int? maxAgeDays = null,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> GetEmailThreadAsync(
            string caseNumber,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<string>> GetNotesThreadAsync(
            string caseNumber,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyDictionary<string, string>> GetEmailThreadsBatchAsync(
            IEnumerable<string> caseNumbers,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyDictionary<string, string>> GetNotesThreadsBatchAsync(
            IEnumerable<string> caseNumbers,
            CancellationToken cancellationToken = default);
    }
}