using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Dnstap.Tests;

[TestClass]
public sealed class LibraryContractTests
{
    [TestMethod]
    public void OfficialDnstapProtobufTypeIsExposed()
    {
        Type? model = typeof(AssemblyMarker).Assembly.GetType("Dnstap.Dnstap");
        Assert.IsNotNull(model, "The library must expose the official schema-generated dnstap message.");
        Assert.IsTrue(model.IsPublic);
        Assert.IsNotNull(model.GetProperty("Parser"), "Generated messages must provide deserialization.");
    }

    [TestMethod]
    public void MinimalPublicWriterIsExposed()
    {
        Type? writer = typeof(AssemblyMarker).Assembly.GetType("Dnstap.DnstapWriter");
        Assert.IsNotNull(writer, "A public Frame Streams writer must be usable by Tapper.");
        Assert.IsTrue(writer.IsPublic);
        Assert.IsNotNull(writer.GetMethod("StartAsync"));
        Assert.IsNotNull(writer.GetMethod("WriteAsync"));
        Assert.IsNotNull(writer.GetMethod("CompleteAsync"));
    }
}
