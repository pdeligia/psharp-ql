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
        private static extern ulong next_integer(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern ulong next_integer(IntPtr scheduler, ulong max_value);

        [DllImport("coyote.dll")]
        private static extern ulong random_seed(IntPtr scheduler);

        [DllImport("coyote.dll")]
        private static extern int dispose_scheduler(IntPtr scheduler);

        /// <summary>
        /// Disposes scheduling resources.
        /// </summary>
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
