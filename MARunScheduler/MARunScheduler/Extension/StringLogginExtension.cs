using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Granfeldt
{
    public static class TraceExtension
    {
        public static void LogMessage(this object e, string message)
        {
            Trace.WriteLine($"{DateTime.Now} MARunScheduler: {message}");
        }
    }
}
