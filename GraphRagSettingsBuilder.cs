using System.Text;

using Microsoft.Extensions.Configuration;

namespace SearchFrontend.Endpoints.GraphRAG
{
    /// <summary>
    /// Builds the YAML that GraphRAG expects and substitutes the Azure
    /// OpenAI values from configuration / environment variables.
    /// Enhanced with FMEA and Self-Help analysis prompts.
    /// </summary>
    internal static class GraphRagSettingsBuilder
    {
        public static string Build(IConfiguration cfg)
        {
            // Read required values
            string apiBase = cfg["AzureOpenAIEndpoint"]
                ?? throw new InvalidOperationException("AzureOpenAIEndpoint missing");
            string chatDeployment = cfg["AzureOpenAIChatDeployment"]
                ?? throw new InvalidOperationException("AzureOpenAIChatDeployment missing");
            string embedDeployment = cfg["AzureOpenAIEmbedDeployment"]
                ?? throw new InvalidOperationException("AzureOpenAIEmbedDeployment missing");

            var sb = new StringBuilder();

            sb.AppendLine("### GraphRAG Configuration for FMEA-Enhanced Self-Help and Failure Prevention Analysis ###");
            sb.AppendLine();

            // Models configuration
            sb.AppendLine("models:");
            sb.AppendLine("  default_chat_model:");
            sb.AppendLine("    type: azure_openai_chat");
            sb.AppendLine($"    api_base: {apiBase}");
            sb.AppendLine("    api_version: \"2025-01-01-preview\"");
            sb.AppendLine("    auth_type: api_key");
            sb.AppendLine("    api_key: ${GRAPHRAG_API_KEY}");
            sb.AppendLine($"    deployment_name: {chatDeployment}");
            sb.AppendLine($"    model: {chatDeployment}");
            sb.AppendLine("    model_supports_json: true");
            sb.AppendLine();
            sb.AppendLine("  default_embedding_model:");
            sb.AppendLine("    type: azure_openai_embedding");
            sb.AppendLine($"    api_base: {apiBase}");
            sb.AppendLine("    api_version: \"2023-05-15\"");
            sb.AppendLine("    auth_type: api_key");
            sb.AppendLine("    api_key: ${GRAPHRAG_API_KEY}");
            sb.AppendLine($"    deployment_name: {embedDeployment}");
            sb.AppendLine($"    model: {embedDeployment}");
            sb.AppendLine("    model_supports_json: true");
            sb.AppendLine();

            // Input configuration
            sb.AppendLine("input:");
            sb.AppendLine("  storage:");
            sb.AppendLine("    type: file");
            sb.AppendLine("    base_dir: \"input\"");
            sb.AppendLine("  file_type: text");
            sb.AppendLine();

            // 🆕 FMEA-ENHANCED ENTITY EXTRACTION PROMPT
            sb.AppendLine("prompts:");
            sb.AppendLine("  entity_extraction:");
            sb.AppendLine("    template: |");
            sb.AppendLine("      -Goal-");
            sb.AppendLine("      Given a text document containing support cases and self-help documentation, identify all entities relevant to comprehensive FMEA (Failure Mode and Effects Analysis) and self-help gap analysis. Focus on understanding failure patterns, effects, causes, and prevention strategies.");
            sb.AppendLine("      ");
            sb.AppendLine("      -Context Engineering Guidelines-");
            sb.AppendLine("      CRITICAL: Process case problems and self-help content separately to avoid context confusion.");
            sb.AppendLine("      - CASE_DATA sections contain customer problems from support tickets");
            sb.AppendLine("      - SELFHELP_DATA sections contain existing documentation content");
            sb.AppendLine("      - Mark each entity with its SOURCE_TYPE to enable proper gap analysis");
            sb.AppendLine("      ");
            sb.AppendLine("      -Steps-");
            sb.AppendLine("      1. Identify all named entities that match the FMEA and self-help analysis specification below.");
            sb.AppendLine("      2. For each identified entity, extract the following information:");
            sb.AppendLine("      - entity_name: Name of the entity, capitalized and specific");
            sb.AppendLine("      - entity_type: One of the predefined types below");
            sb.AppendLine("      - entity_description: Comprehensive description including FMEA context, prevention relevance, and SOURCE_TYPE");
            sb.AppendLine("      Format each entity as (\"entity\"{tuple_delimiter}<entity_name>{tuple_delimiter}<entity_type>{tuple_delimiter}<entity_description>)");
            sb.AppendLine("      ");
            sb.AppendLine("      -Entity Types for FMEA and Self-Help Analysis-");
            sb.AppendLine("      ");
            sb.AppendLine("      CASE_PROBLEM: Customer problems identified from support cases (mark with CASE_SOURCE)");
            sb.AppendLine("      Examples: \"Pipeline Timeout in Support Cases\", \"Memory Issues from Customer Reports\", \"Authentication Failures in Cases\"");
            sb.AppendLine("      ");
            sb.AppendLine("      PROBLEM_STATEMENT: High-level customer problems or business impact descriptions");
            sb.AppendLine("      Examples: \"Data Pipeline Execution Failure\", \"Service Unavailability Impact\", \"Business Process Interruption\"");
            sb.AppendLine("      ");
            sb.AppendLine("      ISSUE: Specific technical issues, errors, or malfunctions");
            sb.AppendLine("      Examples: \"Pipeline Timeout After 30 Minutes\", \"Memory Overflow Exception\", \"Authentication Token Expired\"");
            sb.AppendLine("      ");
            sb.AppendLine("      ISSUE_CATEGORY: Broad classification of technical issues");
            sb.AppendLine("      Examples: \"Performance Issues\", \"Security Issues\", \"Configuration Issues\", \"Data Quality Issues\", \"Infrastructure Issues\"");
            sb.AppendLine("      ");
            sb.AppendLine("      ISSUE_SUBCATEGORY: Specific categorization within main issue categories");
            sb.AppendLine("      Examples: \"Memory Performance\", \"CPU Performance\", \"Authentication Security\", \"Network Connectivity\", \"Data Validation\"");
            sb.AppendLine("      ");
            sb.AppendLine("      SYMPTOM: Observable behaviors, error messages, or manifestations of issues");
            sb.AppendLine("      Examples: \"Pipeline Status Shows Queued\", \"Error 500 in Logs\", \"High Memory Usage Alert\", \"Slow Query Response Time\"");
            sb.AppendLine("      ");
            sb.AppendLine("      ROOT_CAUSE: Underlying technical or procedural causes of issues");
            sb.AppendLine("      Examples: \"Insufficient Memory Allocation\", \"Expired Service Principal Certificate\", \"Network Firewall Blocking Port\", \"Invalid Data Schema\"");
            sb.AppendLine("      ");
            sb.AppendLine("      FAILURE_MODE: Specific ways systems, processes, or components can fail");
            sb.AppendLine("      Examples: \"Memory Exhaustion Failure\", \"Authentication Timeout Failure\", \"Network Connection Drop\", \"Data Corruption During Transfer\", \"Service Degradation Under Load\"");
            sb.AppendLine("      ");
            sb.AppendLine("      FAILURE_EFFECT: Consequences or impacts resulting from failure modes");
            sb.AppendLine("      Examples: \"Complete Pipeline Stoppage\", \"Data Processing Delay\", \"Partial Data Loss\", \"User Access Denial\", \"Performance Degradation\"");
            sb.AppendLine("      ");
            sb.AppendLine("      FAILURE_CAUSE: Specific conditions or events that trigger failure modes");
            sb.AppendLine("      Examples: \"Memory Limit Exceeded\", \"Certificate Expiration\", \"Network Congestion\", \"Invalid Input Data Format\", \"Concurrent User Limit Reached\"");
            sb.AppendLine("      ");
            sb.AppendLine("      PREVENTION_CONTROL: Proactive measures to prevent failure modes from occurring");
            sb.AppendLine("      Examples: \"Memory Monitoring Alerts\", \"Certificate Auto-Renewal\", \"Input Data Validation\", \"Load Balancing Configuration\", \"Automated Health Checks\"");
            sb.AppendLine("      ");
            sb.AppendLine("      DETECTION_CONTROL: Methods to detect failure modes when they occur");
            sb.AppendLine("      Examples: \"Memory Usage Alerts\", \"Authentication Failure Logs\", \"Network Connectivity Monitoring\", \"Data Quality Checks\", \"Performance Threshold Alerts\"");
            sb.AppendLine("      ");
            sb.AppendLine("      SEVERITY_RATING: Assessment of failure impact severity (Critical, High, Medium, Low)");
            sb.AppendLine("      Examples: \"Critical Service Outage\", \"High Performance Impact\", \"Medium User Inconvenience\", \"Low Data Delay\"");
            sb.AppendLine("      ");
            sb.AppendLine("      OCCURRENCE_FREQUENCY: How often failure modes occur");
            sb.AppendLine("      Examples: \"Daily Occurrence\", \"Weekly Pattern\", \"Monthly Incident\", \"Rare Event\", \"Seasonal Issue\"");
            sb.AppendLine("      ");
            sb.AppendLine("      DETECTABILITY_RATING: How easily failure modes can be detected");
            sb.AppendLine("      Examples: \"Immediately Detectable\", \"Detectable with Monitoring\", \"Requires Investigation\", \"Silent Failure\"");
            sb.AppendLine("      ");
            sb.AppendLine("      RESOLUTION_STEPS: Specific step-by-step procedures to resolve issues");
            sb.AppendLine("      Examples: \"Increase Memory Configuration to 8GB\", \"Restart ADF Integration Runtime\", \"Update Service Principal Certificate\"");
            sb.AppendLine("      ");
            sb.AppendLine("      OUTAGE: Service disruptions, downtime events, or availability issues");
            sb.AppendLine("      Examples: \"Regional ADF Service Outage\", \"Database Connectivity Outage\", \"Authentication Service Downtime\"");
            sb.AppendLine("      ");
            sb.AppendLine("      KNOWN_ISSUE: Documented, recognized issues with established patterns");
            sb.AppendLine("      Examples: \"Known Memory Leak in ADF v2.1\", \"Documented Timeout Issue with Large Datasets\", \"Recognized Performance Bug\"");
            sb.AppendLine("      ");
            sb.AppendLine("      SELFHELP_TOPIC: Topics, guides, or sections covered in existing self-help documentation (mark with SELFHELP_SOURCE)");
            sb.AppendLine("      Examples: \"Pipeline Configuration Guide\", \"Troubleshooting Authentication\", \"Performance Optimization Tutorial\", \"Memory Settings Documentation\"");
            sb.AppendLine("      ");
            sb.AppendLine("      SELFHELP_STEP: Individual steps or instructions within self-help documentation (mark with SELFHELP_SOURCE)");
            sb.AppendLine("      Examples: \"Navigate to Integration Runtime Settings\", \"Click Configure Memory Options\", \"Enter 8GB in Memory Field\", \"Save and Restart Pipeline\"");
            sb.AppendLine("      ");
            sb.AppendLine("      PRODUCT: Software products, services, or components");
            sb.AppendLine("      Examples: \"Azure Data Factory\", \"SQL Database\", \"Integration Runtime\", \"Power BI\"");
            sb.AppendLine("      ");
            sb.AppendLine("      CUSTOMER_SOLVABLE: Issues or problems that customers can resolve independently with proper guidance");
            sb.AppendLine("      Examples: \"Configuration Parameter Update\", \"Simple Service Restart\", \"Basic Performance Tuning\"");
            sb.AppendLine("      ");
            sb.AppendLine("      FREQUENCY_INDICATOR: Problems that appear multiple times across cases (mark with case count when possible)");
            sb.AppendLine("      Examples: \"Recurring Pipeline Timeout\", \"Frequent Memory Issues\", \"Common Authentication Problems\"");
            sb.AppendLine("      ");
            sb.AppendLine("      -Special Instructions for FMEA Analysis-");
            sb.AppendLine("      ");
            sb.AppendLine("      1. **Failure Mode Focus**: For each ISSUE, identify the specific FAILURE_MODE that describes how the system fails");
            sb.AppendLine("      2. **Cause-Effect Chain**: Map FAILURE_CAUSE → FAILURE_MODE → FAILURE_EFFECT relationships");
            sb.AppendLine("      3. **Risk Assessment**: Extract or infer SEVERITY_RATING, OCCURRENCE_FREQUENCY, and DETECTABILITY_RATING");
            sb.AppendLine("      4. **Control Identification**: Look for existing PREVENTION_CONTROL and DETECTION_CONTROL measures");
            sb.AppendLine("      5. **Prevention Opportunities**: Identify where new controls could prevent recurring issues");
            sb.AppendLine("      6. **Self-Help Integration**: Map which failure modes could be prevented through better customer education");
            sb.AppendLine("      ");
            sb.AppendLine("      -Context Engineering Validation Rules-");
            sb.AppendLine("      1. Only extract entities from actual content, not from metadata or headers");
            sb.AppendLine("      2. Mark entity source as either CASE_SOURCE or SELFHELP_SOURCE in the description");
            sb.AppendLine("      3. For frequency indicators, include case count when identifiable");
            sb.AppendLine("      4. Separate technical problems from their solutions clearly");
            sb.AppendLine("      ");
            sb.AppendLine("      3. Return output in English as a single list of all the entities identified. Use **{record_delimiter}** as the list delimiter.");
            sb.AppendLine("      4. When finished, output {completion_delimiter}");
            sb.AppendLine("      ");
            sb.AppendLine("      ######################");
            sb.AppendLine("      -Real Data-");
            sb.AppendLine("      ######################");
            sb.AppendLine("      Text: {input_text}");
            sb.AppendLine("      ######################");
            sb.AppendLine("      Output:");
            sb.AppendLine();

            // 🆕 FMEA-ENHANCED RELATIONSHIP EXTRACTION PROMPT
            sb.AppendLine("  relationship_extraction:");
            sb.AppendLine("    template: |");
            sb.AppendLine("      -Goal-");
            sb.AppendLine("      Given entities extracted from support cases and self-help documentation, identify all relationships that support FMEA analysis, failure prevention, and comprehensive self-help gap analysis.");
            sb.AppendLine("      ");
            sb.AppendLine("      -Context Engineering Guidelines-");
            sb.AppendLine("      CRITICAL: Focus on relationships that reveal self-help content gaps.");
            sb.AppendLine("      - Prioritize relationships between CASE_SOURCE problems and SELFHELP_SOURCE coverage");
            sb.AppendLine("      - Identify high-frequency problems that lack self-help coverage");
            sb.AppendLine("      - Map customer-solvable problems to existing or missing self-help content");
            sb.AppendLine("      ");
            sb.AppendLine("      -Steps-");
            sb.AppendLine("      1. From the entities identified, identify all pairs of (source_entity, target_entity) that are clearly related in an FMEA or self-help context.");
            sb.AppendLine("      2. For each pair of related entities, extract the following information:");
            sb.AppendLine("      - source_entity: name of the source entity");
            sb.AppendLine("      - target_entity: name of the target entity");
            sb.AppendLine("      - relationship_description: explanation of relationship in FMEA/self-help context");
            sb.AppendLine("      - relationship_strength: numeric score 1-10 indicating relationship strength");
            sb.AppendLine("      Format each relationship as (\"relationship\"{tuple_delimiter}<source_entity>{tuple_delimiter}<target_entity>{tuple_delimiter}<relationship_description>{tuple_delimiter}<relationship_strength>)");
            sb.AppendLine("      ");
            sb.AppendLine("      -Critical FMEA Relationship Types-");
            sb.AppendLine("      ");
            sb.AppendLine("      **SELF-HELP GAP ANALYSIS RELATIONSHIPS (HIGHEST PRIORITY):**");
            sb.AppendLine("      COVERED_BY: Case problem has corresponding self-help content (strength: 8-10)");
            sb.AppendLine("      NOT_COVERED_BY: Case problem lacks self-help coverage - CRITICAL GAP! (strength: 9-10)");
            sb.AppendLine("      PARTIALLY_COVERED_BY: Case problem has incomplete self-help coverage (strength: 6-8)");
            sb.AppendLine("      FREQUENTLY_OCCURS_WITHOUT_HELP: High-frequency case problem with no self-help (strength: 10)");
            sb.AppendLine("      CUSTOMER_SOLVABLE_BUT_NO_GUIDE: Customer-solvable problem lacking self-help guide (strength: 9-10)");
            sb.AppendLine("      ");
            sb.AppendLine("      **FAILURE CHAIN RELATIONSHIPS:**");
            sb.AppendLine("      TRIGGERS: What triggers what (FAILURE_CAUSE → FAILURE_MODE)");
            sb.AppendLine("      RESULTS_IN: What results in what (FAILURE_MODE → FAILURE_EFFECT)");
            sb.AppendLine("      MANIFESTS_AS: How failures appear (FAILURE_MODE → SYMPTOM)");
            sb.AppendLine("      ESCALATES_TO: How issues escalate (ISSUE → PROBLEM_STATEMENT)");
            sb.AppendLine("      ");
            sb.AppendLine("      **PREVENTION RELATIONSHIPS:**");
            sb.AppendLine("      PREVENTS: What prevents what (PREVENTION_CONTROL → FAILURE_MODE)");
            sb.AppendLine("      DETECTS: What detects what (DETECTION_CONTROL → FAILURE_MODE)");
            sb.AppendLine("      MITIGATES: What reduces impact (PREVENTION_CONTROL → FAILURE_EFFECT)");
            sb.AppendLine("      RESOLVES: What fixes what (RESOLUTION_STEPS → FAILURE_MODE)");
            sb.AppendLine("      ");
            sb.AppendLine("      **SELF-HELP INTEGRATION RELATIONSHIPS:**");
            sb.AppendLine("      COVERED_BY: Issue is addressed by existing self-help content");
            sb.AppendLine("      NOT_COVERED_BY: Issue lacks self-help coverage (CRITICAL GAP!)");
            sb.AppendLine("      PARTIALLY_COVERED_BY: Issue has some but incomplete self-help coverage");
            sb.AppendLine("      PREVENTABLE_BY_CUSTOMER: Customer can prevent (FAILURE_MODE → CUSTOMER_SOLVABLE)");
            sb.AppendLine("      PREVENTION_STEPS_MISSING: Missing prevention guidance (PREVENTION_CONTROL → SELFHELP_STEP with negative)");
            sb.AppendLine("      ");
            sb.AppendLine("      **RISK ASSESSMENT RELATIONSHIPS:**");
            sb.AppendLine("      HAS_SEVERITY: Severity assessment (FAILURE_MODE → SEVERITY_RATING)");
            sb.AppendLine("      HAS_FREQUENCY: Occurrence pattern (FAILURE_MODE → OCCURRENCE_FREQUENCY)");
            sb.AppendLine("      HAS_DETECTABILITY: Detection capability (FAILURE_MODE → DETECTABILITY_RATING)");
            sb.AppendLine("      ");
            sb.AppendLine("      -Context Engineering Validation-");
            sb.AppendLine("      1. Verify relationships span CASE_SOURCE to SELFHELP_SOURCE entities for gap analysis");
            sb.AppendLine("      2. Prioritize relationships that identify missing self-help opportunities");
            sb.AppendLine("      3. Validate frequency claims by checking for multiple case references");
            sb.AppendLine("      ");
            sb.AppendLine("      3. Return output in English as a single list of all the relationships identified. Use **{record_delimiter}** as the list delimiter.");
            sb.AppendLine("      4. When finished, output {completion_delimiter}");
            sb.AppendLine("      ");
            sb.AppendLine("      ######################");
            sb.AppendLine("      -Real Data-");
            sb.AppendLine("      ######################");
            sb.AppendLine("      Entities: {entity_specs}");
            sb.AppendLine("      Text: {input_text}");
            sb.AppendLine("      ######################");
            sb.AppendLine("      Output:");
            sb.AppendLine();

            // 🆕 FMEA-ENHANCED COMMUNITY REPORT PROMPT
            sb.AppendLine("  community_report:");
            sb.AppendLine("    template: |");
            sb.AppendLine("      You are an AI assistant specializing in FMEA (Failure Mode and Effects Analysis) and comprehensive self-help gap analysis for technical support operations.");
            sb.AppendLine("      ");
            sb.AppendLine("      # Context Engineering Framework");
            sb.AppendLine("      ANALYSIS_CONTEXT: You are analyzing a community of support entities to identify self-help content gaps and failure prevention opportunities.");
            sb.AppendLine("      DATA_SOURCES: Support case problems (CASE_SOURCE) and existing self-help documentation (SELFHELP_SOURCE)");
            sb.AppendLine("      PRIMARY_GOAL: Identify high-impact self-help content creation opportunities based on case frequency and coverage gaps");
            sb.AppendLine("      ");
            sb.AppendLine("      # Goal");
            sb.AppendLine("      Write a comprehensive FMEA-based report analyzing a community of support entities to identify failure patterns, prevention opportunities, and self-help content gaps. The report will guide proactive issue prevention and effective self-help documentation creation.");
            sb.AppendLine("      ");
            sb.AppendLine("      # Report Structure");
            sb.AppendLine("      The report should include the following sections:");
            sb.AppendLine("      - TITLE: community's name representing the key failure area or system focus");
            sb.AppendLine("      - SUMMARY: Executive summary covering failure patterns, risk assessment, current controls, gaps, and prevention recommendations");
            sb.AppendLine("      - FAILURE_PREVENTION_PRIORITY_RATING: float score 0-10 representing urgency for implementing failure prevention measures");
            sb.AppendLine("      - RATING_EXPLANATION: Single sentence explaining the priority rating based on RPN factors (Severity × Occurrence × Detectability)");
            sb.AppendLine("      - DETAILED_FINDINGS: 7-12 key insights focusing on FMEA analysis and prevention strategies");
            sb.AppendLine("      ");
            sb.AppendLine("      Return output as a well-formed JSON-formatted string with the following format:");
            sb.AppendLine("      {{");
            sb.AppendLine("          \"title\": \"<report_title>\",");
            sb.AppendLine("          \"summary\": \"<executive_summary>\",");
            sb.AppendLine("          \"rating\": <failure_prevention_priority_rating>,");
            sb.AppendLine("          \"rating_explanation\": \"<rating_explanation>\",");
            sb.AppendLine("          \"findings\": [");
            sb.AppendLine("              {{");
            sb.AppendLine("                  \"summary\":\"<insight_1_summary>\",");
            sb.AppendLine("                  \"explanation\": \"<insight_1_explanation>\"");
            sb.AppendLine("              }}");
            sb.AppendLine("          ]");
            sb.AppendLine("      }}");
            sb.AppendLine("      ");
            sb.AppendLine("      # Context Engineering Analysis Framework");
            sb.AppendLine("      Structure your analysis around these critical components for self-help gap identification:");
            sb.AppendLine("      ");
            sb.AppendLine("      **1. CASE_SOURCE PROBLEM IDENTIFICATION:**");
            sb.AppendLine("      - What specific problems appear in support cases (CASE_SOURCE entities)?");
            sb.AppendLine("      - Which problems occur most frequently across multiple cases?");
            sb.AppendLine("      - What are the common patterns in customer-reported issues?");
            sb.AppendLine("      ");
            sb.AppendLine("      **2. SELFHELP_SOURCE COVERAGE ASSESSMENT:**");
            sb.AppendLine("      - What topics are covered in existing self-help documentation (SELFHELP_SOURCE entities)?");
            sb.AppendLine("      - How comprehensive is the current self-help coverage?");
            sb.AppendLine("      - Which self-help topics are well-documented vs. minimally covered?");
            sb.AppendLine("      ");
            sb.AppendLine("      **3. CRITICAL GAP IDENTIFICATION:**");
            sb.AppendLine("      - Which case problems have 'NOT_COVERED_BY' relationships with self-help content?");
            sb.AppendLine("      - What high-frequency case problems lack corresponding self-help documentation?");
            sb.AppendLine("      - Which customer-solvable problems have no self-service guidance available?");
            sb.AppendLine("      ");
            sb.AppendLine("      **4. CONTENT CREATION PRIORITIZATION:**");
            sb.AppendLine("      - Which self-help content gaps would have the highest impact on case reduction?");
            sb.AppendLine("      - What content should be created first based on frequency and customer impact?");
            sb.AppendLine("      - Which gaps represent the best ROI for self-help content creation?");
            sb.AppendLine("      ");
            sb.AppendLine("      **5. VALIDATION AND ACCURACY:**");
            sb.AppendLine("      - Base frequency claims only on entities with CASE_SOURCE markers");
            sb.AppendLine("      - Separate factual observations from recommendations");
            sb.AppendLine("      - Flag uncertain assessments for manual review when data is insufficient");
            sb.AppendLine("      ");
            sb.AppendLine("      Text: {input_text}");
            sb.AppendLine("      Output:");
            sb.AppendLine();

            // Rest of configuration
            sb.AppendLine("chunks:");
            sb.AppendLine("  size: 1200");
            sb.AppendLine("  overlap: 100");
            sb.AppendLine("  group_by_columns: [id]");
            sb.AppendLine();
            sb.AppendLine("output:");
            sb.AppendLine("  type: file");
            sb.AppendLine("  base_dir: \"output\"");
            sb.AppendLine();
            sb.AppendLine("cache:");
            sb.AppendLine("  type: file");
            sb.AppendLine("  base_dir: \"cache\"");
            sb.AppendLine();
            sb.AppendLine("reporting:");
            sb.AppendLine("  type: file");
            sb.AppendLine("  base_dir: \"logs\"");
            sb.AppendLine();
            sb.AppendLine("vector_store:");
            sb.AppendLine("  default_vector_store:");
            sb.AppendLine("    type: lancedb");
            sb.AppendLine("    db_uri: output\\lancedb");
            sb.AppendLine("    container_name: default");
            sb.AppendLine("    overwrite: true");

            return sb.ToString();
        }
    }
}
