namespace OSMImporter.Traffic.Rules
{
    public interface ITrafficRule<TContext>
    {
        string RuleId { get; }
        TrafficRuleStage Stage { get; }
        int Priority { get; }
        bool Enabled { get; set; }
        void Execute(TContext context, TrafficRuleCommandBuffer commands);
    }
}
