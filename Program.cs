using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;
using Vosk;

namespace VoskClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("[+] Vosk License Plate STT starting...");

            const string modelPath = "vosk-model-small-en-us-0.15/";

            if (!Directory.Exists(modelPath))
            {
                Console.WriteLine("[-] Vosk model not found!");
                return;
            }

            Vosk.Vosk.SetLogLevel(0);
            var model = new Model(modelPath);

            // Grammar for license plates + phonetic alphabet
            var grammar = new string[]
            {
                "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
                "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z",
                "ZERO", "ONE", "TWO", "THREE", "FOUR", "FIVE", "SIX", "SEVEN", "EIGHT", "NINE"
            };

            var grammarJson = JsonSerializer.Serialize(grammar);
            var recognizer = new VoskRecognizer(model, 16000.0f);
            recognizer.SetMaxAlternatives(3);

            var audioService = new AudioService();
            Console.WriteLine("Press ENTER to start real-time capture...");
            Console.ReadLine();
            await audioService.StreamAudioAsync(recognizer);

            recognizer.Dispose();
            model.Dispose();
        }
    }

    public class AudioService
    {
        public async Task StreamAudioAsync(VoskRecognizer recognizer)
        {
            var waveFormat = new WaveFormat(16000, 1);
            using var waveIn = new WaveInEvent
            {
                WaveFormat = waveFormat,
                BufferMilliseconds = 1000
            };

            waveIn.DataAvailable += (s, a) =>
            {
                if (recognizer.AcceptWaveform(a.Buffer, a.BytesRecorded))
                {
                    var result = recognizer.Result();
                    Console.WriteLine("[+] Partial (accepted): " + result);
                }
                else
                {
                    var interim = recognizer.PartialResult();
                    Console.WriteLine("[~] Interim: " + interim);
                }
            };

            waveIn.StartRecording();
            Console.WriteLine("[*] Recording... Press ENTER to stop.");
            await Task.Run(() => Console.ReadLine());
            waveIn.StopRecording();

            Console.WriteLine("[+] Final: " + recognizer.FinalResult());
        }
    }
}
