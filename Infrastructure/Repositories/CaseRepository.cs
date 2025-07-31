using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SearchFrontend.Domain.Interfaces;
using SearchFrontend.Domain.Models;
using SearchFrontend.Infrastructure.Configuration;
using ProvidersLibrary.DataProviders.Implementations;

namespace SearchFrontend.Infrastructure.Repositories
{
    public sealed class CaseRepository : ICaseRepository
    {
        private readonly SQLQueryExecutor _sqlExecutor;
        private readonly ILogger<CaseRepository> _logger;
        private readonly DatabaseOptions _databaseOptions;
        private readonly SemaphoreSlim _batchSemaphore;

        private const int DefaultBatchSize = 75;
        private const int MaxConcurrentBatches = 5;
        private const int MaxRetryAttempts = 3;

        private static readonly string EmailFilterClauses = @"
            AND Subject NOT LIKE '%New File Uploaded for%'
            AND Subject NOT LIKE '%{CREDITCARDPII}%'
            AND Subject NOT LIKE '%Automated Notification%'
            AND Subject NOT LIKE '%Automated reply:%'
            AND Subject NOT LIKE '%Automatic reply:%'";

        public CaseRepository(
            SQLQueryExecutor sqlExecutor,
            ILogger<CaseRepository> logger,
            IOptions<DatabaseOptions> databaseOptions)
        {
            _sqlExecutor = sqlExecutor ?? throw new ArgumentNullException(nameof(sqlExecutor));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _databaseOptions = databaseOptions?.Value ?? throw new ArgumentNullException(nameof(databaseOptions));
            _batchSemaphore = new SemaphoreSlim(MaxConcurrentBatches, MaxConcurrentBatches);
        }

        public async Task<IReadOnlyList<ClosedCaseRecord>> GetClosedCasesAsync(
            string productId,
            string sapPath,
            string? caseFilter = null,
            int numCases = 100,
            int? maxAgeDays = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(productId))
                throw new ArgumentException("Product ID cannot be null or empty", nameof(productId));

            const string sql = @"
                DECLARE @NUM_CASES  INT           = @numCases;
                DECLARE @PRODUCT_ID NVARCHAR(MAX) = @prodId;
                DECLARE @SAP_PATH   NVARCHAR(MAX) = NULLIF(@sapPath , '');
                DECLARE @SR_FILTER  NVARCHAR(50)  = NULLIF(@srFilter, '');
                DECLARE @MAX_AGE_D   INT          = ISNULL(@maxAgeDays , -1);

                WITH CTE AS (
                    SELECT TOP (@NUM_CASES)
                           A.SRNUMBER,
                           B.PRODUCTNAME          AS CURRENTPRODUCTNAME,
                           B.PESProductID,
                           A.SROwner,
                           A.SRStatus,
                           A.ServiceRequestStatus,
                           A.SRSeverity,
                           A.SRMaxSeverity,
                           A.InternalTitle,
                           A.FirstAssignmentDateTime,
                           A.CaseIdleDays,
                           A.CommunicationIdleDays,
                           A.IsS500Program,
                           A.CollaborationTasks,
                           A.PhoneInteractions,
                           A.EmailInteractions,
                           A.IRStatus,
                           A.IsTransferred,
                           A.SRAgeDays,
                           B.SAPPath,
                           B.SupportTopicID,
                           SUBSTRING(B.SupportTopicID,1,CHARINDEX('\',B.SupportTopicID+'\')-1) AS SupportTopicIDL2,
                           SUBSTRING(B.SupportTopicID,CHARINDEX('\',B.SupportTopicID+'\')+1,1000) AS SupportTopicIDL3,
                           A.SRCreationDateTime,
                           A.SRClosedDateTime
                    FROM  VWFACTSR A
                    JOIN  VWDIMSUPPORTAREAPATH B ON A.SAPKEY = B.SAPKEY
                    WHERE A.SRStatus     = 'Closed'
                      AND B.PESProductID = @PRODUCT_ID
                      AND (@SAP_PATH  IS NULL OR REPLACE(B.SAPPath,'\','/') = REPLACE(@SAP_PATH,'\','/'))
                      AND (@SR_FILTER IS NULL OR A.SRNUMBER = @SR_FILTER)
                      AND (@MAX_AGE_D  < 0 OR  A.SRAgeDays <= @MAX_AGE_D)
                )
                SELECT *,
                       DATEDIFF(DAY, SRCreationDateTime, GETDATE()) AS CaseAge
                FROM   CTE
                ORDER  BY SRCreationDateTime DESC;";

            var parameters = new Dictionary<string, string>
            {
                ["@numCases"] = numCases.ToString(),
                ["@prodId"] = productId,
                ["@sapPath"] = sapPath ?? string.Empty,
                ["@srFilter"] = caseFilter ?? string.Empty,
                ["@maxAgeDays"] = (maxAgeDays ?? -1).ToString()
            };

            try
            {
                var dataTable = await _sqlExecutor.ExecuteQueryAsync(
                    _databaseOptions.UdpServer,
                    _databaseOptions.UdpDatabase,
                    sql,
                    parameters,
                    usePMEApp: false);

                var records = dataTable.AsEnumerable()
                    .Select(MapToClosedCaseRecord)
                    .ToList();

                _logger.LogInformation(
                    "Retrieved {Count} closed cases for ProductId: {ProductId}",
                    records.Count,
                    productId);

                return records.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to retrieve closed cases for ProductId: {ProductId}",
                    productId);
                throw;
            }
        }

        public async Task<IReadOnlyList<string>> GetEmailThreadAsync(
            string caseNumber,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(caseNumber))
                throw new ArgumentException("Case number cannot be null or empty", nameof(caseNumber));

            var batch = await GetEmailThreadsBatchAsync(new[] { caseNumber }, cancellationToken);
            return batch.TryGetValue(caseNumber, out var emailThread)
                ? emailThread.Split(new[] { "\n----\n" }, StringSplitOptions.None).ToList().AsReadOnly()
                : Array.Empty<string>();
        }

        public async Task<IReadOnlyList<string>> GetNotesThreadAsync(
            string caseNumber,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(caseNumber))
                throw new ArgumentException("Case number cannot be null or empty", nameof(caseNumber));

            var batch = await GetNotesThreadsBatchAsync(new[] { caseNumber }, cancellationToken);
            return batch.TryGetValue(caseNumber, out var notesThread)
                ? notesThread.Split(new[] { "\n----\n" }, StringSplitOptions.None).ToList().AsReadOnly()
                : Array.Empty<string>();
        }

        public async Task<IReadOnlyDictionary<string, string>> GetEmailThreadsBatchAsync(
            IEnumerable<string> caseNumbers,
            CancellationToken cancellationToken = default)
        {
            var validCaseNumbers = caseNumbers
                .Where(cn => !string.IsNullOrWhiteSpace(cn))
                .Distinct()
                .ToList();

            if (validCaseNumbers.Count == 0)
                return new Dictionary<string, string>().AsReadOnly();

            _logger.LogInformation(
                "Starting email batch processing for {Count} case(s)",
                validCaseNumbers.Count);

            var chunks = SplitIntoChunks(validCaseNumbers, DefaultBatchSize);
            var results = new ConcurrentDictionary<string, string>();

            var tasks = chunks.Select(async (chunk, index) =>
            {
                await _batchSemaphore.WaitAsync(cancellationToken);
                try
                {
                    var chunkResults = await ProcessEmailChunkAsync(
                        chunk,
                        index + 1,
                        chunks.Count,
                        cancellationToken);

                    foreach (var kvp in chunkResults)
                    {
                        results.TryAdd(kvp.Key, kvp.Value);
                    }
                }
                finally
                {
                    _batchSemaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            return new Dictionary<string, string>(results).AsReadOnly();
        }

        public async Task<IReadOnlyDictionary<string, string>> GetNotesThreadsBatchAsync(
            IEnumerable<string> caseNumbers,
            CancellationToken cancellationToken = default)
        {
            var validCaseNumbers = caseNumbers
                .Where(cn => !string.IsNullOrWhiteSpace(cn))
                .Distinct()
                .ToList();

            if (validCaseNumbers.Count == 0)
                return new Dictionary<string, string>().AsReadOnly();

            _logger.LogInformation(
                "Starting notes batch processing for {Count} case(s)",
                validCaseNumbers.Count);

            var chunks = SplitIntoChunks(validCaseNumbers, DefaultBatchSize);
            var results = new ConcurrentDictionary<string, string>();

            var tasks = chunks.Select(async (chunk, index) =>
            {
                await _batchSemaphore.WaitAsync(cancellationToken);
                try
                {
                    var chunkResults = await ProcessNotesChunkAsync(
                        chunk,
                        index + 1,
                        chunks.Count,
                        cancellationToken);

                    foreach (var kvp in chunkResults)
                    {
                        results.TryAdd(kvp.Key, kvp.Value);
                    }
                }
                finally
                {
                    _batchSemaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            return new Dictionary<string, string>(results).AsReadOnly();
        }

        private async Task<Dictionary<string, string>> ProcessEmailChunkAsync(
            IReadOnlyList<string> chunk,
            int chunkNumber,
            int totalChunks,
            CancellationToken cancellationToken)
        {
            var parameterNames = chunk.Select((_, i) => $"@p{i}").ToArray();
            var inClause = string.Join(", ", parameterNames);

            var sql = $@"
                SELECT ticketnumber,
                       createdon   AS CreatedOn,
                       subject     AS Subject,
                       description AS Message
                FROM dbo.vwDFMEmails WITH (NOLOCK)
                WHERE ticketnumber IN ({inClause})
                  {EmailFilterClauses}
                ORDER BY ticketnumber, createdon ASC;";

            var parameters = parameterNames
                .Zip(chunk, (name, id) => new { name, id })
                .ToDictionary(x => x.name, x => x.id);

            var dataTable = await ExecuteWithRetryAsync(
                sql,
                parameters,
                chunkNumber,
                totalChunks,
                "Email",
                cancellationToken);

            return dataTable.AsEnumerable()
                .GroupBy(r => r["ticketnumber"]?.ToString() ?? "")
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("\n----\n",
                        g.Select(r =>
                            $"Subject: {r["Subject"]}; Created On: {r["CreatedOn"]}; " +
                            $"Message: {CleanMessage(r["Message"]?.ToString())}")));
        }

        private async Task<Dictionary<string, string>> ProcessNotesChunkAsync(
            IReadOnlyList<string> chunk,
            int chunkNumber,
            int totalChunks,
            CancellationToken cancellationToken)
        {
            var parameterNames = chunk.Select((_, i) => $"@p{i}").ToArray();
            var inClause = string.Join(", ", parameterNames);

            var sql = $@"
                WITH Patterns AS (
                    SELECT Pattern, Replacement
                    FROM (VALUES
                        ('ALPHANUMERICPII',''),
                        ('NAMEPII',''),
                        ('EMAILPII',''),
                        ('@microsoft.com',''),
                        ('NAME+EMAILPII',''),
                        ('ADDRESSPII',''),
                        ('PHONEPII',''),
                        ('{{}}',''),
                        ('CREDITCARDPII',''),
                        ('<a href=""mailto:.{{NAME+}}',''),
                        ('href=""mailto: "">',''),
                        ('<a href=""mailto:@',''),
                        ('PHONE+',''),
                        ('NAME+',''),
                        ('UNCPII',''),
                        ('==============================================',''),
                        ('___','')
                    ) AS P(Pattern,Replacement)
                ),
                RankedNotes AS (
                    SELECT
                        N.ticketnumber,
                        N.notetext      AS OriginalNoteText,
                        N.CreatedOn,
                        ROW_NUMBER() OVER (PARTITION BY N.ticketnumber, N.notetext
                                           ORDER BY N.CreatedOn DESC) AS rn
                    FROM dbo.vwDFMNotes N
                    WHERE N.ticketnumber IN ({inClause})
                )
                SELECT
                    RN.ticketnumber,
                    CONVERT(NVARCHAR(120), RN.CreatedOn, 120) + ' | ' +
                    (SELECT REPLACE(RN.OriginalNoteText, P.Pattern, P.Replacement)
                     FROM Patterns P FOR XML PATH(''), TYPE).value('.','NVARCHAR(MAX)')
                   AS CleanedNote
                FROM  RankedNotes RN
                WHERE RN.rn = 1
                ORDER BY RN.ticketnumber, RN.CreatedOn ASC;";

            var parameters = parameterNames
                .Zip(chunk, (name, id) => new { name, id })
                .ToDictionary(x => x.name, x => x.id);

            var dataTable = await ExecuteWithRetryAsync(
                sql,
                parameters,
                chunkNumber,
                totalChunks,
                "Notes",
                cancellationToken);

            return dataTable.AsEnumerable()
                .GroupBy(r => r["ticketnumber"]?.ToString() ?? "")
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("\n----\n",
                        g.Select(r => r["CleanedNote"]?.ToString() ?? "")));
        }

        private async Task<DataTable> ExecuteWithRetryAsync(
            string sql,
            Dictionary<string, string> parameters,
            int chunkNumber,
            int totalChunks,
            string operationType,
            CancellationToken cancellationToken)
        {
            for (int attempt = 1; attempt <= MaxRetryAttempts; attempt++)
            {
                try
                {
                    var dataTable = await _sqlExecutor.ExecuteQueryAsync(
                        _databaseOptions.SupportServer,
                        _databaseOptions.SupportDatabase,
                        sql,
                        parameters,
                        usePMEApp: true);

                    _logger.LogInformation(
                        "{OperationType} chunk {ChunkNumber}/{TotalChunks} completed with {RowCount} rows",
                        operationType,
                        chunkNumber,
                        totalChunks,
                        dataTable.Rows.Count);

                    return dataTable;
                }
                catch (SqlException ex) when (attempt < MaxRetryAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    
                    _logger.LogWarning(ex,
                        "{OperationType} chunk {ChunkNumber}/{TotalChunks} attempt {Attempt} failed. Retrying in {Delay}ms",
                        operationType,
                        chunkNumber,
                        totalChunks,
                        attempt,
                        delay.TotalMilliseconds);

                    await Task.Delay(delay, cancellationToken);
                }
            }

            throw new InvalidOperationException(
                $"Failed to execute {operationType} query after {MaxRetryAttempts} attempts");
        }

        private static ClosedCaseRecord MapToClosedCaseRecord(DataRow row)
        {
            return new ClosedCaseRecord
            {
                SRNumber = row["SRNUMBER"]?.ToString() ?? "",
                PESProductID = row["PESProductID"]?.ToString() ?? "",
                CurrentProductName = row["CURRENTPRODUCTNAME"]?.ToString() ?? "",
                SRCreationDateTime = row.Field<DateTime?>("SRCreationDateTime"),
                SRClosedDateTime = row.Field<DateTime?>("SRClosedDateTime"),
                SROwner = row["SROwner"]?.ToString() ?? "",
                SRStatus = row["SRStatus"]?.ToString() ?? "",
                ServiceRequestStatus = row["ServiceRequestStatus"]?.ToString() ?? "",
                SRSeverity = row["SRSeverity"]?.ToString() ?? "",
                SRMaxSeverity = row["SRMaxSeverity"]?.ToString() ?? "",
                InternalTitle = row["InternalTitle"]?.ToString() ?? "",
                FirstAssignmentDateTime = row.Field<DateTime?>("FirstAssignmentDateTime"),
                CaseIdleDays = Convert.ToInt32(row["CaseIdleDays"]),
                CommunicationIdleDays = Convert.ToInt32(row["CommunicationIdleDays"]),
                IsS500Program = row.Field<bool>("IsS500Program"),
                CollaborationTasks = Convert.ToInt32(row["CollaborationTasks"]),
                PhoneInteractions = Convert.ToInt32(row["PhoneInteractions"]),
                EmailInteractions = Convert.ToInt32(row["EmailInteractions"]),
                IRStatus = row["IRStatus"]?.ToString() ?? "",
                IsTransferred = row.Field<bool>("IsTransferred"),
                SAPPath = row["SAPPath"]?.ToString() ?? "",
                SupportTopicID = row["SupportTopicID"]?.ToString() ?? "",
                SupportTopicIDL2 = row["SupportTopicIDL2"]?.ToString() ?? "",
                SupportTopicIDL3 = row["SupportTopicIDL3"]?.ToString() ?? "",
                CaseAge = row.Field<int>("CaseAge")
            };
        }

        private static List<List<T>> SplitIntoChunks<T>(IReadOnlyList<T> source, int chunkSize)
        {
            var chunks = new List<List<T>>();
            for (int i = 0; i < source.Count; i += chunkSize)
            {
                chunks.Add(source.Skip(i).Take(chunkSize).ToList());
            }
            return chunks;
        }

        private static string CleanMessage(string? rawMessage)
        {
            if (string.IsNullOrEmpty(rawMessage))
                return string.Empty;

            var cleaned = Regex.Replace(rawMessage, @"\t|\n|\r", "");
            cleaned = Regex.Replace(cleaned, @"From:(.+?)To:(.+)", "");
            cleaned = Regex.Replace(cleaned, @"<img(.+?)>", "");
            cleaned = Regex.Replace(cleaned, @"<a(.*?)>", "");
            
            return cleaned.Trim();
        }

        public void Dispose()
        {
            _batchSemaphore?.Dispose();
        }
    }
}