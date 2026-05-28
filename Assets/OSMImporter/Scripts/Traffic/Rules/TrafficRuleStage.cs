namespace OSMImporter.Traffic.Rules
{
    public enum TrafficRuleStage
    {
        Sense = 0,
        Plan = 10,
        Constrain = 20,
        Recover = 30,
        Act = 40,
        Observe = 50
    }
}
