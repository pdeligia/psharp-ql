using Microsoft.PSharp;

namespace Benchmarks.Protocols
{
    public class Driver
    {
        private static IMachineRuntime Runtime;

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_FailureDetector()
        {
            Runtime = PSharpRuntime.Create(CreateConfiguration());
            FailureDetector.Execute(Runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_CoffeeMachine()
        {
            Runtime = PSharpRuntime.Create(CreateConfiguration());
            CoffeeMachine.Execute(Runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_Chord()
        {
            Runtime = PSharpRuntime.Create(CreateConfiguration());
            Chord.Execute(Runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_Raft()
        {
            Runtime = PSharpRuntime.Create(CreateConfiguration());
            Raft.Execute(Runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_Paxos()
        {
            Runtime = PSharpRuntime.Create(CreateConfiguration());
            Paxos.Execute(Runtime);
        }

        [Microsoft.Coyote.SystematicTesting.TestStateHash]
        public static int GetStateHash() => Runtime.GetHashedExecutionState();

        private static Configuration CreateConfiguration()
        {
            var config = Configuration.Create();
            config.EnableMonitorsInProduction = true;
            return config;
        }
    }
}
