// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading;
using System.Threading.Tasks;

namespace System.Web.Mvc.Async
{
    // Derived from https://github.com/StephenCleary/AsyncEx/blob/edb2c6b66d41471008a56e4098f9670b5143617e/src/Nito.AsyncEx.Tasks/Interop/ApmAsyncFactory.cs
    // The MIT License (MIT) Copyright(c) 2014 StephenCleary
    // https://github.com/StephenCleary/AsyncEx/blob/edb2c6b66d41471008a56e4098f9670b5143617e/LICENSE

    public static class ApmWrapper
    {
        /// <summary>
        /// Wraps a <see cref="Task"/> into the Begin method of an APM pattern.
        /// </summary>
        /// <param name="task">The task to wrap.</param>
        /// <param name="callback">The callback method passed into the Begin method of the APM pattern.</param>
        /// <param name="state">The state passed into the Begin method of the APM pattern.</param>
        /// <returns>The asynchronous operation, to be returned by the Begin method of the APM pattern.</returns>
        public static IAsyncResult ToBegin(Task task, AsyncCallback callback, object state)
        {
            var tcs = new TaskCompletionSource<object>(state); // TODO: .NET 4.6, TaskCreationOptions.RunContinuationsAsynchronously);
            SynchronizationContext oldContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                CompleteAsync(task, callback, tcs);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(oldContext);
            }
            return tcs.Task;
        }

        // `async void` is on purpose, to raise `callback` exceptions directly on the thread pool.
        private static async void CompleteAsync(Task task, AsyncCallback callback, TaskCompletionSource<object> tcs)
        {
            try
            {
                await task.ConfigureAwait(false);
                tcs.TrySetResult(null);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
                // TODO: .NET 4.6 tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (callback != null)
                {
                    callback.Invoke(tcs.Task);
                }
            }
        }

        /// <summary>
        /// Wraps a <see cref="Task"/> into the End method of an APM pattern.
        /// </summary>
        /// <param name="asyncResult">The asynchronous operation returned by the matching Begin method of this APM pattern.</param>
        /// <returns>The result of the asynchronous operation, to be returned by the End method of the APM pattern.</returns>
        public static void ToEnd(IAsyncResult asyncResult)
        {
            // Wait an unwrap Exception
            ((Task)asyncResult).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Wraps a <see cref="Task{TResult}"/> into the Begin method of an APM pattern.
        /// </summary>
        /// <param name="task">The task to wrap. May not be <c>null</c>.</param>
        /// <param name="callback">The callback method passed into the Begin method of the APM pattern.</param>
        /// <param name="state">The state passed into the Begin method of the APM pattern.</param>
        /// <returns>The asynchronous operation, to be returned by the Begin method of the APM pattern.</returns>
        public static IAsyncResult ToBegin<TResult>(Task<TResult> task, AsyncCallback callback, object state)
        {
            var tcs = new TaskCompletionSource<object>(state); // TODO: .NET 4.6, TaskCreationOptions.RunContinuationsAsynchronously);
            SynchronizationContext oldContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                CompleteAsync(task, callback, tcs);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(oldContext);
            }
            return tcs.Task;
        }

        // `async void` is on purpose, to raise `callback` exceptions directly on the thread pool.
        private static async void CompleteAsync<TResult>(Task<TResult> task, AsyncCallback callback, TaskCompletionSource<TResult> tcs)
        {
            try
            {
                tcs.TrySetResult(await task.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
                // TODO: .NET 4.6 tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (callback != null)
                {
                    callback.Invoke(tcs.Task);
                }
            }
        }

        /// <summary>
        /// Wraps a <see cref="Task{TResult}"/> into the End method of an APM pattern.
        /// </summary>
        /// <param name="asyncResult">The asynchronous operation returned by the matching Begin method of this APM pattern.</param>
        /// <returns>The result of the asynchronous operation, to be returned by the End method of the APM pattern.</returns>
        public static TResult ToEnd<TResult>(IAsyncResult asyncResult)
        {
            // Wait an unwrap Exception
            return ((Task<TResult>)asyncResult).GetAwaiter().GetResult();
        }
    }
}
