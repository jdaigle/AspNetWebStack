using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc;
using System.Web.Mvc.Async;
using System.Web.Mvc.Filters;
using System.Web.Mvc.Routing;

namespace System.Web.Mvc.Async
{
    public class AsyncControllerActionInvokerNew : ControllerActionInvoker, IAsyncActionInvoker
    {
        public Task<bool> InvokeActionAsync(ControllerContext controllerContext, string actionName)
        {
            if (controllerContext == null)
            {
                throw new ArgumentNullException("controllerContext");
            }

            Contract.Assert(controllerContext.RouteData != null);
            if (String.IsNullOrEmpty(actionName) && !controllerContext.RouteData.HasDirectRouteMatch())
            {
                throw Error.ParameterCannotBeNullOrEmpty("actionName");
            }

            ControllerDescriptor controllerDescriptor = GetControllerDescriptor(controllerContext);
            ActionDescriptor actionDescriptor = FindAction(controllerContext, controllerDescriptor, actionName);
            if (actionDescriptor == null)
            {
                return Task.FromResult(false);
            }

            FilterInfo filterInfo = GetFilters(controllerContext, actionDescriptor);

            try
            {
                AuthenticationContext authenticationContext
                    = InvokeAuthenticationFilters(controllerContext, filterInfo.AuthenticationFilters, actionDescriptor);
                if (authenticationContext.Result != null)
                {
                    // An authentication filter signaled that we should short-circuit the request. Let all
                    // authentication filters contribute to an action result (to combine authentication
                    // challenges). Then, run this action result.
                    AuthenticationChallengeContext challengeContext =
                        InvokeAuthenticationFiltersChallenge(controllerContext, filterInfo.AuthenticationFilters, actionDescriptor, authenticationContext.Result);

                    InvokeActionResult(controllerContext, challengeContext.Result ?? authenticationContext.Result);
                    return Task.FromResult(true);
                }

                AuthorizationContext authorizationContext
                    = InvokeAuthorizationFilters(controllerContext, filterInfo.AuthorizationFilters, actionDescriptor);
                if (authorizationContext.Result != null)
                {
                    // An authorization filter signaled that we should short-circuit the request. Let all
                    // authentication filters contribute to an action result (to combine authentication
                    // challenges). Then, run this action result.
                    AuthenticationChallengeContext challengeContext =
                        InvokeAuthenticationFiltersChallenge(controllerContext,
                        filterInfo.AuthenticationFilters, actionDescriptor, authorizationContext.Result);
                    InvokeActionResult(controllerContext, challengeContext.Result ?? authorizationContext.Result);
                    return Task.FromResult(true);
                }

                if (controllerContext.Controller.ValidateRequest)
                {
                    ValidateRequest(controllerContext);
                }

                IDictionary<string, object> parameters = GetParameterValues(controllerContext, actionDescriptor);
                return InvokeInvokeActionMethodWithFiltersAsync(controllerContext, filterInfo, actionDescriptor, parameters);
            }
            catch (ThreadAbortException)
            {
                // This type of exception occurs as a result of Response.Redirect(), but we special-case so that
                // the filters don't see this as an error.
                throw;
            }
            catch (Exception ex)
            {
                // something blew up, so execute the exception filters
                ExceptionContext exceptionContext = InvokeExceptionFilters(controllerContext, filterInfo.ExceptionFilters, ex);
                if (!exceptionContext.ExceptionHandled)
                {
                    throw;
                }
                InvokeActionResult(controllerContext, exceptionContext.Result);
            }

            return Task.FromResult(true);
        }

        protected override ControllerDescriptor GetControllerDescriptor(ControllerContext controllerContext)
        {
            // Frequently called, so ensure delegate is static
            Type controllerType = controllerContext.Controller.GetType();
            ControllerDescriptor controllerDescriptor = DescriptorCache.GetDescriptor(
                controllerType: controllerType,
                creator: ReflectedAsyncControllerDescriptor.DefaultDescriptorFactory,
                state: controllerType);
            return controllerDescriptor;
        }

        public async Task<bool> InvokeInvokeActionMethodWithFiltersAsync(ControllerContext controllerContext, FilterInfo filterInfo, ActionDescriptor actionDescriptor, IDictionary<string, object> parameters)
        {
            try
            {
                var postActionContext = await InvokeActionMethodWithFiltersAsync(controllerContext, filterInfo.ActionFilters, actionDescriptor, parameters).ConfigureAwait(false);

                // The action succeeded. Let all authentication filters contribute to an action
                // result (to combine authentication challenges; some authentication filters need
                // to do negotiation even on a successful result). Then, run this action result.
                AuthenticationChallengeContext postChallengeContext
                    = InvokeAuthenticationFiltersChallenge(controllerContext, filterInfo.AuthenticationFilters, actionDescriptor, postActionContext.Result);

                InvokeActionResultWithFilters(controllerContext, filterInfo.ResultFilters, postChallengeContext.Result ?? postActionContext.Result);
            }
            catch (ThreadAbortException)
            {
                // This type of exception occurs as a result of Response.Redirect(), but we special-case so that
                // the filters don't see this as an error.
                throw;
            }
            catch (Exception ex)
            {
                // something blew up, so execute the exception filters
                ExceptionContext exceptionContext = InvokeExceptionFilters(controllerContext, filterInfo.ExceptionFilters, ex);
                if (!exceptionContext.ExceptionHandled)
                {
                    throw;
                }
                InvokeActionResult(controllerContext, exceptionContext.Result);
            }

            return true;
        }


        public Task<ActionExecutedContext> InvokeActionMethodWithFiltersAsync(ControllerContext controllerContext, IList<IActionFilter> filters, ActionDescriptor actionDescriptor, IDictionary<string, object> parameters)
        {
            AsyncInvocationWithFilters invocation = new AsyncInvocationWithFilters(this, controllerContext, actionDescriptor, filters, parameters);

            const int StartingFilterIndex = 0;
            return invocation.InvokeActionMethodFilterAsynchronouslyRecursive(StartingFilterIndex);
        }

        public virtual Task<ActionResult> InvokeActionMethodAsync(ControllerContext controllerContext, ActionDescriptor actionDescriptor, IDictionary<string, object> parameters)
        {
            AsyncActionDescriptor asyncActionDescriptor = actionDescriptor as AsyncActionDescriptor;
            if (asyncActionDescriptor != null)
            {
                return InvokeAsynchronousActionMethod(controllerContext, asyncActionDescriptor, parameters);
            }
            else
            {
                return Task.FromResult(base.InvokeActionMethod(controllerContext, actionDescriptor, parameters));
            }
        }

        private async Task<ActionResult> InvokeAsynchronousActionMethod(ControllerContext controllerContext, AsyncActionDescriptor actionDescriptor, IDictionary<string, object> parameters)
        {
            object returnValue = await Task.Factory.FromAsync(actionDescriptor.BeginExecute, actionDescriptor.EndExecute, controllerContext, parameters, null).ConfigureAwait(false);
            ActionResult result = base.CreateActionResult(controllerContext, actionDescriptor, returnValue);
            return result;
        }

        // Large and passed to many function calls, so keep as a reference type to minimize copying
        private class AsyncInvocationWithFilters
        {
            private readonly AsyncControllerActionInvokerNew _invoker;
            private readonly ControllerContext _controllerContext;
            private readonly ActionDescriptor _actionDescriptor;
            private readonly IList<IActionFilter> _filters;
            private readonly IDictionary<string, object> _parameters;
            private readonly int _filterCount;
            private readonly ActionExecutingContext _preContext;

            internal AsyncInvocationWithFilters(AsyncControllerActionInvokerNew invoker, ControllerContext controllerContext, ActionDescriptor actionDescriptor, IList<IActionFilter> filters, IDictionary<string, object> parameters)
            {
                Contract.Assert(invoker != null);
                Contract.Assert(controllerContext != null);
                Contract.Assert(actionDescriptor != null);
                Contract.Assert(filters != null);
                Contract.Assert(parameters != null);

                _invoker = invoker;
                _controllerContext = controllerContext;
                _actionDescriptor = actionDescriptor;
                _filters = filters;
                _parameters = parameters;

                _preContext = new ActionExecutingContext(controllerContext, actionDescriptor, parameters);
                // For IList<T> it is faster to cache the count
                _filterCount = _filters.Count;
            }

            internal async Task<ActionExecutedContext> InvokeActionMethodFilterAsynchronouslyRecursive(int filterIndex)
            {
                // Performance-sensitive

                // For compatability, the following behavior must be maintained
                //   The OnActionExecuting events must fire in forward order
                //   The Begin and End events must fire
                //   The OnActionExecuted events must fire in reverse order
                //   Earlier filters can process the results and exceptions from the handling of later filters
                // This is achieved by calling recursively and moving through the filter list forwards

                // If there are no more filters to recurse over, create the main result
                if (filterIndex > _filterCount - 1)
                {
                    var result = await _invoker.InvokeActionMethodAsync(_controllerContext, _actionDescriptor, _parameters);
                    return new ActionExecutedContext(_controllerContext, _actionDescriptor, canceled: false, exception: null)
                    {
                        Result = result,
                    };
                }

                // Otherwise process the filters recursively
                IActionFilter filter = _filters[filterIndex];
                ActionExecutingContext preContext = _preContext;
                filter.OnActionExecuting(preContext);
                if (preContext.Result != null)
                {
                    return new ActionExecutedContext(preContext, preContext.ActionDescriptor, canceled: true, exception: null)
                    {
                        Result = preContext.Result
                    };
                }

                // Use the filters in forward direction
                int nextFilterIndex = filterIndex + 1;

                ActionExecutedContext postContext;
                bool wasError = true;

                try
                {
                    postContext = await InvokeActionMethodFilterAsynchronouslyRecursive(nextFilterIndex);
                    wasError = false;
                }
                catch (ThreadAbortException)
                {
                    // This type of exception occurs as a result of Response.Redirect(), but we special-case so that
                    // the filters don't see this as an error.
                    postContext = new ActionExecutedContext(preContext, preContext.ActionDescriptor, canceled: false, exception: null);
                    filter.OnActionExecuted(postContext);
                    throw;
                }
                catch (Exception ex)
                {
                    postContext = new ActionExecutedContext(preContext, preContext.ActionDescriptor, canceled: false, exception: ex);
                    filter.OnActionExecuted(postContext);
                    if (!postContext.ExceptionHandled)
                    {
                        throw;
                    }
                }
                if (!wasError)
                {
                    filter.OnActionExecuted(postContext);
                }

                return postContext;
            }
        }

        IAsyncResult IAsyncActionInvoker.BeginInvokeAction(ControllerContext controllerContext, string actionName, AsyncCallback callback, object state)
        {
            var task = InvokeActionAsync(controllerContext, actionName);

            var result = new TaskWrapperAsyncResult(task, state);

            // if user supplied a callback, invoke that when their task has finished running. 
            if (callback != null)
            {
                if (task.IsCompleted)
                {
                    // If the underlying task is already finished, from our caller's perspective this is just
                    // a synchronous completion.
                    result.CompletedSynchronously = true;
                    callback(result);
                }
                else
                {
                    // If the underlying task isn't yet finished, from our caller's perspective this will be
                    // an asynchronous completion. We'll use ContinueWith instead of Finally for two reasons:
                    //
                    // - Finally propagates the antecedent Task's exception, which we don't need to do here.
                    //   Out caller will eventually call EndExecute, which correctly observes the
                    //   antecedent Task's exception anyway if it faulted.
                    //
                    // - Finally invokes the callback on the captured SynchronizationContext, which is
                    //   unnecessary when using APM (Begin / End). APM assumes that the callback is invoked
                    //   on an arbitrary ThreadPool thread with no SynchronizationContext set up, so
                    //   ContinueWith gets us closer to the desired semantic.
                    result.CompletedSynchronously = false;
                    task.ContinueWith(_ =>
                    {
                        callback(result);
                    });
                }
            }

            return result;
        }

        bool IAsyncActionInvoker.EndInvokeAction(IAsyncResult asyncResult)
        {
            var wrapperResult = (TaskWrapperAsyncResult)asyncResult;

            // Throw an exception with the correct call stack
            try
            {
                wrapperResult.Task.ThrowIfFaulted();
            }
            finally
            {
                wrapperResult.CleanupThunk?.Invoke();
            }

            return ((Task<bool>)wrapperResult.Task).Result;
        }
    }
}
