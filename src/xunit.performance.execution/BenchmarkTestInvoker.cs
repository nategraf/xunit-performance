﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Xunit.Performance.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.Xunit.Performance
{
    internal class BenchmarkTestInvoker : XunitTestInvoker
    {
        private static SemaphoreSlim s_semaphore = new SemaphoreSlim(1);

        public BenchmarkTestInvoker(ITest test,
                                IMessageBus messageBus,
                                Type testClass,
                                object[] constructorArguments,
                                MethodInfo testMethod,
                                object[] testMethodArguments,
                                IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes,
                                ExceptionAggregator aggregator,
                                CancellationTokenSource cancellationTokenSource)
            : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, beforeAfterAttributes, aggregator, cancellationTokenSource)
        {
        }

        protected override async Task<decimal> InvokeTestMethodAsync(object testClassInstance)
        {
            //
            // Serialize all benchmarks
            //
            await s_semaphore.WaitAsync();
            try
            {
                return await InvokeTestMethodImplAsync(testClassInstance);
            }
            finally
            {
                s_semaphore.Release();
            }
        }

        private object CallTestMethod(object testClassInstance)
        {
            return IterateAsync(testClassInstance);
        }

        private async Task IterateAsync(object testClassInstance)
        {
            var asyncSyncContext = (AsyncTestSyncContext)SynchronizationContext.Current;

                BenchmarkEventSource.Log.BenchmarkStart(BenchmarkConfiguration.RunId, DisplayName);

            var iterator = new BenchmarkIteratorImpl(DisplayName);
            var success = false;
            try
                {
                await iterator.RunAsync(async () =>
                    {
                    var result = TestMethod.Invoke(testClassInstance, TestMethodArguments);

                            var task = result as Task;
                            if (task != null)
                            {
                                await task;
                                success = true;
                            }
                            else
                            {
                                var ex = await asyncSyncContext.WaitForCompletionAsync();
                                if (ex == null)
                                    success = true;
                                else
                                    Aggregator.Add(ex);
                            }
                });
                        }
                        finally
                        {
                var stopReason = success ? iterator.IterationStopReason : "TestFailed";
                BenchmarkEventSource.Log.BenchmarkStop(BenchmarkConfiguration.RunId, DisplayName, stopReason);
            }
        }


        internal class BenchmarkIteratorImpl : BenchmarkIterator
        {
            private readonly string _testName;
            private readonly Stopwatch _overallTimer;
            private int _currentIteration;
            private bool _currentIterationMeasurementStarted;
            private bool _currentIterationMesaurementStopped;

            internal string IterationStopReason { get; private set; }

            public BenchmarkIteratorImpl(string testName)
            {
                _testName = testName;
                _overallTimer = new Stopwatch();
                _currentIteration = -1;
                        }

            private bool DoneIterating
            {
                get
                {
                    if (_currentIteration == 0)
                        return false;

                    if (_currentIteration > BenchmarkConfiguration.MaxIteration)
                    {
                        IterationStopReason = "MaxIterations";
                        return true;
                    }

                    if (_currentIteration > 1 && _overallTimer.ElapsedMilliseconds > BenchmarkConfiguration.MaxTotalMilliseconds)
                    {
                        IterationStopReason = "MaxTime";
                        return true;
                    }

                    return false;
                }
            }

            protected override IEnumerable<BenchmarkIteration> Iterations
                    {
                get
                {
                    for (_currentIteration = 0; !DoneIterating; _currentIteration++)
                    {
                        _currentIterationMeasurementStarted = false;
                        _currentIterationMesaurementStopped = false;

                        yield return CreateIteration(_currentIteration);

                        if (_currentIterationMeasurementStarted)
                            StopMeasurement(_currentIteration);

                        if (_currentIteration == 0)
                            _overallTimer.Start();
                    }
                }
            }

            protected override void StartMeasurement(int iterationNumber)
                    {
                if (iterationNumber == _currentIteration)
                {
                    if (_currentIterationMeasurementStarted)
                        throw new InvalidOperationException("StartMeasurement already called for the current iteration");

                    _currentIterationMeasurementStarted = true;

                    GC.Collect(2, GCCollectionMode.Optimized);
                    GC.WaitForPendingFinalizers();

                    BenchmarkEventSource.Log.BenchmarkIterationStart(BenchmarkConfiguration.RunId, _testName, iterationNumber);
                    }
                }

            protected override void StopMeasurement(int iterationNumber)
            {
                if (iterationNumber == _currentIteration && !_currentIterationMesaurementStopped)
                {
                    Debug.Assert(_currentIterationMeasurementStarted);
                    _currentIterationMesaurementStopped = true;

                    // TODO: we should remove the "Success" parameter; this is already communicated elsewhere, and the information isn't
                    // easily available here.
                    BenchmarkEventSource.Log.BenchmarkIterationStop(BenchmarkConfiguration.RunId, _testName, iterationNumber, Success: true);
            }
            }
        }


        //
        // This duplicates the implementation of InvokeTestMethodAsync, but delegates to CallTestMethod to actually run the test.
        // This can be removed when we move to xunit 2.1 beta 4 or later.
        //
        protected async Task<decimal> InvokeTestMethodImplAsync(object testClassInstance)
        {
            var oldSyncContext = SynchronizationContext.Current;

            try
            {
                var asyncSyncContext = new AsyncTestSyncContext(oldSyncContext);
                SetSynchronizationContext(asyncSyncContext);

                await Aggregator.RunAsync(
                    () => Timer.AggregateAsync(
                        async () =>
                        {
                            var parameterCount = TestMethod.GetParameters().Length;
                            var valueCount = TestMethodArguments == null ? 0 : TestMethodArguments.Length;
                            if (parameterCount != valueCount)
                            {
                                Aggregator.Add(
                                    new InvalidOperationException(
                                        $"The test method expected {parameterCount} parameter value{(parameterCount == 1 ? "" : "s")}, but {valueCount} parameter value{(valueCount == 1 ? "" : "s")} {(valueCount == 1 ? "was" : "were")} provided."
                                    )
                                );
                            }
                            else
                            {
                                var result = CallTestMethod(testClassInstance);
                                var task = result as Task;
                                if (task != null)
                                    await task;
                                else
                                {
                                    var ex = await asyncSyncContext.WaitForCompletionAsync();
                                    if (ex != null)
                                        Aggregator.Add(ex);
                                }
                            }
                        }
                    )
                );
            }
            finally
            {
                SetSynchronizationContext(oldSyncContext);
            }

            return Timer.Total;
        }

        [SecuritySafeCritical]
        static void SetSynchronizationContext(SynchronizationContext context)
            => SynchronizationContext.SetSynchronizationContext(context);
    }
}
