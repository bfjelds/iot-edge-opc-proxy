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
            stream.SendBlock.LinkTo(new ActionBlock<Message>(async (message) => {
                message.Source = _streamId;
                message.Target = _remoteId;
                try {
                    var response = await _iotHub.TryInvokeDeviceMethodWithRetryAsync(
                        _link, message, _pollTimeout, _open.Token).ConfigureAwait(false);
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
            _producerTask = Task.Run(async () => {
                try {
                    while (true) {
                        var response = await _iotHub.TryInvokeDeviceMethodAsync(_link,
                            new Message(_streamId, _remoteId, new PollRequest(30000)),
                                _pollTimeout, _open.Token).ConfigureAwait(false);
                        if (response != null) {
                            await stream.ReceiveBlock.SendAsync(response).ConfigureAwait(false);
                        }
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
            });
            return Task.FromResult(true);
        }

        /// <summary>
        /// Close stream
        /// </summary>
        /// <returns></returns>
        public Task CloseAsync() {
            _open.Cancel();
            return Task.FromResult(true);
        }

        private Task _producerTask;
        private readonly CancellationTokenSource _open = new CancellationTokenSource();
        private readonly IoTHubService _iotHub;
        private readonly Reference _streamId;
        private readonly Reference _remoteId;
        private readonly INameRecord _link;
        private static readonly TimeSpan _pollTimeout = TimeSpan.FromMinutes(1);
    }
}