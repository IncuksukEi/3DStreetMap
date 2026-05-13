using System.Collections.Generic;
using NUnit.Framework;
using OSMImporter.Traffic.Rules;

namespace OSMImporter.Tests.Traffic.Rules
{
    public class TrafficRulePipelineTests
    {
        [Test]
        public void Execute_RunsEnabledRulesByStageThenDescendingPriority()
        {
            var log = new List<string>();
            var pipeline = new TrafficRulePipeline<List<string>>();

            pipeline.AddRule(new LogRule("act", TrafficRuleStage.Act, 0));
            pipeline.AddRule(new LogRule("constrain-low", TrafficRuleStage.Constrain, 1));
            pipeline.AddRule(new LogRule("sense", TrafficRuleStage.Sense, 0));
            pipeline.AddRule(new LogRule("constrain-high", TrafficRuleStage.Constrain, 10));

            pipeline.Execute(log);

            CollectionAssert.AreEqual(
                new[] { "sense", "constrain-high", "constrain-low", "act" },
                log);
        }

        [Test]
        public void Execute_SkipsDisabledRules()
        {
            var log = new List<string>();
            var pipeline = new TrafficRulePipeline<List<string>>();

            pipeline.AddRule(new LogRule("enabled", TrafficRuleStage.Constrain, 0));
            pipeline.AddRule(new LogRule("disabled", TrafficRuleStage.Constrain, 10)
            {
                Enabled = false
            });

            pipeline.Execute(log);

            CollectionAssert.AreEqual(new[] { "enabled" }, log);
        }

        private sealed class LogRule : ITrafficRule<List<string>>
        {
            public LogRule(string ruleId, TrafficRuleStage stage, int priority)
            {
                RuleId = ruleId;
                Stage = stage;
                Priority = priority;
            }

            public string RuleId { get; }
            public TrafficRuleStage Stage { get; }
            public int Priority { get; }
            public bool Enabled { get; set; } = true;

            public void Execute(List<string> context, TrafficRuleCommandBuffer commands)
            {
                context.Add(RuleId);
            }
        }
    }
}
