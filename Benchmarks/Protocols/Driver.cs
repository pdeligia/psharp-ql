using Microsoft.PSharp;

namespace Benchmarks.Protocols
{
    public class Driver
    {
        [Test]
        public static void Test_FailureDetector(IMachineRuntime runtime)
        {
            FailureDetector.Execute(runtime);
        }

        [Test]
        public static void Test_CoffeeMachine(IMachineRuntime runtime)
        {
            CoffeeMachine.Execute(runtime);
        }

        [Test]
        public static void Test_Chord(IMachineRuntime runtime)
        {
            Chord.Execute(runtime);
        }

        [Test]
        public static void Test_Raftv1(IMachineRuntime runtime)
        {
            Raft.Execute(runtime, false);
        }

        [Test]
        public static void Test_Raftv2(IMachineRuntime runtime)
        {
            Raft.Execute(runtime, true);
        }

        [Test]
        public static void Test_Paxos(IMachineRuntime runtime)
        {
            Paxos.Execute(runtime);
        }
    }
}
