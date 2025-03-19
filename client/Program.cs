using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using NAudio.Wave;
using Vosk;

namespace VoskClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("[+] Vosk License Plate STT with WebSocket...");

            const string modelPath = "vosk-model-small-en-us-0.15/";

            if (!Directory.Exists(modelPath))
            {
                Console.WriteLine("[-] Vosk model not found!");
                return;
            }

            Vosk.Vosk.SetLogLevel(0);
            var model = new Model(modelPath);

            var grammar = new string[]
            {
                // TODO: I'm sure people are going to use other edge-cases like "one hundred" or "three sevens", what should be considered there?
                "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
                "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
                "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
                "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
                "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
                "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey",
                "xray", "yankee", "zulu",
            };

            var grammarJSON = JsonSerializer.Serialize(grammar);
            var recognizer = new VoskRecognizer(model, 16000.0f, grammarJSON);
            recognizer.SetMaxAlternatives(3);

            var wsClient = new WebSocketClient("ws://localhost:8080/ws");
            await wsClient.ConnectAsync();

            var audioService = new AudioService(wsClient);
            Console.WriteLine("Press ENTER to start real-time capture...");
            Console.ReadLine();
            await audioService.StreamAudioAsync(recognizer);

            recognizer.Dispose();
            model.Dispose();
        }
    }

    public class AudioService
    {
        private readonly WebSocketClient _ws;

        public AudioService(WebSocketClient ws)
        {
            _ws = ws;
        }

        public async Task StreamAudioAsync(VoskRecognizer recognizer)
        {
            var waveFormat = new WaveFormat(16000, 1);
            using var waveIn = new WaveInEvent
            {
                WaveFormat = waveFormat,
                BufferMilliseconds = 500
            };

            // Optional: send start-of-stream message
            await _ws.SendTextAsync(new { type = "audio_start" });

            waveIn.DataAvailable += async (s, a) =>
            {
                // Stream raw audio PCM data (binary frames)
                await _ws.SendBinaryAsync(a.Buffer, a.BytesRecorded);

                // STT
                if (recognizer.AcceptWaveform(a.Buffer, a.BytesRecorded))
                {
                    var result = recognizer.Result();
                    Console.WriteLine("[+] Final: " + result);
                    await _ws.SendTextAsync(new { type = "final", payload = JsonDocument.Parse(result) });
                }
                else
                {
                    var interim = recognizer.PartialResult();
                    await _ws.SendTextAsync(new { type = "partial", payload = JsonDocument.Parse(interim) });
                }
            };

            waveIn.StartRecording();
            Console.WriteLine("[*] Streaming... Press ENTER to stop.");
            await Task.Run(() => Console.ReadLine());
            waveIn.StopRecording();

            // Send stop-of-stream indicator so server knows audio is complete
            await _ws.SendTextAsync(new { type = "audio_end" });
        }
    }


    public class WebSocketClient
    {
        private readonly ClientWebSocket _client;
        private readonly Uri _uri;

        public WebSocketClient(string url)
        {
            _client = new ClientWebSocket();
            _uri = new Uri(url);
        }

        public async Task ConnectAsync(int maxAttempts = 0, int retryIntervalMs = 2000, CancellationToken cancellationToken = default)
        {
            int attemptCount = 0;
            bool connected = false;

            Console.WriteLine($"[*] Attempting to connect to WebSocket at {_uri}...");
            
            while (!connected && (maxAttempts == 0 || attemptCount < maxAttempts) && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_client.State == WebSocketState.Aborted || _client.State == WebSocketState.Closed)
                    {
                        // Create a new client if previous one is in terminal state
                        _client.Dispose();
                        var newClient = new ClientWebSocket();
                        typeof(WebSocketClient).GetField("_client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                            ?.SetValue(this, newClient);
                    }

                    attemptCount++;
                    Console.WriteLine($"[*] Connection attempt {attemptCount}" + (maxAttempts > 0 ? $" of {maxAttempts}..." : "..."));
                    
                    await _client.ConnectAsync(_uri, cancellationToken);
                    connected = true;
                    Console.WriteLine($"[+] Successfully connected to WebSocket after {attemptCount} attempt(s)!");
                    
                    // Start receive loop only when successfully connected
                    _ = Task.Run(ReceiveLoop, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[-] Connection attempt {attemptCount} failed: {ex.Message}");
                    
                    if (maxAttempts > 0 && attemptCount >= maxAttempts)
                    {
                        throw new Exception($"Failed to connect after {maxAttempts} attempts", ex);
                    }
                    
                    Console.WriteLine($"[*] Retrying in {retryIntervalMs}ms...");
                    await Task.Delay(retryIntervalMs, cancellationToken);
                }
            }
            
            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("[!] Connection attempts cancelled");
                throw new OperationCanceledException(cancellationToken);
            }
        }

        public async Task SendTextAsync(object obj)
        {
            var json = JsonSerializer.Serialize(obj);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task SendBinaryAsync(byte[] data, int length)
        {
            await _client.SendAsync(new ArraySegment<byte>(data, 0, length), WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[4096];
            while (_client.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine("[<] Received: " + message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[-] Error in receive loop: {ex.Message}");
                    break;
                }
            }
            
            Console.WriteLine($"[!] WebSocket connection closed. State: {_client.State}");
        }
    }
}
