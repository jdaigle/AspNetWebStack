using System.Threading.Tasks;

namespace System.Web.Mvc.Async
{
    public static class TaskEx
    {
        public static readonly Task Completed = Task.FromResult(0);
    }
}
