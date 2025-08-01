using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using SearchFrontend.Endpoints.GraphRAG.Configuration;
using SearchFrontend.Endpoints.GraphRAG.Interfaces;
using SearchFrontend.Endpoints.GraphRAG.Models.Entities;
using ProvidersLibrary.DataProviders.Implementations;

namespace SearchFrontend.Endpoints.GraphRAG.Services
{
    /// <summary>
    /// Service for loading case data with optimized parallel processing
    /// </summary>
    public sealed class CaseLoaderService : ICaseLoader
    {
        private readonly SQLQueryExecutor _sqlExec;
        private readonly ILogger<CaseLoaderService> _logger;
        private readonly GraphRagConfiguration _config;
        private readonly IAsyncPolicy _retryPolicy;

        private const int BatchSize = 75;
        private const int MaxConcurrentBatches = 4;

        private const string EmailFilterClauses = @"
        AND Subject NOT LIKE '%New File Uploaded for%'
        AND Subject NOT LIKE '%{CREDITCARDPII}%'
        AND Subject NOT LIKE '%Automated Notification%'
        AND Subject NOT LIKE '%Automated reply:%'
        AND Subject NOT LIKE '%Automatic reply:%'";

        public CaseLoaderService(
            SQLQueryExecutor sqlExec, 
            ILogger<CaseLoaderService> logger,
            IOptions<GraphRagConfiguration> config)
        {
            _sqlExec = sqlExec;
            _logger = logger;
            _config = config.Value;
            
            // Configure retry policy with exponential backoff
            _retryPolicy = Policy
                .Handle<SqlException>()
                .Or<TimeoutException>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                    onRetry: (outcome, timespan, retryCount, context) =>
                    {
                        _logger.LogWarning("Retry {RetryCount} in {Delay}ms for {Operation}", 
                            retryCount, timespan.TotalMilliseconds, context.OperationKey);
                    });
        }

        public async Task<List<ClosedCaseRecord>> GetClosedCasesAsync(
            string productId,
            string sapPath,
            string? caseFilter,
            int numCases,
            int? maxAgeDays = null,
            CancellationToken ct = default)
        {
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
                    FROM  VWFACTSR A WITH (NOLOCK)
                    JOIN  VWDIMSUPPORTAREAPATH B WITH (NOLOCK) ON A.SAPKEY = B.SAPKEY
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
                ["@sapPath"] = sapPath,
                ["@srFilter"] = caseFilter ?? string.Empty,
                ["@maxAgeDays"] = (maxAgeDays ?? -1).ToString()
            };

            try
            {
                var table = await _retryPolicy.ExecuteAsync(async () =>
                    await _sqlExec.ExecuteQueryAsync(
                        _config.UdpSqlServer, 
                        _config.UdpSqlDatabase, 
                        sql, 
                        parameters, 
                        usePMEApp: false));

                var records = table.AsEnumerable().Select(MapToClosedCaseRecord).ToList();

                _logger.LogInformation("Retrieved {Count} closed cases for ProductId: {ProductId}", 
                    records.Count, productId);

                return records;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve closed cases for ProductId: {ProductId}", productId);
                throw;
            }
        }

        public async Task<List<string>> GetEmailThreadAsync(string sr, CancellationToken ct = default)
        {
            var map = await GetEmailThreadsBatchAsync(new[] { sr }, ct);
            return map.TryGetValue(sr, out var txt)
                   ? txt.Split(new[] { "\n----\n" }, StringSplitOptions.None).ToList()
                   : new List<string>();
        }

        public async Task<List<string>> GetNotesThreadAsync(string sr, CancellationToken ct = default)
        {
            var map = await GetNotesThreadsBatchAsync(new[] { sr }, ct);
            return map.TryGetValue(sr, out var txt)
                   ? txt.Split(new[] { "\n----\n" }, StringSplitOptions.None).ToList()
                   : new List<string>();
        }

        public async Task<Dictionary<string, string>> GetEmailThreadsBatchAsync(
            IEnumerable<string> caseNumbers, 
            CancellationToken ct = default)
        {
            var validCaseNumbers = caseNumbers
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();

            if (validCaseNumbers.Count == 0)
                return new Dictionary<string, string>();

            _logger.LogInformation("Processing email threads for {Count} cases", validCaseNumbers.Count);

            var chunks = SplitList(validCaseNumbers, BatchSize);
            var semaphore = new SemaphoreSlim(MaxConcurrentBatches, MaxConcurrentBatches);
            
            var tasks = chunks.Select(async (chunk, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    return await ProcessEmailChunkAsync(chunk, index + 1, chunks.Count, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var tables = await Task.WhenAll(tasks);

            return tables
                .SelectMany(t => t.AsEnumerable())
                .GroupBy(r => r["ticketnumber"]?.ToString() ?? "")
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("\n----\n",
                        g.Select(r =>
                            $"Subject: {r["Subject"]}; Created On: {r["CreatedOn"]}; " +
                            $"Message: {CleanMessage(r["Message"]?.ToString())}")));
        }

        public async Task<Dictionary<string, string>> GetNotesThreadsBatchAsync(
            IEnumerable<string> caseNumbers, 
            CancellationToken ct = default)
        {
            var validCaseNumbers = caseNumbers
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();

            if (validCaseNumbers.Count == 0)
                return new Dictionary<string, string>();

            _logger.LogInformation("Processing notes threads for {Count} cases", validCaseNumbers.Count);

            var chunks = SplitList(validCaseNumbers, BatchSize);
            var semaphore = new SemaphoreSlim(MaxConcurrentBatches, MaxConcurrentBatches);
            
            var tasks = chunks.Select(async (chunk, index) =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    return await ProcessNotesChunkAsync(chunk, index + 1, chunks.Count, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var tables = await Task.WhenAll(tasks);

            return tables
                .SelectMany(t => t.AsEnumerable())
                .GroupBy(r => r["ticketnumber"]?.ToString() ?? "")
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("\n----\n",
                        g.Select(r => r["CleanedNote"]?.ToString() ?? "")));
        }

        private async Task<DataTable> ProcessEmailChunkAsync(
            List<string> chunk, 
            int chunkNo, 
            int totalChunks, 
            CancellationToken ct)
        {
            var parameterNames = chunk.Select((_, i) => $"@p{i}").ToArray();
            var inClause = string.Join(", ", parameterNames);

            _logger.LogDebug("Processing email chunk {ChunkNo}/{TotalChunks} with {Count} cases", 
                chunkNo, totalChunks, chunk.Count);

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

            return await ExecuteWithRetryAsync(sql, parameters, chunkNo, totalChunks, "Email", ct);
        }

        private async Task<DataTable> ProcessNotesChunkAsync(
            List<string> chunk, 
            int chunkNo, 
            int totalChunks, 
            CancellationToken ct)
        {
            var parameterNames = chunk.Select((_, i) => $"@p{i}").ToArray();
            var inClause = string.Join(", ", parameterNames);

            _logger.LogDebug("Processing notes chunk {ChunkNo}/{TotalChunks} with {Count} cases", 
                chunkNo, totalChunks, chunk.Count);

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
                    FROM dbo.vwDFMNotes N WITH (NOLOCK)
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

            return await ExecuteWithRetryAsync(sql, parameters, chunkNo, totalChunks, "Notes", ct);
        }

        private async Task<DataTable> ExecuteWithRetryAsync(
            string sql, 
            Dictionary<string, string> parameters, 
            int chunkNo, 
            int totalChunks, 
            string label, 
            CancellationToken ct)
        {
            return await _retryPolicy.ExecuteAsync(async () =>
            {
                ct.ThrowIfCancellationRequested();
                
                var table = await _sqlExec.ExecuteQueryAsync(
                    _config.SupportSqlServer, 
                    _config.SupportSqlDatabase, 
                    sql, 
                    parameters, 
                    usePMEApp: true);

                _logger.LogDebug("{Label} chunk {ChunkNo}/{TotalChunks} completed with {RowCount} rows", 
                    label, chunkNo, totalChunks, table.Rows.Count);

                return table;
            });
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

        private static List<List<T>> SplitList<T>(IList<T> source, int size)
        {
            var chunks = new List<List<T>>();
            for (int i = 0; i < source.Count; i += size)
            {
                chunks.Add(source.Skip(i).Take(size).ToList());
            }
            return chunks;
        }

        private static string CleanMessage(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) 
                return string.Empty;

            var cleaned = Regex.Replace(raw, @"\t|\n|\r", "");
            cleaned = Regex.Replace(cleaned, @"From:(.+?)To:(.+)", "");
            cleaned = Regex.Replace(cleaned, @"<img(.+?)>", "");
            cleaned = Regex.Replace(cleaned, @"<a(.*?)>", "");
            
            return cleaned.Trim();
        }
    }
}