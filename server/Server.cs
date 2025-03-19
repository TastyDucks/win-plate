using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoskServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("[+] WebSocket server starting on ws://localhost:8080/ws");
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/ws/");
            listener.Start();

            while (true)
            {
                var context = await listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    _ = HandleConnection(context);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }

        private static async Task HandleConnection(HttpListenerContext context)
        {
            Console.WriteLine("[*] New WebSocket connection");
            var wsContext = await context.AcceptWebSocketAsync(null);
            var ws = wsContext.WebSocket;

            var sessionId = Guid.NewGuid().ToString();
            Console.WriteLine($"[*] Session: {sessionId}");

            var audioBuffer = new MemoryStream();
            var buffer = new byte[4096];
            bool isAudioSessionActive = false;

            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"[*] Session {sessionId} closed by client");
                    break;
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"[>] Text received: {text}");

                    try
                    {
                        var json = JsonDocument.Parse(text);
                        var type = json.RootElement.GetProperty("type").GetString();

                        switch (type)
                        {
                            case "audio_start":
                                isAudioSessionActive = true;
                                audioBuffer = new MemoryStream();
                                Console.WriteLine($"[+] Audio stream started for session {sessionId}");
                                break;

                            case "audio_end":
                                if (isAudioSessionActive)
                                {
                                    var outputPath = $"audio_{sessionId}_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
                                    WriteWavFile(outputPath, audioBuffer.ToArray(), 16000, 1, 16);
                                    Console.WriteLine($"[+] Audio stream saved to {outputPath}");
                                    isAudioSessionActive = false;
                                }
                                break;

                            case "partial":
                                var response = new
                                {
                                    type = "mock_db_result",
                                    payload = new { plate = "G7B2JK", match = true, score = 0.98 }
                                };
                                var respBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
                                await ws.SendAsync(new ArraySegment<byte>(respBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                                Console.WriteLine("[<] Sent mock DB hit");
                                break;

                            case "final":
                                var finalResponse = new
                                {
                                    type = "mock_db_result",
                                    payload = new { plate = "G7B2JK", match = true, score = 0.98 }
                                };
                                var finalRespBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(finalResponse));
                                await ws.SendAsync(new ArraySegment<byte>(finalRespBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                                Console.WriteLine("[<] Sent mock DB hit");
                                break;

                            default:
                                Console.WriteLine("[!] Unknown control message type");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] JSON Parse Error: {ex.Message}");
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (isAudioSessionActive)
                    {
                        audioBuffer.Write(buffer, 0, result.Count);
                    }
                }
            }

            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
            ws.Dispose();
        }

        private static void WriteWavFile(string path, byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;
            int subchunk2Size = pcmData.Length;

            // WAV header
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + subchunk2Size);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16); // PCM chunk size
            bw.Write((short)1); // PCM format
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)blockAlign);
            bw.Write((short)bitsPerSample);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(subchunk2Size);

            // PCM data
            bw.Write(pcmData);
        }
    }
}
