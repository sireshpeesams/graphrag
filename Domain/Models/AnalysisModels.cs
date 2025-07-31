using System;
using System.Collections.Generic;

namespace SearchFrontend.Domain.Models
{
    public class GraphRagAnalysisResult
    {
        public string AnalysisId { get; init; } = Guid.NewGuid().ToString();
        public string ProductId { get; init; } = "";
        public DateTime AnalysisTimestamp { get; init; } = DateTime.UtcNow;
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
        public string SkipReason { get; set; } = "";
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
        public string GapId { get; init; } = Guid.NewGuid().ToString();
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
        public string RecommendationId { get; init; } = Guid.NewGuid().ToString();
        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Priority { get; set; } = "";
        public int EstimatedImpact { get; set; }
        public string ActionOwner { get; set; } = "";
        public List<string> Prerequisites { get; set; } = new();
        public string ExpectedOutcome { get; set; } = "";
    }
}