using OutSmart.DAXon.Api;
using Xunit;

namespace OutSmart.DAXon.RuntimeTests;

public sealed class RuntimeBehaviorTests
{
    [Fact]
    public void PublicApiEvaluatesOnEveryTargetFramework()
    {
        Processor processor = new Processor();

        XdmValue result = processor.NewXPathCompiler().Evaluate("1 + 1", null);

        Assert.Equal("2", result.ToString());
    }

    [Fact]
    public void UcaCollationDoesNotSilentlyUseAnApproximation()
    {
        Processor processor = new Processor();

        DAXonApiException exception = Assert.Throws<DAXonApiException>(() =>
            processor.NewXPathCompiler().Evaluate(
                "starts-with('\u00E9clair', 'e', 'http://www.w3.org/2013/collation/UCA?strength=1')",
                null));

        Assert.Equal("err:FOCH0002", exception.GetErrorCode().ToString());
    }
}
