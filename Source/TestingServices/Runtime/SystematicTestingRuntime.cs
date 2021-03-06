﻿// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.PSharp.Runtime;
using Microsoft.PSharp.TestingServices.Coverage;
using Microsoft.PSharp.TestingServices.Scheduling;
using Microsoft.PSharp.TestingServices.Scheduling.Strategies;
using Microsoft.PSharp.TestingServices.StateCaching;
using Microsoft.PSharp.TestingServices.Threading;
using Microsoft.PSharp.TestingServices.Timers;
using Microsoft.PSharp.TestingServices.Tracing.Error;
using Microsoft.PSharp.TestingServices.Tracing.Schedule;
using Microsoft.PSharp.Threading;
using Microsoft.PSharp.Timers;
using Microsoft.PSharp.Utilities;

namespace Microsoft.PSharp.TestingServices.Runtime
{
    /// <summary>
    /// Runtime for systematically testing machines by controlling the scheduler.
    /// </summary>
    internal sealed class SystematicTestingRuntime : MachineRuntime
    {
        /// <summary>
        /// Stores the runtime that executes each operation in a given asynchronous context.
        /// </summary>
        private static readonly AsyncLocal<SystematicTestingRuntime> AsyncLocalRuntime =
            new AsyncLocal<SystematicTestingRuntime>();

        /// <summary>
        /// The asynchronous operation scheduler.
        /// </summary>
        internal OperationScheduler Scheduler;

        /// <summary>
        /// The controlled task scheduler.
        /// </summary>
        private readonly ControlledTaskScheduler TaskScheduler;

        /// <summary>
        /// The bug trace.
        /// </summary>
        internal BugTrace BugTrace;

        /// <summary>
        /// Data structure containing information
        /// regarding testing coverage.
        /// </summary>
        internal CoverageInfo CoverageInfo;

        /// <summary>
        /// Interface for registering runtime operations.
        /// </summary>
        internal IRegisterRuntimeOperation Reporter;

        /// <summary>
        /// The P# program state cache.
        /// </summary>
        internal StateCache StateCache;

        /// <summary>
        /// List of monitors in the program.
        /// </summary>
        private readonly List<Monitor> Monitors;

        /// <summary>
        /// Map from unique ids to operations.
        /// </summary>
        private readonly ConcurrentDictionary<ulong, MachineOperation> MachineOperations;

        /// <summary>
        /// Map that stores all unique names and their corresponding machine ids.
        /// </summary>
        internal readonly ConcurrentDictionary<string, MachineId> NameValueToMachineId;

        /// <summary>
        /// Set of all machine Ids created by this runtime.
        /// </summary>
        internal HashSet<MachineId> CreatedMachineIds;

        /// <summary>
        /// The root task id.
        /// </summary>
        internal readonly int? RootTaskId;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystematicTestingRuntime"/> class.
        /// </summary>
        internal SystematicTestingRuntime(Configuration configuration, ISchedulingStrategy strategy,
            IRegisterRuntimeOperation reporter)
            : base(configuration)
        {
            this.Monitors = new List<Monitor>();
            this.MachineOperations = new ConcurrentDictionary<ulong, MachineOperation>();
            this.RootTaskId = Task.CurrentId;
            this.CreatedMachineIds = new HashSet<MachineId>();
            this.NameValueToMachineId = new ConcurrentDictionary<string, MachineId>();

            this.BugTrace = new BugTrace();
            this.StateCache = new StateCache(this);
            this.CoverageInfo = new CoverageInfo();
            this.Reporter = reporter;

            var scheduleTrace = new ScheduleTrace();
            if (configuration.EnableLivenessChecking && configuration.EnableCycleDetection)
            {
                strategy = new CycleDetectionStrategy(configuration, this.StateCache,
                    scheduleTrace, this.Monitors, strategy);
            }
            else if (configuration.EnableLivenessChecking)
            {
                strategy = new TemperatureCheckingStrategy(configuration, this.Monitors, strategy);
            }

            this.Scheduler = new OperationScheduler(this, strategy, scheduleTrace, this.Configuration);
            this.TaskScheduler = new ControlledTaskScheduler(this, this.Scheduler.ControlledTaskMap);
            // this.SyncContext = new ControlledSynchronizationContext(this);
        }

        /// <summary>
        /// Creates a machine id that is uniquely tied to the specified unique name. The
        /// returned machine id can either be a fresh id (not yet bound to any machine),
        /// or it can be bound to a previously created machine. In the second case, this
        /// machine id can be directly used to communicate with the corresponding machine.
        /// </summary>
        public override MachineId CreateMachineIdFromName(Type type, string machineName)
        {
            // It is important that all machine ids use the monotonically incrementing
            // value as the id during testing, and not the unique name.
            var mid = new MachineId(type, machineName, this);
            return this.NameValueToMachineId.GetOrAdd(machineName, mid);
        }

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and with
        /// the specified optional <see cref="Event"/>. This event can only be
        /// used to access its payload, and cannot be handled.
        /// </summary>
        public override MachineId CreateMachine(Type type, Event e = null, Guid opGroupId = default) =>
            this.CreateMachine(null, type, null, e, opGroupId);

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and name, and
        /// with the specified optional <see cref="Event"/>. This event can only be
        /// used to access its payload, and cannot be handled.
        /// </summary>
        public override MachineId CreateMachine(Type type, string machineName, Event e = null, Guid opGroupId = default) =>
            this.CreateMachine(null, type, machineName, e, opGroupId);

        /// <summary>
        /// Creates a new machine of the specified type, using the specified <see cref="MachineId"/>.
        /// This method optionally passes an <see cref="Event"/> to the new machine, which can only
        /// be used to access its payload, and cannot be handled.
        /// </summary>
        public override MachineId CreateMachine(MachineId mid, Type type, Event e = null, Guid opGroupId = default)
        {
            this.Assert(mid != null, "Cannot create a machine using a null machine id.");
            return this.CreateMachine(mid, type, null, e, opGroupId);
        }

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and with the
        /// specified optional <see cref="Event"/>. This event can only be used to
        /// access its payload, and cannot be handled. The method returns only when
        /// the machine is initialized and the <see cref="Event"/> (if any) is handled.
        /// </summary>
        public override Task<MachineId> CreateMachineAndExecuteAsync(Type type, Event e = null, Guid opGroupId = default) =>
            this.CreateMachineAndExecuteAsync(null, type, null, e, opGroupId);

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and name, and with
        /// the specified optional <see cref="Event"/>. This event can only be used to
        /// access its payload, and cannot be handled. The method returns only when the
        /// machine is initialized and the <see cref="Event"/> (if any) is handled.
        /// </summary>
        public override Task<MachineId> CreateMachineAndExecuteAsync(Type type, string machineName, Event e = null, Guid opGroupId = default) =>
            this.CreateMachineAndExecuteAsync(null, type, machineName, e, opGroupId);

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/>, using the specified
        /// unbound machine id, and passes the specified optional <see cref="Event"/>. This
        /// event can only be used to access its payload, and cannot be handled. The method
        /// returns only when the machine is initialized and the <see cref="Event"/> (if any)
        /// is handled.
        /// </summary>
        public override Task<MachineId> CreateMachineAndExecuteAsync(MachineId mid, Type type, Event e = null, Guid opGroupId = default)
        {
            this.Assert(mid != null, "Cannot create a machine using a null machine id.");
            return this.CreateMachineAndExecuteAsync(mid, type, null, e, opGroupId);
        }

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and with the
        /// specified optional <see cref="Event"/>. This event can only be used to
        /// access its payload, and cannot be handled. The method returns only when
        /// the machine is initialized and the <see cref="Event"/> (if any) is handled.
        /// </summary>
        public override Task<MachineId> CreateMachineAndExecute(Type type, Event e = null, Guid opGroupId = default) =>
            this.CreateMachineAndExecuteAsync(null, type, null, e, opGroupId);

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and name, and with
        /// the specified optional <see cref="Event"/>. This event can only be used to
        /// access its payload, and cannot be handled. The method returns only when the
        /// machine is initialized and the <see cref="Event"/> (if any) is handled.
        /// </summary>
        public override Task<MachineId> CreateMachineAndExecute(Type type, string machineName, Event e = null, Guid opGroupId = default) =>
            this.CreateMachineAndExecuteAsync(null, type, machineName, e, opGroupId);

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/>, using the specified
        /// unbound machine id, and passes the specified optional <see cref="Event"/>. This
        /// event can only be used to access its payload, and cannot be handled. The method
        /// returns only when the machine is initialized and the <see cref="Event"/> (if any)
        /// is handled.
        /// </summary>
        public override Task<MachineId> CreateMachineAndExecute(MachineId mid, Type type, Event e = null, Guid opGroupId = default)
        {
            this.Assert(mid != null, "Cannot create a machine using a null machine id.");
            return this.CreateMachineAndExecuteAsync(mid, type, null, e, opGroupId);
        }

        /// <summary>
        /// Sends an asynchronous <see cref="Event"/> to a machine.
        /// </summary>
        public override void SendEvent(MachineId target, Event e, Guid opGroupId = default, SendOptions options = null)
        {
            this.SendEvent(target, e, this.GetExecutingMachine<Machine>(), opGroupId, options);
        }

        /// <summary>
        /// Sends an <see cref="Event"/> to a machine. Returns immediately if the target machine was already
        /// running. Otherwise blocks until the machine handles the event and reaches quiescense.
        /// </summary>
        public override Task<bool> SendEventAndExecuteAsync(MachineId target, Event e, Guid opGroupId = default, SendOptions options = null) =>
            this.SendEventAndExecuteAsync(target, e, this.GetExecutingMachine<Machine>(), opGroupId, options);

        /// <summary>
        /// Sends an <see cref="Event"/> to a machine. Returns immediately if the target machine was already
        /// running. Otherwise blocks until the machine handles the event and reaches quiescense.
        /// </summary>
        public override Task<bool> SendEventAndExecute(MachineId target, Event e, Guid opGroupId = default, SendOptions options = null) =>
            this.SendEventAndExecuteAsync(target, e, opGroupId, options);

        /// <summary>
        /// Returns the operation group id of the specified machine. Returns <see cref="Guid.Empty"/>
        /// if the id is not set, or if the <see cref="MachineId"/> is not associated with this runtime.
        /// During testing, the runtime asserts that the specified machine is currently executing.
        /// </summary>
        public override Guid GetCurrentOperationGroupId(MachineId currentMachine)
        {
            this.Assert(currentMachine == this.GetCurrentMachineId(),
                "Trying to access the operation group id of '{0}', which is not the currently executing machine.",
                currentMachine);

            Machine machine = this.GetMachineFromId<Machine>(currentMachine);
            return machine is null ? Guid.Empty : machine.OperationGroupId;
        }

        /// <summary>
        /// Runs the specified test inside a synchronous test harness machine.
        /// </summary>
        internal void RunTestHarness(Delegate test, string testName)
        {
            this.Assert(Task.CurrentId != null, "The test harness machine must execute inside a task.");
            this.Assert(test != null, "The test harness machine cannot execute a null test.");

            testName = string.IsNullOrEmpty(testName) ? "anonymous" : testName;
            this.Logger.WriteLine($"<TestLog> Running test '{testName}'.");

            var machine = new TestEntryPointWorkMachine(this, test);
            this.DispatchWork(machine, null);
        }

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and name, using the specified
        /// unbound machine id, and passes the specified optional <see cref="Event"/>. This event
        /// can only be used to access its payload, and cannot be handled.
        /// </summary>
        internal MachineId CreateMachine(MachineId mid, Type type, string machineName, Event e = null, Guid opGroupId = default)
        {
            Machine creator = this.GetExecutingMachine<Machine>();
            return this.CreateMachine(mid, type, machineName, e, creator, opGroupId);
        }

        /// <summary>
        /// Creates a new <see cref="Machine"/> of the specified <see cref="Type"/>.
        /// </summary>
        internal override MachineId CreateMachine(MachineId mid, Type type, string machineName, Event e,
            Machine creator, Guid opGroupId)
        {
            this.AssertCorrectCallerMachine(creator, "CreateMachine");
            if (creator != null)
            {
                this.AssertNoPendingTransitionStatement(creator, "create a machine");
            }

            Machine machine = this.CreateMachine(mid, type, machineName, creator, opGroupId);

            this.BugTrace.AddCreateMachineStep(creator, machine.Id, e is null ? null : new EventInfo(e));
            this.RunMachineEventHandler(machine, e, true, null, null);

            return machine.Id;
        }

        /// <summary>
        /// Creates a new machine of the specified <see cref="Type"/> and name, using the specified
        /// unbound machine id, and passes the specified optional <see cref="Event"/>. This event
        /// can only be used to access its payload, and cannot be handled. The method returns only
        /// when the machine is initialized and the <see cref="Event"/> (if any) is handled.
        /// </summary>
        internal Task<MachineId> CreateMachineAndExecuteAsync(MachineId mid, Type type, string machineName, Event e = null,
            Guid opGroupId = default)
        {
            Machine creator = this.GetExecutingMachine<Machine>();
            return this.CreateMachineAndExecuteAsync(mid, type, machineName, e, creator, opGroupId);
        }

        /// <summary>
        /// Creates a new <see cref="Machine"/> of the specified <see cref="Type"/>. The
        /// method returns only when the machine is initialized and the <see cref="Event"/>
        /// (if any) is handled.
        /// </summary>
        internal override async Task<MachineId> CreateMachineAndExecuteAsync(MachineId mid, Type type, string machineName, Event e,
            Machine creator, Guid opGroupId)
        {
            this.AssertCorrectCallerMachine(creator, "CreateMachineAndExecute");
            this.Assert(creator != null,
                "Only a machine can call 'CreateMachineAndExecute': avoid calling it directly from the 'Test' method; instead call it through a 'harness' machine.");
            this.AssertNoPendingTransitionStatement(creator, "create a machine");

            Machine machine = this.CreateMachine(mid, type, machineName, creator, opGroupId);

            this.BugTrace.AddCreateMachineStep(creator, machine.Id, e is null ? null : new EventInfo(e));
            this.RunMachineEventHandler(machine, e, true, creator, null);

            // Wait until the machine reaches quiescence.
            await creator.Receive(typeof(QuiescentEvent), rev => (rev as QuiescentEvent).MachineId == machine.Id);

            return await Task.FromResult(machine.Id);
        }

        /// <summary>
        /// Creates a new <see cref="Machine"/> of the specified <see cref="Type"/>.
        /// </summary>
        private Machine CreateMachine(MachineId mid, Type type, string machineName, Machine creator, Guid opGroupId)
        {
            this.Assert(type.IsSubclassOf(typeof(Machine)), "Type '{0}' is not a machine.", type.FullName);

            // Using ulong.MaxValue because a 'Create' operation cannot specify
            // the id of its target, because the id does not exist yet.
            this.Scheduler.ScheduleNextOperation(AsyncOperationType.Create, AsyncOperationTarget.Task, ulong.MaxValue);
            ResetProgramCounter(creator);

            if (mid is null)
            {
                mid = new MachineId(type, machineName, this);
            }
            else
            {
                this.Assert(mid.Runtime is null || mid.Runtime == this, "Unbound machine id '{0}' was created by another runtime.", mid.Value);
                this.Assert(mid.Type == type.FullName, "Cannot bind machine id '{0}' of type '{1}' to a machine of type '{2}'.",
                    mid.Value, mid.Type, type.FullName);
                mid.Bind(this);
            }

            // The operation group id of the machine is set using the following precedence:
            // (1) To the specified machine creation operation group id, if it is non-empty.
            // (2) To the operation group id of the creator machine, if it exists and is non-empty.
            // (3) To the empty operation group id.
            if (opGroupId == Guid.Empty && creator != null)
            {
                opGroupId = creator.OperationGroupId;
            }

            Machine machine = MachineFactory.Create(type);
            IMachineStateManager stateManager = new SerializedMachineStateManager(this, machine, opGroupId);
            IEventQueue eventQueue = new SerializedMachineEventQueue(stateManager, machine);
            machine.Initialize(this, mid, stateManager, eventQueue);
            machine.InitializeStateInformation();

            if (this.Configuration.ReportActivityCoverage)
            {
                this.ReportActivityCoverageOfMachine(machine);
            }

            bool result = this.MachineMap.TryAdd(mid, machine);
            this.Assert(result, "Machine id '{0}' is used by an existing machine.", mid.Value);

            this.Assert(!this.CreatedMachineIds.Contains(mid),
                "Machine id '{0}' of a previously halted machine cannot be reused to create a new machine of type '{1}'",
                mid.Value, type.FullName);
            this.CreatedMachineIds.Add(mid);
            this.MachineOperations.GetOrAdd(mid.Value, new MachineOperation(machine));

            this.LogWriter.OnCreateMachine(mid, creator?.Id);

            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.RegisterCreateMachine(creator?.Id, mid);
            }

            return machine;
        }

        /// <summary>
        /// Sends an asynchronous <see cref="Event"/> to a machine.
        /// </summary>
        internal override void SendEvent(MachineId target, Event e, AsyncMachine sender, Guid opGroupId, SendOptions options)
        {
            if (sender != null)
            {
                this.Assert(target != null, "Machine '{0}' is sending to a null machine.", sender.Id);
                this.Assert(e != null, "Machine '{0}' is sending a null event.", sender.Id);
            }
            else
            {
                this.Assert(target != null, "Cannot send to a null machine.");
                this.Assert(e != null, "Cannot send a null event.");
            }

            this.AssertCorrectCallerMachine(sender as Machine, "SendEvent");

            EnqueueStatus enqueueStatus = this.EnqueueEvent(target, e, sender, opGroupId, options,
                out Machine targetMachine, out EventInfo eventInfo);
            if (enqueueStatus is EnqueueStatus.EventHandlerNotRunning)
            {
                this.RunMachineEventHandler(targetMachine, null, false, null, eventInfo);
            }
        }

        /// <summary>
        /// Sends an asynchronous <see cref="Event"/> to a machine. Returns immediately if the target machine was
        /// already running. Otherwise blocks until the machine handles the event and reaches quiescense.
        /// </summary>
        internal override async Task<bool> SendEventAndExecuteAsync(MachineId target, Event e, AsyncMachine sender,
            Guid opGroupId, SendOptions options)
        {
            this.Assert(sender is Machine,
                "Only a machine can call 'SendEventAndExecute': avoid calling it directly from the 'Test' method; instead call it through a 'harness' machine.");
            this.Assert(target != null, "Machine '{0}' is sending to a null machine.", sender.Id);
            this.Assert(e != null, "Machine '{0}' is sending a null event.", sender.Id);
            this.AssertCorrectCallerMachine(sender as Machine, "SendEventAndExecute");

            EnqueueStatus enqueueStatus = this.EnqueueEvent(target, e, sender, opGroupId, options,
                out Machine targetMachine, out EventInfo eventInfo);
            if (enqueueStatus is EnqueueStatus.EventHandlerNotRunning)
            {
                this.RunMachineEventHandler(targetMachine, null, false, sender as Machine, eventInfo);

                // Wait until the machine reaches quiescence.
                await (sender as Machine).Receive(typeof(QuiescentEvent), rev => (rev as QuiescentEvent).MachineId == target);
                return true;
            }

            // 'EnqueueStatus.EventHandlerNotRunning' is not returned by 'EnqueueEvent' (even when
            // the machine was previously inactive) when the event 'e' requires no action by the
            // machine (i.e., it implicitly handles the event).
            return enqueueStatus is EnqueueStatus.Dropped || enqueueStatus is EnqueueStatus.NextEventUnavailable;
        }

        /// <summary>
        /// Enqueues an event to the machine with the specified id.
        /// </summary>
        private EnqueueStatus EnqueueEvent(MachineId target, Event e, AsyncMachine sender, Guid opGroupId,
            SendOptions options, out Machine targetMachine, out EventInfo eventInfo)
        {
            this.Assert(this.CreatedMachineIds.Contains(target),
                "Cannot send event '{0}' to machine id '{1}' that was never previously bound to a machine of type '{2}'",
                e.GetType().FullName, target.Value, target.Type);

            AsyncOperationType opType = AsyncOperationType.Send;

            if (e.GetType().Name == "eTerminateRequest")
            {
                // Hardcoded for now, can convert to a generic attribute for production use.
                opType = AsyncOperationType.InjectFailure;
                this.Logger.WriteLine("<FailureInjectionLog> Injecting a failure.");
            }

            if (e.GetType().Name == "eFailure")
            {
                // Hardcoded for now, can convert to a generic attribute for production use.
                opType = AsyncOperationType.InjectFailure;
                this.Logger.WriteLine("<FailureInjectionLog> Injecting a failure.");
            }

            this.Scheduler.ScheduleNextOperation(opType, AsyncOperationTarget.Inbox, target.Value);
            ResetProgramCounter(sender as Machine);

            // The operation group id of this operation is set using the following precedence:
            // (1) To the specified send operation group id, if it is non-empty.
            // (2) To the operation group id of the sender machine, if it exists and is non-empty.
            // (3) To the empty operation group id.
            if (opGroupId == Guid.Empty && sender != null)
            {
                opGroupId = sender.OperationGroupId;
            }

            targetMachine = this.GetMachineFromId<Machine>(target);
            if (targetMachine is null)
            {
                this.LogWriter.OnSend(target, sender?.Id, (sender as Machine)?.CurrentStateName ?? string.Empty,
                    e.GetType().FullName, opGroupId, isTargetHalted: true);
                this.Assert(options is null || !options.MustHandle,
                    "A must-handle event '{0}' was sent to the halted machine '{1}'.", e.GetType().FullName, target);
                this.TryHandleDroppedEvent(e, target);
                eventInfo = null;
                return EnqueueStatus.Dropped;
            }

            if (sender is Machine)
            {
                this.AssertNoPendingTransitionStatement(sender as Machine, "send an event");
            }

            EnqueueStatus enqueueStatus = this.EnqueueEvent(targetMachine, e, sender, opGroupId, options, out eventInfo);
            if (enqueueStatus == EnqueueStatus.Dropped)
            {
                this.TryHandleDroppedEvent(e, target);
            }

            return enqueueStatus;
        }

        /// <summary>
        /// Enqueues an event to the machine with the specified id.
        /// </summary>
        private EnqueueStatus EnqueueEvent(Machine machine, Event e, AsyncMachine sender, Guid opGroupId,
            SendOptions options, out EventInfo eventInfo)
        {
            EventOriginInfo originInfo;
            if (sender is Machine senderMachine)
            {
                originInfo = new EventOriginInfo(sender.Id, senderMachine.GetType().FullName,
                    NameResolver.GetStateNameForLogging(senderMachine.CurrentState));
            }
            else
            {
                // Message comes from the environment.
                originInfo = new EventOriginInfo(null, "Env", "Env");
            }

            eventInfo = new EventInfo(e, originInfo)
            {
                MustHandle = options?.MustHandle ?? false,
                Assert = options?.Assert ?? -1,
                Assume = options?.Assume ?? -1,
                SendStep = this.Scheduler.ScheduledSteps
            };

            this.LogWriter.OnSend(machine.Id, sender?.Id, (sender as Machine)?.CurrentStateName ?? string.Empty,
                e.GetType().FullName, opGroupId, isTargetHalted: false);

            if (sender != null)
            {
                var stateName = sender is Machine ? (sender as Machine).CurrentStateName : string.Empty;
                this.BugTrace.AddSendEventStep(sender.Id, stateName, eventInfo, machine.Id);
                if (this.Configuration.EnableDataRaceDetection)
                {
                    this.Reporter.RegisterEnqueue(sender.Id, machine.Id, e, (ulong)this.Scheduler.ScheduledSteps);
                }
            }

            return machine.Enqueue(e, opGroupId, eventInfo);
        }

        /// <summary>
        /// Runs a new asynchronous event handler for the specified machine.
        /// This is a fire and forget invocation.
        /// </summary>
        /// <param name="machine">Machine that executes this event handler.</param>
        /// <param name="initialEvent">Event for initializing the machine.</param>
        /// <param name="isFresh">If true, then this is a new machine.</param>
        /// <param name="syncCaller">Caller machine that is blocked for quiscence.</param>
        /// <param name="enablingEvent">If non-null, the event info of the sent event that caused the event handler to be restarted.</param>
        private void RunMachineEventHandler(Machine machine, Event initialEvent, bool isFresh, Machine syncCaller, EventInfo enablingEvent)
        {
            MachineOperation op = this.GetAsynchronousOperation(machine.Id.Value);

            Task task = new Task(async () =>
            {
                // Set the executing runtime in the local asynchronous context,
                // allowing future retrieval in the same asynchronous call stack.
                AsyncLocalRuntime.Value = this;

                try
                {
                    OperationScheduler.NotifyOperationStarted(op);

                    if (isFresh)
                    {
                        await machine.GotoStartState(initialEvent);
                    }

                    await machine.RunEventHandlerAsync();

                    if (syncCaller != null)
                    {
                        this.EnqueueEvent(syncCaller, new QuiescentEvent(machine.Id), machine, machine.OperationGroupId, null, out EventInfo _);
                    }

                    IO.Debug.WriteLine($"<ScheduleDebug> Completed event handler of '{machine.Id}' on task '{Task.CurrentId}'.");
                    op.OnCompleted();

                    if (machine.IsHalted)
                    {
                        this.Scheduler.ScheduleNextOperation(AsyncOperationType.Stop, AsyncOperationTarget.Task, machine.Id.Value);
                    }
                    else
                    {
                        this.Scheduler.ScheduleNextOperation(AsyncOperationType.Receive, AsyncOperationTarget.Inbox, machine.Id.Value);
                    }

                    IO.Debug.WriteLine($"<ScheduleDebug> Terminated event handler of '{machine.Id}' on task '{Task.CurrentId}'.");
                    ResetProgramCounter(machine);
                }
                catch (Exception ex)
                {
                    Exception innerException = ex;
                    while (innerException is TargetInvocationException)
                    {
                        innerException = innerException.InnerException;
                    }

                    if (innerException is AggregateException)
                    {
                        innerException = innerException.InnerException;
                    }

                    if (innerException is ExecutionCanceledException)
                    {
                        IO.Debug.WriteLine($"<Exception> ExecutionCanceledException was thrown from machine '{machine.Id}'.");
                    }
                    else if (innerException is TaskSchedulerException)
                    {
                        IO.Debug.WriteLine($"<Exception> TaskSchedulerException was thrown from machine '{machine.Id}'.");
                    }
                    else if (innerException is ObjectDisposedException)
                    {
                        IO.Debug.WriteLine($"<Exception> ObjectDisposedException was thrown from machine '{machine.Id}' with reason '{ex.Message}'.");
                    }
                    else
                    {
                        // Reports the unhandled exception.
                        string message = string.Format(CultureInfo.InvariantCulture,
                            $"Exception '{ex.GetType()}' was thrown in machine '{machine.Id}', " +
                            $"'{ex.Source}':\n" +
                            $"   {ex.Message}\n" +
                            $"The stack trace is:\n{ex.StackTrace}");
                        this.Scheduler.NotifyAssertionFailure(message, killTasks: true, cancelExecution: false);
                    }
                }
                finally
                {
                    if (machine.IsHalted)
                    {
                        this.MachineMap.TryRemove(machine.Id, out AsyncMachine _);
                    }
                }
            });

            op.OnCreated(enablingEvent?.SendStep ?? 0);
            this.Scheduler.NotifyOperationCreated(op, task);

            task.Start(this.TaskScheduler);
            this.Scheduler.WaitForOperationToStart(op);
        }

        /// <summary>
        /// Creates a new <see cref="MachineTask"/> to execute the specified asynchronous work.
        /// </summary>
        internal override MachineTask CreateMachineTask(Action action, CancellationToken cancellationToken)
        {
            this.Assert(action != null, "The task cannot execute a null action.");
            var machine = new ActionWorkMachine(this, action);
            this.DispatchWork(machine, null);
            return new ControlledMachineTask(this, machine.AwaiterTask, MachineTaskType.ExplicitTask);
        }

        /// <summary>
        /// Creates a new <see cref="MachineTask"/> to execute the specified asynchronous work.
        /// </summary>
        internal override MachineTask CreateMachineTask(Func<Task> function, CancellationToken cancellationToken)
        {
            this.Assert(function != null, "The task cannot execute a null function.");
            var machine = new FuncWorkMachine(this, function);
            this.DispatchWork(machine, null);
            return new ControlledMachineTask(this, machine.AwaiterTask, MachineTaskType.ExplicitTask);
        }

        /// <summary>
        /// Creates a new <see cref="MachineTask{TResult}"/> to execute the specified asynchronous work.
        /// </summary>
        internal override MachineTask<TResult> CreateMachineTask<TResult>(Func<TResult> function,
            CancellationToken cancellationToken)
        {
            this.Assert(function != null, "The task cannot execute a null function.");
            var machine = new FuncWorkMachine<TResult>(this, function);
            this.DispatchWork(machine, null);
            return new ControlledMachineTask<TResult>(this, machine.AwaiterTask, MachineTaskType.ExplicitTask);
        }

        /// <summary>
        /// Creates a new <see cref="MachineTask{TResult}"/> to execute the specified asynchronous work.
        /// </summary>
        internal override MachineTask<TResult> CreateMachineTask<TResult>(Func<Task<TResult>> function,
            CancellationToken cancellationToken)
        {
            this.Assert(function != null, "The task cannot execute a null function.");
            var machine = new FuncTaskWorkMachine<TResult>(this, function);
            this.DispatchWork(machine, null);
            return new ControlledMachineTask<TResult>(this, machine.AwaiterTask, MachineTaskType.ExplicitTask);
        }

        /// <summary>
        /// Creates a new <see cref="MachineTask"/> to execute the specified asynchronous delay.
        /// </summary>
        internal override MachineTask CreateMachineTask(int millisecondsDelay, CancellationToken cancellationToken)
        {
            if (millisecondsDelay == 0)
            {
                // If the delay is 0, then complete synchronously.
                return MachineTask.CompletedTask;
            }

            var machine = new DelayWorkMachine(this);
            this.DispatchWork(machine, null);
            return new ControlledMachineTask(this, machine.AwaiterTask, MachineTaskType.ExplicitTask);
        }

        /// <summary>
        /// Creates a new <see cref="MachineTask"/> to complete with the specified task.
        /// </summary>
        internal override MachineTask CreateCompletionMachineTask(Task task)
        {
            if (AsyncLocalRuntime.Value != this)
            {
                throw new ExecutionCanceledException();
            }

            this.Scheduler.CheckNoExternalConcurrencyUsed();
            // var machine = new TaskCompletionWorkMachine(this, task);
            // this.DispatchWork(machine, task);
            return new ControlledMachineTask(this, task, MachineTaskType.CompletionSourceTask);
        }

        /// <summary>
        /// Creates a new <see cref="MachineTask"/> to complete with the specified task.
        /// </summary>
        internal override MachineTask<TResult> CreateCompletionMachineTask<TResult>(Task<TResult> task)
        {
            if (AsyncLocalRuntime.Value != this)
            {
                throw new ExecutionCanceledException();
            }

            this.Scheduler.CheckNoExternalConcurrencyUsed();
            // var machine = new TaskCompletionWorkMachine<TResult>(this, task);
            // this.DispatchWork(machine, task);
            return new ControlledMachineTask<TResult>(this, /*machine.AwaiterTask*/task,
                MachineTaskType.CompletionSourceTask);
        }

        /// <summary>
        /// Asynchronously waits for the specified tasks to complete.
        /// </summary>
        internal override MachineTask WaitAllTasksAsync(IEnumerable<Task> tasks)
        {
            this.Assert(tasks != null, "Cannot wait for a null array of tasks to complete.");
            this.Assert(tasks.Count() > 0, "Cannot wait for zero tasks to complete.");

            AsyncMachine caller = this.GetExecutingMachine<AsyncMachine>();
            if (caller is null)
            {
                // TODO: throw an error, as a non-controlled task is awaiting?
                return new MachineTask(Task.WhenAll(tasks));
            }

            MachineOperation callerOp = this.GetAsynchronousOperation(caller.Id.Value);
            foreach (var task in tasks)
            {
                if (!task.IsCompleted)
                {
                    callerOp.OnWaitTask(task, true);
                }
            }

            if (callerOp.Status == AsyncOperationStatus.BlockedOnWaitAll)
            {
                // Only schedule if the task is not already completed.
                this.Scheduler.ScheduleNextOperation(AsyncOperationType.Join, AsyncOperationTarget.Task, caller.Id.Value);
            }

            return MachineTask.CompletedTask;
        }

        /// <summary>
        /// Asynchronously waits for all specified tasks to complete.
        /// </summary>
        internal override MachineTask<TResult[]> WaitAllTasksAsync<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            this.Assert(tasks != null, "Cannot wait for a null array of tasks to complete.");
            this.Assert(tasks.Count() > 0, "Cannot wait for zero tasks to complete.");

            AsyncMachine caller = this.GetExecutingMachine<AsyncMachine>();
            if (caller is null)
            {
                // TODO: throw an error, as a non-controlled task is awaiting?
                return new MachineTask<TResult[]>(Task.WhenAll(tasks));
            }

            int size = 0;
            MachineOperation callerOp = this.GetAsynchronousOperation(caller.Id.Value);
            foreach (var task in tasks)
            {
                size++;
                if (!task.IsCompleted)
                {
                    callerOp.OnWaitTask(task, true);
                }
            }

            if (callerOp.Status == AsyncOperationStatus.BlockedOnWaitAll)
            {
                // Only schedule if the task is not already completed.
                this.Scheduler.ScheduleNextOperation(AsyncOperationType.Join, AsyncOperationTarget.Task, caller.Id.Value);
            }

            int idx = 0;
            TResult[] result = new TResult[size];
            foreach (var task in tasks)
            {
                result[idx] = task.Result;
                idx++;
            }

            return MachineTask.FromResult(result);
        }

        /// <summary>
        /// Asynchronously waits for any of the specified tasks to complete.
        /// </summary>
        internal override MachineTask<Task> WaitAnyTaskAsync(IEnumerable<Task> tasks)
        {
            this.Assert(tasks != null, "Cannot wait for a null array of tasks to complete.");
            this.Assert(tasks.Count() > 0, "Cannot wait for zero tasks to complete.");

            AsyncMachine caller = this.GetExecutingMachine<AsyncMachine>();
            if (caller is null)
            {
                // TODO: throw an error, as a non-controlled task is awaiting?
                return new MachineTask<Task>(Task.WhenAny(tasks));
            }

            MachineOperation callerOp = this.GetAsynchronousOperation(caller.Id.Value);
            foreach (var task in tasks)
            {
                if (!task.IsCompleted)
                {
                    callerOp.OnWaitTask(task, false);
                }
            }

            if (callerOp.Status == AsyncOperationStatus.BlockedOnWaitAny)
            {
                // Only schedule if the task is not already completed.
                this.Scheduler.ScheduleNextOperation(AsyncOperationType.Join, AsyncOperationTarget.Task, caller.Id.Value);
            }

            Task result = null;
            foreach (var task in tasks)
            {
                if (task.IsCompleted)
                {
                    result = task;
                    break;
                }
            }

            return MachineTask.FromResult(result);
        }

        /// <summary>
        /// Asynchronously waits for any of the specified tasks to complete.
        /// </summary>
        internal override MachineTask<Task<TResult>> WaitAnyTaskAsync<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            this.Assert(tasks != null, "Cannot wait for a null array of tasks to complete.");
            this.Assert(tasks.Count() > 0, "Cannot wait for zero tasks to complete.");

            AsyncMachine caller = this.GetExecutingMachine<AsyncMachine>();
            if (caller is null)
            {
                // TODO: throw an error, as a non-controlled task is awaiting?
                return new MachineTask<Task<TResult>>(Task.WhenAny(tasks));
            }

            int size = 0;
            MachineOperation callerOp = this.GetAsynchronousOperation(caller.Id.Value);
            foreach (var task in tasks)
            {
                size++;
                if (!task.IsCompleted)
                {
                    callerOp.OnWaitTask(task, false);
                }
            }

            if (callerOp.Status == AsyncOperationStatus.BlockedOnWaitAny)
            {
                // Only schedule if the task is not already completed.
                this.Scheduler.ScheduleNextOperation(AsyncOperationType.Join, AsyncOperationTarget.Task, caller.Id.Value);
            }

            Task<TResult> result = null;
            foreach (var task in tasks)
            {
                if (task.IsCompleted)
                {
                    result = task;
                    break;
                }
            }

            return MachineTask.FromResult(result);
        }

        /// <summary>
        /// Schedules the specified work machine to be executed asynchronously.
        /// This is a fire and forget invocation.
        /// </summary>
        internal void DispatchWork(WorkMachine machine, Task parentTask)
        {
            // this.Scheduler.ScheduleNextOperation(AsyncOperationType.Create, AsyncOperationTarget.Task, ulong.MaxValue);

            MachineOperation op = new MachineOperation(machine);

            this.MachineOperations.GetOrAdd(machine.Id.Value, op);
            this.MachineMap.TryAdd(machine.Id, machine);
            this.CreatedMachineIds.Add(machine.Id);

            // TODO: change to custom log.
            this.LogWriter.OnCreateMachine(machine.Id, null);

            Task task = new Task(async () =>
            {
                // Set the executing runtime in the local asynchronous context,
                // allowing future retrieval in the same asynchronous call stack.
                AsyncLocalRuntime.Value = this;

                // SynchronizationContext.SetSynchronizationContext(this.SyncContext);

                try
                {
                    OperationScheduler.NotifyOperationStarted(op);

                    await machine.ExecuteAsync();

                    IO.Debug.WriteLine($"<ScheduleDebug> Completed '{machine.Id}' on task '{Task.CurrentId}'.");
                    op.OnCompleted();

                    this.Scheduler.ScheduleNextOperation(AsyncOperationType.Stop, AsyncOperationTarget.Task, machine.Id.Value);

                    IO.Debug.WriteLine($"<ScheduleDebug> Terminated '{machine.Id}' on task '{Task.CurrentId}'.");
                }
                catch (Exception ex)
                {
                    Exception innerException = ex;
                    while (innerException is TargetInvocationException)
                    {
                        innerException = innerException.InnerException;
                    }

                    if (innerException is AggregateException)
                    {
                        innerException = innerException.InnerException;
                    }

                    if (innerException is ExecutionCanceledException)
                    {
                        IO.Debug.WriteLine($"<Exception> ExecutionCanceledException was thrown from machine '{machine.Id}'.");
                    }
                    else if (innerException is TaskSchedulerException)
                    {
                        IO.Debug.WriteLine($"<Exception> TaskSchedulerException was thrown from machine '{machine.Id}'.");
                    }
                    else
                    {
                        // Reports the unhandled exception.
                        string message = string.Format(CultureInfo.InvariantCulture,
                            $"Exception '{ex.GetType()}' was thrown in machine {machine.Id}, " +
                            $"'{ex.Source}':\n" +
                            $"   {ex.Message}\n" +
                            $"The stack trace is:\n{ex.StackTrace}");
                        this.Scheduler.NotifyAssertionFailure(message, killTasks: true, cancelExecution: false);
                    }
                }
                finally
                {
                    // TODO: properly cleanup machine tasks.
                    this.MachineMap.TryRemove(machine.Id, out AsyncMachine _);
                }
            });

            op.OnCreated(0);
            if (parentTask != null)
            {
                op.OnWaitTask(parentTask);
            }

            this.Scheduler.NotifyOperationCreated(op, task);

            task.Start();
            // task.Start(this.TaskScheduler);
            this.Scheduler.WaitForOperationToStart(op);

            this.Scheduler.ScheduleNextOperation(AsyncOperationType.Yield, AsyncOperationTarget.Task, machine.Id.Value);
        }

        /// <summary>
        /// Waits until all machines have finished execution.
        /// </summary>
        internal async Task WaitAsync()
        {
            await this.Scheduler.WaitAsync();
            this.IsRunning = false;
        }

        /// <summary>
        /// Creates a new timer that sends a <see cref="TimerElapsedEvent"/> to its owner machine.
        /// </summary>
        internal override IMachineTimer CreateMachineTimer(TimerInfo info, Machine owner)
        {
            var mid = this.CreateMachineId(typeof(MockMachineTimer));
            this.CreateMachine(mid, typeof(MockMachineTimer), new TimerSetupEvent(info, owner, this.Configuration.TimeoutDelay));
            return this.GetMachineFromId<MockMachineTimer>(mid);
        }

        /// <summary>
        /// Tries to create a new monitor of the given type.
        /// </summary>
        internal override void TryCreateMonitor(Type type)
        {
            if (this.Monitors.Any(m => m.GetType() == type))
            {
                // Idempotence: only one monitor per type can exist.
                return;
            }

            this.Assert(type.IsSubclassOf(typeof(Monitor)), "Type '{0}' is not a subclass of Monitor.", type.FullName);

            MachineId mid = new MachineId(type, null, this);

            // MachineOperation op = this.MachineOperations.GetOrAdd(mid.Value, new MachineOperation(mid));
            // this.Scheduler.NotifyMonitorRegistered(op);

            Monitor monitor = Activator.CreateInstance(type) as Monitor;
            monitor.Initialize(this, mid);
            monitor.InitializeStateInformation();

            this.LogWriter.OnCreateMonitor(type.FullName, monitor.Id);

            this.ReportActivityCoverageOfMonitor(monitor);
            this.BugTrace.AddCreateMonitorStep(mid);

            this.Monitors.Add(monitor);

            monitor.GotoStartState();
        }

        /// <summary>
        /// Invokes the specified monitor with the given event.
        /// </summary>
        internal override void Monitor(Type type, AsyncMachine sender, Event e)
        {
            this.AssertCorrectCallerMachine(sender as Machine, "Monitor");
            foreach (var m in this.Monitors)
            {
                if (m.GetType() == type)
                {
                    if (this.Configuration.ReportActivityCoverage)
                    {
                        this.ReportActivityCoverageOfMonitorEvent(sender, m, e);
                        this.ReportActivityCoverageOfMonitorTransition(m, e);
                    }

                    if (this.Configuration.EnableDataRaceDetection)
                    {
                        this.Reporter.InMonitor = (long)m.Id.Value;
                    }

                    m.MonitorEvent(e);
                    if (this.Configuration.EnableDataRaceDetection)
                    {
                        this.Reporter.InMonitor = -1;
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// Checks if the assertion holds, and if not, throws an <see cref="AssertionFailureException"/> exception.
        /// </summary>
        public override void Assert(bool predicate)
        {
            if (!predicate)
            {
                this.Scheduler.NotifyAssertionFailure("Detected an assertion failure.");
            }
        }

        /// <summary>
        /// Checks if the assertion holds, and if not, throws an <see cref="AssertionFailureException"/> exception.
        /// </summary>
        public override void Assert(bool predicate, string s, object arg0)
        {
            if (!predicate)
            {
                this.Scheduler.NotifyAssertionFailure(string.Format(CultureInfo.InvariantCulture, s, arg0.ToString()));
            }
        }

        /// <summary>
        /// Checks if the assertion holds, and if not, throws an <see cref="AssertionFailureException"/> exception.
        /// </summary>
        public override void Assert(bool predicate, string s, object arg0, object arg1)
        {
            if (!predicate)
            {
                this.Scheduler.NotifyAssertionFailure(string.Format(CultureInfo.InvariantCulture, s, arg0.ToString(), arg1.ToString()));
            }
        }

        /// <summary>
        /// Checks if the assertion holds, and if not, throws an <see cref="AssertionFailureException"/> exception.
        /// </summary>
        public override void Assert(bool predicate, string s, object arg0, object arg1, object arg2)
        {
            if (!predicate)
            {
                this.Scheduler.NotifyAssertionFailure(string.Format(CultureInfo.InvariantCulture, s, arg0.ToString(), arg1.ToString(), arg2.ToString()));
            }
        }

        /// <summary>
        /// Checks if the assertion holds, and if not, throws an <see cref="AssertionFailureException"/> exception.
        /// </summary>
        public override void Assert(bool predicate, string s, params object[] args)
        {
            if (!predicate)
            {
                this.Scheduler.NotifyAssertionFailure(string.Format(CultureInfo.InvariantCulture, s, args));
            }
        }

        /// <summary>
        /// Asserts that a transition statement (raise, goto or pop) has not
        /// already been called. Records that RGP has been called.
        /// </summary>
        internal void AssertTransitionStatement(Machine machine)
        {
            var stateManager = machine.StateManager as SerializedMachineStateManager;
            this.Assert(!stateManager.IsInsideOnExit,
                "Machine '{0}' has called raise, goto, push or pop inside an OnExit method.",
                machine.Id.Name);
            this.Assert(!stateManager.IsTransitionStatementCalledInCurrentAction,
                "Machine '{0}' has called multiple raise, goto, push or pop in the same action.",
                machine.Id.Name);
            stateManager.IsTransitionStatementCalledInCurrentAction = true;
        }

        /// <summary>
        /// Asserts that a transition statement (raise, goto or pop) has not already been called.
        /// </summary>
        private void AssertNoPendingTransitionStatement(Machine machine, string action)
        {
            if (!this.Configuration.EnableNoApiCallAfterTransitionStmtAssertion)
            {
                // The check is disabled.
                return;
            }

            var stateManager = machine.StateManager as SerializedMachineStateManager;
            this.Assert(!stateManager.IsTransitionStatementCalledInCurrentAction,
                "Machine '{0}' cannot {1} after calling raise, goto, push or pop in the same action.",
                machine.Id.Name, action);
        }

        /// <summary>
        /// Asserts that the machine calling a P# machine method is also
        /// the machine that is currently executing.
        /// </summary>
        private void AssertCorrectCallerMachine(Machine callerMachine, string calledAPI)
        {
            if (callerMachine is null)
            {
                return;
            }

            var executingMachine = this.GetExecutingMachine<Machine>();
            if (executingMachine is null)
            {
                return;
            }

            this.Assert(executingMachine.Equals(callerMachine), "Machine '{0}' invoked {1} on behalf of machine '{2}'.",
                executingMachine.Id, calledAPI, callerMachine.Id);
        }

        /// <summary>
        /// Checks that no monitor is in a hot state upon program termination.
        /// If the program is still running, then this method returns without
        /// performing a check.
        /// </summary>
        internal void CheckNoMonitorInHotStateAtTermination()
        {
            if (!this.Scheduler.HasFullyExploredSchedule)
            {
                return;
            }

            foreach (var monitor in this.Monitors)
            {
                if (monitor.IsInHotState(out string stateName))
                {
                    string message = string.Format(CultureInfo.InvariantCulture,
                        "Monitor '{0}' detected liveness bug in hot state '{1}' at the end of program execution.",
                        monitor.GetType().FullName, stateName);
                    this.Scheduler.NotifyAssertionFailure(message, killTasks: false, cancelExecution: false);
                }
            }
        }

        /// <summary>
        /// Returns a nondeterministic boolean choice, that can be
        /// controlled during analysis or testing.
        /// </summary>
        internal override bool GetNondeterministicBooleanChoice(AsyncMachine caller, int maxValue)
        {
            caller = caller ?? this.GetExecutingMachine<Machine>();
            this.AssertCorrectCallerMachine(caller as Machine, "Random");
            if (caller is Machine machine)
            {
                this.AssertNoPendingTransitionStatement(caller as Machine, "invoke 'Random'");
                (machine.StateManager as SerializedMachineStateManager).ProgramCounter++;
            }

            var choice = this.Scheduler.GetNextNondeterministicBooleanChoice(maxValue);
            this.LogWriter.OnRandom(caller?.Id, choice);

            var stateName = caller is Machine ? (caller as Machine).CurrentStateName : string.Empty;
            this.BugTrace.AddRandomChoiceStep(caller?.Id, stateName, choice);

            return choice;
        }

        /// <summary>
        /// Returns a fair nondeterministic boolean choice, that can be
        /// controlled during analysis or testing.
        /// </summary>
        internal override bool GetFairNondeterministicBooleanChoice(AsyncMachine caller, string uniqueId)
        {
            caller = caller ?? this.GetExecutingMachine<Machine>();
            this.AssertCorrectCallerMachine(caller as Machine, "FairRandom");
            if (caller is Machine machine)
            {
                this.AssertNoPendingTransitionStatement(caller as Machine, "invoke 'FairRandom'");
                (machine.StateManager as SerializedMachineStateManager).ProgramCounter++;
            }

            var choice = this.Scheduler.GetNextNondeterministicBooleanChoice(2, uniqueId);
            this.LogWriter.OnRandom(caller?.Id, choice);

            var stateName = caller is Machine ? (caller as Machine).CurrentStateName : string.Empty;
            this.BugTrace.AddRandomChoiceStep(caller?.Id, stateName, choice);

            return choice;
        }

        /// <summary>
        /// Returns a nondeterministic integer, that can be
        /// controlled during analysis or testing.
        /// </summary>
        internal override int GetNondeterministicIntegerChoice(AsyncMachine caller, int maxValue)
        {
            caller = caller ?? this.GetExecutingMachine<Machine>();
            this.AssertCorrectCallerMachine(caller as Machine, "RandomInteger");
            if (caller is Machine)
            {
                this.AssertNoPendingTransitionStatement(caller as Machine, "invoke 'RandomInteger'");
            }

            var choice = this.Scheduler.GetNextNondeterministicIntegerChoice(maxValue);
            this.LogWriter.OnRandom(caller?.Id, choice);

            var stateName = caller is Machine ? (caller as Machine).CurrentStateName : string.Empty;
            this.BugTrace.AddRandomChoiceStep(caller?.Id, stateName, choice);

            return choice;
        }

        /// <summary>
        /// Notifies that a machine entered a state.
        /// </summary>
        internal override void NotifyEnteredState(Machine machine)
        {
            string machineState = machine.CurrentStateName;
            this.BugTrace.AddGotoStateStep(machine.Id, machineState);

            this.LogWriter.OnMachineState(machine.Id, machineState, isEntry: true);
        }

        /// <summary>
        /// Notifies that a monitor entered a state.
        /// </summary>
        internal override void NotifyEnteredState(Monitor monitor)
        {
            string monitorState = monitor.CurrentStateNameWithTemperature;
            this.BugTrace.AddGotoStateStep(monitor.Id, monitorState);

            this.LogWriter.OnMonitorState(monitor.GetType().FullName, monitor.Id, monitorState, true, monitor.GetHotState());
        }

        /// <summary>
        /// Notifies that a machine exited a state.
        /// </summary>
        internal override void NotifyExitedState(Machine machine)
        {
            this.LogWriter.OnMachineState(machine.Id, machine.CurrentStateName, isEntry: false);
        }

        /// <summary>
        /// Notifies that a monitor exited a state.
        /// </summary>
        internal override void NotifyExitedState(Monitor monitor)
        {
            string monitorState = monitor.CurrentStateNameWithTemperature;
            this.LogWriter.OnMonitorState(monitor.GetType().FullName, monitor.Id, monitorState, false, monitor.GetHotState());
        }

        /// <summary>
        /// Notifies that a machine invoked an action.
        /// </summary>
        internal override void NotifyInvokedAction(Machine machine, MethodInfo action, Event receivedEvent)
        {
            (machine.StateManager as SerializedMachineStateManager).IsTransitionStatementCalledInCurrentAction = false;
            if (action.ReturnType == typeof(MachineTask))
            {
                (machine.StateManager as SerializedMachineStateManager).IsInsideMachineTaskHandler = true;
            }

            string machineState = machine.CurrentStateName;
            this.BugTrace.AddInvokeActionStep(machine.Id, machineState, action);

            this.LogWriter.OnMachineAction(machine.Id, machineState, action.Name);
            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.InAction[machine.Id.Value] = true;
            }
        }

        /// <summary>
        /// Notifies that a machine completed an action.
        /// </summary>
        internal override void NotifyCompletedAction(Machine machine, MethodInfo action, Event receivedEvent)
        {
            (machine.StateManager as SerializedMachineStateManager).IsTransitionStatementCalledInCurrentAction = false;
            (machine.StateManager as SerializedMachineStateManager).IsInsideMachineTaskHandler = false;
            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.InAction[machine.Id.Value] = false;
            }
        }

        /// <summary>
        /// Notifies that a machine invoked an action.
        /// </summary>
        internal override void NotifyInvokedOnEntryAction(Machine machine, MethodInfo action, Event receivedEvent)
        {
            (machine.StateManager as SerializedMachineStateManager).IsTransitionStatementCalledInCurrentAction = false;
            if (action.ReturnType == typeof(MachineTask))
            {
                (machine.StateManager as SerializedMachineStateManager).IsInsideMachineTaskHandler = true;
            }

            string machineState = machine.CurrentStateName;
            this.BugTrace.AddInvokeActionStep(machine.Id, machineState, action);

            this.LogWriter.OnMachineAction(machine.Id, machineState, action.Name);
            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.InAction[machine.Id.Value] = true;
            }
        }

        /// <summary>
        /// Notifies that a machine completed invoking an action.
        /// </summary>
        internal override void NotifyCompletedOnEntryAction(Machine machine, MethodInfo action, Event receivedEvent)
        {
            (machine.StateManager as SerializedMachineStateManager).IsTransitionStatementCalledInCurrentAction = false;
            (machine.StateManager as SerializedMachineStateManager).IsInsideMachineTaskHandler = false;
            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.InAction[machine.Id.Value] = false;
            }
        }

        /// <summary>
        /// Notifies that a machine invoked an action.
        /// </summary>
        internal override void NotifyInvokedOnExitAction(Machine machine, MethodInfo action, Event receivedEvent)
        {
            (machine.StateManager as SerializedMachineStateManager).IsInsideOnExit = true;
            (machine.StateManager as SerializedMachineStateManager).IsTransitionStatementCalledInCurrentAction = false;
            if (action.ReturnType == typeof(MachineTask))
            {
                (machine.StateManager as SerializedMachineStateManager).IsInsideMachineTaskHandler = true;
            }

            string machineState = machine.CurrentStateName;
            this.BugTrace.AddInvokeActionStep(machine.Id, machineState, action);

            this.LogWriter.OnMachineAction(machine.Id, machineState, action.Name);
            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.InAction[machine.Id.Value] = true;
            }
        }

        /// <summary>
        /// Notifies that a machine completed invoking an action.
        /// </summary>
        internal override void NotifyCompletedOnExitAction(Machine machine, MethodInfo action, Event receivedEvent)
        {
            (machine.StateManager as SerializedMachineStateManager).IsInsideOnExit = false;
            (machine.StateManager as SerializedMachineStateManager).IsTransitionStatementCalledInCurrentAction = false;
            (machine.StateManager as SerializedMachineStateManager).IsInsideMachineTaskHandler = false;
            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.InAction[machine.Id.Value] = false;
            }
        }

        /// <summary>
        /// Notifies that a monitor invoked an action.
        /// </summary>
        internal override void NotifyInvokedAction(Monitor monitor, MethodInfo action, Event receivedEvent)
        {
            string monitorState = monitor.CurrentStateName;
            this.BugTrace.AddInvokeActionStep(monitor.Id, monitorState, action);

            this.LogWriter.OnMonitorAction(monitor.GetType().FullName, monitor.Id, action.Name, monitorState);
        }

        /// <summary>
        /// Notifies that a machine raised an <see cref="Event"/>.
        /// </summary>
        internal override void NotifyRaisedEvent(Machine machine, Event e, EventInfo eventInfo)
        {
            this.AssertTransitionStatement(machine);

            string machineState = machine.CurrentStateName;
            this.BugTrace.AddRaiseEventStep(machine.Id, machineState, eventInfo);

            this.LogWriter.OnMachineEvent(machine.Id, machineState, eventInfo.EventName);
        }

        /// <summary>
        /// Notifies that a monitor raised an <see cref="Event"/>.
        /// </summary>
        internal override void NotifyRaisedEvent(Monitor monitor, Event e, EventInfo eventInfo)
        {
            string monitorState = monitor.CurrentStateName;
            this.BugTrace.AddRaiseEventStep(monitor.Id, monitorState, eventInfo);

            this.LogWriter.OnMonitorEvent(monitor.GetType().FullName, monitor.Id, monitor.CurrentStateName,
                eventInfo.EventName, isProcessing: false);
        }

        /// <summary>
        /// Notifies that a machine dequeued an <see cref="Event"/>.
        /// </summary>
        internal override void NotifyDequeuedEvent(Machine machine, Event e, EventInfo eventInfo)
        {
            MachineOperation op = this.GetAsynchronousOperation(machine.Id.Value);

            // Skip `Receive` if the last operation exited the previous event handler,
            // to avoid scheduling duplicate `Receive` operations.
            if (op.SkipNextReceiveSchedulingPoint)
            {
                op.SkipNextReceiveSchedulingPoint = false;
            }
            else
            {
                op.MatchingSendIndex = (ulong)eventInfo.SendStep;
                this.Scheduler.ScheduleNextOperation(AsyncOperationType.Receive, AsyncOperationTarget.Inbox, machine.Id.Value);
                ResetProgramCounter(machine);
            }

            this.LogWriter.OnDequeue(machine.Id, machine.CurrentStateName, eventInfo.EventName);

            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.RegisterDequeue(eventInfo.OriginInfo?.SenderMachineId, machine.Id, e, (ulong)eventInfo.SendStep);
            }

            this.BugTrace.AddDequeueEventStep(machine.Id, machine.CurrentStateName, eventInfo);

            if (this.Configuration.ReportActivityCoverage)
            {
                this.ReportActivityCoverageOfReceivedEvent(machine, eventInfo);
                this.ReportActivityCoverageOfStateTransition(machine, e);
            }
        }

        /// <summary>
        /// Notifies that a machine invoked pop.
        /// </summary>
        internal override void NotifyPop(Machine machine)
        {
            this.AssertCorrectCallerMachine(machine, "Pop");
            this.AssertTransitionStatement(machine);

            this.LogWriter.OnPop(machine.Id, string.Empty, machine.CurrentStateName);

            if (this.Configuration.ReportActivityCoverage)
            {
                this.ReportActivityCoverageOfPopTransition(machine, machine.CurrentState, machine.GetStateTypeAtStackIndex(1));
            }
        }

        /// <summary>
        /// Notifies that a machine called Receive.
        /// </summary>
        internal override void NotifyReceiveCalled(Machine machine)
        {
            this.AssertCorrectCallerMachine(machine, "Receive");
            this.AssertNoPendingTransitionStatement(machine, "invoke 'Receive'");
        }

        /// <summary>
        /// Notifies that a machine is handling a raised event.
        /// </summary>
        internal override void NotifyHandleRaisedEvent(Machine machine, Event e)
        {
            if (this.Configuration.ReportActivityCoverage)
            {
                this.ReportActivityCoverageOfStateTransition(machine, e);
            }
        }

        /// <summary>
        /// Notifies that a machine is waiting for the specified task to complete.
        /// </summary>
        internal override void NotifyWaitTask(AsyncMachine machine, Task task)
        {
            this.Assert(task != null, "Cannot wait for a null task to complete.");
            MachineOperation callerOp = this.GetAsynchronousOperation(machine.Id.Value);
            if (callerOp == null)
            {
                return;
            }

            if (!task.IsCompleted)
            {
                callerOp.OnWaitTask(task);
            }

            if (callerOp.Status == AsyncOperationStatus.BlockedOnWaitAll)
            {
                // Only schedule if the task is not already completed.
                this.Scheduler.ScheduleNextOperation(AsyncOperationType.Join, AsyncOperationTarget.Task, machine.Id.Value);
            }
        }

        /// <summary>
        /// Notifies that a machine is waiting to receive an event of one of the specified types.
        /// </summary>
        internal override void NotifyWaitEvent(Machine machine, IEnumerable<Type> eventTypes)
        {
            MachineOperation op = this.GetAsynchronousOperation(machine.Id.Value);
            op.OnWaitEvent(eventTypes);

            string eventNames;
            var eventWaitTypesArray = eventTypes.ToArray();
            if (eventWaitTypesArray.Length == 1)
            {
                this.LogWriter.OnWait(machine.Id, machine.CurrentStateName, eventWaitTypesArray[0]);
                eventNames = eventWaitTypesArray[0].FullName;
            }
            else
            {
                this.LogWriter.OnWait(machine.Id, machine.CurrentStateName, eventWaitTypesArray);
                if (eventWaitTypesArray.Length > 0)
                {
                    string[] eventNameArray = new string[eventWaitTypesArray.Length - 1];
                    for (int i = 0; i < eventWaitTypesArray.Length - 2; i++)
                    {
                        eventNameArray[i] = eventWaitTypesArray[i].FullName;
                    }

                    eventNames = string.Join(", ", eventNameArray) + " or " + eventWaitTypesArray[eventWaitTypesArray.Length - 1].FullName;
                }
                else
                {
                    eventNames = string.Empty;
                }
            }

            this.BugTrace.AddWaitToReceiveStep(machine.Id, machine.CurrentStateName, eventNames);
            this.Scheduler.ScheduleNextOperation(AsyncOperationType.Receive, AsyncOperationTarget.Inbox, machine.Id.Value);
            ResetProgramCounter(machine);
        }

        /// <summary>
        /// Notifies that a machine enqueued an event that it was waiting to receive.
        /// </summary>
        internal override void NotifyReceivedEvent(Machine machine, Event e, EventInfo eventInfo)
        {
            this.LogWriter.OnReceive(machine.Id, machine.CurrentStateName, e.GetType().FullName, wasBlocked: true);
            this.BugTrace.AddReceivedEventStep(machine.Id, machine.CurrentStateName, eventInfo);

            // A subsequent enqueue unblocked the receive action of machine.
            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.RegisterDequeue(eventInfo.OriginInfo?.SenderMachineId, machine.Id, e, (ulong)eventInfo.SendStep);
            }

            MachineOperation op = this.GetAsynchronousOperation(machine.Id.Value);
            op.OnReceivedEvent((ulong)eventInfo.SendStep);

            if (this.Configuration.ReportActivityCoverage)
            {
                this.ReportActivityCoverageOfReceivedEvent(machine, eventInfo);
            }
        }

        /// <summary>
        /// Notifies that a machine received an event without waiting because the event
        /// was already in the inbox when the machine invoked the receive statement.
        /// </summary>
        internal override void NotifyReceivedEventWithoutWaiting(Machine machine, Event e, EventInfo eventInfo)
        {
            this.LogWriter.OnReceive(machine.Id, machine.CurrentStateName, e.GetType().FullName, wasBlocked: false);

            MachineOperation op = this.GetAsynchronousOperation(machine.Id.Value);
            op.MatchingSendIndex = (ulong)eventInfo.SendStep;

            if (this.Configuration.EnableDataRaceDetection)
            {
                this.Reporter.RegisterDequeue(eventInfo.OriginInfo?.SenderMachineId, machine.Id, e, (ulong)eventInfo.SendStep);
            }

            this.Scheduler.ScheduleNextOperation(AsyncOperationType.Receive, AsyncOperationTarget.Inbox, machine.Id.Value);
            ResetProgramCounter(machine);
        }

        /// <summary>
        /// Notifies that a machine has halted.
        /// </summary>
        internal override void NotifyHalted(Machine machine)
        {
            this.BugTrace.AddHaltStep(machine.Id, null);
        }

        /// <summary>
        /// Notifies that the inbox of the specified machine is about to be
        /// checked to see if the default event handler should fire.
        /// </summary>
        internal override void NotifyDefaultEventHandlerCheck(Machine machine)
        {
            this.Scheduler.ScheduleNextOperation(AsyncOperationType.Send, AsyncOperationTarget.Inbox, machine.Id.Value);

            // If the default event handler fires, the next receive in NotifyDefaultHandlerFired
            // will use this as its MatchingSendIndex.
            // If it does not fire, MatchingSendIndex will be overwritten.
            this.GetAsynchronousOperation(machine.Id.Value).MatchingSendIndex = (ulong)this.Scheduler.ScheduledSteps;
        }

        /// <summary>
        /// Notifies that the default handler of the specified machine has been fired.
        /// </summary>
        internal override void NotifyDefaultHandlerFired(Machine machine)
        {
            // MatchingSendIndex is set in NotifyDefaultEventHandlerCheck.
            this.Scheduler.ScheduleNextOperation(AsyncOperationType.Receive, AsyncOperationTarget.Inbox, machine.Id.Value);
            ResetProgramCounter(machine);
        }

        /// <summary>
        /// Reports coverage for the specified received event.
        /// </summary>
        private void ReportActivityCoverageOfReceivedEvent(Machine machine, EventInfo eventInfo)
        {
            string originMachine = eventInfo.OriginInfo.SenderMachineName;
            string originState = eventInfo.OriginInfo.SenderStateName;
            string edgeLabel = eventInfo.EventName;
            string destMachine = machine.GetType().FullName;
            string destState = NameResolver.GetStateNameForLogging(machine.CurrentState);

            this.CoverageInfo.AddTransition(originMachine, originState, edgeLabel, destMachine, destState);
        }

        /// <summary>
        /// Reports coverage for the specified monitor event.
        /// </summary>
        private void ReportActivityCoverageOfMonitorEvent(AsyncMachine sender, Monitor monitor, Event e)
        {
            string originMachine = sender is null ? "Env" : sender.GetType().FullName;
            string originState = sender is null ? "Env" :
                (sender is Machine) ? NameResolver.GetStateNameForLogging((sender as Machine).CurrentState) : "Env";

            string edgeLabel = e.GetType().FullName;
            string destMachine = monitor.GetType().FullName;
            string destState = NameResolver.GetStateNameForLogging(monitor.CurrentState);

            this.CoverageInfo.AddTransition(originMachine, originState, edgeLabel, destMachine, destState);
        }

        /// <summary>
        /// Reports coverage for the specified machine.
        /// </summary>
        private void ReportActivityCoverageOfMachine(Machine machine)
        {
            var machineName = machine.GetType().FullName;
            if (this.CoverageInfo.IsMachineDeclared(machineName))
            {
                return;
            }

            // Fetch states.
            var states = machine.GetAllStates();
            foreach (var state in states)
            {
                this.CoverageInfo.DeclareMachineState(machineName, state);
            }

            // Fetch registered events.
            var pairs = machine.GetAllStateEventPairs();
            foreach (var tup in pairs)
            {
                this.CoverageInfo.DeclareStateEvent(machineName, tup.Item1, tup.Item2);
            }
        }

        /// <summary>
        /// Reports coverage for the specified monitor.
        /// </summary>
        private void ReportActivityCoverageOfMonitor(Monitor monitor)
        {
            var monitorName = monitor.GetType().FullName;

            // Fetch states.
            var states = monitor.GetAllStates();

            foreach (var state in states)
            {
                this.CoverageInfo.DeclareMachineState(monitorName, state);
            }

            // Fetch registered events.
            var pairs = monitor.GetAllStateEventPairs();

            foreach (var tup in pairs)
            {
                this.CoverageInfo.DeclareStateEvent(monitorName, tup.Item1, tup.Item2);
            }
        }

        /// <summary>
        /// Reports coverage for the specified state transition.
        /// </summary>
        private void ReportActivityCoverageOfStateTransition(Machine machine, Event e)
        {
            string originMachine = machine.GetType().FullName;
            string originState = NameResolver.GetStateNameForLogging(machine.CurrentState);
            string destMachine = machine.GetType().FullName;

            string edgeLabel;
            string destState;
            if (e is GotoStateEvent gotoStateEvent)
            {
                edgeLabel = "goto";
                destState = NameResolver.GetStateNameForLogging(gotoStateEvent.State);
            }
            else if (e is PushStateEvent pushStateEvent)
            {
                edgeLabel = "push";
                destState = NameResolver.GetStateNameForLogging(pushStateEvent.State);
            }
            else if (machine.GotoTransitions.ContainsKey(e.GetType()))
            {
                edgeLabel = e.GetType().FullName;
                destState = NameResolver.GetStateNameForLogging(
                    machine.GotoTransitions[e.GetType()].TargetState);
            }
            else if (machine.PushTransitions.ContainsKey(e.GetType()))
            {
                edgeLabel = e.GetType().FullName;
                destState = NameResolver.GetStateNameForLogging(
                    machine.PushTransitions[e.GetType()].TargetState);
            }
            else
            {
                return;
            }

            this.CoverageInfo.AddTransition(originMachine, originState, edgeLabel, destMachine, destState);
        }

        /// <summary>
        /// Reports coverage for a pop transition.
        /// </summary>
        private void ReportActivityCoverageOfPopTransition(Machine machine, Type fromState, Type toState)
        {
            string originMachine = machine.GetType().FullName;
            string originState = NameResolver.GetStateNameForLogging(fromState);
            string destMachine = machine.GetType().FullName;
            string edgeLabel = "pop";
            string destState = NameResolver.GetStateNameForLogging(toState);

            this.CoverageInfo.AddTransition(originMachine, originState, edgeLabel, destMachine, destState);
        }

        /// <summary>
        /// Reports coverage for the specified state transition.
        /// </summary>
        private void ReportActivityCoverageOfMonitorTransition(Monitor monitor, Event e)
        {
            string originMachine = monitor.GetType().FullName;
            string originState = NameResolver.GetStateNameForLogging(monitor.CurrentState);
            string destMachine = originMachine;

            string edgeLabel;
            string destState;
            if (e is GotoStateEvent)
            {
                edgeLabel = "goto";
                destState = NameResolver.GetStateNameForLogging((e as GotoStateEvent).State);
            }
            else if (monitor.GotoTransitions.ContainsKey(e.GetType()))
            {
                edgeLabel = e.GetType().FullName;
                destState = NameResolver.GetStateNameForLogging(
                    monitor.GotoTransitions[e.GetType()].TargetState);
            }
            else
            {
                return;
            }

            this.CoverageInfo.AddTransition(originMachine, originState, edgeLabel, destMachine, destState);
        }

        /// <summary>
        /// Resets the program counter of the specified machine.
        /// </summary>
        private static void ResetProgramCounter(Machine machine)
        {
            if (machine != null)
            {
                (machine.StateManager as SerializedMachineStateManager).ProgramCounter = 0;
            }
        }

        /// <summary>
        /// Gets the currently executing machine of type <typeparamref name="TMachine"/>,
        /// or null if no such machine is currently executing.
        /// </summary>
        internal TMachine GetExecutingMachine<TMachine>()
            where TMachine : AsyncMachine
        {
            if (Task.CurrentId.HasValue &&
                this.Scheduler.ControlledTaskMap.TryGetValue(Task.CurrentId.Value, out MachineOperation op) &&
                op?.Machine is TMachine machine)
            {
                return machine;
            }

            return null;
        }

        /// <summary>
        /// Gets the id of the currently executing machine.
        /// </summary>
        internal MachineId GetCurrentMachineId() => this.GetExecutingMachine<AsyncMachine>()?.Id;

        /// <summary>
        /// Gets the asynchronous operation associated with the specified id.
        /// </summary>
        internal MachineOperation GetAsynchronousOperation(ulong id)
        {
            if (!this.IsRunning)
            {
                throw new ExecutionCanceledException();
            }

            this.MachineOperations.TryGetValue(id, out MachineOperation op);
            return op;
        }

        /// <summary>
        /// Returns the current hashed state of the execution using the specified
        /// level of abstraction. The hash is updated in each execution step.
        /// </summary>
        internal int GetHashedExecutionState(AbstractionLevel abstractionLevel)
        {
            unchecked
            {
                int hash = 14689;

                if (abstractionLevel is AbstractionLevel.Default ||
                    abstractionLevel is AbstractionLevel.Full)
                {
                    foreach (var machine in this.MachineMap.Values)
                    {
                        int machineHash = 37;
                        machineHash = (machineHash * 397) + machine.GetHashedState(abstractionLevel);
                        machineHash = (machineHash * 397) + this.GetAsynchronousOperation(machine.Id.Value).Type.GetHashCode();
                        hash *= machineHash;
                    }

                    foreach (var monitor in this.Monitors)
                    {
                        hash = (hash * 397) + monitor.GetHashedState(abstractionLevel);
                    }
                }
                else if (abstractionLevel is AbstractionLevel.InboxOnly ||
                    abstractionLevel is AbstractionLevel.Custom)
                {
                    foreach (var machine in this.MachineMap.Values)
                    {
                        int machineHash = 37;
                        machineHash = (machineHash * 397) + machine.GetHashedState(abstractionLevel);
                        hash *= machineHash;
                    }

                    foreach (var monitor in this.Monitors)
                    {
                        hash = (hash * 397) + monitor.GetHashedState(abstractionLevel);
                    }
                }

                return hash;
            }
        }

        /// <summary>
        /// Throws an <see cref="AssertionFailureException"/> exception
        /// containing the specified exception.
        /// </summary>
        internal override void WrapAndThrowException(Exception exception, string s, params object[] args)
        {
            string message = string.Format(CultureInfo.InvariantCulture, s, args);
            this.Scheduler.NotifyAssertionFailure(message);
        }

        /// <summary>
        /// Disposes runtime resources.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Monitors.Clear();
                this.MachineMap.Clear();
                this.MachineOperations.Clear();
            }

            base.Dispose(disposing);
        }
    }
}
