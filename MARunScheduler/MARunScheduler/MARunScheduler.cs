// Version History
// December 6, 2011 | Soren Granfeldt
//  - initial version
// January 12, 2012 | Soren Granfeldt
//  - fixed bug with PostProcessing must be present before PreProcessing was run
//  - changed UseShellExecute from false to true
//  - fixed error on RunCommand to properly dispose process
//  - added Pre- and PostprocessingArguments for running commands with parameters
// February 17, 2012 | Soren Granfeldt
//  - added .Replace("*MA_RUN_RESULT*", result) to arguments
// July 13, 2012 | Soren Granfeldt
//  - added support for OnlyRunIfPendingExports
//  - fixed logging bug where thread didn't use LogThread
//  - changed AgeInDays to AgeInMinutes
// July 13, 2012 | Soren Granfeldt
//	- added support for time intervals for threads and items
// September 23, 2012 | Soren Granfeldt
//  - added support for day limits for threads and items
//  - fixed bug for clears runs where time restrictions were not enforced
// September 23, 2012 | Soren Granfeldt
//  - added options for thresholds for imports and exports
//  - added logging about import adds/updates/deletes
//  - added writes to eventlog for threshold warnings
// November 5, 2012 | Soren Granfeldt
//  - added try/catch on eventlog logging if unable to create 
//    eventlog source
    
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using System.Globalization;

namespace Granfeldt
{

    public class MARunSchedulerProgram
    {
        const int ERROR_UNSPECIFIEDERROR = 1000;
        const int ERROR_COULDNOTCONNECTOMANAGEMENTAGENT = 1001;
        const int ERROR_CONFIGURATIONFILENOTFOUND = 1002;
        const int WARNING_THRESHOLDMET = 2001;

        const string EventLogSource = "MARunScheduler";
        const string EventLogName = "Application";
        const string FIMWMINamespace = @"root\MicrosoftIdentityIntegrationServer";
        public static MARunScheduler configuration = new MARunScheduler();
        private static Mutex loggingMutex = new Mutex();

        #region Logging

        static void LogThread(string threadName, string s)
        {
            Log(string.Format("({0}) {1}", threadName, s));
        }
        static void Log(string s)
        {
            if (configuration.EnableLogging)
            {
                // wait until it is safe to enter and write to log file
                loggingMutex.WaitOne();
                string logLine = null;
                if (configuration != null)
                {
                    string logFilename = configuration.LogFile == null ? "MARunScheduler.log" : string.Format(configuration.LogFile, DateTime.Now);
                    using (StreamWriter l = new StreamWriter(logFilename, true))
                    {
                        logLine = string.Format("{0:G}: {1}", DateTime.Now, s);
                        l.WriteLine(logLine);
                        if (configuration.Console)
                        {
                            Console.WriteLine(logLine);
                        }
                    }
                }
                System.Diagnostics.Trace.WriteLine(logLine);
                // release the mutex
                loggingMutex.ReleaseMutex();
            }
        }
        static void Log(string warningMessage, int errorNumber)
        {
            WriteEvent(warningMessage, EventLogEntryType.Warning, errorNumber);
            Log(string.Format("Warning: {0}", warningMessage));
        }
        static void Log(Exception ex, int errorNumber)
        {
            WriteEvent(ex.Message, EventLogEntryType.Error, errorNumber);
            Log(string.Format("Error: {0}", ex.Message));
        }
        static void WriteEvent(string message, EventLogEntryType type, int eventID)
        {
            try
            {
                if (!EventLog.SourceExists(EventLogSource))
                {
                    EventLog.CreateEventSource(EventLogSource, EventLogName);
                }
                EventLog.WriteEntry(EventLogSource, message, EventLogEntryType.Warning, eventID);
            }
            catch
            {
                // we may get here if we don't have permission to create
                // an event log source
            }
        }

        #endregion

        public static int ExecuteCommand(string threadName, string command, string arguments, string result)
        {
            ProcessStartInfo processStartInfo = new ProcessStartInfo();

            processStartInfo.WorkingDirectory = Directory.GetCurrentDirectory();
            processStartInfo.LoadUserProfile = true;
            processStartInfo.UseShellExecute = true;
            processStartInfo.CreateNoWindow = true;
            processStartInfo.ErrorDialog = false;
            if (!string.IsNullOrEmpty(arguments))
            {
                processStartInfo.Arguments = arguments.Replace("*MA_RUN_RESULT*", result);
            }
            processStartInfo.FileName = command.Replace("*MA_RUN_RESULT*", result);

            Process executeProcess = new Process();
            executeProcess.StartInfo = processStartInfo;
            try
            {
                LogThread(threadName, string.Format("Running command '{0}'", command));
                executeProcess.Start();
            }
            catch (Exception e)
            {
                LogThread(threadName, string.Format("Error running command: " + e.Message));
                return 0;
            }

            executeProcess.WaitForExit();

            if (executeProcess.ExitCode == 0)
            {
                executeProcess.Dispose();
                return 0;
            }
            else
            {
                LogThread(threadName, string.Format("Command exited with code: {0}", executeProcess.ExitCode));
                int returnCode = executeProcess.ExitCode;
                executeProcess.Dispose();
                return returnCode;
            }
        }
        public static bool IsThresholdMet(string thresholdName, string threadName, long total, string limit, long actual)
        {
            bool returnValue = false;
            if (string.IsNullOrEmpty(limit))
            {
                return false;
            }
            if (Regex.IsMatch(limit, @"^(\d+|\d+%)$"))
            {
                decimal threshold = 0;
                threshold = decimal.Parse(limit.TrimEnd('%').Trim());
                if (Regex.IsMatch(limit, @"^\d+%$"))
                {
                    LogThread(threadName, string.Format("'{0}' threshold is {1:n0}%, {2:n0}% pending", thresholdName, threshold, actual == 0 ? 0 : (decimal)(actual * 100 / total)));
                    returnValue = actual == 0 ? false : ((decimal)(actual * 100 / total) >= threshold);
                }
                else
                {
                    // we have a plain value
                    LogThread(threadName, string.Format("'{0}' threshold is {1:n0} object(s), {2:n0} pending object(s)", thresholdName, threshold, actual));
                    returnValue = (actual >= threshold);
                }
            }
            else
            {
                LogThread(threadName, "Invalid threshold value specified");
                return false;
            }
            if (returnValue)
            {
                Log(string.Format("'{0}' threshold is met for thread '{1}'", thresholdName, threadName), WARNING_THRESHOLDMET);
            }
            return returnValue;
        }
        public static bool RunToday(string AllowedDays)
        {
            if (string.IsNullOrEmpty(AllowedDays))
                return true;

            string today = DateTime.Now.ToString("ddd", new CultureInfo("en-US")).ToUpper();
            string[] days = AllowedDays.ToUpper().Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            return days.Where(d => d.StartsWith(today)).Count() > 0;
        }
        public static void Main(string[] args)
        {
            try
            {
                string serverName = System.Environment.MachineName;
                string configurationFilename = null;
                foreach (string arg in args.ToList<string>())
                {
                    if (arg.StartsWith("/s:", StringComparison.InvariantCultureIgnoreCase))
                    {
                        serverName = Regex.Replace(arg, @"^/s\:", "", RegexOptions.IgnoreCase).ToUpper();
                    }
                    if (arg.StartsWith("/f:", StringComparison.InvariantCultureIgnoreCase))
                    {
                        configurationFilename = Regex.Replace(arg, @"^/f\:", "", RegexOptions.IgnoreCase);
                    }
                }

                #region Syntax information

                if (string.IsNullOrEmpty(configurationFilename))
                {
                    Console.WriteLine("Granfeldt FIM/ILM Management Agent Run Profile Scheduler");
                    Console.WriteLine("Copyright (c) 2011-2012 Soren Granfeldt. All rights reserved.");
                    Console.WriteLine();

                    Console.WriteLine("Description: Uses an XML input file to run management agents in a");
                    Console.WriteLine("specified sequence.  Functionality is based on Microsoft Identity");
                    Console.WriteLine("Integration Server MASequencer Utility.");
                    Console.WriteLine();
                    Console.WriteLine("Syntax: MARunScheduler /f:filename");
                    Console.WriteLine();
                    Console.WriteLine("Parameters:");
                    Console.WriteLine();
                    Console.WriteLine("Value                           Description");
                    Console.WriteLine();
                    Console.WriteLine("/f:     filename                Specifies the XML input file that contain");
                    Console.WriteLine("                                the configuration details for running the");
                    Console.WriteLine("                                management agents.");
                    Console.WriteLine();
                    Console.WriteLine("Example:");
                    Console.WriteLine();
                    Console.WriteLine("To run MASequencer on the server {0} using the input file", serverName);
                    Console.WriteLine("MARunScheduler.xml, use the following command:");
                    Console.WriteLine();
                    Console.WriteLine("MARunScheduler.exe /f:MARunScheduler.xml");
                    Console.WriteLine();
                    Console.WriteLine("For more information on parameters, see MARunScheduler.xml.");
                    return;
                }

                #endregion

                configuration = MARunScheduler.Deserialize(Path.GetFullPath(configurationFilename));
                Log("Started");

                Log(string.Format("Running against server '{0}'", serverName));

                ManagementScope WMInamespace;
                ConnectionOptions connOpt = new ConnectionOptions();
                connOpt.Authentication = AuthenticationLevel.PacketPrivacy;
                WMInamespace = new ManagementScope(String.Format(@"\\{0}\{1}", serverName, FIMWMINamespace), connOpt);

                Action<object> action = (object runThread) =>
                {
                    RunThread thread = (RunThread)runThread;
                    int repeatCount = thread.RepeatCount - 1;

                    DateTime defaultThreadRunAfter = DateTime.MinValue;
                    DateTime defaultThreadRunBefore = DateTime.MaxValue.AddSeconds(-1);
                    DateTime threadRunBefore;
                    DateTime threadRunAfter;
                    if (!DateTime.TryParse(thread.RunBefore, out threadRunBefore))
                    {
                        threadRunBefore = defaultThreadRunBefore;
                    }
                    if (!DateTime.TryParse(thread.RunAfter, out threadRunAfter))
                    {
                        threadRunAfter = defaultThreadRunAfter;
                    }

                    while (thread.LoopIndefinitely || thread.RepeatCount > 0)
                    {
                        LogThread(thread.Name, string.Format("It's now {0}", DateTime.Now.TimeOfDay));
                        LogThread(thread.Name, string.Format("Thread should run before {0}", threadRunBefore.TimeOfDay));
                        LogThread(thread.Name, string.Format("Thread should after {0}", threadRunAfter.TimeOfDay));
                        LogThread(thread.Name, string.Format("Thread should on {0}", string.IsNullOrEmpty(thread.RunOnDays) ? "all days" : thread.RunOnDays));
                        if (DateTime.Now.TimeOfDay > threadRunAfter.TimeOfDay && DateTime.Now.TimeOfDay < threadRunBefore.TimeOfDay && RunToday(thread.RunOnDays))
                        {
                            LogThread(thread.Name, string.Format("Starting cycle #{0}", (repeatCount - thread.RepeatCount) + 1));

                            DateTime defaultItemRunAfter = DateTime.MinValue;
                            DateTime defaultItemRunBefore = DateTime.MaxValue.AddSeconds(-1);
                            foreach (RunItem ri in thread.Item)
                            {
                                DateTime runItemRunBefore;
                                DateTime runItemRunAfter;
                                if (!DateTime.TryParse(ri.RunBefore, out runItemRunBefore))
                                {
                                    runItemRunBefore = defaultItemRunBefore;
                                }
                                if (!DateTime.TryParse(ri.RunAfter, out runItemRunAfter))
                                {
                                    runItemRunAfter = defaultItemRunAfter;
                                }

                                LogThread(thread.Name, string.Format("Run Profile item should run before {0}", runItemRunBefore.TimeOfDay));
                                LogThread(thread.Name, string.Format("Run Profile item should after {0}", runItemRunAfter.TimeOfDay));
                                LogThread(thread.Name, string.Format("Run Profile item should on {0}", string.IsNullOrEmpty(ri.RunOnDays) ? "all days" : ri.RunOnDays));
                                if ((DateTime.Now.TimeOfDay > runItemRunAfter.TimeOfDay && DateTime.Now.TimeOfDay < runItemRunBefore.TimeOfDay && RunToday(ri.RunOnDays)))
                                {
                                    #region Pre processing

                                    if (!(string.IsNullOrEmpty(ri.Preprocessing)))
                                    {
                                        ExecuteCommand(thread.Name, ri.Preprocessing, ri.PreprocessingArguments, "");
                                    }

                                    #endregion

                                    #region Run profile

                                    string managementAgentQueryString = String.Format("SELECT * FROM MIIS_ManagementAgent WHERE Name='{0}'", ri.MA);

                                    ObjectQuery managementAgentQuery = new ObjectQuery(managementAgentQueryString);
                                    ManagementObjectSearcher MASearcher = new ManagementObjectSearcher(WMInamespace, managementAgentQuery);
                                    ManagementObjectCollection MAObjects = MASearcher.Get();
                                    string result = "management-agent-not-found";
                                    if (MAObjects.Count == 1)
                                    {
                                        ManagementObjectCollection.ManagementObjectEnumerator Enum = MAObjects.GetEnumerator();
                                        Enum.MoveNext();
                                        ManagementObject MAObject = (ManagementObject)Enum.Current;

                                        LogThread(thread.Name, string.Format("Connected to MA '{0}' (Type: {1}, GUID: {2})", MAObject["name"].ToString(), MAObject["type"].ToString(), MAObject["Guid"].ToString()));

                                        List<string> param = new List<string>();
                                        if (!string.IsNullOrEmpty(ri.RunProfile))
                                        {
                                            param.Add(ri.RunProfile);
                                        }

                                        long NumCSObjects = long.Parse((string)MAObject.InvokeMethod("NumCSObjects", null));
                                        LogThread(thread.Name, string.Format("Number of CS object(s): {0:n0}", NumCSObjects));

                                        long NumTotalConnectors = long.Parse((string)MAObject.InvokeMethod("NumTotalConnectors", null));
                                        LogThread(thread.Name, string.Format("Number of connectors: {0:n0}", NumTotalConnectors));

                                        long NumTotalDisconnectors = long.Parse((string)MAObject.InvokeMethod("NumTotalDisconnectors", null));
                                        LogThread(thread.Name, string.Format("Number of disconnectors: {0:n0}", NumTotalDisconnectors));

                                        // pending imports
                                        long PendingImportAdds = long.Parse((string)MAObject.InvokeMethod("NumImportAdd", null));
                                        LogThread(thread.Name, string.Format("Pending Import Add(s): {0:n0}", PendingImportAdds));

                                        long PendingImportUpdates = long.Parse((string)MAObject.InvokeMethod("NumImportUpdate", null));
                                        LogThread(thread.Name, string.Format("Pending Import Update(s): {0:n0}", PendingImportUpdates));

                                        long PendingImportDeletes = long.Parse((string)MAObject.InvokeMethod("NumImportDelete", null));
                                        LogThread(thread.Name, string.Format("Pending Import Delete(s): {0:n0}", PendingImportDeletes));


                                        // pending exports
                                        long PendingExportAdds = long.Parse((string)MAObject.InvokeMethod("NumExportAdd", null));
                                        LogThread(thread.Name, string.Format("Pending Export Add(s): {0:n0}", PendingExportAdds));

                                        long PendingExportUpdates = long.Parse((string)MAObject.InvokeMethod("NumExportUpdate", null));
                                        LogThread(thread.Name, string.Format("Pending Export Update(s): {0:n0}", PendingExportUpdates));

                                        long PendingExportDeletes = long.Parse((string)MAObject.InvokeMethod("NumExportDelete", null));
                                        LogThread(thread.Name, string.Format("Pending Export Delete(s): {0:n0}", PendingExportDeletes));

                                        bool thresholdsMet = false;
                                        if (ri.ThresholdLimits != null)
                                        {
                                            thresholdsMet = IsThresholdMet("Import Adds", thread.Name, NumTotalConnectors, ri.ThresholdLimits.MaximumPendingImportAdds, PendingImportAdds) ||
                                                                IsThresholdMet("Import Updates", thread.Name, NumTotalConnectors, ri.ThresholdLimits.MaximumPendingImportUpdates, PendingImportUpdates) ||
                                                                IsThresholdMet("Import Deletes", thread.Name, NumTotalConnectors, ri.ThresholdLimits.MaximumPendingImportDeletes, PendingImportDeletes) ||
                                                                IsThresholdMet("Export Adds", thread.Name, NumTotalConnectors, ri.ThresholdLimits.MaximumPendingExportAdds, PendingExportAdds) ||
                                                                IsThresholdMet("Export Updates", thread.Name, NumTotalConnectors, ri.ThresholdLimits.MaximumPendingExportUpdates, PendingExportUpdates) ||
                                                                IsThresholdMet("Export Deletes", thread.Name, NumTotalConnectors, ri.ThresholdLimits.MaximumPendingExportDeletes, PendingExportDeletes);
                                        }

                                        if (thresholdsMet)
                                        {
                                            LogThread(thread.Name, "One or more thresholds met. Skipping run profile");
                                        }
                                        else
                                        {
                                            LogThread(thread.Name, string.Format("Only run on pending imports: {0:n0}", ri.OnlyRunIfPendingImports));
                                            LogThread(thread.Name, string.Format("Only run on pending exports: {0:n0}", ri.OnlyRunIfPendingExports));
                                            if ( ((ri.OnlyRunIfPendingExports && (PendingExportAdds + PendingExportUpdates + PendingExportDeletes) > 0) || !ri.OnlyRunIfPendingExports) && 
                                                ((ri.OnlyRunIfPendingImports && (PendingImportAdds + PendingImportUpdates + PendingImportDeletes) > 0) || !ri.OnlyRunIfPendingImports))
                                            {
                                                LogThread(thread.Name, string.Format("Running: '{0}'", ri.RunProfile));
                                                result = (string)MAObject.InvokeMethod("Execute", param.ToArray());
                                                LogThread(thread.Name, string.Format("Run result: {0}", result));
                                            }
                                            else
                                            {
                                                LogThread(thread.Name, string.Format("There are no pending imports/exports; skipping run"));
                                            }
                                        }

                                        Enum.Dispose();
                                    }
                                    else
                                    {

                                        Log(new Exception("Error connecting to MA"), ERROR_COULDNOTCONNECTOMANAGEMENTAGENT);
                                        LogThread(thread.Name, string.Format("Error: Unable to connect to Management Agent '{0}'", ri.MA));
                                    }

                                    #endregion

                                    #region RunDetails
                                    /*
                             // Check Run For Errors
switch (maResult.ToLower()) {
    case "success":
    case "completed-no-objects":
    case "completed-warnings":
        break;
    case "completed-export-errors":
        throw( new ApplicationException( "The Management Agent '" + maName + 
            "' reported import errors on execution. " +
            "You will need to use the Identity Manager to determine the problem(s)." ) );
    default:
        throw( new ApplicationException( "The Management Agent '" + maName + 
            "' failed on execution. The error returned is '" + maResult + "'" ) );
}
// Fetch the run details for the ma
object xmlContent = ma.InvokeMethod( "RunDetails", null );
XmlDocument xmlDoc = new XmlDocument();
xmlDoc.LoadXml( xmlContent.ToString() );
// Check Run Stage Count
int runStateCount = 0;
runStateCount += GetNodeCount( xmlDoc, "stage-add" );
runStateCount += GetNodeCount( xmlDoc, "stage-update" );
runStateCount += GetNodeCount( xmlDoc, "stage-rename" );
runStateCount += GetNodeCount( xmlDoc, "stage-delete" );
runStateCount += GetNodeCount( xmlDoc, "stage-delete-add" );
// Check Export Count
int exportCount = 0;
exportCount += GetNodeCount( xmlDoc, "export-add" );
exportCount += GetNodeCount( xmlDoc, "export-update" );
exportCount += GetNodeCount( xmlDoc, "export-rename" );
exportCount += GetNodeCount( xmlDoc, "export-delete" );
exportCount += GetNodeCount( xmlDoc, "export-delete-add" );
public static int GetNodeCount(XmlDocument xmlDoc, string nodeName) {
    int returnCount = 0;
    XmlNodeList nodes = xmlDoc.DocumentElement.GetElementsByTagName( nodeName );
    if ((nodes != null) && (nodes.Count > 0)) {
        foreach (XmlNode node in nodes) {
            if (node.InnerText.Length > 0) {
                returnCount += int.Parse( node.InnerText );
            }
        }
    }
    return returnCount;
}
                             * */

                                    #endregion

                                    #region Post processing
                                    if (!(string.IsNullOrEmpty(ri.Postprocessing)))
                                    {
                                        if ((result.Equals("success", StringComparison.InvariantCultureIgnoreCase)) || (ri.ContinueOnFailure))
                                        {
                                            ExecuteCommand(thread.Name, ri.Postprocessing, ri.PostprocessingArguments, result);
                                        }
                                        else
                                        {
                                            Log("Post-processing not run because of run error");
                                        }
                                    }
                                    #endregion

                                    #region Wait minutes

                                    if (ri.WaitMinutes > 0)
                                    {
                                        LogThread(thread.Name, ri.WaitMinutes.ToString("Start: Waiting 0 minute(s)"));
                                        Thread.Sleep(ri.WaitMinutes * 60000);
                                        LogThread(thread.Name, ri.WaitMinutes.ToString("End: Waiting 0 minute(s)"));
                                    }

                                    #endregion
                                }
                                else
                                {
                                    LogThread(thread.Name, string.Format("Item '{0}' won't run due to day or time restrictions", ri.MA));
                                }
                            }
                            LogThread(thread.Name, string.Format("Ending cycle #{0}", repeatCount - thread.RepeatCount));
                        }
                        else
                        {
                            LogThread(thread.Name, string.Format("Thread '{0}' won't run due to day or time restrictions", thread.Name));
                        }
                        thread.RepeatCount--;
                    }
                };

                List<Task> tasks = new List<Task>();
                foreach (RunThread thread in configuration.Thread)
                {
                    tasks.Add(new Task(action, thread));
                }
                foreach (Task t in tasks)
                {
                    t.Start();
                }
                foreach (Task t in tasks)
                {
                    t.Wait();
                }
                tasks = null;

                #region Clearing run histories
                if (configuration.ClearRunHistory.ClearRuns)
                {
                    ObjectQuery cleartAgentQuery = new ObjectQuery(string.Format("SELECT * FROM MIIS_Server", serverName));
                    ManagementObjectSearcher clearRunsMASearcher = new ManagementObjectSearcher(WMInamespace, cleartAgentQuery);
                    ManagementObjectCollection clearRunsMAObjects = clearRunsMASearcher.Get();
                    if (clearRunsMAObjects.Count == 0)
                    {
                        throw new Exception("Unable to find server: " + serverName);
                    }
                    else
                    {
                        foreach (ManagementObject oReturn in clearRunsMAObjects)
                        {
                            DateTime clearDate = DateTime.Now.AddMinutes(-configuration.ClearRunHistory.AgeInMinutes);
                            Log(string.Format("Clearing runs older than {0}", clearDate.ToLocalTime().ToLocalTime()));
                            object[] methodArgs = { clearDate.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:00") };
                            string clearRunsResult = (string)oReturn.InvokeMethod("ClearRuns", methodArgs);
                            Log(string.Format("Clear Runs Result: {0}", string.Format("Run result: {0}", clearRunsResult)));
                            if (clearRunsResult.ToString().ToLower() != "success")
                            {
                                throw new Exception("Failed to delete old operation logs: " + clearRunsResult.ToString());
                            }
                        }
                    }
                }
                #endregion

                Log("Ended");
            }
            catch (FileNotFoundException fileNotFoundException)
            {
                string error = string.Format("File Not Found: {0}", fileNotFoundException.FileName);
                Console.WriteLine(error);
                Log(new Exception(error), ERROR_CONFIGURATIONFILENOTFOUND);
            }
            catch (Exception ex)
            {
                string error = string.Format("{0}-{1}", ex.GetType().ToString(), ex.Message);
                Log(new Exception(error), ERROR_UNSPECIFIEDERROR);
            }
        }

        [Serializable]
        public class MARunScheduler
        {

            public void Serialize(string file, MARunScheduler c)
            {
                System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(c.GetType());
                StreamWriter writer = File.CreateText(file);
                xs.Serialize(writer, c);
                writer.Flush();
                writer.Close();
            }
            public static MARunScheduler Deserialize(string file)
            {
                System.Xml.Serialization.XmlSerializer xs = new System.Xml.Serialization.XmlSerializer(typeof(MARunScheduler));
                StreamReader reader = File.OpenText(file);
                MARunScheduler c = (MARunScheduler)xs.Deserialize(reader);
                reader.Close();
                return c;
            }

            [XmlAttribute("Console")]
            public bool Console { get; set; }

            [XmlAttribute("EnableLogging")]
            public bool EnableLogging { get; set; }

            [XmlAttribute("LogFile")]
            public string LogFile { get; set; }

            [XmlElement("ClearRunHistory")]
            public ClearRunHistorySettings ClearRunHistory = new ClearRunHistorySettings();

            [XmlElement("Thread")]
            public List<RunThread> Thread = new List<RunThread>();

        }

        [Serializable]
        public class RunThread
        {
            [XmlAttribute("RepeatCount")]
            public int RepeatCount { get; set; }

            [XmlAttribute("Name")]
            public string Name { get; set; }

            [XmlAttribute("RunAfter")]
            public string RunAfter { get; set; }

            [XmlAttribute("RunBefore")]
            public string RunBefore { get; set; }

            [XmlAttribute("RunOnDays")]
            public string RunOnDays { get; set; }

            [XmlAttribute("LoopIndefinitely")]
            public bool LoopIndefinitely { get; set; }

            [XmlElement("Item")]
            public List<RunItem> Item = new List<RunItem>();

        }

        [Serializable]
        public class ClearRunHistorySettings
        {
            public bool ClearRuns { get; set; }
            public int AgeInMinutes { get; set; }
        }

        [Serializable]
        public class RunItem
        {
            [XmlAttribute("RunAfter")]
            public string RunAfter { get; set; }

            [XmlAttribute("RunBefore")]
            public string RunBefore { get; set; }

            [XmlAttribute("RunOnDays")]
            public string RunOnDays { get; set; }

            public ThresholdLimits ThresholdLimits { get; set; }

            public bool OnlyRunIfPendingExports { get; set; }
            public bool OnlyRunIfPendingImports { get; set; }
            public string Preprocessing { get; set; }
            public string PreprocessingArguments { get; set; }
            public string MA { get; set; }
            public string RunProfile { get; set; }
            public int WaitMinutes { get; set; }
            public string Postprocessing { get; set; }
            public string PostprocessingArguments { get; set; }
            public bool ContinueOnFailure { get; set; }
        }

        [Serializable]
        public class ThresholdLimits
        {

            public string MaximumPendingImportAdds { get; set; }
            public string MaximumPendingImportUpdates { get; set; }
            public string MaximumPendingImportDeletes { get; set; }

            public string MaximumPendingExportAdds { get; set; }
            public string MaximumPendingExportUpdates { get; set; }
            public string MaximumPendingExportDeletes { get; set; }

        }

    }
}
