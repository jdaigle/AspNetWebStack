using System.Web.Mvc.Async.Benchmark;
using BenchmarkDotNet.Running;

namespace System.Web.Mvc
{
    class Program
    {
        static void Main(string[] args)
        {
            BenchmarkRunner.Run<AsyncControllerActionInvokerBenchmark>();
        }
    }
}