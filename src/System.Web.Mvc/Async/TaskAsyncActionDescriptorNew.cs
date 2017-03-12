// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Mvc.Properties;

namespace System.Web.Mvc.Async
{
    /// <summary>
    /// When an action method returns either Task or Task{T} the TaskAsyncActionDescriptor provides information about the action.
    /// </summary>
    public class TaskAsyncActionDescriptorNew : AsyncActionDescriptorNew, IMethodInfoActionDescriptor
    {
        /// <summary>
        /// dictionary to hold methods that can read Task{T}.Result
        /// </summary>
        private static readonly ConcurrentDictionary<Type, Func<object, object>> _taskValueExtractors = new ConcurrentDictionary<Type, Func<object, object>>();
        private readonly string _actionName;
        private readonly ControllerDescriptor _controllerDescriptor;
        private readonly Lazy<string> _uniqueId;
        private ParameterDescriptor[] _parametersCache;

        public TaskAsyncActionDescriptorNew(MethodInfo taskMethodInfo, string actionName, ControllerDescriptor controllerDescriptor)
            : this(taskMethodInfo, actionName, controllerDescriptor, validateMethod: true)
        {
        }

        internal TaskAsyncActionDescriptorNew(MethodInfo taskMethodInfo, string actionName, ControllerDescriptor controllerDescriptor, bool validateMethod)
        {
            if (taskMethodInfo == null)
            {
                throw new ArgumentNullException("taskMethodInfo");
            }
            if (String.IsNullOrEmpty(actionName))
            {
                throw Error.ParameterCannotBeNullOrEmpty("actionName");
            }
            if (controllerDescriptor == null)
            {
                throw new ArgumentNullException("controllerDescriptor");
            }

            if (validateMethod)
            {
                string taskFailedMessage = VerifyActionMethodIsCallable(taskMethodInfo);
                if (taskFailedMessage != null)
                {
                    throw new ArgumentException(taskFailedMessage, "taskMethodInfo");
                }
            }

            TaskMethodInfo = taskMethodInfo;
            _actionName = actionName;
            _controllerDescriptor = controllerDescriptor;
            _uniqueId = new Lazy<string>(CreateUniqueId);
        }

        public override string ActionName
        {
            get { return _actionName; }
        }

        public MethodInfo TaskMethodInfo { get; private set; }

        public override ControllerDescriptor ControllerDescriptor
        {
            get { return _controllerDescriptor; }
        }

        public MethodInfo MethodInfo
        {
            get { return TaskMethodInfo; }
        }

        public override string UniqueId
        {
            get { return _uniqueId.Value; }
        }

        private string CreateUniqueId()
        {
            var builder = new StringBuilder(base.UniqueId);
            DescriptorUtil.AppendUniqueId(builder, MethodInfo);
            return builder.ToString();
        }

        [SuppressMessage("Microsoft.Web.FxCop", "MW1201:DoNotCallProblematicMethodsOnTask", Justification = "This is commented in great detail.")]
        public override Task<object> ExecuteAsync(ControllerContext controllerContext, IDictionary<string, object> parameters)
        {
            if (controllerContext == null)
            {
                throw new ArgumentNullException("controllerContext");
            }
            if (parameters == null)
            {
                throw new ArgumentNullException("parameters");
            }

            ParameterInfo[] parameterInfos = TaskMethodInfo.GetParameters();
            object[] parametersArray = parameterInfos
                .Select(parameterInfo => ExtractParameterFromDictionary(parameterInfo, parameters, TaskMethodInfo))
                .ToArray();

            CancellationTokenSource tokenSource = null;
            bool disposedTimer = false;
            Timer taskCancelledTimer = null;
            bool taskCancelledTimerRequired = false;

            int timeout = GetAsyncManager(controllerContext.Controller).Timeout;

            for (int i = 0; i < parametersArray.Length; i++)
            {
                if (default(CancellationToken).Equals(parametersArray[i]))
                {
                    tokenSource = new CancellationTokenSource();
                    parametersArray[i] = tokenSource.Token;

                    // If there is a timeout we will create a timer to cancel the task when the
                    // timeout expires.
                    taskCancelledTimerRequired = timeout > Timeout.Infinite;
                    break;
                }
            }

            ActionMethodDispatcher dispatcher = DispatcherCache.GetDispatcher(TaskMethodInfo);

            if (taskCancelledTimerRequired)
            {
                taskCancelledTimer = new Timer(_ =>
                {
                    lock (tokenSource)
                    {
                        if (!disposedTimer)
                        {
                            tokenSource.Cancel();
                        }
                    }
                }, state: null, dueTime: timeout, period: Timeout.Infinite);
            }

            var taskUser = dispatcher.Execute(controllerContext.Controller, parametersArray) as Task;

            var taskValueExtractor = _taskValueExtractors.GetOrAdd(TaskMethodInfo.ReturnType, CreateTaskValueExtractor);

            // See: http://stackoverflow.com/a/15530170
            var tcs = new TaskCompletionSource<object>();
            taskUser.ContinueWith(t =>
            {
                try
                {
                    if (taskCancelledTimer != null)
                    {
                        // Timer callback may still fire after Dispose is called. 
                        taskCancelledTimer.Dispose();
                    }
                    if (tokenSource != null)
                    {
                        lock (tokenSource)
                        {
                            disposedTimer = true;
                            tokenSource.Dispose();
                            if (tokenSource.IsCancellationRequested)
                            {
                                // Give Timeout exceptions higher priority over other exceptions, mainly OperationCancelled exceptions
                                // that were signaled with out timeout token.
                                tcs.TrySetException(new TimeoutException());
                                return;
                            }
                        }
                    }

                    if (t.IsFaulted)
                    {
                        tcs.TrySetException(t.Exception.InnerExceptions);
                    }
                    else if (t.IsCanceled)
                    {
                        tcs.TrySetCanceled();
                    }
                    else
                    {
                        var result = taskValueExtractor(t);
                        tcs.TrySetResult(result);
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
            return tcs.Task;
        }

        public override object Execute(ControllerContext controllerContext, IDictionary<string, object> parameters)
        {
            string errorMessage = String.Format(CultureInfo.CurrentCulture, MvcResources.TaskAsyncActionDescriptor_CannotExecuteSynchronously,
                                                ActionName);

            throw new InvalidOperationException(errorMessage);
        }

        private static Func<object, object> CreateTaskValueExtractor(Type taskType)
        {
            // Task<T>?
            if (taskType.IsGenericType && taskType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                // lambda = arg => (object)(((Task<T>)arg).Result)
                var arg = Expression.Parameter(typeof(object));
                var castArg = Expression.Convert(arg, taskType);
                var fieldAccess = Expression.Property(castArg, nameof(Task<object>.Result));
                var castResult = Expression.Convert(fieldAccess, typeof(object));
                var lambda = Expression.Lambda<Func<object, object>>(castResult, arg);
                return lambda.Compile();
            }

            // Any exceptions should be thrown before getting the task value so just return null.
            return theTask =>
            {
                return null;
            };
        }

        public override object[] GetCustomAttributes(bool inherit)
        {
            return ActionDescriptorHelper.GetCustomAttributes(TaskMethodInfo, inherit);
        }

        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            return ActionDescriptorHelper.GetCustomAttributes(TaskMethodInfo, attributeType, inherit);
        }

        public override ParameterDescriptor[] GetParameters()
        {
            return ActionDescriptorHelper.GetParameters(this, TaskMethodInfo, ref _parametersCache);
        }

        public override ICollection<ActionSelector> GetSelectors()
        {
            return ActionDescriptorHelper.GetSelectors(TaskMethodInfo);
        }

        internal override ICollection<ActionNameSelector> GetNameSelectors()
        {
            return ActionDescriptorHelper.GetNameSelectors(TaskMethodInfo);
        }

        public override bool IsDefined(Type attributeType, bool inherit)
        {
            return ActionDescriptorHelper.IsDefined(TaskMethodInfo, attributeType, inherit);
        }

        public override IEnumerable<FilterAttribute> GetFilterAttributes(bool useCache)
        {
            if (useCache && GetType() == typeof(TaskAsyncActionDescriptorNew))
            {
                // Do not look at cache in types derived from this type because they might incorrectly implement GetCustomAttributes
                return ReflectedAttributeCache.GetMethodFilterAttributes(TaskMethodInfo);
            }
            return base.GetFilterAttributes(useCache);
        }
    }
}
