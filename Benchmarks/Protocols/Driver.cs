using Microsoft.PSharp;

namespace Benchmarks.Protocols
{
    public class Driver
    {
        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_FailureDetector()
        {
            var runtime = PSharpRuntime.Create(CreateConfiguration());
            FailureDetector.Execute(runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_CoffeeMachine()
        {
            var runtime = PSharpRuntime.Create(CreateConfiguration());
            CoffeeMachine.Execute(runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_Chord()
        {
            var runtime = PSharpRuntime.Create(CreateConfiguration());
            Chord.Execute(runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_Raft()
        {
            var runtime = PSharpRuntime.Create(CreateConfiguration());
            Raft.Execute(runtime);
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static void Test_Paxos()
        {
            var runtime = PSharpRuntime.Create(CreateConfiguration());
            Paxos.Execute(runtime);
        }

        private static Configuration CreateConfiguration()
        {
            var config = Configuration.Create().WithVerbosityEnabled();
            config.EnableMonitorsInProduction = true;
            return config;
        }
    }
}
