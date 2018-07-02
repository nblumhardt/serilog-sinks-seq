using Xunit;

namespace Serilog.Sinks.Seq.Tests
{
    public class SeqApiTests
    {
        [Fact]
        public void NormalizedBaseUriIncludesPath()
        {
            var uri = SeqApi.NormalizeServerBaseAddress("https://seq.example.com/seq");
            Assert.Equal("seq/", uri.PathAndQuery);
        }
    }
}