// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.PSharp.IO;
using Microsoft.PSharp.Runtime;
using Microsoft.PSharp.TestingServices;
using Microsoft.PSharp.TestingServices.Runtime;
using Microsoft.PSharp.TestingServices.Tests;
using Microsoft.PSharp.TestingServices.Threading;
using Microsoft.PSharp.Tests.Common;
using Microsoft.PSharp.Threading;
using Microsoft.PSharp.Timers;
using Xunit.Abstractions;

using BaseBugFindingTest = Microsoft.PSharp.TestingServices.Tests.BaseTest;
using BaseCoreTest = Microsoft.PSharp.Core.Tests.BaseTest;

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

    public sealed class CoreTest : BaseCoreTest
    {
        public CoreTest(ITestOutputHelper output)
            : base(output)
        {
        }

        public async Task Run()
        {
            await Task.CompletedTask;
        }
    }

    public static class Assert
    {
        public static void True(bool predicate, string message = null)
        {
            Specification.Assert(predicate, message ?? string.Empty);
        }

        public static void Equal<T>(T expected, T actual)
            where T : IEquatable<T>
        {
            True(expected.Equals(actual), $"actual '{actual}' != expected '{expected}'");
        }
    }

    public static class Program
    {
        [Test]
        public static void Execute(IMachineRuntime r)
        {
            r.CreateMachine(typeof(NetworkEnvironment));
        }

        public static async Task Main()
        {
            Console.WriteLine("Start...");
            await Task.CompletedTask;
        }
    }
#pragma warning restore CA2200 // Rethrow to preserve stack details.
#pragma warning restore CA1822 // Mark members as static
#pragma warning restore CA1801 // Parameter not used
#pragma warning restore SA1005 // Single line comments must begin with single space
}
