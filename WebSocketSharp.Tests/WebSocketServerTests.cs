﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="WebSocketServerTests.cs" company="Reimers.dk">
//   The MIT License
//   Copyright (c) 2012-2014 sta.blockhead
//   Copyright (c) 2014 Reimers.dk
//   
//   Permission is hereby granted, free of charge, to any person obtaining a copy  of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//   
//   The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//   
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// </copyright>
// <summary>
//   Defines the WebSocketServerTests type.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace WebSocketSharp.Tests
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using NUnit.Framework;

    using global::WebSocketSharp.Server;

    public sealed class WebSocketServerTests
    {
        public class GivenAWebSocketServer
        {
            private WebSocketServer _sut;

            [SetUp]
            public void Setup()
            {
                Debug.Listeners.Add(new ConsoleTraceListener());
                _sut = new WebSocketServer(port: 8080);
                _sut.AddWebSocketService<TestEchoService>("/echo");
                _sut.AddWebSocketService<TestRadioService>("/radio");
                _sut.Start();
            }

            [TearDown]
            public void Teardown()
            {
                _sut.Stop();
                Debug.Listeners.Clear();
            }

            [Test]
            public void CanGetDefinedPort()
            {
                Assert.AreEqual(8080, _sut.Port);
            }

            [Test]
            public async Task ClientCanConnectAsyncToServer()
            {
                using (var client = new WebSocket("ws://localhost:8080/echo"))
                {
                    await client.Connect();

                    Assert.AreEqual(WebSocketState.Open, client.ReadyState);

                    await client.Close();
                }
            }

            [Test]
            public async Task WhenClientSendsTextMessageThenResponds()
            {
                const string Message = "Message";
                var waitHandle = new ManualResetEventSlim(false);
                using (var client = new WebSocket("ws://localhost:8080/echo"))
                {
                    Func<MessageEventArgs, Task> onMessage = e =>
                        {
                            if (e.Text.ReadToEnd() == Message)
                            {
                                waitHandle.Set();
                            }
                            return Task.FromResult(true);
                        };
                    client.OnMessage = onMessage;

                    await client.Connect();
                    await client.Send(Message);

                    var result = waitHandle.Wait(Debugger.IsAttached ? 30000 : 2000);

                    Assert.True(result);

                    client.OnMessage -= onMessage;
                    await client.Close();
                }
            }

            [Test]
            public async Task WhenClientConnectsToNonExistingPathThenStateIsClosed()
            {
                using (var client = new WebSocket("ws://localhost:8080/fjgkdfjhgld"))
                {
                    await client.Connect();

                    Assert.True(client.ReadyState == WebSocketState.Closed);

                    await client.Close();
                }
            }

            [Test]
            public void WhenClientConnectsToNonExistingPathThenDoesNotThrow()
            {
                using (var client = new WebSocket("ws://localhost:8080/fjgkdfjhgld"))
                {
                    Assert.That(async () => await client.Connect(), Throws.Nothing);
                }
            }

            [Test]
            public async Task WhenClientSendsMultipleTextMessageThenResponds([Random(1, 100, 10)]int multiplicity)
            {
                int count = 0;
                const string Message = "Message";
                var waitHandle = new ManualResetEventSlim(false);
                using (var client = new WebSocket("ws://localhost:8080/echo"))
                {
                    Func<MessageEventArgs, Task> onMessage = e =>
                        {
                            if (e.Text.ReadToEnd() == Message)
                            {
                                if (Interlocked.Increment(ref count) == multiplicity)
                                {
                                    waitHandle.Set();
                                }
                            }
                            return Task.FromResult(true);
                        };
                    client.OnMessage = onMessage;

                    await client.Connect();
                    for (int i = 0; i < multiplicity; i++)
                    {
                        await client.Send(Message);
                    }

                    var result = waitHandle.Wait(Debugger.IsAttached ? 30000 : 5000);

                    Assert.True(result);

                    client.OnMessage -= onMessage;
                    await client.Close();
                }
            }

            [Test]
            [Ignore]
            public async Task CanSendTwentyThousandSynchronousRequestsPerSecond()
            {
                var stopwatch = new Stopwatch();
                int count = 0;
                const string Message = "Message";
                var stream = Encoding.UTF8.GetBytes(Message);
                var waitHandle = new ManualResetEventSlim(false);
                using (var client = new WebSocket("ws://localhost:8080/echo"))
                {
                    const int Multiplicity = 20000;
                    Func<MessageEventArgs, Task> onMessage = e =>
                        {
                            if (e.Text.ReadToEnd() == Message)
                            {
                                count++;
                            }

                            if (count == Multiplicity)
                            {
                                waitHandle.Set();
                            }
                            return Task.FromResult(true);
                        };
                    client.OnMessage = onMessage;

                    await client.Connect();
                    stopwatch.Start();
                    for (int i = 0; i < Multiplicity; i++)
                    {
                        await client.Send(stream);
                    }

                    stopwatch.Stop();

                    waitHandle.Wait(Debugger.IsAttached ? 30000 : 5000);

                    Console.WriteLine(stopwatch.Elapsed);

                    Assert.LessOrEqual(stopwatch.Elapsed, TimeSpan.FromSeconds(1));

                    client.OnMessage -= onMessage;
                    await client.Close();
                }
            }

            [Test]
            [Ignore]
            public async Task CanReceiveTwentyFiveThousandSynchronousRequestsInSixSeconds()
            {
                var responseWatch = new Stopwatch();
                int count = 0;
                const string Message = "Message";
                var stream = Encoding.UTF8.GetBytes(Message);
                var waitHandle = new ManualResetEventSlim(false);
                using (var client = new WebSocket("ws://localhost:8080/echo"))
                {
                    const int Multiplicity = 25000;
                    Func<MessageEventArgs, Task> onMessage = e =>
                        {
                            if (e.Text.ReadToEnd() == Message)
                            {
                                if (Interlocked.Increment(ref count) == Multiplicity)
                                {
                                    responseWatch.Stop();
                                    waitHandle.Set();
                                }
                            }
                            return Task.FromResult(true);
                        };
                    client.OnMessage += onMessage;

                    await client.Connect();
                    responseWatch.Start();
                    for (int i = 0; i < Multiplicity; i++)
                    {
                        await client.Send(stream);
                    }


                    waitHandle.Wait(Debugger.IsAttached ? 30000 : 5000);

                    Console.WriteLine(responseWatch.Elapsed);
                    Assert.LessOrEqual(responseWatch.Elapsed, TimeSpan.FromSeconds(6));

                    client.OnMessage -= onMessage;
                    await client.Close();
                }
            }

            [Test]
            [Ignore]
            public async Task CanSendOneMillionAsynchronousRequestsPerSecond()
            {
                var stopwatch = new Stopwatch();

                int count = 0;
                const string Message = "Message";
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(Message));
                var length = (int)stream.Length;
                var waitHandle = new ManualResetEventSlim(false);
                using (var client = new WebSocket("ws://localhost:8080/echo"))
                {
                    const int Multiplicity = 1000000;
                    Func<MessageEventArgs, Task> onMessage = e =>
                        {
                            if (e.Text.ReadToEnd() == Message)
                            {
                                if (Interlocked.Increment(ref count) == Multiplicity)
                                {
                                    waitHandle.Set();
                                }
                            }
                            return Task.FromResult(true);
                        };
                    client.OnMessage += onMessage;

                    await client.Connect();
                    stopwatch.Start();

                    var tasks = Enumerable.Range(0, Multiplicity).Select(x => client.Send(stream, length));

                    await Task.WhenAll(tasks);
                    stopwatch.Stop();

                    waitHandle.Wait(Debugger.IsAttached ? 30000 : 5000);

                    Console.WriteLine(stopwatch.Elapsed);

                    Assert.LessOrEqual(stopwatch.Elapsed, TimeSpan.FromSeconds(1));

                    client.OnMessage -= onMessage;
                    await client.Close();
                }
            }

            [Test]
            [Ignore]
            public async Task CanReceiveOneMillionAsynchronousResponsesInTenSecond()
            {
                var responseWatch = new Stopwatch();

                int count = 0;
                const string Message = "Message";
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(Message));
                var length = (int)stream.Length;
                var waitHandle = new ManualResetEventSlim(false);
                var client = new WebSocket("ws://localhost:8080/echo");

                const int Multiplicity = 1000000;
                Func<MessageEventArgs, Task> onMessage = e =>
                    {
                        if (e.Text.ReadToEnd() == Message && Interlocked.Increment(ref count) == Multiplicity)
                        {
                            responseWatch.Stop();
                            waitHandle.Set();
                        }
                        return Task.FromResult(true);
                    };
                client.OnMessage += onMessage;

                await client.Connect();
                responseWatch.Start();

                var tasks = Enumerable.Range(0, Multiplicity).Select(x => client.Send(stream, length));

                await Task.WhenAll(tasks);

                waitHandle.Wait(Debugger.IsAttached ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5));

                Console.WriteLine(responseWatch.Elapsed);

                Assert.LessOrEqual(responseWatch.Elapsed, TimeSpan.FromSeconds(10));

                client.OnMessage -= onMessage;
                await client.Close();
                client.Dispose();
            }

            [Test]
            public async Task WhenStreamVeryLargeStreamToServerThenResponds([Random(750000, 1500000, 5)]int length)
            {
                var responseLength = 0;
                //const int Length = 1000000;

                var stream = new EnumerableStream(Enumerable.Repeat((byte)123, length));
                var waitHandle = new ManualResetEventSlim(false);
                using (var client = new WebSocket("ws://localhost:8080/echo"))
                {
                    Func<MessageEventArgs, Task> onMessage = e =>
                        {
                            var bytesRead = 0;
                            var readLength = 10240;
                            do
                            {
                                var buffer = new byte[readLength];
                                bytesRead = e.Data.Read(buffer, 0, readLength);
                                responseLength += buffer.Count(x => x == 123);
                            }
                            while (bytesRead == readLength);

                            waitHandle.Set();
                            return Task.FromResult(true);
                        };

                    client.OnMessage += onMessage;

                    await client.Connect();
                    await client.Send(stream);

                    var result = waitHandle.Wait(Debugger.IsAttached ? -1 : 20000);

                    Assert.True(result);
                    Assert.AreEqual(length, responseLength);

                    client.OnMessage -= onMessage;
                    await client.Close();
                }
            }

            [Test]
            public async Task WhenStreamVeryLargeStreamToServerThenBroadcasts([Random(750000, 1500000, 5)]int length)
            {
                var responseLength = 0;
                //const int Length = 1000000;

                var stream = new EnumerableStream(Enumerable.Repeat((byte)123, length));
                var waitHandle = new ManualResetEventSlim(false);

                var sender = new WebSocket("ws://localhost:8080/radio");
                var client = new WebSocket("ws://localhost:8080/radio");

                Func<MessageEventArgs, Task> onMessage = e =>
                    {
                        while (e.Data.ReadByte() == 123)
                        {
                            responseLength++;
                        }

                        waitHandle.Set();
                        return Task.FromResult(true);
                    };

                client.OnMessage += onMessage;

                await sender.Connect();
                await client.Connect();
                await sender.Send(stream);

                var result = waitHandle.Wait(Debugger.IsAttached ? -1 : 15000);

                Assert.True(result);
                Assert.AreEqual(length, responseLength);

                await client.Close();
                await sender.Close();

                client.OnMessage -= onMessage;
                sender.Dispose();
                client.Dispose();
            }
        }
    }
}