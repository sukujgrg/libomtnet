/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
*
* The above copyright notice and this permission notice shall be included in all
* copies or substantial portions of the Software.
*
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*
*/

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace libomtnet
{
    internal static class OMTLogging
    {
        internal static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();

        public static void Write(string message, string category)
        {
            Messages.Enqueue(category + ": " + message);
        }
    }

    internal static class Program
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        // Use the base class's in-memory registration instead of system DNS-SD.
        private sealed class LocalDiscovery : OMTDiscovery { }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static int ExpectedBufferSize(int requested)
        {
            using var reference = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            reference.SendBufferSize = requested;
            return reference.SendBufferSize;
        }

        private static int SenderSetting(OMTSend sender)
        {
            return (int)typeof(OMTSend).GetField("sendBufferSize", PrivateInstance).GetValue(sender);
        }

        private sealed class Connection : IDisposable
        {
            private readonly Socket peer;
            public readonly Socket Socket;

            public Connection(OMTSend sender)
            {
                var listener = (Socket)typeof(OMTSend).GetField("listener", PrivateInstance).GetValue(sender);
                int port = ((IPEndPoint)listener.LocalEndPoint).Port;
                peer = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    peer.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port))
                        .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    int peerPort = ((IPEndPoint)peer.LocalEndPoint).Port;
                    OMTChannel channel = null;
                    object channelsLock = typeof(OMTSend).GetField("channelsLock", PrivateInstance).GetValue(sender);
                    Require(SpinWait.SpinUntil(() =>
                    {
                        lock (channelsLock)
                        {
                            var channels = (OMTChannel[])typeof(OMTSend).GetField("channels", PrivateInstance).GetValue(sender);
                            channel = channels.FirstOrDefault(c => c.RemoteEndPoint.Port == peerPort);
                            return channel != null;
                        }
                    }, 3000), "Sender did not accept the connection");
                    Socket = (Socket)typeof(OMTChannel).GetField("socket", PrivateInstance).GetValue(channel);
                    var pool = (OMTSocketAsyncPool)typeof(OMTChannel).GetField("sendpool", PrivateInstance).GetValue(channel);
                    bool metadataServer = (bool)typeof(OMTSend).GetField("metadataServer", PrivateInstance).GetValue(sender);
                    Require(pool.Count == (metadataServer ? 64 : 4), "Send-buffer setting changed the bounded send pool");
                }
                catch
                {
                    peer.Dispose();
                    throw;
                }
            }

            public void Dispose()
            {
                peer.Dispose();
            }
        }

        private static void CheckSetting(string value, int expected)
        {
            OMTSettings.GetInstance().SetString("NetworkSendBufferSize", value);
            using var sender = new OMTSend("Send buffer regression", OMTQuality.Default);
            Require(SenderSetting(sender) == expected, "Unexpected setting for '" + value + "'");
            using var connection = new Connection(sender);
            Require(connection.Socket.SendBufferSize == ExpectedBufferSize(expected),
                "Socket did not apply the expected request for '" + value + "'");
            string diagnostic = "OMTChannel: SendBufferSize: requested=" + expected + ", actual=" + connection.Socket.SendBufferSize;
            Require(OMTLogging.Messages.Contains(diagnostic), "Requested/actual buffer sizes were not logged");
        }

        private static void DefaultAndInvalidValues()
        {
            foreach (string value in new[] { "", "invalid", "0", "-1", "65535", "4194305", "2147483647", "2147483648" })
            {
                CheckSetting(value, 65536);
            }
        }

        private static void ValidValues()
        {
            foreach (int value in new[] { 65536, 524288, 4194304 })
            {
                CheckSetting(value.ToString(), value);
            }
        }

        private static void SenderIsolationAndLaterConnections()
        {
            OMTSettings settings = OMTSettings.GetInstance();
            settings.SetInteger("NetworkSendBufferSize", 524288);
            using var first = new OMTSend("First send buffer regression", OMTQuality.Default);
            using var original = new Connection(first);
            settings.SetInteger("NetworkSendBufferSize", 1048576);
            using var second = new OMTSend("Second send buffer regression", OMTQuality.Default);
            using var next = new Connection(second);
            using var later = new Connection(first);
            Require(SenderSetting(first) == 524288 && SenderSetting(second) == 1048576,
                "Changing the process setting changed an existing sender");
            Require(original.Socket.SendBufferSize == ExpectedBufferSize(524288), "Existing connection changed");
            Require(later.Socket.SendBufferSize == ExpectedBufferSize(524288), "Later connection lost its sender setting");
            Require(next.Socket.SendBufferSize == ExpectedBufferSize(1048576), "New sender did not use its setting");
        }

        private static void ReceiverBufferUnchanged()
        {
            OMTSettings.GetInstance().SetInteger("NetworkSendBufferSize", 524288);
            foreach (OMTFrameType frameType in new[] { OMTFrameType.Video, OMTFrameType.Audio, OMTFrameType.Metadata })
            {
                using var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                listener.Listen(1);
                var accept = listener.AcceptAsync();
                using var peer = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                peer.ConnectAsync(listener.LocalEndPoint).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                using var socket = accept.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                using var channel = new OMTChannel(socket, frameType, null, null, false);
                Require(socket.SendBufferSize == ExpectedBufferSize(65536), "Receiver send buffer changed");
            }
        }

        private static void DiscoveryServerBufferUnchanged()
        {
            OMTSettings.GetInstance().SetInteger("NetworkSendBufferSize", 524288);
            using var sender = new OMTSend(new IPEndPoint(IPAddress.IPv6Loopback, 0), null);
            using var connection = new Connection(sender);
            Require(connection.Socket.SendBufferSize == ExpectedBufferSize(65536), "Discovery-server send buffer changed");
        }

        public static int Main()
        {
            string previousPath = Environment.GetEnvironmentVariable("OMT_STORAGE_PATH");
            string directory = Path.Combine(Path.GetTempPath(), "omt-send-buffer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("OMT_STORAGE_PATH", directory);
            FieldInfo discoveryInstance = typeof(OMTDiscovery).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
            using var discovery = new LocalDiscovery();
            discoveryInstance.SetValue(null, discovery);
            OMTSettings.GetInstance().SetInteger("NetworkPortStart", 0);
            OMTSettings.GetInstance().SetInteger("NetworkPortEnd", 0);
            var tests = new (string name, Action run)[]
            {
                ("default and invalid values retain 64 KiB", DefaultAndInvalidValues),
                ("valid requests and actual sizes are applied and logged", ValidValues),
                ("senders retain their setting for existing and later connections", SenderIsolationAndLaterConnections),
                ("receiver buffers are unchanged", ReceiverBufferUnchanged),
                ("discovery-server buffers are unchanged", DiscoveryServerBufferUnchanged)
            };
            int failed = 0;
            try
            {
                foreach (var test in tests)
                {
                    try
                    {
                        test.run();
                        Console.WriteLine("PASS " + test.name);
                    }
                    catch (Exception error)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + test.name + ": " + error.Message);
                    }
                }
            }
            finally
            {
                discoveryInstance.SetValue(null, null);
                Environment.SetEnvironmentVariable("OMT_STORAGE_PATH", previousPath);
                Directory.Delete(directory, true);
            }
            return failed == 0 ? 0 : 1;
        }
    }
}
