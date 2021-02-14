using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using static Splunk.Logging.HttpEventCollectorSender;

namespace Splunk.Logging
{
    public class HttpEventCollectorEventInfoBatch : IDisposable
    {
        private readonly Func<byte[], CancellationToken, Task<HttpStatusCode>> postEvents;

        private readonly StreamWriter serializedEventsBatch = new StreamWriter(new MemoryStream(), HttpContentEncoding, 1024, true);

        private readonly JsonSerializerSettings jsonSerializerSettings;

        private readonly HttpEventCollectorFormatter formatter;

        private readonly HttpEventCollectorEventInfo.Metadata metadata;

        private JsonSerializer jsonSerializer;

        public HttpEventCollectorEventInfoBatch(
            Func<byte[], CancellationToken, Task<HttpStatusCode>> postEvents,
            JsonSerializerSettings jsonSerializerSettings,
            HttpEventCollectorEventInfo.Metadata metadata,
            HttpEventCollectorFormatter formatter = null)
        {
            this.postEvents = postEvents ?? throw new ArgumentNullException(nameof(postEvents));
            this.jsonSerializerSettings = jsonSerializerSettings ?? throw new ArgumentNullException(nameof(jsonSerializerSettings));
            jsonSerializer = JsonSerializer.CreateDefault(this.jsonSerializerSettings);
            this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            this.formatter = formatter;
        }

        public void Dispose() => serializedEventsBatch.Dispose();

        /// <summary>
        /// Send an event to Splunk HTTP endpoint. Actual event send is done
        /// asynchronously and this method doesn't block client application.
        /// </summary>
        /// <param name="timestamp">Timestamp to use.</param>
        /// <param name="id">Event id.</param>
        /// <param name="level">Event level info.</param>
        /// <param name="messageTemplate">Event message template.</param>
        /// <param name="renderedMessage">Event message rendered.</param>
        /// <param name="exception">Event exception.</param>
        /// <param name="properties">Additional event data.</param>
        /// <param name="metadataOverride">Metadata to use for this send.</param>
        public void AddEvent(
            DateTime timestamp,
            string id = null,
            string level = null,
            string messageTemplate = null,
            string renderedMessage = null,
            object exception = null,
            object properties = null,
            HttpEventCollectorEventInfo.Metadata metadataOverride = null)
        {
            HttpEventCollectorEventInfo ei = new HttpEventCollectorEventInfo(timestamp, id, level, messageTemplate, renderedMessage, exception, properties, metadataOverride ?? metadata);
            DoSerialization(ei);
        }

        public Task<HttpStatusCode> Send(CancellationToken cancellationToken)
        {
            var batchPayload = ((MemoryStream)serializedEventsBatch.BaseStream).ToArray();
            serializedEventsBatch.BaseStream.Position = 0;
            serializedEventsBatch.BaseStream.SetLength(0);
            return postEvents(batchPayload, cancellationToken);
        }

        /// <summary>
        /// Does the serialization.
        /// </summary>
        /// <param name="ei">The ei.</param>
        private void DoSerialization(HttpEventCollectorEventInfo ei)
        {
            if (formatter != null)
            {
                var formattedEvent = formatter(ei);
                ei.Event = formattedEvent;
            }

            long orgLength = serializedEventsBatch.BaseStream.Length;

            try
            {
                using (JsonTextWriter jsonWriter = new JsonTextWriter(serializedEventsBatch))
                {
                    jsonWriter.Formatting = jsonSerializer.Formatting;
                    jsonSerializer.Serialize(jsonWriter, ei);
                }

                serializedEventsBatch.Flush();
            }
            catch
            {
                // Unwind / truncate any bad output
                serializedEventsBatch.Flush();
                serializedEventsBatch.BaseStream.Position = orgLength;
                serializedEventsBatch.BaseStream.SetLength(orgLength);
                jsonSerializer = JsonSerializer.CreateDefault(jsonSerializerSettings);   // Reset bad state
                throw;
            }
        }
    }
}
