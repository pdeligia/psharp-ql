// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Microsoft.PSharp.TestingServices;

namespace Microsoft.PSharp.Tests.Launcher
{
#pragma warning disable SA1005 // Single line comments must begin with single space
#pragma warning disable CA1801 // Parameter not used
#pragma warning disable CA1822 // Mark members as static
#pragma warning disable CA2200 // Rethrow to preserve stack details.
    internal class Client : Machine
    {
        internal class Config : Event
        {
            public MachineId Server;

            public Config(MachineId server)
            {
                this.Server = server;
            }
        }

        internal class Unit : Event
        {
        }

        internal class Ping : Event
        {
            public MachineId Client;

            public Ping(MachineId client)
            {
                this.Client = client;
            }
        }

        private MachineId Server;
        private int Counter;

        [Start]
        [OnEntry(nameof(InitOnEntry))]
        private class Init : MachineState
        {
        }

        private void InitOnEntry()
        {
            this.Server = (this.ReceivedEvent as Config).Server;
            this.Counter = 0;
            this.Goto<Active>();
        }

        [OnEntry(nameof(ActiveOnEntry))]
        [OnEventDoAction(typeof(Server.Pong), nameof(SendPing))]

        private class Active : MachineState
        {
        }

        private void ActiveOnEntry()
        {
            this.SendPing();
        }

        private void SendPing()
        {
            this.Counter++;

            this.Send(this.Server, new Ping(this.Id));

            this.Logger.WriteLine("Client request: {0} / 5", this.Counter);

            if (this.Counter == 5)
            {
                this.Raise(new Halt());
            }
        }
    }

    internal class Server : Machine
    {
        internal class Pong : Event
        {
        }

        [Start]
        [OnEventDoAction(typeof(Client.Ping), nameof(SendPong))]
        private class Active : MachineState
        {
        }

        private void SendPong()
        {
            var client = (this.ReceivedEvent as Client.Ping).Client;
            this.Send(client, new Pong());
        }
    }

    internal class NetworkEnvironment : Machine
    {
        [Start]
        [OnEntry(nameof(InitOnEntry))]

        private class Init : MachineState
        {
        }

        private void InitOnEntry()
        {
            var server = this.CreateMachine(typeof(Server));
            this.CreateMachine(typeof(Client), new Client.Config(server));
        }
    }

    public static class Program
    {
        [System.Runtime.InteropServices.DllImport("coyote.dll")]
#pragma warning disable SA1300 // Element must begin with upper-case letter
#pragma warning disable SA1400 // Access modifier must be declared
        static extern void foo();
#pragma warning restore SA1400 // Access modifier must be declared
#pragma warning restore SA1300 // Element must begin with upper-case letter

        [Test]
        public static void Execute(IMachineRuntime r)
        {
            r.CreateMachine(typeof(NetworkEnvironment));
        }

        public static async Task Main()
        {
            Console.WriteLine("Start...");
            var config = Configuration.Create().WithVerbosityEnabled().WithNumberOfIterations(2);
            var test = TestingEngineFactory.CreateBugFindingEngine(config, Execute);
            test.Run();
            Console.WriteLine("End...");
            await Task.CompletedTask;
        }
    }
#pragma warning restore CA2200 // Rethrow to preserve stack details.
#pragma warning restore CA1822 // Mark members as static
#pragma warning restore CA1801 // Parameter not used
#pragma warning restore SA1005 // Single line comments must begin with single space
}
