using Whisper.net.Ggml;
using WetFlow;
using Xunit;

namespace WetFlow.Tests;

public class TranscriberParseModelTests
{
    [Theory]
    [InlineData("tiny",       GgmlType.Tiny,    QuantizationType.NoQuantization)]
    [InlineData("base",       GgmlType.Base,    QuantizationType.NoQuantization)]
    [InlineData("base.en",    GgmlType.BaseEn,  QuantizationType.NoQuantization)]
    [InlineData("base-q5_1",  GgmlType.Base,    QuantizationType.Q5_1)]
    [InlineData("small",      GgmlType.Small,   QuantizationType.NoQuantization)]
    [InlineData("small.en",   GgmlType.SmallEn, QuantizationType.NoQuantization)]
    [InlineData("small-q5_1", GgmlType.Small,   QuantizationType.Q5_1)]
    [InlineData("medium",     GgmlType.Medium,  QuantizationType.NoQuantization)]
    public void ParseModel_KnownName_ReturnsCorrectPair(
        string name, GgmlType expectedType, QuantizationType expectedQuant)
    {
        var (type, quant) = Transcriber.ParseModel(name);
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedQuant, quant);
    }

    [Fact]
    public void ParseModel_UnknownName_FallsBackToBase()
    {
        var (type, quant) = Transcriber.ParseModel("unknown-model");
        Assert.Equal(GgmlType.Base, type);
        Assert.Equal(QuantizationType.NoQuantization, quant);
    }
}
