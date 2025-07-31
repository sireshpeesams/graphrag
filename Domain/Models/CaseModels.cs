using System;
using System.Collections.Generic;

namespace SearchFrontend.Domain.Models
{
    public sealed record ClosedCaseRecord
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

    public record CaseDocument
    {
        public string CaseId { get; init; } = "";
        public string Content { get; init; } = "";
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public string ContentHash { get; init; } = "";
    }

    public record ProcessingDecision
    {
        public bool ShouldProcess { get; init; }
        public string Decision { get; init; } = "";
        public string Reason { get; init; } = "";
        public ProcessingStats Stats { get; init; } = new();
        public List<string> SkipReasons { get; init; } = new();
    }

    public record ProcessingStats
    {
        public int TotalCasesRequested { get; init; }
        public int NewCases { get; init; }
        public int UpdatedCases { get; init; }
        public int UnchangedCases { get; init; }
        public int TotalExistingCases { get; init; }
        public bool SelfHelpContentChanged { get; init; }
        public DateTime LastProcessingTime { get; init; }
        public TimeSpan TimeSinceLastProcess { get; init; }
    }

    public record ProcessingTracking
    {
        public DateTime LastProcessingTime { get; init; }
        public Dictionary<string, string> CaseHashes { get; init; } = new();
        public Dictionary<string, DateTime> CaseLastUpdated { get; init; } = new();
    }

    public record CaseChangeAnalysis
    {
        public List<CaseChange> NewCases { get; init; } = new();
        public List<CaseChange> UpdatedCases { get; init; } = new();
        public List<CaseChange> UnchangedCases { get; init; } = new();
    }

    public record CaseChange
    {
        public string CaseId { get; init; } = "";
        public string ChangeType { get; init; } = "";
        public CaseDocument Document { get; init; } = new();
        public string OldHash { get; init; } = "";
        public string NewHash { get; init; } = "";
    }

    public record ProcessedContentInfo
    {
        public string FileName { get; init; } = "";
        public string ProcessedContent { get; init; } = "";
        public DateTime LastProcessed { get; init; }
        public int ContentLength { get; init; }
    }

    public record GraphRAGIndexResult
    {
        public string RootDir { get; init; } = "";
        public string OutputDir { get; init; } = "";
        public bool Success { get; init; } = true;
        public string ErrorMessage { get; init; } = "";
    }
}