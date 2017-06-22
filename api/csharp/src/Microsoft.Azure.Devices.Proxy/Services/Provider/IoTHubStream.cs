// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.Azure.Devices.Proxy.Provider {
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    /// <summary>
    /// IoT Hub device method based message stream
    /// </summary>
    internal class IoTHubStream : IConnection {

        /// <summary>
        /// Connection string for connection
        /// </summary>
        public ConnectionString ConnectionString { get; private set; }

        /// <summary>
        /// Always polled
        /// </summary>
        public bool IsPolled { get; } = true;


        /// <summary>
        /// Constructor creating a method based polled stream.
        /// </summary>
        /// <param name="iothub"></param>
        /// <param name="streamId"></param>
        /// <param name="remoteId"></param>
        /// <param name="link"></param>
        /// <param name="connectionString"></param>
        public IoTHubStream(IoTHubService iothub, Reference streamId,
            Reference remoteId, INameRecord link, ConnectionString connectionString) {
            _iotHub = iothub;
            _streamId = streamId;
            _remoteId = remoteId;
            _link = link;
            ConnectionString = connectionString;
        }

        /// <summary>
        /// Open stream
        /// </summary>
        /// <param name="ct"></param>
        /// <returns></returns>
        public Task OpenAsync(IMessageStream stream, CancellationToken ct) {

            // Link to action block sending in order
            var sendTimeout = TimeSpan.FromMilliseconds(_pollTimeout * 2);
            stream.SendBlock.LinkTo(new ActionBlock<Message>(async (message) => {
                message.Source = _streamId;
                message.Target = _remoteId;
                try {
                    var response = await _iotHub.TryInvokeDeviceMethodWithRetryAsync(
                        _link, message, sendTimeout, _open.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) {
                    stream.SendBlock.Complete();
                }
                catch (Exception e) {
                    ProxyEventSource.Log.HandledExceptionAsError(this, e);
                    stream.SendBlock.Fault(e);
                }
            }, new ExecutionDataflowBlockOptions {
                CancellationToken = _open.Token,
                MaxDegreeOfParallelism = 1,
                EnsureOrdered = true
            }));

            // Start producer receiving from poll
            _producerTask = Task.Factory.StartNew(async () => {
                try {
                    while (true) {
                        var response = await _iotHub.TryInvokeDeviceMethodAsync(_link,
                            new Message(_streamId, _remoteId, new PollRequest(_pollTimeout)),
                                sendTimeout, _open.Token).ConfigureAwait(false);
                        //
                        // Poll receives back a timeout error in case no data was available 
                        // within the requested timeout. This is decoupled from the consumers
                        // that time out on their cancellation tokens.
                        //
                        if (response != null && response.Error != (int)SocketError.Timeout) {
                            await stream.ReceiveBlock.SendAsync(response).ConfigureAwait(false);
                        }
                        // Continue polling until closed in which case we complete receive
                        _open.Token.ThrowIfCancellationRequested();
                    }
                }
                catch(OperationCanceledException) {
                    stream.ReceiveBlock.Complete();
                }
                catch (Exception e) {
                    stream.ReceiveBlock.Fault(e);
                    ProxyEventSource.Log.HandledExceptionAsError(this, e);
                }
            }, _open.Token, TaskCreationOptions.LongRunning, QueuedTaskScheduler.Priority[0]).Unwrap();
            return TaskEx.Completed;
        }

        /// <summary>
        /// Close stream
        /// </summary>
        /// <returns></returns>
        public Task CloseAsync() {
            _open.Cancel();
            return TaskEx.Completed;
        }

        private Task _producerTask;
        private readonly CancellationTokenSource _open = new CancellationTokenSource();
        private readonly IoTHubService _iotHub;
        private readonly Reference _streamId;
        private readonly Reference _remoteId;
        private readonly INameRecord _link;
        private static readonly ulong _pollTimeout = 60000; // 60 seconds default poll timeout
    }
}