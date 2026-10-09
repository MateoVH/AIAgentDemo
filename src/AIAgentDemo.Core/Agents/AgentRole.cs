namespace AIAgentDemo.Core.Agents;

/// <summary>The specialist agents that take part in a support run, in pipeline order.</summary>
public enum AgentRole
{
    /// <summary>Classifies intent, language, urgency and extracts entities (structured output).</summary>
    Classifier,

    /// <summary>Queries the SQL store through read-only tools and reports facts.</summary>
    DataAnalyst,

    /// <summary>Applies business policy and proposes actions; sensitive actions need human approval.</summary>
    Supervisor,

    /// <summary>Drafts the customer-facing reply in the customer's language.</summary>
    Writer,
}
