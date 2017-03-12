// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Web.Mvc.Properties;

namespace System.Web.Mvc.Async
{
    public abstract class AsyncActionDescriptorNew : AsyncActionDescriptor
    {
        public abstract Task<object> ExecuteAsync(ControllerContext controllerContext, IDictionary<string, object> parameters);

        public override IAsyncResult BeginExecute(ControllerContext controllerContext, IDictionary<string, object> parameters, AsyncCallback callback, object state)
        {
            // TODO: deprecate
            var task = ExecuteAsync(controllerContext, parameters);
            var tcs = new TaskCompletionSource<object>(state);
            //TODO .NET 4.5.2 support
            //var tcs = new TaskCompletionSource<bool>(state, TaskCreationOptions.RunContinuationsAsynchronously);
            SynchronizationContextSwitcher.NoContext(() => CompleteAsync(task, callback, tcs));
            return tcs.Task;
        }

        // `async void` is on purpose, to raise `callback` exceptions directly on the thread pool.
        private static async void CompleteAsync(Task<object> task, AsyncCallback callback, TaskCompletionSource<object> tcs)
        {
            try
            {
                tcs.TrySetResult(await task.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
                // TODO: .NET 4.5.2 support
                //tcs.TrySetCanceled(ex.CancellationToken);
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

        public override object EndExecute(IAsyncResult asyncResult)
        {
            // TODO: deprecate
            // Wait and Unwrap any Exceptions
            return ((Task<object>)asyncResult).GetAwaiter().GetResult();
        }
    }
}
