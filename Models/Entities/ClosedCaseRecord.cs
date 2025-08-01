namespace SearchFrontend.Endpoints.GraphRAG.Models.Entities
{
    /// <summary>
    /// Represents a closed support case record from the database
    /// </summary>
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
}