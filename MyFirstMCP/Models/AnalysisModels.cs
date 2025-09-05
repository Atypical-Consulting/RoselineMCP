using System.Text.Json.Serialization;

namespace MyFirstMCP.Models;

public class ListDiagnosticsResponse
{
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;
    
    [JsonPropertyName("totalDiagnostics")]
    public int TotalDiagnostics { get; set; }
    
    [JsonPropertyName("diagnostics")]
    public List<DiagnosticDetail> Diagnostics { get; set; } = new();
    
    [JsonPropertyName("stats")]
    public DiagnosticStats Stats { get; set; } = new();
    
    [JsonPropertyName("suggestedFixableIds")]
    public List<string> SuggestedFixableIds { get; set; } = new();
}

public class DiagnosticStats
{
    [JsonPropertyName("byId")]
    public Dictionary<string, int> ById { get; set; } = new();
    
    [JsonPropertyName("bySeverity")]
    public Dictionary<string, int> BySeverity { get; set; } = new();
}

public class AnalyzeSolutionResponse
{
    [JsonPropertyName("solution")]
    public string Solution { get; set; } = string.Empty;
    
    [JsonPropertyName("projects")]
    public int Projects { get; set; }
    
    [JsonPropertyName("diagnosticSummary")]
    public DiagnosticSummary DiagnosticSummary { get; set; } = new();
    
    [JsonPropertyName("topDiagnostics")]
    public List<DiagnosticDetail> TopDiagnostics { get; set; } = new();
}

public class DiagnosticSummary
{
    [JsonPropertyName("error")]
    public int Error { get; set; }
    
    [JsonPropertyName("warning")]
    public int Warning { get; set; }
    
    [JsonPropertyName("info")]
    public int Info { get; set; }
    
    [JsonPropertyName("hidden")]
    public int Hidden { get; set; }
}

public class DiagnosticDetail
{
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;
    
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;
    
    [JsonPropertyName("line")]
    public int Line { get; set; }
    
    [JsonPropertyName("column")]
    public int Column { get; set; }
    
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;
    
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}