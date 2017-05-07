// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Mvc.Async;
using System.Web.Mvc.Properties;
using System.Web.Routing;
using System.Web.SessionState;
using Microsoft.Web.Infrastructure.DynamicValidationHelper;

namespace System.Web.Mvc
{
    public class MvcHandler : HttpTaskAsyncHandler, IHttpAsyncHandler, IHttpHandler, IRequiresSessionState
    {
        private static readonly object _processRequestTag = new object();

        internal static readonly string MvcVersion = GetMvcVersionString();
        public static readonly string MvcVersionHeaderName = "X-AspNetMvc-Version";
        private ControllerBuilder _controllerBuilder;

        public MvcHandler(RequestContext requestContext)
        {
            if (requestContext == null)
            {
                throw new ArgumentNullException("requestContext");
            }

            RequestContext = requestContext;
        }

        internal ControllerBuilder ControllerBuilder
        {
            get
            {
                if (_controllerBuilder == null)
                {
                    _controllerBuilder = ControllerBuilder.Current;
                }
                return _controllerBuilder;
            }
            set { _controllerBuilder = value; }
        }

        public static bool DisableMvcResponseHeader { get; set; }

        public override bool IsReusable
        {
            get { return false; }
        }

        public RequestContext RequestContext { get; private set; }

        protected internal virtual void AddVersionHeader(HttpContextBase httpContext)
        {
            if (!DisableMvcResponseHeader)
            {
                httpContext.Response.AppendHeader(MvcVersionHeaderName, MvcVersion);
            }
        }

        public override Task ProcessRequestAsync(HttpContext httpContext)
        {
            HttpContextBase httpContextBase = new HttpContextWrapper(httpContext);
            return ProcessRequestAsync(httpContextBase);
        }

        public Task ProcessRequestAsync(HttpContextBase httpContext)
        {
            IController controller;
            IControllerFactory factory;
            ProcessRequestInit(httpContext, out controller, out factory);

            ITaskAsyncController taskAsyncController = controller as ITaskAsyncController;
            if (taskAsyncController != null)
            {
                // asynchronous Task controller
                return AwaitAsyncControllerExecuteAsync(taskAsyncController, factory);
            }

            IAsyncController asyncController = controller as IAsyncController;
            if (asyncController != null)
            {
                // legacy asynchronous APM controller
                return AwaitAsyncControllerExecuteAsync(asyncController, factory);
            }
            else
            {
                // synchronous controller
                try
                {
                    controller.Execute(RequestContext);
                }
                finally
                {
                    factory.ReleaseController(controller);
                }
                return TaskEx.Completed;
            }
        }

        private async Task AwaitAsyncControllerExecuteAsync(ITaskAsyncController controller, IControllerFactory factory)
        {
            try
            {
                await controller.ExecuteAsync(RequestContext).ConfigureAwait(continueOnCapturedContext: true);
            }
            finally
            {
                factory.ReleaseController(controller);
            }
        }

        private async Task AwaitAsyncControllerExecuteAsync(IAsyncController controller, IControllerFactory factory)
        {
            try
            {
                await Task.Factory.FromAsync(controller.BeginExecute, controller.EndExecute, RequestContext, null).ConfigureAwait(continueOnCapturedContext: true);
            }
            finally
            {
                factory.ReleaseController(controller);
            }
        }

        private static string GetMvcVersionString()
        {
            // DevDiv 216459:
            // This code originally used Assembly.GetName(), but that requires FileIOPermission, which isn't granted in
            // medium trust. However, Assembly.FullName *is* accessible in medium trust.
            return new AssemblyName(typeof(MvcHandler).Assembly.FullName).Version.ToString(2);
        }

        public override void ProcessRequest(HttpContext httpContext)
        {
            HttpContextBase httpContextBase = new HttpContextWrapper(httpContext);
            ProcessRequest(httpContextBase);
        }

        protected internal virtual void ProcessRequest(HttpContextBase httpContext)
        {
            IController controller;
            IControllerFactory factory;
            ProcessRequestInit(httpContext, out controller, out factory);

            try
            {
                controller.Execute(RequestContext);
            }
            finally
            {
                factory.ReleaseController(controller);
            }
        }

        private void ProcessRequestInit(HttpContextBase httpContext, out IController controller, out IControllerFactory factory)
        {
            // If request validation has already been enabled, make it lazy. This allows attributes like [HttpPost] (which looks
            // at Request.Form) to work correctly without triggering full validation.
            // Tolerate null HttpContext for testing.
            HttpContext currentContext = HttpContext.Current;
            if (currentContext != null)
            {
                bool? isRequestValidationEnabled = ValidationUtility.IsValidationEnabled(currentContext);
                if (isRequestValidationEnabled == true)
                {
                    ValidationUtility.EnableDynamicValidation(currentContext);
                }
            }

            AddVersionHeader(httpContext);
            RemoveOptionalRoutingParameters();

            // Get the controller type
            string controllerName = RequestContext.RouteData.GetRequiredString("controller");

            // Instantiate the controller and call Execute
            factory = ControllerBuilder.GetControllerFactory();
            controller = factory.CreateController(RequestContext, controllerName);
            if (controller == null)
            {
                throw new InvalidOperationException(
                    String.Format(
                        CultureInfo.CurrentCulture,
                        MvcResources.ControllerBuilder_FactoryReturnedNull,
                        factory.GetType(),
                        controllerName));
            }
        }

        private void RemoveOptionalRoutingParameters()
        {
            RouteValueDictionary rvd = RequestContext.RouteData.Values;

            // Ensure delegate is stateless
            rvd.RemoveFromDictionary((entry) => entry.Value == UrlParameter.Optional);
        }

        #region IHttpHandler Members

        bool IHttpHandler.IsReusable
        {
            get { return IsReusable; }
        }

        void IHttpHandler.ProcessRequest(HttpContext httpContext)
        {
            ProcessRequest(httpContext);
        }

        #endregion
    }
}
