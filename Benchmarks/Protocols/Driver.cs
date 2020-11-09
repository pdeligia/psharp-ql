using System;
using System.Diagnostics;
using System.Threading.Tasks;
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
        public static void Test_Raft(IMachineRuntime runtime)
        {
            Raft.Execute(runtime);
        }

        [Test]
        public static void Test_Paxos(IMachineRuntime runtime)
        {
            Paxos.Execute(runtime);
        }

        public static async Task Main()
        {
            var config = Configuration.Create();
            //config.IsVerbose = true;
            config.EnableMonitorsInProduction = true;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            int bugsFound = 0;
            for (int i = 0; i < 10241; i++)
            {
                // Console.WriteLine($"==================> Iteration {i}");

                var runtime = PSharpRuntime.Create(config);
                Test_Raft(runtime);

                // await Task.WhenAny(runtime.WaitAsync(), Task.Delay(300));
                await runtime.WaitAsync();

                lock (Raft.BugsFoundLock)
                {
                    if (Raft.BugsFound > bugsFound)
                    {
                        Console.WriteLine($"==================> Found bug #{Raft.BugsFound}");
                        bugsFound = Raft.BugsFound;
                    }
                }

                if (i == 10 || i == 20 || i == 40 || i == 80 ||
                    i == 160 || i == 320 || i == 640 || i == 1280 || i == 2560 ||
                    i == 5120 || i == 10240 || i == 20480 || i == 40960 ||
                    i == 81920 || i == 163840)
                {
                    Console.WriteLine($"==================> #{i} Custom States (size: {Raft.States.Count})");
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"... Found {bugsFound} bugs in {stopwatch.Elapsed.TotalMilliseconds}ms");
        }
    }
}
