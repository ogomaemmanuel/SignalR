﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Sockets.Internal.Transports
{
    public class LongPollingTransport : IHttpTransport
    {
        private readonly IPipeReader _application;
        private readonly ILogger _logger;
        private readonly CancellationToken _timeoutToken;
        private readonly string _connectionId;

        public LongPollingTransport(CancellationToken timeoutToken, IPipeReader application, string connectionId, ILoggerFactory loggerFactory)
        {
            _timeoutToken = timeoutToken;
            _application = application;
            _connectionId = connectionId;
            _logger = loggerFactory.CreateLogger<LongPollingTransport>();
        }

        public async Task ProcessRequestAsync(HttpContext context, CancellationToken token)
        {
            try
            {
                var result = await _application.ReadAsync(token);
                var buffer = result.Buffer;

                try
                {
                    // REVIEW: What should the content type be?

                    // We're intentionally not checking cancellation here because we need to drain messages we've got so far,
                    // but it's too late to emit the 204 required by being cancelled.

                    if (!buffer.IsEmpty)
                    {
                        context.Response.ContentLength = buffer.Length;

                        foreach (var b in buffer)
                        {
                            if (!b.TryGetArray(out var segment))
                            {
                                throw new InvalidOperationException("No managed buffers");
                            }
                            await context.Response.Body.WriteAsync(segment.Array, segment.Offset, segment.Count);
                        }

                        _logger.LongPollingWritingMessage(_connectionId, context.TraceIdentifier, buffer.Length);
                    }
                    else if (result.IsCompleted)
                    {
                        _logger.LongPolling204(_connectionId, context.TraceIdentifier);
                        context.Response.StatusCode = StatusCodes.Status204NoContent;
                    }
                }
                finally
                {
                    _application.Advance(buffer.End);
                }
            }
            catch (OperationCanceledException)
            {
                // 3 cases:
                // 1 - Request aborted, the client disconnected (no response)
                // 2 - The poll timeout is hit (204)
                // 3 - A new request comes in and cancels this request (204)

                // Case 1
                if (context.RequestAborted.IsCancellationRequested)
                {
                    // Don't count this as cancellation, this is normal as the poll can end due to the browser closing.
                    // The background thread will eventually dispose this connection if it's inactive
                    _logger.LongPollingDisconnected(_connectionId, context.TraceIdentifier);
                }
                // Case 2
                else if (_timeoutToken.IsCancellationRequested)
                {
                    _logger.PollTimedOut(_connectionId, context.TraceIdentifier);

                    context.Response.ContentLength = 0;
                    context.Response.StatusCode = StatusCodes.Status200OK;
                }
                else
                {
                    // Case 3
                    _logger.LongPolling204(_connectionId, context.TraceIdentifier);
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                }
            }
            catch (Exception ex)
            {
                _logger.LongPollingTerminated(_connectionId, context.TraceIdentifier, ex);
                throw;
            }
        }
    }
}