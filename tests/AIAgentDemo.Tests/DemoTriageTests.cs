using AIAgentDemo.Core.Data;
using AIAgentDemo.Core.Orchestration;
using AIAgentDemo.Core.Providers.Demo;

namespace AIAgentDemo.Tests;

/// <summary>The simulated model must classify the inbox samples the way a real model would.</summary>
public class DemoTriageTests
{
    [Theory]
    [InlineData("damaged", SupportIntent.DamagedItem, "es", 1001)]
    [InlineData("late", SupportIntent.OrderStatus, "es", 1002)]
    [InlineData("cancel", SupportIntent.Cancellation, "en", 1004)]
    [InlineData("product", SupportIntent.ProductQuestion, "en", null)]
    [InlineData("missing", SupportIntent.MissingItem, "es", 1005)]
    [InlineData("policy", SupportIntent.RefundRequest, "en", 1003)]
    public void Classifies_sample_tickets(string sampleId, SupportIntent intent, string language, int? orderId)
    {
        var sample = SampleTickets.All.Single(t => t.Id == sampleId);

        var triage = DemoTriage.Classify(sample.Message, operatorLanguage: "en");

        Assert.Equal(intent, triage.Intent);
        Assert.Equal(language, triage.Language);
        Assert.Equal(orderId, triage.OrderId);
        Assert.True(triage.NeedsDataLookup);
    }

    [Fact]
    public void Writes_the_summary_in_the_operator_language()
    {
        var triage = DemoTriage.Classify("Hi, please cancel order 1004.", operatorLanguage: "es");

        Assert.Equal("en", triage.Language);
        Assert.StartsWith("Solicita cancelar", triage.Summary);
    }
}
