using System.Collections.Generic;

namespace OSMImporter.Traffic.Rules
{
    public class TrafficRulePipeline<TContext>
    {
        private readonly List<ITrafficRule<TContext>> _rules = new List<ITrafficRule<TContext>>();
        private readonly TrafficRuleCommandBuffer _commandBuffer = new TrafficRuleCommandBuffer();
        private bool _dirty = true;

        public void AddRule(ITrafficRule<TContext> rule)
        {
            _rules.Add(rule);
            _dirty = true;
        }

        public void RemoveRule(string ruleId)
        {
            _rules.RemoveAll(r => r.RuleId == ruleId);
            _dirty = true;
        }

        public TrafficRuleCommandBuffer Execute(TContext context)
        {
            if (_dirty)
            {
                _rules.Sort((a, b) =>
                {
                    int cmp = ((int)a.Stage).CompareTo((int)b.Stage);
                    if (cmp != 0) return cmp;
                    return b.Priority.CompareTo(a.Priority);
                });
                _dirty = false;
            }

            _commandBuffer.Clear();

            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Enabled)
                    _rules[i].Execute(context, _commandBuffer);
            }

            return _commandBuffer;
        }
    }
}
