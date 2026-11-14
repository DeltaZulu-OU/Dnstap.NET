using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class AssemblyBoundaryTests
{
    [TestMethod]
    public void FrameStreamsImplementationIsNotPublicApi()
    {
        var exportedTypes = typeof(AssemblyMarker).Assembly.GetExportedTypes();
        Assert.IsFalse(exportedTypes.Any(type =>
            type.Namespace?.StartsWith("Dnstap.Protocol", StringComparison.Ordinal) == true));
    }
}
