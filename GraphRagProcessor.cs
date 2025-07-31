using Clc.Common.Helpers;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;

using Newtonsoft.Json;

using ProviderLibrary.AuthenticationProvider.Abstractions;
using ProviderLibrary.DataProviders.Abstractions;
using ProviderLibrary.HTTPProviders.Abstractions;

using ProvidersLibrary.DataProviders.Implementations;
using ProvidersLibrary.HTTPProviders.RequestBodies;

using SearchFrontend.Providers.GPT;
using SearchFrontend.Endpoints.OpenCaseReview.Common;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SearchFrontend.Endpoints.GraphRAG
{
    // Enhanced request model
    public class IngestionRequest
    {
        public string ProductId { get; set; } = "";
        public string SapPath { get; set; } = "";
        public string Mode { get; set; } = "multi";
        public int NumCasesToFetch { get; set; } = 1;
        public bool SkipExisting { get; set; } = true;
        public bool SkipSearch { get; set; } = true;
        public int? MaxAgeDays { get; set; } = 14;
        public string CallbackUri { get; set; } = "";
        public string CaseFilter { get; set; } = null;
        public string RootDir { get; set; } = "";
        public bool GenerateReports { get; set; } = true;
        public bool SaveToCosmosDB { get; set; } = false;
        public bool SaveToAzureDataExplorer { get; set; } = false;
        public bool ForceReindex { get; set; } = false;
        public string UpdateStrategy { get; set; } = "track";
    }

    // Analysis result models
    public class GraphRagAnalysisResult
    {
        public string AnalysisId { get; set; } = Guid.NewGuid().ToString();
        public string ProductId { get; set; } = "";
        public DateTime AnalysisTimestamp { get; set; } = DateTime.UtcNow;
        public int TotalCasesAnalyzed { get; set; }
        public bool HasSelfHelpContent { get; set; }
        public string RootDirectory { get; set; } = "";
        public string OutputDirectory { get; set; } = "";

        public SelfHelpGapAnalysis SelfHelpGaps { get; set; } = new();
        public FMEAAnalysisResult FMEAAnalysis { get; set; } = new();
        public List<EntityInsight> EntityInsights { get; set; } = new();
        public List<RelationshipInsight> RelationshipInsights { get; set; } = new();
        public List<CommunityInsight> CommunityInsights { get; set; } = new();

        public AnalysisMetrics Metrics { get; set; } = new();
        public List<ActionableRecommendation> Recommendations { get; set; } = new();

        public bool ProcessingSkipped { get; set; }
        public string SkipReason { get; set; }
        public List<string> SkipDetails { get; set; } = new();
    }

    public class SelfHelpGapAnalysis
    {
        public List<SelfHelpGap> IdentifiedGaps { get; set; } = new();
        public List<SelfHelpCoverage> ExistingCoverage { get; set; } = new();
        public decimal OverallCoverageScore { get; set; }
        public List<string> HighPriorityContentNeeds { get; set; } = new();
    }

    public class SelfHelpGap
    {
        public string GapId { get; set; } = "";
        public string ProblemArea { get; set; } = "";
        public string IssueCategory { get; set; } = "";
        public int FrequencyInCases { get; set; }
        public string SeverityLevel { get; set; } = "";
        public bool CustomerSolvable { get; set; }
        public string RecommendedContentType { get; set; } = "";
        public string SuggestedTitle { get; set; } = "";
        public int EstimatedImpact { get; set; }
        public List<string> RelatedCaseNumbers { get; set; } = new();
        public List<string> RelatedSAPPaths { get; set; } = new();
    }

    public class SelfHelpCoverage
    {
        public string Topic { get; set; } = "";
        public List<string> CoveredIssues { get; set; } = new();
        public string CoverageQuality { get; set; } = "";
        public bool NeedsUpdate { get; set; }
    }

    public class FMEAAnalysisResult
    {
        public List<FailureModeAnalysis> FailureModes { get; set; } = new();
        public List<RiskPriorityItem> HighRiskItems { get; set; } = new();
        public List<PreventionOpportunity> PreventionOpportunities { get; set; } = new();
        public List<DetectionGap> DetectionGaps { get; set; } = new();
        public decimal OverallRiskScore { get; set; }
    }

    public class FailureModeAnalysis
    {
        public string FailureMode { get; set; } = "";
        public string FailureCause { get; set; } = "";
        public string FailureEffect { get; set; } = "";
        public int SeverityRating { get; set; }
        public int OccurrenceFrequency { get; set; }
        public int DetectabilityRating { get; set; }
        public int RiskPriorityNumber => SeverityRating * OccurrenceFrequency * DetectabilityRating;
        public List<string> ExistingControls { get; set; } = new();
        public List<string> RecommendedActions { get; set; } = new();
    }

    public class RiskPriorityItem
    {
        public string FailureMode { get; set; } = "";
        public int RPN { get; set; }
        public string RiskLevel { get; set; } = "";
        public string ImmediateAction { get; set; } = "";
    }

    public class PreventionOpportunity
    {
        public string FailureMode { get; set; } = "";
        public string PreventionType { get; set; } = "";
        public string RecommendedControl { get; set; } = "";
        public bool CustomerActionable { get; set; }
        public string SelfHelpContentNeeded { get; set; } = "";
    }

    public class DetectionGap
    {
        public string FailureMode { get; set; } = "";
        public string CurrentDetectionMethod { get; set; } = "";
        public string DetectionGapDescription { get; set; } = "";
        public string RecommendedDetectionControl { get; set; } = "";
    }

    public class EntityInsight
    {
        public string EntityType { get; set; } = "";
        public string EntityName { get; set; } = "";
        public int Frequency { get; set; }
        public string BusinessImpact { get; set; } = "";
        public List<string> RelatedEntities { get; set; } = new();
    }

    public class RelationshipInsight
    {
        public string SourceEntity { get; set; } = "";
        public string TargetEntity { get; set; } = "";
        public string RelationshipType { get; set; } = "";
        public decimal Strength { get; set; }
        public string BusinessSignificance { get; set; } = "";
    }

    public class CommunityInsight
    {
        public string CommunityTitle { get; set; } = "";
        public string CommunityDescription { get; set; } = "";
        public decimal SelfHelpOpportunityRating { get; set; }
        public List<string> KeyFindings { get; set; } = new();
        public List<string> RecommendedActions { get; set; } = new();
    }

    public class AnalysisMetrics
    {
        public int TotalEntitiesExtracted { get; set; }
        public int TotalRelationshipsFound { get; set; }
        public int TotalCommunitiesIdentified { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public Dictionary<string, int> EntityTypeDistribution { get; set; } = new();
        public Dictionary<string, int> IssueFrequencyDistribution { get; set; } = new();
    }

    public class ActionableRecommendation
    {
        public string RecommendationId { get; set; } = Guid.NewGuid().ToString();
        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Priority { get; set; } = "";
        public int EstimatedImpact { get; set; }
        public string ActionOwner { get; set; } = "";
        public List<string> Prerequisites { get; set; } = new();
        public string ExpectedOutcome { get; set; } = "";
    }

    // Supporting classes
    public sealed class ClosedCaseRecord
    {
        public string SRNumber { get; init; } = "";
        public string PESProductID { get; init; } = "";
        public string CurrentProductName { get; init; } = "";
        public DateTime? SRCreationDateTime { get; init; }
        public DateTime? SRClosedDateTime { get; init; }
        public string SROwner { get; init; } = "";
        public string SRStatus { get; init; } = "";
        public string ServiceRequestStatus { get; init; } = "";
        public string SRSeverity { get; init; } = "";
        public string SRMaxSeverity { get; init; } = "";
        public string InternalTitle { get; init; } = "";
        public DateTime? FirstAssignmentDateTime { get; init; }
        public int CaseIdleDays { get; init; }
        public int CommunicationIdleDays { get; init; }
        public bool IsS500Program { get; init; }
        public int CollaborationTasks { get; init; }
        public int PhoneInteractions { get; init; }
        public int EmailInteractions { get; init; }
        public string IRStatus { get; init; } = "";
        public bool IsTransferred { get; init; }
        public string SAPPath { get; init; } = "";
        public string SupportTopicID { get; init; } = "";
        public string SupportTopicIDL2 { get; init; } = "";
        public string SupportTopicIDL3 { get; init; } = "";
        public int CaseAge { get; init; }
    }

    // Interface for CaseLoader
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
        Task<Dictionary<string, string>> GetEmailThreadsBatchAsync(IEnumerable<string> caseNumbers);
        Task<Dictionary<string, string>> GetNotesThreadsBatchAsync(IEnumerable<string> caseNumbers);
    }

    // Your existing CaseLoader implementation (preserved)
    public sealed class CaseLoader : ICaseLoader
    {
        private readonly SQLQueryExecutor _sqlExec;
        private readonly ILogger<CaseLoader> _log;
        private readonly string _udpServer;
        private readonly string _udpDb;
        private readonly string _supServer;
        private readonly string _supDb;

        private const int BatchSize = 75;

        private const string EmailFilterClauses = @"
        AND Subject NOT LIKE '%New File Uploaded for%'
        AND Subject NOT LIKE '%{CREDITCARDPII}%'
        AND Subject NOT LIKE '%Automated Notification%'
        AND Subject NOT LIKE '%Automated reply:%'
        AND Subject NOT LIKE '%Automatic reply:%'";

        public CaseLoader(SQLQueryExecutor sqlExec, ILogger<CaseLoader> log)
        {
            _sqlExec = sqlExec;
            _log = log;

            _udpServer = ConfigHelper.GetVariable<string>("UDP_SQL_SERVER");
            _udpDb = ConfigHelper.GetVariable<string>("UDP_SQL_DATABASE");

            _supServer = ConfigHelper.GetVariable<string>("SUPPORT_SQL_SERVER");
            _supDb = ConfigHelper.GetVariable<string>("SUPPORT_SQL_DATABASE");
        }

        public async Task<List<ClosedCaseRecord>> GetClosedCasesAsync(
            string productId,
            string sapPath,
            string? caseFilter,
            int numCases,
            int? maxAgeDays = null,
            CancellationToken ct = default)
        {
            const string Sql = @"
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

            var param = new Dictionary<string, string>
            {
                ["@numCases"] = numCases.ToString(),
                ["@prodId"] = productId,
                ["@sapPath"] = sapPath,
                ["@srFilter"] = caseFilter ?? string.Empty,
                ["@maxAgeDays"] = (maxAgeDays ?? -1).ToString()
            };

            var tbl = await _sqlExec.ExecuteQueryAsync(_udpServer, _udpDb, Sql, param, usePMEApp: false);

            var list = tbl.AsEnumerable().Select(r => new ClosedCaseRecord
            {
                SRNumber = r["SRNUMBER"]?.ToString() ?? "",
                PESProductID = r["PESProductID"]?.ToString() ?? "",
                CurrentProductName = r["CURRENTPRODUCTNAME"]?.ToString() ?? "",
                SRCreationDateTime = r.Field<DateTime?>("SRCreationDateTime"),
                SRClosedDateTime = r.Field<DateTime?>("SRClosedDateTime"),

                SROwner = r["SROwner"]?.ToString() ?? "",
                SRStatus = r["SRStatus"]?.ToString() ?? "",
                ServiceRequestStatus = r["ServiceRequestStatus"]?.ToString() ?? "",
                SRSeverity = r["SRSeverity"]?.ToString() ?? "",
                SRMaxSeverity = r["SRMaxSeverity"]?.ToString() ?? "",
                InternalTitle = r["InternalTitle"]?.ToString() ?? "",
                FirstAssignmentDateTime = r.Field<DateTime?>("FirstAssignmentDateTime"),

                CaseIdleDays = Convert.ToInt32(r["CaseIdleDays"]),
                CommunicationIdleDays = Convert.ToInt32(r["CommunicationIdleDays"]),
                IsS500Program = r.Field<bool>("IsS500Program"),
                CollaborationTasks = Convert.ToInt32(r["CollaborationTasks"]),
                PhoneInteractions = Convert.ToInt32(r["PhoneInteractions"]),
                EmailInteractions = Convert.ToInt32(r["EmailInteractions"]),
                IRStatus = r["IRStatus"]?.ToString() ?? "",
                IsTransferred = r.Field<bool>("IsTransferred"),

                SAPPath = r["SAPPath"]?.ToString() ?? "",
                SupportTopicID = r["SupportTopicID"]?.ToString() ?? "",
                SupportTopicIDL2 = r["SupportTopicIDL2"]?.ToString() ?? "",
                SupportTopicIDL3 = r["SupportTopicIDL3"]?.ToString() ?? "",

                CaseAge = r.Field<int>("CaseAge")
            }).ToList();

            _log.LogInformation("[CaseLoader] GetClosedCasesAsync → {Count}", list.Count);
            return list;
        }

        public async Task<List<string>> GetEmailThreadAsync(string sr, CancellationToken ct = default)
        {
            var map = await GetEmailThreadsBatchAsync(new[] { sr });
            return map.TryGetValue(sr, out var txt)
                   ? txt.Split(new[] { "\n----\n" }, StringSplitOptions.None).ToList()
                   : new List<string>();
        }

        public async Task<List<string>> GetNotesThreadAsync(string sr, CancellationToken ct = default)
        {
            var map = await GetNotesThreadsBatchAsync(new[] { sr });
            return map.TryGetValue(sr, out var txt)
                   ? txt.Split(new[] { "\n----\n" }, StringSplitOptions.None).ToList()
                   : new List<string>();
        }

        public async Task<Dictionary<string, string>> GetEmailThreadsBatchAsync(IEnumerable<string> caseNumbers)
        {
            var list = caseNumbers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            _log.LogInformation("🔍 Email batch starting for {Count} SR(s)", list.Count);
            Console.WriteLine($"🔍 Email batch starting for {list.Count} SR(s)");
            if (list.Count == 0) return new();

            var chunks = SplitList(list, BatchSize);
            var tables = new List<DataTable>();

            for (int i = 0; i < chunks.Count; i++)
                tables.Add(await RunEmailChunk(chunks[i], i + 1, chunks.Count));

            return tables.SelectMany(t => t.AsEnumerable())
                         .GroupBy(r => r["ticketnumber"]?.ToString() ?? "")
                         .ToDictionary(
                             g => g.Key,
                             g => string.Join("\n----\n",
                                  g.Select(r =>
                                      $"Subject: {r["Subject"]}; Created On: {r["CreatedOn"]}; " +
                                      $"Message: {CleanMessage(r["Message"]?.ToString())}")));
        }

        public async Task<Dictionary<string, string>> GetNotesThreadsBatchAsync(IEnumerable<string> caseNumbers)
        {
            var list = caseNumbers.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            _log.LogInformation("🔍 Notes batch starting for {Count} SR(s)", list.Count);
            Console.WriteLine($"🔍 Notes batch starting for {list.Count} SR(s)");
            if (list.Count == 0) return new();

            var chunks = SplitList(list, BatchSize);
            var tables = new List<DataTable>();

            for (int i = 0; i < chunks.Count; i++)
                tables.Add(await RunNotesChunk(chunks[i], i + 1, chunks.Count));

            return tables.SelectMany(t => t.AsEnumerable())
                         .GroupBy(r => r["ticketnumber"]?.ToString() ?? "")
                         .ToDictionary(
                             g => g.Key,
                             g => string.Join("\n----\n",
                                  g.Select(r => r["CleanedNote"]?.ToString() ?? "")));
        }

        private async Task<DataTable> RunEmailChunk(List<string> chunk, int chunkNo, int totalChunks)
        {
            var names = chunk.Select((_, i) => $"@p{i}").ToArray();
            var inClause = string.Join(", ", names);

            _log.LogInformation("[CaseLoader] Email chunk {C}/{T}: {Ids}", chunkNo, totalChunks, string.Join(',', chunk));

            string sql = $@"
                SELECT ticketnumber,
                       createdon   AS CreatedOn,
                       subject     AS Subject,
                       description AS Message
                FROM dbo.vwDFMEmails WITH (NOLOCK)
                WHERE ticketnumber IN ({inClause})
                  {EmailFilterClauses}
                ORDER BY ticketnumber, createdon ASC;";

            var param = names.Zip(chunk, (n, id) => new { n, id }).ToDictionary(x => x.n, x => x.id);
            return await ExecWithRetry(sql, param, chunkNo, totalChunks, "Email");
        }

        private async Task<DataTable> RunNotesChunk(List<string> chunk, int chunkNo, int totalChunks)
        {
            var names = chunk.Select((_, i) => $"@p{i}").ToArray();
            var inClause = string.Join(", ", names);

            _log.LogInformation("[CaseLoader] Notes chunk {C}/{T}: {Ids}", chunkNo, totalChunks, string.Join(',', chunk));

            string sql = $@"
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

            var param = names.Zip(chunk, (n, id) => new { n, id }).ToDictionary(x => x.n, x => x.id);
            return await ExecWithRetry(sql, param, chunkNo, totalChunks, "Notes");
        }

        private async Task<DataTable> ExecWithRetry(string sql, Dictionary<string, string> param, int chunkNo, int totalChunks, string label)
        {
            const int maxRetries = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    var tbl = await _sqlExec.ExecuteQueryAsync(_supServer, _supDb, sql, param, usePMEApp: true);
                    _log.LogInformation("[CaseLoader] {Lbl} chunk {C}/{T} → {Rows} rows", label, chunkNo, totalChunks, tbl.Rows.Count);
                    return tbl;
                }
                catch (SqlException ex) when (attempt < maxRetries)
                {
                    _log.LogWarning(ex, "[CaseLoader] {Lbl} chunk {C}/{T} attempt {A} failed – retrying", label, chunkNo, totalChunks, attempt);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
                }
            }
        }

        private static List<List<T>> SplitList<T>(IList<T> src, int size)
        {
            var chunks = new List<List<T>>();
            for (int i = 0; i < src.Count; i += size)
                chunks.Add(src.Skip(i).Take(size).ToList());
            return chunks;
        }

        private string CleanMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var s = Regex.Replace(raw, @"\t|\n|\r", "");
            s = Regex.Replace(s, @"From:(.+?)To:(.+)", "");
            s = Regex.Replace(s, @"<img(.+?)>", "");
            s = Regex.Replace(s, @"<a(.*?)>", "");
            return s.Trim();
        }
    }

    // Interface implementations
    public interface ICaseUploader
    {
        Task UploadAsync(string path);
    }

    public class CaseUploader : ICaseUploader
    {
        public Task UploadAsync(string path) => Task.CompletedTask;
    }

    // Main GraphRagProcessor class
    public class GraphRagProcessor
    {
        private readonly ICaseLoader _loader;
        private readonly IGptChatProviderV2 _gptProvider;
        private readonly KustoQueryExecutor _kustoExecutor;
        private readonly SQLQueryExecutor _sqlExecutor;
        private readonly ICaseUploader _uploader;
        private readonly IAuthenticationProvider _authProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<GraphRagProcessor> _logger;

        public GraphRagProcessor(
            ICaseLoader loader,
            IGptChatProviderV2 gptProvider,
            KustoQueryExecutor kustoExecutor,
            SQLQueryExecutor sqlExecutor,
            ICaseUploader uploader,
            IAuthenticationProvider authProvider,
            IConfiguration configuration,
            ILogger<GraphRagProcessor> logger)
        {
            _loader = loader;
            _gptProvider = gptProvider;
            _kustoExecutor = kustoExecutor;
            _sqlExecutor = sqlExecutor;
            _uploader = uploader;
            _authProvider = authProvider;
            _configuration = configuration;
            _logger = logger;
        }
        private class ProcessedContentInfo
        {
            public string FileName { get; set; } = "";
            public string ProcessedContent { get; set; } = "";  // Full content we've seen before
            public DateTime LastProcessed { get; set; }
            public int ContentLength { get; set; }
        }
        private string ExtractNewContent(string currentContent, string previousContent)
        {
            try
            {
                // If content is same length or shorter, check if identical
                if (currentContent.Length <= previousContent.Length)
                {
                    return currentContent.Equals(previousContent) ? "" : currentContent;
                }

                // If content is longer and starts with previous content, extract new part
                if (currentContent.StartsWith(previousContent))
                {
                    string newPart = currentContent.Substring(previousContent.Length);
                    Console.WriteLine($"📝 Detected appended content: {newPart.Length} characters");
                    return newPart;
                }

                // Content was modified (not just appended), process all
                Console.WriteLine($"📝 Content modified (not just appended), processing full content");
                return currentContent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract new content, processing full content");
                return currentContent;
            }
        }
        #region Main Processing Methods

        [Function("ProcessGraphRagIngestion")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
        {
            _logger.LogInformation("▶️ GraphRAG Function started at {Utc}", DateTime.UtcNow);
            Console.WriteLine($"▶️ GraphRAG Function started at {DateTime.UtcNow}");

            try
            {
                string body = await new StreamReader(req.Body).ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return await CreateResponseAsync(req, HttpStatusCode.BadRequest, "Empty request body.");

                var input = JsonConvert.DeserializeObject<IngestionRequest>(body) ?? new IngestionRequest();

                if (string.IsNullOrWhiteSpace(input.ProductId))
                    return await CreateResponseAsync(req, HttpStatusCode.BadRequest, "ProductId is required.");

                _ = FireAndForget(() => RunComprehensiveAnalysisAsync(input, _logger), _logger);

                return await CreateResponseAsync(req, HttpStatusCode.Accepted,
                    JsonConvert.SerializeObject(new { Status = "Accepted", AnalysisId = Guid.NewGuid().ToString() }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ GraphRAG Function failed");
                return await CreateResponseAsync(req, HttpStatusCode.InternalServerError,
                    JsonConvert.SerializeObject(new { Error = "Internal server error" }));
            }
        }

        public async Task<GraphRagAnalysisResult> RunComprehensiveAnalysisAsync(
     IngestionRequest input,
     ILogger log,
     CancellationToken ct = default)
        {
            var analysisResult = new GraphRagAnalysisResult
            {
                ProductId = input.ProductId,
                AnalysisTimestamp = DateTime.UtcNow
            };

            var stopwatch = Stopwatch.StartNew();

            try
            {
                log.LogInformation("⚙️ Starting comprehensive analysis for ProductId: {ProductId}", input.ProductId);
                Console.WriteLine($"⚙️ Starting comprehensive analysis for ProductId: {input.ProductId}");

                var records = await LoadCaseDataAsync(input);
                analysisResult.TotalCasesAnalyzed = records.Count;

                if (records.Count == 0)
                {
                    log.LogWarning("No cases found for {Product}", input.ProductId);
                    return analysisResult;
                }

                var docs = await BuildCaseDocumentsAsync(records);

                // ===== NEW SECTION START =====
                // Make intelligent processing decision
                var decision = await MakeProcessingDecisionAsync(docs, input);

                // Log the decision with detailed reasoning
                LogProcessingDecision(decision, log);

                // Act based on decision
                if (!decision.ShouldProcess)
                {
                    log.LogInformation("⏭️ Skipping processing: {Reason}", decision.Reason);

                    // Return existing results if available
                    if (decision.Decision == "UseExisting")
                    {
                        var existingResult = await LoadExistingAnalysisResultAsync(input.ProductId, input);
                        if (existingResult != null)
                        {
                            existingResult.SkipReason = decision.Reason;
                            existingResult.ProcessingSkipped = true;
                            existingResult.SkipDetails = decision.SkipReasons;
                            return existingResult;
                        }
                    }

                    // Return minimal result with skip information
                    analysisResult.ProcessingSkipped = true;
                    analysisResult.SkipReason = decision.Reason;
                    analysisResult.SkipDetails = decision.SkipReasons;
                    return analysisResult;
                }

                log.LogInformation("✅ Processing required: {Reason}", decision.Reason);
                // ===== NEW SECTION END =====

                // Continue with existing code...
                var graphRagResult = await PrepareAndIndexWithGraphRAGAsync(docs, input, decision); // Note: added decision parameter
                analysisResult.RootDirectory = graphRagResult.RootDir;
                analysisResult.OutputDirectory = graphRagResult.OutputDir;

                // Rest of your existing code continues...
                await PerformComprehensiveAnalysisAsync(analysisResult, graphRagResult.OutputDir, input);

                if (input.GenerateReports)
                {
                    await GenerateAndSaveReportsAsync(analysisResult, input);
                }

                if (input.SaveToCosmosDB)
                {
                    await SaveToCosmosDBAsync(analysisResult);
                }

                if (input.SaveToAzureDataExplorer)
                {
                    await SaveToAzureDataExplorerAsync(analysisResult);
                }

                await _uploader.UploadAsync(graphRagResult.OutputDir);

                if (!string.IsNullOrWhiteSpace(input.CallbackUri))
                {
                    await SendCallbackAsync(input.CallbackUri, analysisResult);
                }

                stopwatch.Stop();
                analysisResult.Metrics.ProcessingTime = stopwatch.Elapsed;

                log.LogInformation("✅ Comprehensive analysis completed for ProductId: {ProductId} in {Duration}",
                    input.ProductId, stopwatch.Elapsed);

                return analysisResult;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "❌ Comprehensive analysis failed for ProductId: {ProductId}", input.ProductId);
                throw;
            }
        }
        #endregion

        #region Case Data Loading
        private void LogProcessingDecision(ProcessingDecision decision, ILogger log)
        {
            log.LogInformation("📊 Processing Decision: {Decision}", decision.Decision);
            log.LogInformation("   Reason: {Reason}", decision.Reason);
            log.LogInformation("   Stats:");
            log.LogInformation("     - Total cases analyzed: {Total}", decision.Stats.TotalCasesRequested);
            log.LogInformation("     - New cases: {New}", decision.Stats.NewCases);
            log.LogInformation("     - Updated cases: {Updated}", decision.Stats.UpdatedCases);
            log.LogInformation("     - Unchanged cases: {Unchanged}", decision.Stats.UnchangedCases);
            log.LogInformation("     - Self-help changed: {SelfHelp}", decision.Stats.SelfHelpContentChanged);

            if (decision.SkipReasons.Any())
            {
                log.LogInformation("   Skip reasons:");
                foreach (var reason in decision.SkipReasons)
                {
                    log.LogInformation("     - {Reason}", reason);
                }
            }
        }
        private async Task<List<ClosedCaseRecord>> LoadCaseDataAsync(IngestionRequest input)
        {
            return await _loader.GetClosedCasesAsync(
                input.ProductId,
                input.SapPath ?? "",
                input.CaseFilter,
                input.NumCasesToFetch,
                input.MaxAgeDays);
        }

        private async Task<List<CaseDocument>> BuildCaseDocumentsAsync(List<ClosedCaseRecord> records)
        {
            var tasks = records.Select(r => FetchCaseDocumentAsync(r));
            var docs = await Task.WhenAll(tasks);
            return docs.ToList();
        }

        private async Task<CaseDocument> FetchCaseDocumentAsync(ClosedCaseRecord rec)
        {
            try
            {
                string metaText = $@"
=== SUPPORT CASE: {rec.SRNumber} ===
Product: {rec.CurrentProductName}
Area: {rec.SAPPath}
Title: {rec.InternalTitle}
Severity: {rec.SRSeverity} (Max: {rec.SRMaxSeverity})
Resolution Time: {rec.CaseAge} days
Customer Interactions: {rec.EmailInteractions} emails, {rec.PhoneInteractions} calls
Case Age: {rec.CaseAge} days
Idle Days: {rec.CaseIdleDays} case, {rec.CommunicationIdleDays} communication

=== CUSTOMER PROBLEM ===
";

                var emailTask = _loader.GetEmailThreadAsync(rec.SRNumber);
                var noteTask = _loader.GetNotesThreadAsync(rec.SRNumber);
                await Task.WhenAll(emailTask, noteTask);

                var emails = emailTask.Result ?? new List<string>();
                var notes = noteTask.Result ?? new List<string>();

                string customerProblems = "";
                string resolutionSteps = "";

                if (emails.Any())
                {
                    customerProblems += "CUSTOMER COMMUNICATIONS:\n" + string.Join("\n---\n", emails.Take(3)) + "\n\n";
                }

                if (notes.Any())
                {
                    resolutionSteps += "=== RESOLUTION STEPS ===\n" + string.Join("\n---\n", notes) + "\n";
                }

                string combined = metaText + customerProblems + resolutionSteps;

                return new CaseDocument
                {
                    CaseId = rec.SRNumber,
                    Content = combined
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build document for SR {SR}", rec.SRNumber);
                return new CaseDocument { CaseId = rec.SRNumber, Content = string.Empty };
            }
        }

        #endregion

        #region GraphRAG Processing

        private string GetGraphRagWorkRoot(IngestionRequest input = null)
        {
            var rootDir = input?.RootDir;
            return !string.IsNullOrEmpty(rootDir) ? rootDir : @"C:\New folder\GraphRAG";
        }
        private async Task<GraphRagAnalysisResult> LoadExistingAnalysisResultAsync(string productId, IngestionRequest input = null)
        {
            try
            {
                string workRoot = GetGraphRagWorkRoot(input);
                string productDir = Path.Combine(workRoot, $"Product_{productId}_cumulative");
                string reportsDir = Path.Combine(productDir, "reports");

                if (!Directory.Exists(reportsDir))
                    return null;

                // Find the most recent analysis result
                var jsonFiles = Directory.GetFiles(reportsDir, "analysis_result_*.json")
                    .OrderByDescending(f => f)
                    .ToArray();

                if (jsonFiles.Length == 0)
                    return null;

                var json = await File.ReadAllTextAsync(jsonFiles[0]);
                return JsonConvert.DeserializeObject<GraphRagAnalysisResult>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load existing analysis result");
                return null;
            }
        }

        // 4. NEW METHOD: ComputeContentHash
        private string ComputeContentHash(string content)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content ?? "");
                byte[] hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }

        // 5. NEW METHOD: AnalyzeCaseChangesAsync
        private async Task<CaseChangeAnalysis> AnalyzeCaseChangesAsync(
      List<CaseDocument> docs,
      string[] existingFiles,
      ProcessingTracking tracking)
        {
            var analysis = new CaseChangeAnalysis();
            var existingCaseMap = new Dictionary<string, string>();

            // Load existing case hashes
            foreach (var file in existingFiles)
            {
                var caseId = Path.GetFileNameWithoutExtension(file).Replace("case_", "");
                var content = await File.ReadAllTextAsync(file);
                existingCaseMap[caseId] = ComputeContentHash(content);
            }

            // Check each requested case
            foreach (var doc in docs)
            {
                var currentHash = ComputeContentHash(doc.Content);

                if (!existingCaseMap.ContainsKey(doc.CaseId))
                {
                    // New case
                    analysis.NewCases.Add(new CaseChange
                    {
                        CaseId = doc.CaseId,
                        ChangeType = "New",
                        Document = doc
                    });
                }
                else if (existingCaseMap[doc.CaseId] != currentHash)
                {
                    // Updated case
                    analysis.UpdatedCases.Add(new CaseChange
                    {
                        CaseId = doc.CaseId,
                        ChangeType = "Updated",
                        Document = doc,
                        OldHash = existingCaseMap[doc.CaseId],
                        NewHash = currentHash
                    });
                }
                else
                {
                    // Unchanged case
                    analysis.UnchangedCases.Add(new CaseChange
                    {
                        CaseId = doc.CaseId,
                        ChangeType = "Unchanged",
                        Document = doc,
                        OldHash = existingCaseMap[doc.CaseId],
                        NewHash = currentHash
                    });
                }
            }

            return analysis;
        }

        private async Task<bool> CheckSelfHelpContentChangesAsync(string productDir, string productId, IngestionRequest input)
        {
            var trackingFile = Path.Combine(productDir, "input", "selfhelp_content_tracking.json");

            if (!File.Exists(trackingFile))
                return true; // No tracking means content might be new

            try
            {
                var trackingJson = await File.ReadAllTextAsync(trackingFile);
                var tracking = JsonConvert.DeserializeObject<Dictionary<string, ProcessedContentInfo>>(trackingJson);

                string selfHelpDir = Path.Combine(GetGraphRagWorkRoot(input), "Selfhelp");
                if (!Directory.Exists(selfHelpDir))
                    return false; // No self-help directory

                var selfHelpFiles = Directory.GetFiles(selfHelpDir, $"{productId}_*.txt");

                foreach (var file in selfHelpFiles)
                {
                    var fileName = Path.GetFileName(file);
                    var currentContent = await File.ReadAllTextAsync(file);
                    var currentHash = ComputeContentHash(currentContent);

                    if (!tracking.ContainsKey(fileName))
                        return true; // New file

                    if (ComputeContentHash(tracking[fileName].ProcessedContent) != currentHash)
                        return true; // Content changed
                }

                return false; // No changes
            }
            catch
            {
                return true; // Error checking, assume changes
            }
        }
        // 7. NEW METHOD: LoadProcessingTrackingAsync
        private async Task<ProcessingTracking> LoadProcessingTrackingAsync(string trackingFile)
        {
            if (!File.Exists(trackingFile))
                return new ProcessingTracking { LastProcessingTime = DateTime.MinValue };

            try
            {
                var json = await File.ReadAllTextAsync(trackingFile);
                return JsonConvert.DeserializeObject<ProcessingTracking>(json) ?? new ProcessingTracking();
            }
            catch
            {
                return new ProcessingTracking { LastProcessingTime = DateTime.MinValue };
            }
        }
        private async Task<ProcessingDecision> MakeProcessingDecisionAsync(
     List<CaseDocument> docs,
     IngestionRequest input)
        {
            var decision = new ProcessingDecision();
            var stats = new ProcessingStats
            {
                TotalCasesRequested = docs.Count
            };

            string workRoot = GetGraphRagWorkRoot(input);
            string productDir = Path.Combine(workRoot, $"Product_{input.ProductId}_cumulative");
            string inputDir = Path.Combine(productDir, "input");
            string trackingFile = Path.Combine(productDir, "processing_tracking.json");

            // Check if we have existing processing
            if (!Directory.Exists(productDir) || !Directory.Exists(inputDir))
            {
                decision.ShouldProcess = true;
                decision.Decision = "Process";
                decision.Reason = "First time processing - no existing data found";
                decision.Stats = stats;
                return decision;
            }

            // Load tracking information
            var tracking = await LoadProcessingTrackingAsync(trackingFile);
            stats.LastProcessingTime = tracking.LastProcessingTime;
            stats.TimeSinceLastProcess = DateTime.UtcNow - tracking.LastProcessingTime;

            // Check existing cases
            var existingCaseFiles = Directory.GetFiles(inputDir, "case_*.txt");
            stats.TotalExistingCases = existingCaseFiles.Length;

            // Analyze case changes
            var caseAnalysis = await AnalyzeCaseChangesAsync(docs, existingCaseFiles, tracking);
            stats.NewCases = caseAnalysis.NewCases.Count;
            stats.UpdatedCases = caseAnalysis.UpdatedCases.Count;
            stats.UnchangedCases = caseAnalysis.UnchangedCases.Count;

            // Check self-help content changes - PASS INPUT PARAMETER HERE
            var selfHelpChanged = await CheckSelfHelpContentChangesAsync(productDir, input.ProductId, input);
            stats.SelfHelpContentChanged = selfHelpChanged;

            // Make decision based on all factors
            decision.Stats = stats;

            // Build skip reasons if no changes
            if (stats.NewCases == 0 && stats.UpdatedCases == 0 && !selfHelpChanged)
            {
                decision.ShouldProcess = false;
                decision.Decision = "UseExisting";

                decision.SkipReasons.Add($"No new cases found (analyzed {stats.TotalCasesRequested} cases, all {stats.UnchangedCases} unchanged)");
                decision.SkipReasons.Add($"No case content updates detected");
                decision.SkipReasons.Add($"Self-help content unchanged");
                decision.SkipReasons.Add($"Last processed: {stats.LastProcessingTime:yyyy-MM-dd HH:mm:ss} ({stats.TimeSinceLastProcess.TotalHours:F1} hours ago)");

                // Check if we have valid existing output
                var outputDirs = Directory.GetDirectories(Path.Combine(productDir, "output"));
                if (outputDirs.Length > 0)
                {
                    decision.SkipReasons.Add($"Using existing GraphRAG index from: {Path.GetFileName(outputDirs.OrderByDescending(d => d).First())}");
                }

                decision.Reason = $"No changes detected - all {stats.TotalCasesRequested} cases and self-help content unchanged since last processing";
            }
            else
            {
                // We need to process - build reason
                decision.ShouldProcess = true;
                decision.Decision = "Process";

                var reasons = new List<string>();
                if (stats.NewCases > 0)
                    reasons.Add($"{stats.NewCases} new cases");
                if (stats.UpdatedCases > 0)
                    reasons.Add($"{stats.UpdatedCases} updated cases");
                if (selfHelpChanged)
                    reasons.Add("self-help content changed");

                decision.Reason = $"Changes detected: {string.Join(", ", reasons)}";
            }

            // Check for force processing conditions
            if (input.Mode == "full" || input.ForceReindex)
            {
                decision.ShouldProcess = true;
                decision.Decision = "Process";
                decision.Reason = "Force reindex requested by user";
                decision.SkipReasons.Clear();
            }

            return decision;
        }
        private async Task<GraphRAGIndexResult> PrepareAndIndexWithGraphRAGAsync(
     List<CaseDocument> docs,
     IngestionRequest input,
     ProcessingDecision decision = null) // Added optional parameter
        {
            try
            {
                string workRoot = GetGraphRagWorkRoot(input);
                Directory.CreateDirectory(workRoot);

                string productCumulativeDir = Path.Combine(workRoot, $"Product_{input.ProductId}_cumulative");
                string inputDir = Path.Combine(productCumulativeDir, "input");

                _logger.LogInformation("📊 Using cumulative analysis for ProductId: {ProductId}", input.ProductId);
                Console.WriteLine($"📊 ProductId: {input.ProductId}");
                Console.WriteLine($"📂 Cumulative directory: {productCumulativeDir}");

                Directory.CreateDirectory(productCumulativeDir);
                Directory.CreateDirectory(inputDir);

                var existingCaseFiles = Directory.GetFiles(inputDir, "case_*.txt");
                bool isIncremental = input.SkipExisting && existingCaseFiles.Length > 0;

                if (isIncremental)
                {
                    Console.WriteLine($"📈 Incremental update: Found {existingCaseFiles.Length} existing cases");
                    var existingCaseIds = existingCaseFiles
                        .Select(f => Path.GetFileNameWithoutExtension(f).Replace("case_", ""))
                        .ToHashSet();

                    // ===== MODIFIED: Only process truly new cases =====
                    docs = docs.Where(d => !existingCaseIds.Contains(d.CaseId)).ToList();
                    Console.WriteLine($"➕ Adding {docs.Count} new cases (filtered duplicates)");

                    // ===== NEW: Early exit if no new cases =====
                    if (docs.Count == 0 && decision?.Stats.UpdatedCases == 0)
                    {
                        var existingOutputDir = Directory.GetDirectories(Path.Combine(productCumulativeDir, "output"))
                            .OrderByDescending(d => d).FirstOrDefault() ?? "";

                        if (!string.IsNullOrEmpty(existingOutputDir))
                        {
                            Console.WriteLine("✅ No new cases to process, using existing index");
                            return new GraphRAGIndexResult { RootDir = productCumulativeDir, OutputDir = existingOutputDir };
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"🆕 Fresh analysis: Processing {docs.Count} cases");
                    if (Directory.Exists(inputDir))
                    {
                        Directory.Delete(inputDir, true);
                        Directory.CreateDirectory(inputDir);
                    }
                }

                foreach (var doc in docs)
                {
                    string caseFilePath = Path.Combine(inputDir, $"case_{doc.CaseId}.txt");
                    await File.WriteAllTextAsync(caseFilePath, doc.Content ?? "");
                }

                await AddSelfHelpContentToInputAsync(inputDir, input.ProductId, workRoot);

                var allInputFiles = Directory.GetFiles(inputDir, "*.txt");
                int totalCases = allInputFiles.Count(f => f.Contains("case_"));
                Console.WriteLine($"📋 Total files for GraphRAG: {allInputFiles.Length} ({totalCases} cases + self-help content)");

                string outputDir = Path.Combine(productCumulativeDir, "output");

                // ===== REMOVED: This section that deletes output =====
                // if (isIncremental && Directory.Exists(outputDir))
                // {
                //     Directory.Delete(outputDir, true);  // <- THIS WAS THE PROBLEM!
                //     Console.WriteLine("🗑️ Removed old output for reprocessing with additional cases");
                // }

                // ===== MODIFIED: Only skip if truly nothing to process =====
                if (input.SkipExisting && Directory.Exists(outputDir) && docs.Count == 0)
                {
                    _logger.LogInformation("Skipping existing index for ProductId: {ProductId}", input.ProductId);
                    var existingOutputDir = Directory.GetDirectories(outputDir)
                        .OrderByDescending(d => d).FirstOrDefault() ?? outputDir;
                    return new GraphRAGIndexResult { RootDir = productCumulativeDir, OutputDir = existingOutputDir };
                }

                // ===== MODIFIED: Only run indexing if we have new content =====
                if (docs.Count > 0 || !Directory.Exists(outputDir))
                {
                    await RunGraphRagIndexingAsync(productCumulativeDir, input);
                }

                outputDir = Directory.GetDirectories(Path.Combine(productCumulativeDir, "output"))
                    .OrderByDescending(d => d).FirstOrDefault() ?? "";

                if (string.IsNullOrEmpty(outputDir))
                    throw new DirectoryNotFoundException("No output directory found after indexing.");

                Console.WriteLine($"✅ GraphRAG processing completed for ProductId {input.ProductId}");
                return new GraphRAGIndexResult { RootDir = productCumulativeDir, OutputDir = outputDir };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prepare and index with GraphRAG for ProductId: {ProductId}", input.ProductId);
                throw;
            }
        }
        private class IncrementalTrackingInfo
        {
            public DateTime LastFullIndex { get; set; }
            public DateTime LastIncrementalUpdate { get; set; }
            public int IncrementalUpdateCount { get; set; }
            public int TotalCasesProcessed { get; set; }
            public string LastGraphRagVersion { get; set; } = "1.0";
            public Dictionary<string, DateTime> ProcessedCases { get; set; } = new();
        }

        private async Task<IncrementalTrackingInfo> LoadIncrementalTrackingAsync(string trackingFile)
        {
            if (!File.Exists(trackingFile))
                return new IncrementalTrackingInfo { LastFullIndex = DateTime.MinValue };

            try
            {
                var json = await File.ReadAllTextAsync(trackingFile);
                return JsonConvert.DeserializeObject<IncrementalTrackingInfo>(json) ?? new IncrementalTrackingInfo();
            }
            catch
            {
                return new IncrementalTrackingInfo { LastFullIndex = DateTime.MinValue };
            }
        }

        private async Task SaveIncrementalTrackingAsync(string trackingFile, IncrementalTrackingInfo info)
        {
            var json = JsonConvert.SerializeObject(info, Formatting.Indented);
            await File.WriteAllTextAsync(trackingFile, json);
        }

        private bool ShouldPerformFullReindex(IncrementalTrackingInfo tracking, IngestionRequest input)
        {
            // Force full reindex if explicitly requested
            if (input.Mode == "full") return true;

            // Reindex if never indexed before
            if (tracking.LastFullIndex == DateTime.MinValue) return true;

            // Reindex if too many incremental updates (affects graph quality)
            if (tracking.IncrementalUpdateCount > 10) return true;

            // Reindex if it's been too long (e.g., weekly)
            if ((DateTime.UtcNow - tracking.LastFullIndex).TotalDays > 7) return true;

            // Reindex if GraphRAG version changed (hypothetical)
            // if (tracking.LastGraphRagVersion != CurrentGraphRagVersion) return true;

            return false;
        }

        private string GetReindexReason(IncrementalTrackingInfo tracking)
        {
            if (tracking.LastFullIndex == DateTime.MinValue) return "First time indexing";
            if (tracking.IncrementalUpdateCount > 10) return "Too many incremental updates";
            if ((DateTime.UtcNow - tracking.LastFullIndex).TotalDays > 7) return "Weekly reindex schedule";
            return "Requested full reindex";
        }

        // Alternative approach: Merge strategy for truly incremental updates
        private async Task<GraphRAGIndexResult> PrepareAndIndexWithMergeStrategyAsync(List<CaseDocument> docs, IngestionRequest input)
        {
            // This approach processes only new cases and merges results
            string workRoot = GetGraphRagWorkRoot(input);
            string productDir = Path.Combine(workRoot, $"Product_{input.ProductId}_cumulative");
            string tempDir = Path.Combine(workRoot, $"Product_{input.ProductId}_temp");

            // Process only new cases in temp directory
            Directory.CreateDirectory(tempDir);
            var tempInputDir = Path.Combine(tempDir, "input");
            Directory.CreateDirectory(tempInputDir);

            foreach (var doc in docs)
            {
                string caseFilePath = Path.Combine(tempInputDir, $"case_{doc.CaseId}.txt");
                await File.WriteAllTextAsync(caseFilePath, doc.Content ?? "");
            }

            // Run GraphRAG on just the new cases
            await RunGraphRagIndexingAsync(tempDir, input);

            // Merge results with existing data
            await MergeGraphRAGResultsAsync(productDir, tempDir);

            // Clean up temp directory
            Directory.Delete(tempDir, true);

            var outputDir = Directory.GetDirectories(Path.Combine(productDir, "output"))
                .OrderByDescending(d => d).FirstOrDefault() ?? "";

            return new GraphRAGIndexResult { RootDir = productDir, OutputDir = outputDir };
        }

        private async Task MergeGraphRAGResultsAsync(string mainDir, string tempDir)
        {
            // This would require custom logic to merge:
            // 1. entities.csv - append new entities, update existing ones
            // 2. relationships.csv - append new relationships
            // 3. communities.parquet - this is tricky, might need full recalculation
            // 4. LanceDB embeddings - append new embeddings

            // Note: This is complex and requires understanding GraphRAG's internal format
            _logger.LogWarning("Merge strategy not fully implemented - would require custom merge logic");
        }
        private async Task RunGraphRagIndexingAsync(string rootDir, IngestionRequest input)
        {
            string pythonExe = _configuration["PythonPath"] ?? throw new InvalidOperationException("PythonPath not configured.");

            string settingsPath = Path.Combine(rootDir, "settings.yaml");
            string graphragDirPath = Path.Combine(rootDir, ".graphrag");
            string graphragSettingsPath = Path.Combine(graphragDirPath, "settings.yaml");

            // Check if either settings file exists
            if (!File.Exists(settingsPath) && !File.Exists(graphragSettingsPath))
            {
                Console.WriteLine("🔧 Initializing GraphRAG...");
                Environment.SetEnvironmentVariable("GRAPHRAG_API_KEY", _configuration["AzureOpenAIKey"]);
                string initOutput = PythonRunner.Run(pythonExe, $"-m graphrag init --root \"{rootDir}\"");
                Console.WriteLine($"[DEBUG] init output: {initOutput}");
            }

            // Always write to the main settings location (safer)
            string settingsContent = GraphRagSettingsBuilder.Build(_configuration);
            await File.WriteAllTextAsync(settingsPath, settingsContent);
            Console.WriteLine($"🔧 Settings written to: {settingsPath}");

            Console.WriteLine("⚙️ Running GraphRAG indexing...");
            string indexOutput = PythonRunner.Run(pythonExe, $"-m graphrag index --root \"{rootDir}\" --verbose");
            Console.WriteLine($"[DEBUG] index output: {indexOutput}");
            _logger.LogInformation("Indexing completed for: {RootDir}", rootDir);
        }

        private async Task AddSelfHelpContentToInputAsync(string inputDir, string productId, string workRoot)
        {
            try
            {
                string selfHelpDir = Path.Combine(workRoot, "Selfhelp");
                string selfHelpDestPath = Path.Combine(inputDir, "existing_selfhelp_content.txt");
                string contentTrackingFile = Path.Combine(inputDir, "selfhelp_content_tracking.json");

                _logger.LogInformation("🔍 Smart content-diff check for ProductId: {ProductId}", productId);
                Console.WriteLine($"🔍 Smart content-diff check for ProductId: {productId}");

                if (!Directory.Exists(selfHelpDir))
                {
                    await CreateNoSelfHelpPlaceholder(inputDir, productId);
                    return;
                }

                var selfHelpFiles = Directory.GetFiles(selfHelpDir, $"{productId}_*.txt");
                if (selfHelpFiles.Length == 0)
                {
                    await CreateNoSelfHelpPlaceholder(inputDir, productId);
                    return;
                }

                // Load content tracking
                var processedContent = new Dictionary<string, ProcessedContentInfo>();
                if (File.Exists(contentTrackingFile))
                {
                    try
                    {
                        var trackingJson = await File.ReadAllTextAsync(contentTrackingFile);
                        processedContent = JsonConvert.DeserializeObject<Dictionary<string, ProcessedContentInfo>>(trackingJson) ?? new();
                        Console.WriteLine($"📊 Loaded content tracking for {processedContent.Count} files");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load content tracking, will process all content");
                        processedContent = new();
                    }
                }

                // Analyze content changes
                var newContentSections = new List<string>();
                var updatedTracking = new Dictionary<string, ProcessedContentInfo>();
                bool hasNewContent = false;

                foreach (var selfHelpFile in selfHelpFiles)
                {
                    string fileName = Path.GetFileName(selfHelpFile);
                    string currentContent = await File.ReadAllTextAsync(selfHelpFile);

                    if (string.IsNullOrWhiteSpace(currentContent)) continue;

                    // Check what's new in this file
                    string newContentOnly = "";

                    if (processedContent.ContainsKey(fileName))
                    {
                        var previousInfo = processedContent[fileName];
                        newContentOnly = ExtractNewContent(currentContent, previousInfo.ProcessedContent);

                        if (string.IsNullOrWhiteSpace(newContentOnly))
                        {
                            Console.WriteLine($"✅ No new content: {fileName}");
                            updatedTracking[fileName] = new ProcessedContentInfo
                            {
                                FileName = fileName,
                                ProcessedContent = currentContent,
                                LastProcessed = previousInfo.LastProcessed,
                                ContentLength = currentContent.Length
                            };
                            continue;
                        }
                        else
                        {
                            Console.WriteLine($"🆕 New content found: {fileName} (+{newContentOnly.Length} characters)");
                            hasNewContent = true;
                        }
                    }
                    else
                    {
                        // Completely new file
                        newContentOnly = currentContent;
                        Console.WriteLine($"🆕 New file: {fileName} ({currentContent.Length} characters)");
                        hasNewContent = true;
                    }

                    // Create section for new content only
                    if (!string.IsNullOrWhiteSpace(newContentOnly))
                    {
                        string productName = fileName.Replace($"{productId}_", "").Replace(".txt", "");
                        string sectionContent = $@"

=== SELF-HELP CONTENT UPDATE: {productName} (ProductId: {productId}) ===
Source File: {fileName}
Update Type: {(processedContent.ContainsKey(fileName) ? "INCREMENTAL UPDATE" : "NEW FILE")}
New Content Length: {newContentOnly.Length} characters
Last Updated: {File.GetLastWriteTime(selfHelpFile):yyyy-MM-dd HH:mm:ss}

{newContentOnly}

=== END {productName} UPDATE ===
";
                        newContentSections.Add(sectionContent);
                    }

                    // Update tracking
                    updatedTracking[fileName] = new ProcessedContentInfo
                    {
                        FileName = fileName,
                        ProcessedContent = currentContent,
                        LastProcessed = DateTime.UtcNow,
                        ContentLength = currentContent.Length
                    };
                }

                // Skip if no new content
                if (!hasNewContent)
                {
                    Console.WriteLine($"✅ All self-help content up-to-date, skipping regeneration");
                    return;
                }

                // Append only new content to existing file
                string existingContent = "";
                if (File.Exists(selfHelpDestPath))
                {
                    existingContent = await File.ReadAllTextAsync(selfHelpDestPath);
                    Console.WriteLine($"📖 Loaded existing self-help content ({existingContent.Length} characters)");
                }

                string finalContent;
                if (string.IsNullOrEmpty(existingContent))
                {
                    finalContent = $@"=== EXISTING SELF-HELP DOCUMENTATION FOR PRODUCT {productId} ===

Product ID: {productId}
Content Type: Self-Help Documentation (Content-Diff Tracking)
Total Files Monitored: {selfHelpFiles.Length}
Last Updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}

{string.Join("\n", newContentSections)}

=== END ALL SELF-HELP CONTENT FOR PRODUCT {productId} ===";
                }
                else
                {
                    finalContent = existingContent.TrimEnd() +
                                  string.Join("\n", newContentSections) +
                                  $"\n\n=== CONTENT-DIFF UPDATE COMPLETED: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} ===";
                }

                // Save updated content and tracking
                await File.WriteAllTextAsync(selfHelpDestPath, finalContent);

                var updatedTrackingJson = JsonConvert.SerializeObject(updatedTracking, Formatting.Indented);  // ← FIXED
                await File.WriteAllTextAsync(contentTrackingFile, updatedTrackingJson);

                Console.WriteLine($"✅ Content-diff update completed: new content from {newContentSections.Count} files, total: {finalContent.Length} characters");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed content-diff processing for ProductId {ProductId}", productId);
                await CreateNoSelfHelpPlaceholder(inputDir, productId);
            }
        }

        private async Task CreateNoSelfHelpPlaceholder(string inputDir, string productId)
        {
            string placeholderContent = $@"=== NO EXISTING SELF-HELP CONTENT FOUND FOR PRODUCT {productId} ===

Product ID: {productId}
Expected File Pattern: C:\New folder\GraphRAG\Selfhelp\{productId}_*.txt
Status: No existing self-help content found

This indicates there may be limited or no existing self-help documentation for this product.
GraphRAG analysis should focus on identifying the most critical content gaps.";

            string placeholderPath = Path.Combine(inputDir, "no_existing_selfhelp_placeholder.txt");
            await File.WriteAllTextAsync(placeholderPath, placeholderContent);

            Console.WriteLine($"📝 Created placeholder - no self-help content found for ProductId {productId}");
        }

        #endregion

        #region Comprehensive Analysis

        private async Task PerformComprehensiveAnalysisAsync(GraphRagAnalysisResult result, string outputDir, IngestionRequest input)
        {
            try
            {
                _logger.LogInformation("📊 Starting comprehensive analysis...");

                result.SelfHelpGaps = await PerformSelfHelpGapAnalysisAsync(result.RootDirectory);
                result.FMEAAnalysis = await PerformFMEAAnalysisAsync(result.RootDirectory);
                await PerformEntityRelationshipAnalysisAsync(result, outputDir);
                await PerformCommunityAnalysisAsync(result, outputDir);
                result.Recommendations = GenerateActionableRecommendations(result);

                _logger.LogInformation("✅ Comprehensive analysis completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Comprehensive analysis failed");
                throw;
            }
        }

        private async Task<SelfHelpGapAnalysis> PerformSelfHelpGapAnalysisAsync(string rootDir)
        {
            var gapAnalysis = new SelfHelpGapAnalysis();

            var queries = new[]
            {
                "What problems appear frequently in support cases but have no corresponding self-help content?",
                "Which issues could customers solve themselves but currently require support intervention?",
                "What existing self-help content covers which specific problems?",
                "Which self-help topics need updates based on current support resolution patterns?"
            };

            foreach (var query in queries)
            {
                try
                {
                    var response = await QueryGraphRagAsync(rootDir, query);
                    await ParseSelfHelpGapResponse(gapAnalysis, query, response);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute self-help gap query: {Query}", query);
                }
            }

            return gapAnalysis;
        }

        private async Task<FMEAAnalysisResult> PerformFMEAAnalysisAsync(string rootDir)
        {
            var fmeaAnalysis = new FMEAAnalysisResult();

            var queries = new[]
            {
                "What are the most common failure modes and their causes across all support cases?",
                "Which failure modes have the highest risk priority based on severity, frequency, and detectability?",
                "What prevention controls exist and where are the gaps?",
                "Which failures could be prevented with better monitoring or customer education?"
            };

            foreach (var query in queries)
            {
                try
                {
                    var response = await QueryGraphRagAsync(rootDir, query);
                    await ParseFMEAResponse(fmeaAnalysis, query, response);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute FMEA query: {Query}", query);
                }
            }

            return fmeaAnalysis;
        }

        private async Task PerformEntityRelationshipAnalysisAsync(GraphRagAnalysisResult result, string outputDir)
        {
            try
            {
                var entitiesPath = Path.Combine(outputDir, "entities.csv");
                var relationshipsPath = Path.Combine(outputDir, "relationships.csv");

                if (File.Exists(entitiesPath))
                {
                    result.EntityInsights = await AnalyzeEntitiesAsync(entitiesPath);
                    result.Metrics.TotalEntitiesExtracted = result.EntityInsights.Count;
                }

                if (File.Exists(relationshipsPath))
                {
                    result.RelationshipInsights = await AnalyzeRelationshipsAsync(relationshipsPath);
                    result.Metrics.TotalRelationshipsFound = result.RelationshipInsights.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform entity/relationship analysis");
            }
        }

        private async Task PerformCommunityAnalysisAsync(GraphRagAnalysisResult result, string outputDir)
        {
            try
            {
                var communityReportsPath = Path.Combine(outputDir, "community_reports.csv");
                if (File.Exists(communityReportsPath))
                {
                    result.CommunityInsights = await AnalyzeCommunitiesAsync(communityReportsPath);
                    result.Metrics.TotalCommunitiesIdentified = result.CommunityInsights.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to perform community analysis");
            }
        }

        #endregion

        #region Query Methods

        public async Task<string> QueryGraphRagAsync(string rootDir, string query, string method = "global")
        {
            try
            {
                string pythonExe = _configuration["PythonPath"] ?? throw new InvalidOperationException("PythonPath not configured.");
                string queryArgs = $"-m graphrag query --root \"{rootDir}\" --method {method} --query \"{query}\"";

                _logger.LogInformation("🔍 Executing GraphRAG query: {Query}", query);
                string result = PythonRunner.Run(pythonExe, queryArgs);

                _logger.LogInformation("✅ Query completed, result length: {Length}", result.Length);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ GraphRAG query failed");
                return $"Query failed: {ex.Message}";
            }
        }

        public async Task<List<string>> RunGapAnalysisQueriesAsync(string rootDir)
        {
            var queries = new[]
            {
                "What problems appear most frequently in support cases but have no self-help documentation?",
                "Which customer issues could be resolved with better self-service content?",
                "What are the top 5 knowledge gaps where customers repeatedly need support help?",
                "Which product areas have the most support cases but least self-help coverage?",
                "What failure modes occur repeatedly and could be prevented?"
            };

            var results = new List<string>();

            foreach (var query in queries)
            {
                Console.WriteLine($"\n📊 Running analysis query: {query}");
                var result = await QueryGraphRagAsync(rootDir, query, "global");
                results.Add($"QUERY: {query}\n\nRESULT:\n{result}\n{new string('=', 80)}");
            }

            return results;
        }

        #endregion

        #region Parsing Methods (Updated with Your GPT Provider)

        private async Task ParseSelfHelpGapResponse(SelfHelpGapAnalysis gapAnalysis, string query, string response)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(response)) return;

                string structurePrompt = $@"
Analyze this GraphRAG response about self-help gaps and extract structured information.

Query: {query}
Response: {response}

Extract specific self-help gaps and return as JSON array with this structure:
[
  {{
    ""problemArea"": ""specific problem area"",
    ""issueCategory"": ""category like Performance, Configuration, etc"",
    ""frequencyInCases"": estimated_number,
    ""customerSolvable"": true/false,
    ""suggestedTitle"": ""proposed article title"",
    ""estimatedImpact"": score_1_to_10,
    ""relatedCaseNumbers"": [""extracted case numbers""],
    ""relatedSAPPaths"": [""extracted SAP paths""]
  }}
]

Focus on problems that appear multiple times and could be solved by customers with proper documentation.";

                var httpResp = await _gptProvider.GetSearchAsync(new GptChatRequestBody
                {
                    Messages = new List<Message>
                    {
                        new Message { Role = "user", Content = structurePrompt }
                    },
                    Temperature = 0.7M,
                    Max_tokens = 2048
                }, _logger);

                var structuredResponse = await _gptProvider.ProcessHttpResponse(httpResp);
                var gaps = JsonConvert.DeserializeObject<List<dynamic>>(structuredResponse);

                foreach (var gapData in gaps)
                {
                    var gap = new SelfHelpGap
                    {
                        GapId = Guid.NewGuid().ToString(),
                        ProblemArea = gapData.problemArea?.ToString() ?? "",
                        IssueCategory = gapData.issueCategory?.ToString() ?? "",
                        FrequencyInCases = (int)(gapData.frequencyInCases ?? 1),
                        CustomerSolvable = (bool)(gapData.customerSolvable ?? false),
                        SuggestedTitle = gapData.suggestedTitle?.ToString() ?? "",
                        EstimatedImpact = (int)(gapData.estimatedImpact ?? 5),
                        RelatedCaseNumbers = JsonConvert.DeserializeObject<List<string>>(gapData.relatedCaseNumbers?.ToString() ?? "[]"),
                        RelatedSAPPaths = JsonConvert.DeserializeObject<List<string>>(gapData.relatedSAPPaths?.ToString() ?? "[]")
                    };

                    gapAnalysis.IdentifiedGaps.Add(gap);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse self-help gap response for query: {Query}", query);
            }
        }

        private async Task ParseFMEAResponse(FMEAAnalysisResult fmeaAnalysis, string query, string response)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(response)) return;

                string structurePrompt = $@"
Analyze this GraphRAG response about FMEA and extract structured failure mode information.

Query: {query}
Response: {response}

Extract failure modes and return as JSON with this structure:
{{
  ""failureModes"": [
    {{
      ""failureMode"": ""specific failure mode"",
      ""failureCause"": ""what causes this failure"",
      ""failureEffect"": ""impact/consequence"",
      ""severityRating"": score_1_to_10,
      ""occurrenceFrequency"": score_1_to_10,
      ""detectabilityRating"": score_1_to_10,
      ""existingControls"": [""current controls""],
      ""recommendedActions"": [""suggested improvements""]
    }}
  ],
  ""preventionOpportunities"": [
    {{
      ""failureMode"": ""failure mode"",
      ""preventionType"": ""type of prevention"",
      ""recommendedControl"": ""suggested control"",
      ""customerActionable"": true/false,
      ""selfHelpContentNeeded"": ""what content is needed""
    }}
  ]
}}

Focus on failures with high risk priority (Severity × Occurrence × Detectability).";

                var httpResp = await _gptProvider.GetSearchAsync(new GptChatRequestBody
                {
                    Messages = new List<Message>
                    {
                        new Message { Role = "user", Content = structurePrompt }
                    },
                    Temperature = 0.7M,
                    Max_tokens = 2048
                }, _logger);

                var structuredResponse = await _gptProvider.ProcessHttpResponse(httpResp);
                var fmeaData = JsonConvert.DeserializeObject<dynamic>(structuredResponse);

                if (fmeaData.failureModes != null)
                {
                    foreach (var fmData in fmeaData.failureModes)
                    {
                        var failureMode = new FailureModeAnalysis
                        {
                            FailureMode = fmData.failureMode?.ToString() ?? "",
                            FailureCause = fmData.failureCause?.ToString() ?? "",
                            FailureEffect = fmData.failureEffect?.ToString() ?? "",
                            SeverityRating = (int)(fmData.severityRating ?? 5),
                            OccurrenceFrequency = (int)(fmData.occurrenceFrequency ?? 5),
                            DetectabilityRating = (int)(fmData.detectabilityRating ?? 5),
                            ExistingControls = JsonConvert.DeserializeObject<List<string>>(fmData.existingControls?.ToString() ?? "[]"),
                            RecommendedActions = JsonConvert.DeserializeObject<List<string>>(fmData.recommendedActions?.ToString() ?? "[]")
                        };

                        fmeaAnalysis.FailureModes.Add(failureMode);

                        if (failureMode.RiskPriorityNumber > 100)
                        {
                            fmeaAnalysis.HighRiskItems.Add(new RiskPriorityItem
                            {
                                FailureMode = failureMode.FailureMode,
                                RPN = failureMode.RiskPriorityNumber,
                                RiskLevel = failureMode.RiskPriorityNumber > 200 ? "Critical" : "High",
                                ImmediateAction = failureMode.RecommendedActions.FirstOrDefault() ?? "Review and assess"
                            });
                        }
                    }
                }

                if (fmeaData.preventionOpportunities != null)
                {
                    foreach (var prevData in fmeaData.preventionOpportunities)
                    {
                        fmeaAnalysis.PreventionOpportunities.Add(new PreventionOpportunity
                        {
                            FailureMode = prevData.failureMode?.ToString() ?? "",
                            PreventionType = prevData.preventionType?.ToString() ?? "",
                            RecommendedControl = prevData.recommendedControl?.ToString() ?? "",
                            CustomerActionable = (bool)(prevData.customerActionable ?? false),
                            SelfHelpContentNeeded = prevData.selfHelpContentNeeded?.ToString() ?? ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse FMEA response for query: {Query}", query);
            }
        }

        #endregion

        #region Report Generation and Storage

        private async Task GenerateAndSaveReportsAsync(GraphRagAnalysisResult result, IngestionRequest input)
        {
            try
            {
                _logger.LogInformation("📄 Generating comprehensive reports...");

                var reports = new Dictionary<string, string>
                {
                    ["executive_summary"] = GenerateExecutiveSummaryReport(result),
                    ["selfhelp_gaps"] = GenerateSelfHelpGapReport(result.SelfHelpGaps),
                    ["fmea_analysis"] = GenerateFMEAReport(result.FMEAAnalysis),
                    ["actionable_recommendations"] = GenerateRecommendationsReport(result.Recommendations),
                    ["detailed_analysis"] = GenerateDetailedAnalysisReport(result)
                };

                string reportsDir = Path.Combine(result.RootDirectory, "reports");
                Directory.CreateDirectory(reportsDir);

                foreach (var report in reports)
                {
                    string reportPath = Path.Combine(reportsDir, $"{report.Key}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.md");
                    await File.WriteAllTextAsync(reportPath, report.Value);
                    _logger.LogInformation("📄 Generated report: {ReportPath}", reportPath);
                }

                string jsonSummary = JsonConvert.SerializeObject(result, Formatting.Indented);
                string jsonPath = Path.Combine(reportsDir, $"analysis_result_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
                await File.WriteAllTextAsync(jsonPath, jsonSummary);

                Console.WriteLine($"📄 All reports generated in: {reportsDir}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to generate reports");
            }
        }

        private async Task SaveToCosmosDBAsync(GraphRagAnalysisResult result)
        {
            try
            {
                _logger.LogInformation("💾 Saving analysis result to Cosmos DB...");

                var cosmosDocument = new
                {
                    id = result.AnalysisId,
                    productId = result.ProductId,
                    analysisTimestamp = result.AnalysisTimestamp,
                    totalCasesAnalyzed = result.TotalCasesAnalyzed,
                    selfHelpGaps = result.SelfHelpGaps.IdentifiedGaps.Select(g => new
                    {
                        gapId = g.GapId,
                        problemArea = g.ProblemArea,
                        frequencyInCases = g.FrequencyInCases,
                        customerSolvable = g.CustomerSolvable,
                        estimatedImpact = g.EstimatedImpact,
                        suggestedTitle = g.SuggestedTitle,
                        relatedCaseNumbers = g.RelatedCaseNumbers,
                        relatedSAPPaths = g.RelatedSAPPaths
                    }),
                    fmeaHighRiskItems = result.FMEAAnalysis.HighRiskItems.Select(r => new
                    {
                        failureMode = r.FailureMode,
                        rpn = r.RPN,
                        riskLevel = r.RiskLevel,
                        immediateAction = r.ImmediateAction
                    }),
                    recommendations = result.Recommendations.Select(r => new
                    {
                        recommendationId = r.RecommendationId,
                        category = r.Category,
                        title = r.Title,
                        priority = r.Priority,
                        estimatedImpact = r.EstimatedImpact
                    }),
                    metrics = new
                    {
                        totalEntitiesExtracted = result.Metrics.TotalEntitiesExtracted,
                        totalRelationshipsFound = result.Metrics.TotalRelationshipsFound,
                        processingTimeMinutes = result.Metrics.ProcessingTime.TotalMinutes
                    },
                    ttl = 86400 * 365 // 1 year TTL
                };

                // Here you would use your Cosmos DB client to save the document
                // await _cosmosClient.CreateDocumentAsync(cosmosDocument);

                _logger.LogInformation("✅ Analysis result saved to Cosmos DB with ID: {AnalysisId}", result.AnalysisId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to save to Cosmos DB");
            }
        }

        private async Task SaveToAzureDataExplorerAsync(GraphRagAnalysisResult result)
        {
            try
            {
                _logger.LogInformation("📊 Saving analysis result to Azure Data Explorer...");

                var analysisRecord = new
                {
                    AnalysisId = result.AnalysisId,
                    ProductId = result.ProductId,
                    AnalysisTimestamp = result.AnalysisTimestamp,
                    TotalCasesAnalyzed = result.TotalCasesAnalyzed,
                    TotalEntitiesExtracted = result.Metrics.TotalEntitiesExtracted,
                    TotalRelationshipsFound = result.Metrics.TotalRelationshipsFound,
                    ProcessingTimeMinutes = result.Metrics.ProcessingTime.TotalMinutes,
                    OverallCoverageScore = result.SelfHelpGaps.OverallCoverageScore,
                    OverallRiskScore = result.FMEAAnalysis.OverallRiskScore
                };

                // Here you would use Kusto ingestion to save the data
                // await _kustoExecutor.IngestDataAsync("GraphRagAnalysis", analysisRecord);

                _logger.LogInformation("✅ Analysis result saved to Azure Data Explorer");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to save to Azure Data Explorer");
            }
        }

        #endregion

        #region Helper Methods

        private static Task FireAndForget(Func<Task> work, ILogger log) =>
            Task.Run(async () =>
            {
                try { await work(); }
                catch (Exception ex)
                {
                    log.LogError(ex, "🔥 Background GraphRAG job failed");
                    Console.WriteLine($"🔥 Background GraphRAG job failed: {ex}");
                }
            });

        private static async Task<HttpResponseData> CreateResponseAsync(
            HttpRequestData req,
            HttpStatusCode statusCode,
            string body)
        {
            var resp = req.CreateResponse(statusCode);
            resp.Headers.Add("Content-Type", "application/json");
            await resp.WriteStringAsync(body);
            return resp;
        }

        private async Task SendCallbackAsync(string callbackUri, GraphRagAnalysisResult result)
        {
            try
            {
                using var client = new HttpClient();
                var payload = new
                {
                    Status = "Success",
                    AnalysisId = result.AnalysisId,
                    ProductId = result.ProductId,
                    TotalCasesAnalyzed = result.TotalCasesAnalyzed,
                    TopGaps = result.SelfHelpGaps.IdentifiedGaps.Take(5).Select(g => g.SuggestedTitle),
                    TopRisks = result.FMEAAnalysis.HighRiskItems.Take(5).Select(r => r.FailureMode),
                    RecommendationsCount = result.Recommendations.Count
                };

                await client.PostAsync(callbackUri,
                    new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"));

                _logger.LogInformation("✅ Callback sent successfully to: {CallbackUri}", callbackUri);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Failed to send callback to: {CallbackUri}", callbackUri);
            }
        }

        #endregion

        #region Report Generation Methods

        private string GenerateExecutiveSummaryReport(GraphRagAnalysisResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Executive Summary - GraphRAG Analysis");
            sb.AppendLine($"**Product ID:** {result.ProductId}");
            sb.AppendLine($"**Analysis Date:** {result.AnalysisTimestamp:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"**Cases Analyzed:** {result.TotalCasesAnalyzed}");
            sb.AppendLine();

            sb.AppendLine("## Key Findings");
            sb.AppendLine($"- **Self-Help Gaps Identified:** {result.SelfHelpGaps.IdentifiedGaps.Count}");
            sb.AppendLine($"- **High-Risk Failure Modes:** {result.FMEAAnalysis.HighRiskItems.Count}");
            sb.AppendLine($"- **Actionable Recommendations:** {result.Recommendations.Count}");
            sb.AppendLine($"- **Overall Coverage Score:** {result.SelfHelpGaps.OverallCoverageScore:P1}");
            sb.AppendLine();

            sb.AppendLine("## Top 5 Self-Help Content Gaps");
            foreach (var gap in result.SelfHelpGaps.IdentifiedGaps.Take(5))
            {
                sb.AppendLine($"1. **{gap.SuggestedTitle}** - {gap.FrequencyInCases} cases, Impact: {gap.EstimatedImpact}/10");
            }

            sb.AppendLine();
            sb.AppendLine("## Top 5 High-Risk Failure Modes");
            foreach (var risk in result.FMEAAnalysis.HighRiskItems.Take(5))
            {
                sb.AppendLine($"1. **{risk.FailureMode}** - RPN: {risk.RPN}, Risk: {risk.RiskLevel}");
            }

            return sb.ToString();
        }

        private string GenerateSelfHelpGapReport(SelfHelpGapAnalysis gaps)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Self-Help Gap Analysis Report");
            sb.AppendLine();

            sb.AppendLine("## Identified Gaps");
            foreach (var gap in gaps.IdentifiedGaps.OrderByDescending(g => g.EstimatedImpact))
            {
                sb.AppendLine($"### {gap.SuggestedTitle}");
                sb.AppendLine($"- **Problem Area:** {gap.ProblemArea}");
                sb.AppendLine($"- **Category:** {gap.IssueCategory}");
                sb.AppendLine($"- **Frequency:** {gap.FrequencyInCases} cases");
                sb.AppendLine($"- **Customer Solvable:** {(gap.CustomerSolvable ? "Yes" : "No")}");
                sb.AppendLine($"- **Estimated Impact:** {gap.EstimatedImpact}/10");
                if (gap.RelatedCaseNumbers.Any())
                {
                    sb.AppendLine($"- **Related Cases:** {string.Join(", ", gap.RelatedCaseNumbers.Take(5))}");
                }
                if (gap.RelatedSAPPaths.Any())
                {
                    sb.AppendLine($"- **SAP Paths:** {string.Join(", ", gap.RelatedSAPPaths)}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string GenerateFMEAReport(FMEAAnalysisResult fmea)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# FMEA Analysis Report");
            sb.AppendLine();

            sb.AppendLine("## High-Risk Items");
            foreach (var item in fmea.HighRiskItems.OrderByDescending(i => i.RPN))
            {
                sb.AppendLine($"### {item.FailureMode}");
                sb.AppendLine($"- **Risk Priority Number:** {item.RPN}");
                sb.AppendLine($"- **Risk Level:** {item.RiskLevel}");
                sb.AppendLine($"- **Immediate Action:** {item.ImmediateAction}");
                sb.AppendLine();
            }

            sb.AppendLine("## Prevention Opportunities");
            foreach (var opportunity in fmea.PreventionOpportunities)
            {
                sb.AppendLine($"### {opportunity.FailureMode}");
                sb.AppendLine($"- **Prevention Type:** {opportunity.PreventionType}");
                sb.AppendLine($"- **Recommended Control:** {opportunity.RecommendedControl}");
                sb.AppendLine($"- **Customer Actionable:** {(opportunity.CustomerActionable ? "Yes" : "No")}");
                if (!string.IsNullOrEmpty(opportunity.SelfHelpContentNeeded))
                {
                    sb.AppendLine($"- **Self-Help Content Needed:** {opportunity.SelfHelpContentNeeded}");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string GenerateRecommendationsReport(List<ActionableRecommendation> recommendations)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Actionable Recommendations Report");
            sb.AppendLine();

            var groupedRecs = recommendations.GroupBy(r => r.Category);
            foreach (var group in groupedRecs)
            {
                sb.AppendLine($"## {group.Key}");
                foreach (var rec in group.OrderByDescending(r => r.EstimatedImpact))
                {
                    sb.AppendLine($"### {rec.Title}");
                    sb.AppendLine($"- **Priority:** {rec.Priority}");
                    sb.AppendLine($"- **Estimated Impact:** {rec.EstimatedImpact}/10");
                    sb.AppendLine($"- **Description:** {rec.Description}");
                    sb.AppendLine($"- **Expected Outcome:** {rec.ExpectedOutcome}");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        private string GenerateDetailedAnalysisReport(GraphRagAnalysisResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Detailed Analysis Report");
            sb.AppendLine();

            sb.AppendLine("## Processing Metrics");
            sb.AppendLine($"- **Total Processing Time:** {result.Metrics.ProcessingTime.TotalMinutes:F1} minutes");
            sb.AppendLine($"- **Entities Extracted:** {result.Metrics.TotalEntitiesExtracted}");
            sb.AppendLine($"- **Relationships Found:** {result.Metrics.TotalRelationshipsFound}");
            sb.AppendLine($"- **Communities Identified:** {result.Metrics.TotalCommunitiesIdentified}");
            sb.AppendLine();

            sb.AppendLine("## Entity Distribution");
            foreach (var entityType in result.Metrics.EntityTypeDistribution.OrderByDescending(e => e.Value))
            {
                sb.AppendLine($"- **{entityType.Key}:** {entityType.Value}");
            }

            return sb.ToString();
        }

        #endregion

        #region Analysis Helper Methods

        private async Task<List<EntityInsight>> AnalyzeEntitiesAsync(string entitiesPath)
        {
            // Implementation to read and analyze entities.csv
            return new List<EntityInsight>();
        }

        private async Task<List<RelationshipInsight>> AnalyzeRelationshipsAsync(string relationshipsPath)
        {
            // Implementation to read and analyze relationships.csv
            return new List<RelationshipInsight>();
        }

        private async Task<List<CommunityInsight>> AnalyzeCommunitiesAsync(string communityReportsPath)
        {
            // Implementation to read and analyze community_reports.csv
            return new List<CommunityInsight>();
        }

        private List<ActionableRecommendation> GenerateActionableRecommendations(GraphRagAnalysisResult result)
        {
            var recommendations = new List<ActionableRecommendation>();

            // Generate recommendations based on gaps and FMEA analysis
            foreach (var gap in result.SelfHelpGaps.IdentifiedGaps.Where(g => g.CustomerSolvable).Take(5))
            {
                recommendations.Add(new ActionableRecommendation
                {
                    Category = "Self-Help Content Creation",
                    Title = $"Create {gap.SuggestedTitle}",
                    Description = $"Address {gap.ProblemArea} issues that appear in {gap.FrequencyInCases} cases",
                    Priority = gap.EstimatedImpact > 7 ? "High" : gap.EstimatedImpact > 4 ? "Medium" : "Low",
                    EstimatedImpact = gap.EstimatedImpact,
                    ActionOwner = "Content Team",
                    ExpectedOutcome = $"Reduce support cases for {gap.ProblemArea} by enabling customer self-service"
                });
            }

            foreach (var riskItem in result.FMEAAnalysis.HighRiskItems.Where(r => r.RPN > 100).Take(3))
            {
                recommendations.Add(new ActionableRecommendation
                {
                    Category = "Risk Mitigation",
                    Title = $"Implement controls for {riskItem.FailureMode}",
                    Description = riskItem.ImmediateAction,
                    Priority = riskItem.RiskLevel,
                    EstimatedImpact = riskItem.RPN / 100,
                    ActionOwner = "Engineering Team",
                    ExpectedOutcome = $"Reduce occurrence and impact of {riskItem.FailureMode}"
                });
            }

            return recommendations;
        }

        #endregion

        #region Supporting Classes

        private class CaseDocument
        {
            public string CaseId { get; set; } = "";
            public string Content { get; set; } = "";
        }

        private class GraphRAGIndexResult
        {
            public string RootDir { get; set; } = "";
            public string OutputDir { get; set; } = "";
        }

        private static class PythonRunner
        {
            public static string Run(string exe, string arguments)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = new Process { StartInfo = psi };
                var sb = new StringBuilder();

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.WriteLine("[PY] " + e.Data);
                        sb.AppendLine(e.Data);
                    }
                };

                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.WriteLine("[PY] " + e.Data);
                        sb.AppendLine(e.Data);
                    }
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                    throw new Exception($"Python exited with {proc.ExitCode}");

                return sb.ToString();
            }
        }

        #endregion
        public class ProcessingDecision
        {
            public bool ShouldProcess { get; set; }
            public string Decision { get; set; }
            public string Reason { get; set; }
            public ProcessingStats Stats { get; set; } = new();
            public List<string> SkipReasons { get; set; } = new();
        }

        public class ProcessingStats
        {
            public int TotalCasesRequested { get; set; }
            public int NewCases { get; set; }
            public int UpdatedCases { get; set; }
            public int UnchangedCases { get; set; }
            public int TotalExistingCases { get; set; }
            public bool SelfHelpContentChanged { get; set; }
            public DateTime LastProcessingTime { get; set; }
            public TimeSpan TimeSinceLastProcess { get; set; }
        }

        private class ProcessingTracking
        {
            public DateTime LastProcessingTime { get; set; }
            public Dictionary<string, string> CaseHashes { get; set; } = new();
            public Dictionary<string, DateTime> CaseLastUpdated { get; set; } = new();
        }

        private class CaseChangeAnalysis
        {
            public List<CaseChange> NewCases { get; set; } = new();
            public List<CaseChange> UpdatedCases { get; set; } = new();
            public List<CaseChange> UnchangedCases { get; set; } = new();
        }

        private class CaseChange
        {
            public string CaseId { get; set; }
            public string ChangeType { get; set; }
            public CaseDocument Document { get; set; }
            public string OldHash { get; set; }
            public string NewHash { get; set; }
        }
    }
}
