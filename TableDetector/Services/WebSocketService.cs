using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using TableDetector.Models;

namespace TableDetector.Services
{
    public class WebSocketService : IDisposable
    {
        private ClientWebSocket socket;
        private Uri serverUri;
        private CancellationTokenSource cts;
        private Task receiveTask;

        public bool IsConnected => socket?.State == WebSocketState.Open;

        public WebSocketService(string uri)
        {
            serverUri = new Uri(uri);
        }

        public async Task ConnectAsync()
        {
            socket = new ClientWebSocket();
            cts = new CancellationTokenSource();

            try
            {
                await socket.ConnectAsync(serverUri, cts.Token);
                receiveTask = Task.Run(ReceiveLoop);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebSocket connection failed: {ex.Message}");
            }
        }

        public async Task SendTokensAsync(IEnumerable<TTRPGToken> tokens)
        {
            if (!IsConnected) return;

            var payload = JsonConvert.SerializeObject(tokens);
            var buffer = Encoding.UTF8.GetBytes(payload);
            var segment = new ArraySegment<byte>(buffer);

            await socket.SendAsync(segment, WebSocketMessageType.Text, true, cts.Token);
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[1024];

            try
            {
                while (!cts.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WebSocket receive error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (socket != null)
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait();
                    }
                    socket.Dispose();
                    socket = null;
                }

                if (cts != null)
                {
                    cts.Cancel();
                    cts.Dispose();
                    cts = null;
                }

                if (receiveTask != null)
                {
                    receiveTask.Wait();
                    receiveTask.Dispose();
                    receiveTask = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("WebSocketService dispose error: " + ex.Message);
            }
        }
    }
}
