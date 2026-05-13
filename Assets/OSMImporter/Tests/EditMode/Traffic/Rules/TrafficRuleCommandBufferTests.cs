using NUnit.Framework;
using OSMImporter.Traffic.Rules;

namespace OSMImporter.Tests.Traffic.Rules
{
    public class TrafficRuleCommandBufferTests
    {
        [Test]
        public void SetTargetSpeed_KeepsLowestNonNegativeTarget()
        {
            var commands = new TrafficRuleCommandBuffer();

            commands.SetTargetSpeed(12f);
            commands.SetTargetSpeed(6f);
            commands.SetTargetSpeed(8f);
            commands.SetTargetSpeed(-1f);

            Assert.That(commands.ResolvedTargetSpeed, Is.EqualTo(6f));
        }

        [Test]
        public void RequestBrake_UsesStrongestBrake()
        {
            var commands = new TrafficRuleCommandBuffer();

            commands.RequestBrake(0.25f);
            commands.RequestBrake(0.9f);
            commands.RequestBrake(0.4f);

            Assert.That(commands.ResolvedBrakeForce, Is.EqualTo(0.9f));
        }

        [Test]
        public void SetMaxSpeed_UsesLowestSpeedLimit()
        {
            var commands = new TrafficRuleCommandBuffer();

            commands.SetMaxSpeed(20f);
            commands.SetMaxSpeed(12f);
            commands.SetMaxSpeed(16f);

            Assert.That(commands.ResolvedMaxSpeed, Is.EqualTo(12f));
        }
    }
}
