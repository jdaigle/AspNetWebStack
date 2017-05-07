// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading.Tasks;
using System.Web.Routing;

namespace System.Web.Mvc.Async
{
    public interface ITaskAsyncController : IController
    {
        Task ExecuteAsync(RequestContext requestContext);
    }
}
