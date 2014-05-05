﻿using Common.Logging;
using Couchbase.Authentication.SASL;
using Couchbase.IO.Operations;
using Couchbase.IO.Strategies.Awaitable;
using Couchbase.IO.Utils;
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;

namespace Couchbase.IO.Strategies.Async
{
    internal class SocketAsyncStrategy : IOStrategy
    {
        private readonly static ILog Log = LogManager.GetCurrentClassLogger();
        private readonly IConnectionPool _connectionPool;
        private readonly SocketAsyncPool _socketAsyncPool;
        private static readonly AutoResetEvent WaitEvent = new AutoResetEvent(true);
        private static readonly AutoResetEvent SendEvent = new AutoResetEvent(false);
        private ISaslMechanism _saslMechanism;
        private volatile bool _disposed;

        public SocketAsyncStrategy(IConnectionPool connectionPool)
            : this(connectionPool,
            new SocketAsyncPool(connectionPool, SocketAsyncFactory.GetSocketAsyncFunc()),
            new PlainTextMechanism("default", string.Empty))
        {
        }

        public SocketAsyncStrategy(IConnectionPool connectionPool, ISaslMechanism saslMechanism)
            : this(connectionPool, new SocketAsyncPool(connectionPool, SocketAsyncFactory.GetSocketAsyncFunc()), saslMechanism)
        {
        }

        public SocketAsyncStrategy(IConnectionPool connectionPool, SocketAsyncPool socketAsyncPool)
        {
            _connectionPool = connectionPool;
            _socketAsyncPool = socketAsyncPool;
        }

        public SocketAsyncStrategy(IConnectionPool connectionPool, SocketAsyncPool socketAsyncPool, ISaslMechanism saslMechanism)
        {
            _connectionPool = connectionPool;
            _socketAsyncPool = socketAsyncPool;
            _saslMechanism = saslMechanism;
            _saslMechanism.IOStrategy = this;
        }

        public IOperationResult<T> Execute<T>(IOperation<T> operation, IConnection connection)
        {
            var socketAsync = new SocketAsyncEventArgs
            {
                AcceptSocket = connection.Socket,
                UserToken = new OperationAsyncState
                {
                    Connection = connection
                }
            };
            socketAsync.Completed -= OnCompleted;
            socketAsync.Completed += OnCompleted;

            var state = (OperationAsyncState)socketAsync.UserToken;
            state.Reset();

            var socket = state.Connection.Socket;
            Log.Debug(m => m("sending key {0}", operation.Key));

            var buffer = operation.GetBuffer();
            socketAsync.SetBuffer(buffer, 0, buffer.Length);
            socket.SendAsync(socketAsync);
            SendEvent.WaitOne();//needs cancellation token timeout

            operation.Header = state.Header;
            operation.Body = state.Body;

            return operation.GetResult();
        }

        private void Authenticate(IConnection connection)
        {
            if (_saslMechanism != null)
            {
                var result = _saslMechanism.Authenticate(connection);
                if (result)
                {
                    connection.IsAuthenticated = true;
                }
                else
                {
                    throw new AuthenticationException(_saslMechanism.Username);
                }
            }
        }

        public IOperationResult<T> Execute<T>(IOperation<T> operation)
        {
            var socketAsync = _socketAsyncPool.Acquire();
            WaitEvent.WaitOne();
            socketAsync.Completed -= OnCompleted;
            socketAsync.Completed += OnCompleted;

            var state = (OperationAsyncState)socketAsync.UserToken;
            state.Reset();

            try
            {
                var connection = state.Connection;
                if (!connection.IsAuthenticated)
                {
                    Authenticate(state.Connection);
                }

                var socket = state.Connection.Socket;
                Log.Debug(m => m("sending key {0} using {1}", operation.Key, state.Connection.Identity));

                var buffer = operation.GetBuffer();
                socketAsync.SetBuffer(buffer, 0, buffer.Length);
                socket.SendAsync(socketAsync);
                WaitEvent.Reset();
                SendEvent.WaitOne(); //needs cancellation token timeout

                operation.Header = state.Header;
                operation.Body = state.Body;
            }
            catch (AuthenticationException)
            {
                throw;
            }
            catch (Exception e)
            {
                operation.Exception = e;
                Log.Error(e);
            }
            finally
            {
                _socketAsyncPool.Release(socketAsync);
                WaitEvent.Set();
            }
            return operation.GetResult();
        }

        private void OnCompleted(object sender, SocketAsyncEventArgs e)
        {
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    Receive(e);
                    break;

                case SocketAsyncOperation.Send:
                    Send(e);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private static void Send(SocketAsyncEventArgs e)
        {
            Log.Debug(m=>m("send..."));
            if (e.SocketError == SocketError.Success)
            {
                var state = (OperationAsyncState)e.UserToken;
                var socket = state.Connection.Socket;

                var willRaiseCompletedEvent = socket.ReceiveAsync(e);
                if (!willRaiseCompletedEvent)
                {
                    Receive(e);
                }
            }
            else
            {
                throw new SocketException((int)e.SocketError);
            }
        }

        private static void Receive(SocketAsyncEventArgs e)
        {
            while (true)
            {
                if (e.SocketError == SocketError.Success)
                {
                    var state = (OperationAsyncState)e.UserToken;
                    state.BytesReceived += e.BytesTransferred;
                    state.Data.Write(e.Buffer, e.Offset, e.Count);
                    Log.Debug(m => m("receive...{0} bytes of {1} offset {2}", state.BytesReceived, e.Count, e.Offset));

                    if (state.Header.BodyLength == 0)
                    {
                        CreateHeader(state);
                        Log.Debug(m => m("received key {0}", state.Header.Key));
                    }

                    if (state.BytesReceived < state.Header.TotalLength)
                    {
                        var willRaiseCompletedEvent = e.AcceptSocket.ReceiveAsync(e);
                        if (!willRaiseCompletedEvent)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        Log.Debug(m => m("bytes rcvd/length: {0}/{1}", state.BytesReceived, state.Header.TotalLength));
                        CreateBody(state);
                        SendEvent.Set();
                    }
                }
                else
                {
                    throw new SocketException((int)e.SocketError);
                }
                break;
            }
        }

        private static void CreateHeader(OperationAsyncState state)
        {
            var buffer = state.Data.GetBuffer();
            if (buffer.Length > 0)
            {
                state.Header = new OperationHeader
                {
                    Magic = buffer[HeaderIndexFor.Magic],
                    OperationCode = buffer[HeaderIndexFor.Opcode].ToOpCode(),
                    KeyLength = buffer.GetInt16(HeaderIndexFor.KeyLength),
                    ExtrasLength = buffer[HeaderIndexFor.ExtrasLength],
                    Status = buffer.GetResponseStatus(HeaderIndexFor.Status),
                    BodyLength = buffer.GetInt32(HeaderIndexFor.Body),
                    Opaque = buffer.GetUInt32(HeaderIndexFor.Opaque),
                    Cas = buffer.GetUInt64(HeaderIndexFor.Cas)
                };
            }
        }

        private static void CreateBody(OperationAsyncState state)
        {
            var buffer = state.Data.GetBuffer();
            state.Body = new OperationBody
            {
                Extras = new ArraySegment<byte>(buffer, OperationBase<object>.HeaderLength, state.Header.ExtrasLength),
                Data = new ArraySegment<byte>(buffer, 28, state.Header.BodyLength),
            };
        }

        public ISaslMechanism SaslMechanism
        {
            set { _saslMechanism = value; }
        }

        public IPEndPoint EndPoint
        {
            get { return _connectionPool.EndPoint; }
        }

        public IConnectionPool ConnectionPool
        {
            get { return _connectionPool; }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        void Dispose(bool disposing)
        {
            if (disposing)
            {
                GC.SuppressFinalize(this);
            }
            if (_connectionPool != null)
            {
                _connectionPool.Dispose();
            }
            if (_socketAsyncPool != null)
            {
                _socketAsyncPool.Dispose();
            }
        }

        ~SocketAsyncStrategy()
        {
            Dispose(false);
        }
    }
}