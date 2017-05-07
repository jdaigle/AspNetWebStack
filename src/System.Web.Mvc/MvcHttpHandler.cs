// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
using System.Web.Mvc.Async;
using System.Web.Routing;
using System.Web.SessionState;

namespace System.Web.Mvc
{
    public class MvcHttpHandler : UrlRoutingHandler, IHttpAsyncHandler, IRequiresSessionState
    {
        private static readonly object _processRequestTag = new object();

        protected virtual Task ProcessRequestAsync(HttpContext httpContext)
        {
            if (httpContext == null)
            {
                throw new ArgumentNullException("httpContext");
            }

            HttpContextBase httpContextBase = new HttpContextWrapper(httpContext);
            return ProcessRequestAsync(httpContextBase);
        }

        protected virtual Task ProcessRequestAsync(HttpContextBase httpContext)
        {
            if (httpContext == null)
            {
                throw new ArgumentNullException("httpContext");
            }

            IHttpHandler httpHandler = GetHttpHandler(httpContext);

            MvcHandler mvcHandler = httpHandler as MvcHandler;
            if (mvcHandler != null)
            {
                return mvcHandler.ProcessRequestAsync(httpContext);
            }

            HttpTaskAsyncHandler httpTaskAsyncHander = httpHandler as HttpTaskAsyncHandler;
            if (httpTaskAsyncHander != null)
            {
                return httpTaskAsyncHander.ProcessRequestAsync(HttpContext.Current);
            }

            IHttpAsyncHandler httpAsyncHandler = httpHandler as IHttpAsyncHandler;
            if (httpAsyncHandler != null)
            {
                // asynchronous handler
                return Task.Factory.FromAsync(httpAsyncHandler.BeginProcessRequest, httpAsyncHandler.EndProcessRequest, HttpContext.Current, null);
            }
            else
            {
                // synchronous handler
                httpHandler.ProcessRequest(HttpContext.Current);
                return TaskEx.Completed;
            }
        }

        private static IHttpHandler GetHttpHandler(HttpContextBase httpContext)
        {
            DummyHttpHandler dummyHandler = new DummyHttpHandler();
            dummyHandler.PublicProcessRequest(httpContext);
            return dummyHandler.HttpHandler;
        }

        // synchronous code
        protected override void VerifyAndProcessRequest(IHttpHandler httpHandler, HttpContextBase httpContext)
        {
            if (httpHandler == null)
            {
                throw new ArgumentNullException("httpHandler");
            }

            httpHandler.ProcessRequest(HttpContext.Current);
        }

        #region IHttpAsyncHandler Members

        IAsyncResult IHttpAsyncHandler.BeginProcessRequest(HttpContext context, AsyncCallback cb, object extraData)
        {
            return ApmWrapper.ToBegin(ProcessRequestAsync(context), cb, extraData);
        }

        void IHttpAsyncHandler.EndProcessRequest(IAsyncResult result)
        {
            ApmWrapper.ToEnd(result);
        }

        #endregion

        // Since UrlRoutingHandler.ProcessRequest() does the heavy lifting of looking at the RouteCollection for
        // a matching route, we need to call into it. However, that method is also responsible for kicking off
        // the synchronous request, and we can't allow it to do that. The purpose of this dummy class is to run
        // only the lookup portion of UrlRoutingHandler.ProcessRequest(), then intercept the handler it returns
        // and execute it asynchronously.

        private sealed class DummyHttpHandler : UrlRoutingHandler
        {
            public IHttpHandler HttpHandler;

            public void PublicProcessRequest(HttpContextBase httpContext)
            {
                ProcessRequest(httpContext);
            }

            protected override void VerifyAndProcessRequest(IHttpHandler httpHandler, HttpContextBase httpContext)
            {
                // don't process the request, just store a reference to it
                HttpHandler = httpHandler;
            }
        }
    }
}
