// ------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Microsoft.PSharp.IO;
using Microsoft.PSharp.Runtime;
using Microsoft.PSharp.TestingServices.Runtime;
using Microsoft.PSharp.TestingServices.Scheduling.Strategies;
using Microsoft.PSharp.TestingServices.Tracing.Schedule;
using Microsoft.PSharp.Utilities;

namespace Microsoft.PSharp.TestingServices.Scheduling
{
#pragma warning disable SA1300 // Element must begin with upper-case letter
    /// <summary>
    /// Provides methods for controlling the schedule of asynchronous operations.
    /// </summary>
    internal sealed class NativeScheduler : OperationScheduler
    {
        /// <summary>
        /// The native scheduler.
        /// </summary>
        private IntPtr SchedulerPtr;

        internal override MachineOperation ScheduledOperation
        {
            get
            {
                var id = (ulong)scheduled_operation_id(this.SchedulerPtr);
                return this.OperationMap[id];
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NativeScheduler"/> class.
        /// </summary>
        internal NativeScheduler(SystematicTestingRuntime runtime, ISchedulingStrategy strategy,
            ScheduleTrace trace, Configuration configuration)
            : base(runtime, strategy, trace, configuration)
        {
            this.SchedulerPtr = create_scheduler_with_random_strategy(1);
        }

        [DllImport("coyote.dll")]
        private static extern IntPtr create_scheduler_with_random_strategy(ulong seed);

        [DllImport("coyote.dll")]
        private static extern int attach(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern int detach(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern int create_operation(IntPtr scheduler, ulong operation_id);

        [DllImport("coyote.dll")]
        private static extern int start_operation(IntPtr scheduler, ulong operation_id);

        [DllImport("coyote.dll")]
        private static extern int join_operation(IntPtr scheduler, ulong operation_id);

        [DllImport("coyote.dll")]
        private static extern int complete_operation(IntPtr scheduler, ulong operation_id);

        [DllImport("coyote.dll")]
        private static extern int create_resource(IntPtr scheduler, ulong resource_id);

        [DllImport("coyote.dll")]
        private static extern int wait_resource(IntPtr scheduler, ulong resource_id);

        [DllImport("coyote.dll")]
        private static extern int signal_resource(IntPtr scheduler, ulong resource_id);

        [DllImport("coyote.dll")]
        private static extern int delete_resource(IntPtr scheduler, ulong resource_id);

        [DllImport("coyote.dll")]
        private static extern int schedule_next(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern bool next_boolean(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern int next_integer(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern int next_integer(IntPtr scheduler, ulong max_value);

        [DllImport("coyote.dll")]
        private static extern int scheduled_operation_id(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern int random_seed(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern int dispose_scheduler(IntPtr scheduler);

        internal override int CreateOperation(MachineOperation op) => create_operation(this.SchedulerPtr, op.Machine.Id.Value);

        internal override int StartOperation(MachineOperation op) => start_operation(this.SchedulerPtr, op.Machine.Id.Value);

        internal override int WaitOperationStart(MachineOperation op) => join_operation(this.SchedulerPtr, op.Machine.Id.Value);

        internal override int CompleteOperation(MachineOperation op) => complete_operation(this.SchedulerPtr, op.Machine.Id.Value);

        internal override int CreateResource(ulong id) => create_operation(this.SchedulerPtr, id);

        internal override int AcquireResource(ulong id) => wait_resource(this.SchedulerPtr, id);

        internal override int ReleaseResource(ulong id) => signal_resource(this.SchedulerPtr, id);

        internal override int ScheduleNextOperation() => schedule_next(this.SchedulerPtr);

        internal override bool GetNextBoolean(ulong maxValue) => next_boolean(this.SchedulerPtr);

        internal override int GetNextInteger(ulong maxValue) => next_integer(this.SchedulerPtr, maxValue);

        internal override void Attach() => _ = attach(this.SchedulerPtr);

        internal override void Detach()
        {
            _ = detach(this.SchedulerPtr);

            if (!this.CompletionSource.Task.IsCompleted)
            {
                lock (this.CompletionSource)
                {
                    if (!this.CompletionSource.Task.IsCompleted)
                    {
                        this.CompletionSource.SetResult(true);
                    }
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && this.SchedulerPtr != IntPtr.Zero)
            {
                _ = dispose_scheduler(this.SchedulerPtr);
                this.SchedulerPtr = IntPtr.Zero;
            }
        }

        ~NativeScheduler() => this.Dispose(false);
    }
#pragma warning restore SA1300 // Element must begin with upper-case letter
}
