using WarbandStudio.Rpfm;

namespace WarbandStudio.Tests;

public sealed class RpfmDiagnosticReportTests
{
    [Theory]
    [InlineData("DB", "InvalidReference", DiagnosticSeverity.Error)]
    [InlineData("Loc", "InvalidLocKey", DiagnosticSeverity.Error)]
    [InlineData("DB", "TableNameEndsInNumber", DiagnosticSeverity.Error)]
    [InlineData("DB", "NoReferenceTableNorColumnFoundNoPak", DiagnosticSeverity.Warning)]
    [InlineData("DB", "NoReferenceTableNorColumnFoundPak", DiagnosticSeverity.Info)]
    [InlineData("DB", "DuplicatedCombinedKeys", DiagnosticSeverity.Warning)]
    [InlineData("Config", "DependenciesCacheNotGenerated", DiagnosticSeverity.Error)]
    [InlineData("Pack", "InvalidPackName", DiagnosticSeverity.Error)]
    [InlineData("Pack", "FileITM", DiagnosticSeverity.Warning)]
    [InlineData("Pack", "FileOverwrite", DiagnosticSeverity.Info)]
    [InlineData("Text", "InvalidKey", DiagnosticSeverity.Error)]
    public void UsesBundledRpfmSeverityForEnumVariants(string category,string type,DiagnosticSeverity severity)
    {
        foreach(var variant in new[] { $"\"{type}\"", "{\""+type+"\":[\"bad_key\",\"column\"]}" })
        {
            var json="{\"results\":[{\""+category+"\":{\"path\":\"db/test/author\",\"results\":[{\"report_type\":"+variant+"}]}}]}";
            var report=RpfmDiagnosticReport.Parse(json);
            Assert.Equal(severity,Assert.Single(report.Items).Severity);
            if(severity==DiagnosticSeverity.Error) Assert.Throws<InvalidDataException>(report.RequireSaveAllowed);
            else report.RequireSaveAllowed();
        }
    }

    [Theory]
    [InlineData("{\"results\":[{}]}")]
    [InlineData("{\"results\":[{\"DB\":{}}]}")]
    [InlineData("{\"results\":[{\"DB\":{\"results\":[{}]}}]}")]
    [InlineData("{\"results\":[{\"DB\":{\"results\":[{\"report_type\":\"NewUnmappedDiagnostic\"}]}}]}")]
    public void IncompleteAndUnknownReportsFailClosed(string json) => Assert.Throws<RpfmException>(()=>RpfmDiagnosticReport.Parse(json));
}
