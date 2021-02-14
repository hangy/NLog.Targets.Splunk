using NLog.Common;
using NLog.Config;
using NLog.Layouts;
using Splunk.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NLog.Targets.Splunk
{
    /// <summary>
    /// Splunk Http Event Collector
    /// </summary>
    /// <seealso cref="NLog.Targets.TargetWithContext" />
    [Target("SplunkHttpEventCollector")]
    public sealed class SplunkHttpEventCollector : AsyncTaskTarget
    {
        private HttpEventCollectorSender _hecSender;

        /// <summary>
        /// Gets or sets the Splunk HTTP Event Collector server URL.
        /// </summary>
        /// <value>
        /// The Splunk HTTP Event Collector server URL.
        /// </value>
        [RequiredParameter]
        public Layout ServerUrl { get; set; }

        /// <summary>
        /// Gets or sets the Splunk HTTP Event Collector token.
        /// </summary>
        /// <value>
        /// The Splunk HTTP Event Collector token.
        /// </value>
        [RequiredParameter]
        public Layout Token { get; set; }

        /// <summary>
        /// Gets or sets the Splunk source type metadata.
        /// </summary>
        /// <value>
        /// The Splunk metadata source type.
        /// </value>
        public Layout SourceType { get; set; } = "_json";

        /// <summary>
        /// Gets or sets the Splunk source metadata.
        /// </summary>
        /// <value>
        /// The Splunk metadata source.
        /// </value>
        public Layout Source { get; set; } = "${logger}";

        /// <summary>
        /// Gets or sets the Splunk  index metadata.
        /// </summary>
        /// <value>
        /// The Splunk metadata index.
        /// </value>
        public Layout Index { get; set; }

        /// <summary>
        /// Gets or sets the optional Splunk HTTP Event Collector data channel.
        /// </summary>
        /// <value>
        /// The Splunk HTTP Event Collector data channel.
        /// </value>
        public Layout Channel { get; set; }

        /// <summary>
        /// Gets or sets whether to include positional parameters
        /// </summary>
        /// <value>
        ///   <c>true</c> if [include positional parameters]; otherwise, <c>false</c>.
        /// </value>
        public bool IncludePositionalParameters { get; set; }

        /// <summary>
        /// Ignore SSL errors when using homemade Ssl Certificates
        /// </summary>
        /// <value>
        ///   <c>true</c> if [ignore SSL errors]; otherwise, <c>false</c>.
        /// </value>
        public bool IgnoreSslErrors { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of concurrent connections (per server endpoint) allowed when making requests
        /// </summary>
        /// <value>0 = Use default limit. Default = 10</value>
        public int MaxConnectionsPerServer { get; set; } = 10;

        public bool UseHttpVersion10Hack { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to use default web proxy.
        /// </summary>
        /// <value>
        /// <c>true</c> = use default web proxy. <c>false</c> = use no proxy. Default is <c>true</c> 
        /// </value>
        public bool UseProxy { get; set; } = true;

        /// <summary>
        /// Configuration of additional properties to include with each LogEvent (Ex. ${logger}, ${machinename}, ${threadid} etc.)
        /// </summary>
        public override IList<TargetPropertyWithContext> ContextProperties { get; } = new List<TargetPropertyWithContext>();

        private readonly Dictionary<string, HttpEventCollectorEventInfo.Metadata> _metaData = new Dictionary<string, HttpEventCollectorEventInfo.Metadata>();

        private string _hostName;

        /// <summary>
        /// Initializes a new instance of the <see cref="SplunkHttpEventCollector"/> class.
        /// </summary>
        public SplunkHttpEventCollector()
        {
            OptimizeBufferReuse = true;
            IncludeEventProperties = true;
            Layout = "${message}";
        }

        /// <summary>
        /// Initializes the target. Can be used by inheriting classes
        /// to initialize logging.
        /// </summary>
        /// <exception cref="NLogConfigurationException">
        /// SplunkHttpEventCollector ServerUrl is not set!
        /// or
        /// SplunkHttpEventCollector Token is not set!
        /// </exception>
        protected override void InitializeTarget()
        {
            base.InitializeTarget();
            InternalLogger.Debug("Initializing SplunkHttpEventCollector");

            _metaData.Clear();

            var serverUri = RenderLogEvent(ServerUrl, LogEventInfo.CreateNullEvent());
            if (string.IsNullOrEmpty(serverUri))
            {
                throw new NLogConfigurationException("SplunkHttpEventCollector ServerUrl is not set!");
            }

            var token = RenderLogEvent(Token, LogEventInfo.CreateNullEvent());
            if (string.IsNullOrEmpty(token))
            {
                throw new NLogConfigurationException("SplunkHttpEventCollector Token is not set!");
            }

            var channel = RenderLogEvent(Channel, LogEventInfo.CreateNullEvent());
            var index = RenderLogEvent(Index, LogEventInfo.CreateNullEvent());
            var source = RenderLogEvent(Source, LogEventInfo.CreateNullEvent());
            var sourceType = RenderLogEvent(SourceType, LogEventInfo.CreateNullEvent());

            _hecSender = new HttpEventCollectorSender(
                new Uri(serverUri),                                                                 // Splunk HEC URL
                token,                                                                              // Splunk HEC token *GUID*
                channel,                                                                            // Splunk HEC data channel *GUID*
                GetMetaData(index, source, sourceType),                                             // Metadata
                HttpEventCollectorSender.SendMode.Sequential,                                       // Sequential sending to keep message in order
                IgnoreSslErrors,                                                                    // Enable Ssl Error ignore for self singed certs *BOOL*
                UseProxy,                                                                           // UseProxy - Set to false to disable
                MaxConnectionsPerServer,
                httpVersion10Hack: UseHttpVersion10Hack
            );
            _hecSender.OnError += (e) => { InternalLogger.Error(e, "SplunkHttpEventCollector(Name={0}): Failed to send LogEvents", Name); };
        }

        /// <summary>
        /// Disposes the initialized HttpEventCollectorSender
        /// </summary>
        protected override void CloseTarget()
        {
            try
            {
                _hecSender?.Dispose();
                base.CloseTarget();
            }
            finally
            {
                _hecSender = null;
            }
        }

        private void AddToBatch(LogEventInfo logEventInfo, HttpEventCollectorEventInfoBatch batch)
        {
            // Sanity check for LogEventInfo
            if (logEventInfo == null)
            {
                throw new ArgumentNullException(nameof(logEventInfo));
            }

            // Make sure we have a properly setup HttpEventCollectorSender
            if (_hecSender == null)
            {
                throw new NLogRuntimeException("SplunkHttpEventCollector SendEventToServer() called before InitializeTarget()");
            }

            // Build MetaData
            var index = RenderLogEvent(Index, logEventInfo);
            var source = RenderLogEvent(Source, logEventInfo);
            var sourceType = RenderLogEvent(SourceType, logEventInfo);
            var metaData = GetMetaData(index, source, sourceType);

            // Use NLog's built in tooling to get properties
            var properties = GetAllProperties(logEventInfo);

            if (IncludePositionalParameters && logEventInfo.Parameters != null)
            {
                for (var i = 0; i < logEventInfo.Parameters.Length; ++i)
                {
                    properties[string.Concat("{", i.ToString(), "}")] = logEventInfo.Parameters[i];
                }
            }

            // Send the event to splunk
            string renderedMessage = RenderLogEvent(Layout, logEventInfo);
            batch.AddEvent(logEventInfo.TimeStamp, null, logEventInfo.Level.Name, logEventInfo.Message, renderedMessage, logEventInfo.Exception, properties, metaData);
        }

        /// <summary>
        /// Gets the meta data.
        /// </summary>
        /// <param name="loggerName">Name of the logger.</param>
        /// <returns></returns>
        private HttpEventCollectorEventInfo.Metadata GetMetaData(string index, string source, string sourcetype)
        {
            var hostName = _hostName ?? (_hostName = GetMachineName());
            if (!_metaData.TryGetValue(source ?? string.Empty, out var metaData))
            {
                if (_metaData.Count > 1000)
                    _metaData.Clear();  // Extreme case that should never happen
                metaData = new HttpEventCollectorEventInfo.Metadata(string.IsNullOrEmpty(index) ? null : index, string.IsNullOrEmpty(source) ? null : source, sourcetype, hostName);
                _metaData[source ?? string.Empty] = metaData;
            }

            return metaData;
        }

        /// <summary>
        /// Gets the machine name
        /// </summary>
        private static string GetMachineName()
        {
            return TryLookupValue(() => Environment.GetEnvironmentVariable("COMPUTERNAME"), "COMPUTERNAME")
                ?? TryLookupValue(() => Environment.GetEnvironmentVariable("HOSTNAME"), "HOSTNAME")
                ?? TryLookupValue(() => Environment.MachineName, "MachineName")
                ?? TryLookupValue(() => System.Net.Dns.GetHostName(), "DnsHostName");
        }

        /// <summary>
        /// Tries the lookup value.
        /// </summary>
        /// <param name="lookupFunc">The lookup function.</param>
        /// <param name="lookupType">Type of the lookup.</param>
        /// <returns></returns>
        private static string TryLookupValue(Func<string> lookupFunc, string lookupType)
        {
            try
            {
                string lookupValue = lookupFunc()?.Trim();
                return string.IsNullOrEmpty(lookupValue) ? null : lookupValue;
            }
            catch (Exception ex)
            {
                InternalLogger.Warn(ex, "SplunkHttpEventCollector: Failed to lookup {0}", lookupType);
                return null;
            }
        }

        protected override Task WriteAsyncTask(LogEventInfo logEvent, CancellationToken cancellationToken)
        {
            using (HttpEventCollectorEventInfoBatch batch = _hecSender.StartBatch())
            {
                AddToBatch(logEvent, batch);
                return batch.Send(cancellationToken);
            }
        }

        protected override Task WriteAsyncTask(IList<LogEventInfo> logEvents, CancellationToken cancellationToken)
        {
            using (HttpEventCollectorEventInfoBatch batch = _hecSender.StartBatch())
            {
                foreach (LogEventInfo logEvent in logEvents)
                {
                    AddToBatch(logEvent, batch);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return batch.Send(cancellationToken);
            }
        }
    }
}
