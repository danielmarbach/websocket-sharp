#region License
/*
 * WebSocketBehavior.cs
 *
 * The MIT License
 *
 * Copyright (c) 2012-2014 sta.blockhead
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion

namespace WebSocketSharp.Server
{
    using System;
    using System.IO;
    using System.Threading.Tasks;

    using WebSocketSharp.Net;
    using WebSocketSharp.Net.WebSockets;

    using ErrorEventArgs = WebSocketSharp.ErrorEventArgs;

    /// <summary>
    /// Exposes the methods and properties used to define the behavior of a WebSocket service
    /// provided by the <see cref="WebSocketServer"/> or <see cref="WebSocketServer"/>.
    /// </summary>
    /// <remarks>
    /// The WebSocketBehavior class is an abstract class.
    /// </remarks>
    public abstract class WebSocketBehavior : IWebSocketSession
    {
        private WebSocketContext _context;
        private Func<CookieCollection, CookieCollection, bool> _cookiesValidator;

        private string _protocol;

        private WebSocket _websocket;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketBehavior"/> class.
        /// </summary>
        protected WebSocketBehavior()
        {
            StartTime = DateTime.MaxValue;
        }

        /// <summary>
        /// Gets the information in the current connection request to the WebSocket service.
        /// </summary>
        /// <value>
        /// A <see cref="WebSocketContext"/> that provides the access to the current connection request,
        /// or <see langword="null"/> if the WebSocket connection isn't established.
        /// </value>
        public WebSocketContext Context => _context;

        /// <summary>
        /// Gets or sets the delegate called to validate the HTTP cookies included in a connection
        /// request to the WebSocket service.
        /// </summary>
        /// <remarks>
        /// The delegate is called when the <see cref="WebSocket"/> used in the current session
        /// validates the connection request.
        /// </remarks>
        /// <value>
        ///   <para>
        ///   A <c>Func&lt;CookieCollection, CookieCollection, bool&gt;</c> delegate that references
        ///   the method(s) used to validate the cookies. 1st <see cref="CookieCollection"/> passed to
        ///   this delegate contains the cookies to validate if any. 2nd <see cref="CookieCollection"/>
        ///   passed to this delegate receives the cookies to send to the client.
        ///   </para>
        ///   <para>
        ///   This delegate should return <c>true</c> if the cookies are valid.
        ///   </para>
        ///   <para>
        ///   The default value is <see langword="null"/>, and it does nothing to validate.
        ///   </para>
        /// </value>
        public Func<CookieCollection, CookieCollection, bool> CookiesValidator
        {
            get
            {
                return _cookiesValidator;
            }

            set
            {
                _cookiesValidator = value;
            }
        }

        /// <summary>
        /// Gets the unique ID of the current session.
        /// </summary>
        /// <value>
        /// A <see cref="string"/> that represents the unique ID of the current session,
        /// or <see langword="null"/> if the WebSocket connection isn't established.
        /// </value>
        public string ID { get; private set; }

        /// <summary>
        /// Gets or sets the delegate called to validate the Origin header included in a connection
        /// request to the WebSocket service.
        /// </summary>
        /// <remarks>
        /// The delegate is called when the <see cref="WebSocket"/> used in the current session
        /// validates the connection request.
        /// </remarks>
        /// <value>
        ///   <para>
        ///   A <c>Func&lt;string, bool&gt;</c> delegate that references the method(s) used to validate
        ///   the origin header. A <see cref="string"/> passed to this delegate represents the value of
        ///   the origin header to validate if any.
        ///   </para>
        ///   <para>
        ///   This delegate should return <c>true</c> if the origin header is valid.
        ///   </para>
        ///   <para>
        ///   The default value is <see langword="null"/>, and it does nothing to validate.
        ///   </para>
        /// </value>
        public Func<string, bool> OriginValidator { get; set; }

        /// <summary>
        /// Gets or sets the WebSocket subprotocol used in the current session.
        /// </summary>
        /// <remarks>
        /// Set operation of this property is available before the WebSocket connection has been
        /// established.
        /// </remarks>
        /// <value>
        ///   <para>
        ///   A <see cref="string"/> that represents the subprotocol if any.
        ///   The default value is <see cref="string.Empty"/>.
        ///   </para>
        ///   <para>
        ///   The value to set must be a token defined in
        ///   <see href="http://tools.ietf.org/html/rfc2616#section-2.2">RFC 2616</see>.
        ///   </para>
        /// </value>
        public string Protocol
        {
            get
            {
                return _websocket != null
                       ? _websocket.Protocol
                       : _protocol ?? string.Empty;
            }

            set
            {
                if (State != WebSocketState.Connecting)
                    return;

                if (value != null && (value.Length == 0 || !value.IsToken()))
                    return;

                _protocol = value;
            }
        }

        /// <summary>
        /// Gets the time that the current session has started.
        /// </summary>
        /// <value>
        /// A <see cref="DateTime"/> that represents the time that the current session has started,
        /// or <see cref="DateTime.MaxValue"/> if the WebSocket connection isn't established.
        /// </value>
        public DateTime StartTime { get; private set; }

        /// <summary>
        /// Gets the state of the <see cref="WebSocket"/> used in the current session.
        /// </summary>
        /// <value>
        /// One of the <see cref="WebSocketState"/> enum values, indicates the state of
        /// the <see cref="WebSocket"/> used in the current session.
        /// </value>
        public WebSocketState State => _websocket?.ReadyState ?? WebSocketState.Connecting;

        /// <summary>
        /// Gets the access to the sessions in the WebSocket service.
        /// </summary>
        /// <value>
        /// A <see cref="WebSocketSessionManager"/> that provides the access to the sessions, or <see langword="null"/> if the WebSocket connection isn't established.
        /// </value>
        protected WebSocketSessionManager Sessions { get; private set; }

        internal Task Start(WebSocketContext context, WebSocketSessionManager sessions)
        {
            if (_websocket != null)
            {
                return context.WebSocket.InnerClose(HttpStatusCode.ServiceUnavailable);
            }

            _context = context;
            Sessions = sessions;

            _websocket = context.WebSocket;
            _websocket.CustomHandshakeRequestChecker = CheckIfValidConnectionRequest;
            _websocket.Protocol = _protocol;

            var waitTime = sessions.WaitTime;
            _websocket.WaitTime = waitTime;

            _websocket.OnOpen = InnerOnOpen;
            _websocket.OnMessage = OnMessage;
            _websocket.OnError = OnError;
            _websocket.OnClose = InnerOnClose;

            return _websocket.ConnectAsServer();
        }

        /// <summary>
        /// Calls the <see cref="OnError"/> method with the specified <paramref name="message"/> and
        /// <paramref name="exception"/>.
        /// </summary>
        /// <remarks>
        /// This method doesn't call the <see cref="OnError"/> method if <paramref name="message"/> is
        /// <see langword="null"/> or empty.
        /// </remarks>
        /// <param name="message">
        /// A <see cref="string"/> that represents the error message.
        /// </param>
        /// <param name="exception">
        /// An <see cref="Exception"/> instance that represents the cause of the error if any.
        /// </param>
        protected void Error(string message, Exception exception)
        {
            if (!string.IsNullOrEmpty(message))
                OnError(new ErrorEventArgs(message, exception));
        }

        /// <summary>
        /// Called when the WebSocket connection used in the current session has been closed.
        /// </summary>
        /// <param name="e">
        /// A <see cref="CloseEventArgs"/> that represents the event data passed to
        /// a <see cref="WebSocket.OnClose"/> event.
        /// </param>
        protected virtual Task OnClose(CloseEventArgs e)
        {
            return Task.FromResult(0);
        }

        /// <summary>
        /// Called when the <see cref="WebSocket"/> used in the current session gets an error.
        /// </summary>
        /// <param name="e">
        /// A <see cref="ErrorEventArgs"/> that represents the event data passed to
        /// a <see cref="WebSocket.OnError"/> event.
        /// </param>
        protected virtual Task OnError(ErrorEventArgs e)
        {
            return Task.FromResult(0);
        }

        /// <summary>
        /// Called when the <see cref="WebSocket"/> used in the current session receives a message.
        /// </summary>
        /// <param name="e">
        /// A <see cref="MessageEventArgs"/> that represents the event data passed to
        /// a <see cref="WebSocket.OnMessage"/> event.
        /// </param>
        protected virtual Task OnMessage(MessageEventArgs e)
        {
            return Task.FromResult(0);
        }

        /// <summary>
        /// Called when the WebSocket connection used in the current session has been established.
        /// </summary>
        protected virtual Task OnOpen()
        {
            return Task.FromResult(false);
        }

        /// <summary>
        /// Sends a binary <paramref name="data"/> to the client on the current session.
        /// </summary>
        /// <remarks>
        /// This method is available after the WebSocket connection has been established.
        /// </remarks>
        /// <param name="data">
        /// An array of <see cref="byte"/> that represents the binary data to send.
        /// </param>
        protected Task Send(byte[] data)
        {
            return _websocket != null ? _websocket.Send(data) : Task.FromResult(false);
        }

        /// <summary>
        /// Sends the specified <paramref name="stream"/> as a binary data to the client
        /// on the current session.
        /// </summary>
        /// <remarks>
        /// This method is available after the WebSocket connection has been established.
        /// </remarks>
        /// <param name="stream">
        /// A <see cref="FileInfo"/> that represents the file to send.
        /// </param>
        protected Task Send(Stream stream)
        {
            return _websocket != null ? _websocket.Send(stream) : Task.FromResult(false);
        }

        /// <summary>
        /// Sends a text <paramref name="data"/> to the client on the current session.
        /// </summary>
        /// <remarks>
        /// This method is available after the WebSocket connection has been established.
        /// </remarks>
        /// <param name="data">
        /// A <see cref="string"/> that represents the text data to send.
        /// </param>
        protected Task Send(string data)
        {
            return _websocket != null ? _websocket.Send(data) : Task.FromResult(false);
        }

        /// <summary>
        /// Sends a binary data from the specified <see cref="Stream"/> asynchronously
        /// to the client on the current session.
        /// </summary>
        /// <remarks>
        ///   <para>
        ///   This method is available after the WebSocket connection has been established.
        ///   </para>
        ///   <para>
        ///   This method doesn't wait for the send to be complete.
        ///   </para>
        /// </remarks>
        /// <param name="stream">
        /// A <see cref="Stream"/> from which contains the binary data to send.
        /// </param>
        /// <param name="length">
        /// An <see cref="int"/> that represents the number of bytes to send.
        /// </param>
        protected Task Send(Stream stream, int length)
        {
            return _websocket != null ? _websocket.Send(stream, length) : Task.FromResult(false);
        }

        private string CheckIfValidConnectionRequest(WebSocketContext context)
        {
            return OriginValidator != null && !OriginValidator(context.Origin)
                   ? "Invalid Origin header."
                   : _cookiesValidator != null &&
                     !_cookiesValidator(context.CookieCollection, context.WebSocket.CookieCollection)
                     ? "Invalid Cookies."
                     : null;
        }

        private Task InnerOnClose(CloseEventArgs e)
        {
            if (ID == null)
            {
                return Task.FromResult(false);
            }

            Sessions.Remove(ID);
            return OnClose(e);
        }

        private Task InnerOnOpen()
        {
            ID = Sessions.Add(this);
            if (ID == null)
            {
                _websocket.Close(CloseStatusCode.Away);
                return Task.FromResult(false);
            }

            StartTime = DateTime.Now;
            return OnOpen();
        }
    }
}
