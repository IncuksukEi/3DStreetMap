using System;
using System.Collections.Generic;

namespace OSMImporter.Traffic.Rules
{
    public class TrafficRuleRegistry<TContext>
    {
        private static TrafficRuleRegistry<TContext> _instance;
        public static TrafficRuleRegistry<TContext> Instance =>
            _instance ?? (_instance = new TrafficRuleRegistry<TContext>());

        private readonly List<FactoryEntry> _factories = new List<FactoryEntry>();

        private struct FactoryEntry
        {
            public string RuleId;
            public Func<ITrafficRule<TContext>> Factory;
        }

        public void Register(Func<ITrafficRule<TContext>> factory)
        {
            var rule = factory();
            _factories.Add(new FactoryEntry { RuleId = rule.RuleId, Factory = factory });
        }

        public void Unregister(string ruleId)
        {
            _factories.RemoveAll(e => e.RuleId == ruleId);
        }

        public void PopulateDefaults(TrafficRulePipeline<TContext> pipeline)
        {
            for (int i = 0; i < _factories.Count; i++)
                pipeline.AddRule(_factories[i].Factory());
        }
    }
}
