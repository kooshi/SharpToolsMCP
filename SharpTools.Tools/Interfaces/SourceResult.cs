namespace SharpTools.Tools.Interfaces;

public class SourceResult
{
    public string Source { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool IsOriginalSource { get; set; }
    public bool IsDecompiled { get; set; }
    public string ResolutionMethod { get; set; } = string.Empty;
}
